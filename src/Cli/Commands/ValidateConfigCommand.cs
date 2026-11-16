using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Cli.Diagram;
using fuseraft.Cli.Protocol;
using fuseraft.Core.Models;
using fuseraft.Core.Models.Config;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.Plugins;
using fuseraft.Orchestration;

namespace fuseraft.Cli.Commands;

public sealed class ValidateConfigSettings : CommandSettings
{
    [CommandArgument(0, "<path>")]
    [Description("Path to the config file to validate.")]
    public string Path { get; set; } = string.Empty;

    [CommandOption("--strict")]
    [Description("Fail if any agent references a plugin not registered in the default registry.")]
    public bool Strict { get; set; }

    [CommandOption("--diagram")]
    [Description("Print a Mermaid flowchart of the workflow after validation. Paste into https://mermaid.live to render.")]
    public bool Diagram { get; set; }

    [CommandOption("-c|--check-connectivity")]
    [Description("Make a minimal test call to each unique provider endpoint to verify the API key is valid and the endpoint is reachable. Incurs a small API cost (~1 token per unique endpoint).")]
    public bool CheckConnectivity { get; set; }

    [CommandOption("--show-paths")]
    [Description("Print all interpolated runtime paths after token expansion so you can verify {project_slug} and {session_id} resolve correctly.")]
    public bool ShowPaths { get; set; }

    [CommandOption("--session-id")]
    [Description("Session ID to use when previewing interpolated paths (default: a synthetic preview ID).")]
    public string? SessionId { get; set; }

    [CommandOption("--json")]
    [Description("Print one JSON object to stdout with every issue found (see docs/protocol.md). Exits 1 when the config has errors.")]
    public bool Json { get; set; }
}

/// <summary>
/// Validates an orchestration config file and reports all issues found.
/// </summary>
public sealed class ValidateConfigCommand(PluginRegistry pluginRegistry) : AsyncCommand<ValidateConfigSettings>
{

    internal Task<int> ExecuteAsync(CommandContext context, ValidateConfigSettings settings) =>
        ExecuteAsync(context, settings, CancellationToken.None);

    private OrchestrationConfig? _config;

    public override async Task<int> ExecuteAsync(CommandContext context, ValidateConfigSettings settings, CancellationToken cancellationToken)
    {
        var issues = new List<(string Level, string Message)>();
        if (!settings.Json)
            return await ValidateAsync(settings, issues);

        if (settings.Diagram || settings.ShowPaths)
            return WireProtocol.WriteError("--json can't be combined with --diagram or --show-paths.");

        var console = AnsiConsole.Console;
        AnsiConsole.Console = WireProtocol.StderrConsole;
        int exit;
        try { exit = await ValidateAsync(settings, issues); }
        finally { AnsiConsole.Console = console; }

        WireProtocol.WriteItem(WireValidation.From(settings.Path, issues, _config));
        return exit;
    }

    private async Task<int> ValidateAsync(ValidateConfigSettings settings, List<(string Level, string Message)> issues)
    {
        AnsiConsole.MarkupLine($"Validating [dim]{Markup.Escape(settings.Path)}[/]...");
        AnsiConsole.WriteLine();

        // File existence
        if (!File.Exists(settings.Path))
        {
            issues.Add(("error", $"File not found: {settings.Path}"));
            PrintIssues(issues);
            return 1;
        }

        // Syntax check — YAML or JSON depending on extension
        var rawContent = string.Empty;
        if (Cli.YamlConfigLoader.IsYamlPath(settings.Path))
        {
            try
            {
                rawContent = File.ReadAllText(settings.Path);
                Cli.YamlConfigLoader.ValidateSyntax(rawContent);
            }
            catch (Exception ex)
            {
                issues.Add(("error", $"Invalid YAML: {ex.Message}"));
                PrintIssues(issues);
                return 1;
            }
        }
        else
        {
            try
            {
                rawContent = File.ReadAllText(settings.Path);
                JsonDocument.Parse(rawContent);
            }
            catch (JsonException ex)
            {
                issues.Add(("error", $"Invalid JSON: {ex.Message}"));
                PrintIssues(issues);
                return 1;
            }
        }

        // Schema binding
        OrchestrationConfig config;
        try
        {
            config = OrchestratorConfigLoader.LoadConfig(settings.Path);
            _config = config;
        }
        catch (Exception ex)
        {
            issues.Add(("error", $"Config binding failed: {ex.Message}"));
            PrintIssues(issues);
            return 1;
        }

        // Semantic checks
        if (string.IsNullOrWhiteSpace(config.Name))
            issues.Add(("warning", "Team is empty."));

        if (!string.IsNullOrWhiteSpace(config.SystemPromptPath))
        {
            var configDir  = Path.GetDirectoryName(Path.GetFullPath(settings.Path)) ?? ".";
            var promptPath = Path.IsPathRooted(config.SystemPromptPath)
                ? config.SystemPromptPath
                : Path.GetFullPath(config.SystemPromptPath, configDir);
            if (!File.Exists(promptPath))
                issues.Add(("error", $"SystemPromptPath file not found: {promptPath}"));
        }

        ValidateAgents(config, settings, issues);

        var selType = config.Selection.Type.ToLowerInvariant();
        if (selType == OrchestratorTypes.Magentic)
            ValidateMagenticSelection(config, issues);

        ValidateCompactionConfig(config, issues);

        ValidateMemoryLayer(config, issues);

        ValidateMcpServers(config, issues);

        ValidateSecurity(config, issues);

        return await ReportResultsAsync(config, settings, issues);
    }

    private void ValidateAgents(
        OrchestrationConfig config,
        ValidateConfigSettings settings,
        List<(string Level, string Message)> issues)
    {
        if (config.Agents.Count == 0)
        {
            issues.Add(("error", "No agents defined."));
        }
        else
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var agent in config.Agents)
            {
                if (string.IsNullOrWhiteSpace(agent.Name))
                    issues.Add(("error", "An agent has an empty Name."));
                else if (!names.Add(agent.Name))
                    issues.Add(("error", $"Duplicate agent name: '{agent.Name}'."));

                if (string.IsNullOrWhiteSpace(agent.Instructions))
                    issues.Add(("warning", $"Agent '{agent.Name}' has no instructions."));

                // Remote A2A agent: validate URL; skip local model checks.
                if (agent.RemoteAgent is not null)
                {
                    if (string.IsNullOrWhiteSpace(agent.RemoteAgent.Url))
                        issues.Add(("error", $"Agent '{agent.Name}': RemoteAgent.Url is required when RemoteAgent is set."));
                    else if (!Uri.TryCreate(agent.RemoteAgent.Url, UriKind.Absolute, out _))
                        issues.Add(("error", $"Agent '{agent.Name}': RemoteAgent.Url '{agent.RemoteAgent.Url}' is not a valid absolute URL."));

                    foreach (var (header, variable) in RemoteAgentHttpClient.UnsetEnvTokens(agent.RemoteAgent))
                        issues.Add(("warning", $"Agent '{agent.Name}': RemoteAgent header '{header}' uses ${{{variable}}}, which is not set; it will be sent empty."));

                    if (!string.IsNullOrWhiteSpace(agent.Model.ModelId))
                        issues.Add(("warning", $"Agent '{agent.Name}': model.modelId is ignored when remoteAgent is set."));

                    if (agent.Plugins.Count > 0)
                        issues.Add(("warning", $"Agent '{agent.Name}': Plugins are ignored when RemoteAgent is set."));
                }
                else
                {
                if (string.IsNullOrWhiteSpace(agent.Model.ModelId))
                {
                    if (string.IsNullOrWhiteSpace(UserConfigStore.Load().Config?.ModelId))
                        issues.Add(("error", $"Agent '{agent.Name}': no model, and no default model is set. Give the agent a model, or set one with 'fuseraft settings set provider.modelId <model>'."));
                }
                else
                {
                // Resolve the model against the Models registry before checking connection
                // fields — an alias fills in Endpoint and ApiKeyEnvVar at runtime.
                var resolved = ResolveModelAlias(agent.Model, config.Models);

                if (string.IsNullOrWhiteSpace(resolved.Endpoint)
                    && !HasKnownProviderPrefix(resolved.ModelId)
                    && !IsOllamaModel(resolved.ModelId))
                    issues.Add(("error", $"Agent '{agent.Name}': model.endpoint is empty and '{resolved.ModelId}' does not match a known provider prefix. Set endpoint explicitly or use a model ID that starts with a recognised prefix (grok-, gpt-, claude-, gemini-, etc.)."));

                if (string.IsNullOrWhiteSpace(resolved.ApiKeyEnvVar)
                    && !IsOllamaModel(resolved.ModelId))
                    issues.Add(("warning", $"Agent '{agent.Name}': no apiKeyEnvVar set."));
                else if (!string.IsNullOrWhiteSpace(resolved.ApiKeyEnvVar)
                    && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(resolved.ApiKeyEnvVar)))
                    issues.Add(("warning", $"Agent '{agent.Name}': Env var '{resolved.ApiKeyEnvVar}' is not set in this shell."));
                }
                }

                if (agent.FunctionChoice.ToLowerInvariant() is not ("auto" or "required" or "none"))
                    issues.Add(("error", $"Agent '{agent.Name}': functionChoice '{agent.FunctionChoice}' is invalid. Valid values: auto, required, none."));

                if (agent.TrustScore is < 0.0 or > 1.0)
                    issues.Add(("error", $"Agent '{agent.Name}': trustScore must be 0.0–1.0 (got {agent.TrustScore})."));

                // Accepted values are provider- and model-specific (and keep growing — e.g.
                // "xhigh", "max"), so this isn't checked against a fixed enum. The only thing
                // that's unambiguously wrong is a value with embedded whitespace.
                var effort = agent.Model.ReasoningEffort;
                if (effort is not null && effort.Trim().Contains(' '))
                    issues.Add(("error", $"Agent '{agent.Name}': model.reasoningEffort '{effort}' looks malformed — expected a single token (e.g. none, low, medium, high, xhigh, max)."));

                if (settings.Strict)
                {
                    var registered = pluginRegistry.RegisteredPlugins
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var plugin in agent.Plugins)
                        if (!registered.Contains(plugin))
                            issues.Add(("warning", $"Agent '{agent.Name}': plugin '{plugin}' is not registered."));
                }
            }
        }
    }

    private static void ValidateCompactionConfig(
        OrchestrationConfig config,
        List<(string Level, string Message)> issues)
    {
        // Context budget — mirror the guards in OrchestratorBuilder.BuildAsync so they
        // surface here rather than only at session startup.
        if (config.ContextBudget is { } cb)
        {
            bool needsCompactor = cb.CutoverAt > 0 || cb.MaxSingleTurnInputTokens > 0;
            if (needsCompactor && config.Compaction is null)
                issues.Add(("error",
                    "advanced.contextBudget.cutoverAt and maxSingleTurnInputTokens require " +
                    "advanced.compaction. Add it to enable automatic context trimming."));

            if (cb.WarnAt > 0 && cb.CutoverAt > 0 && cb.WarnAt >= cb.CutoverAt)
                issues.Add(("error",
                    $"advanced.contextBudget.warnAt ({cb.WarnAt:N0}) must be less than cutoverAt ({cb.CutoverAt:N0})."));

            if (config.WarnTurnTokens > 0 && cb.CutoverAt > 0 && config.WarnTurnTokens >= cb.CutoverAt)
                issues.Add(("warning",
                    $"advanced.warnTurnTokens ({config.WarnTurnTokens:N0}) is >= advanced.contextBudget.cutoverAt ({cb.CutoverAt:N0}). " +
                    "The per-turn warning fires in the same turn as compaction — lower warnTurnTokens " +
                    "below cutoverAt to get an advance signal."));
        }

        if (config.Compaction?.AntiThrashMinSavingsRatio is < 0.0 or > 1.0)
            issues.Add(("error", $"advanced.compaction.antiThrashMinSavingsRatio must be 0.0–1.0 (got {config.Compaction.AntiThrashMinSavingsRatio})."));
    }

    private static void ValidateMemoryLayer(
        OrchestrationConfig config,
        List<(string Level, string Message)> issues)
    {
        // Telemetry
        if (config.Telemetry is { OtlpEndpoint: { } endpoint })
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out _))
                issues.Add(("error", $"advanced.telemetry.otlpEndpoint is not a valid URI: '{endpoint}'."));
        }
    }

    private static void ValidateSecurity(
        OrchestrationConfig config,
        List<(string Level, string Message)> issues)
    {
        if (config.Security.ShellPolicy is not { } policy) return;

        var mode = policy.AllowMode;
        if (!string.Equals(mode, ShellPolicy.AllowModeSubstring, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(mode, ShellPolicy.AllowModeSegments, StringComparison.OrdinalIgnoreCase))
            issues.Add(("error",
                $"security.shellPolicy.allowMode '{mode}' is invalid. Valid values: " +
                $"{ShellPolicy.AllowModeSubstring}, {ShellPolicy.AllowModeSegments}."));

        if (policy.AllowSegments && policy.Allow.Count == 0)
            issues.Add(("warning",
                "security.shellPolicy.allowMode is 'segments' but allow is empty, so it has no effect " +
                "(an empty allow list means every command is allowed)."));
    }

    private static void ValidateMcpServers(
        OrchestrationConfig config,
        List<(string Level, string Message)> issues)
    {
        if (config.McpServers.Count == 0) return;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var server in config.McpServers)
        {
            var prefix = string.IsNullOrWhiteSpace(server.Name) ? "mcpServers[?]" : $"mcpServers['{server.Name}']";

            if (string.IsNullOrWhiteSpace(server.Name))
                issues.Add(("error", $"{prefix}: name is required."));
            else if (!names.Add(server.Name))
                issues.Add(("error", $"{prefix}: Duplicate MCP server name."));

            var transport = server.Transport.ToLowerInvariant();
            if (transport is not ("stdio" or "http"))
            {
                issues.Add(("error", $"{prefix}: unknown transport '{server.Transport}'. Valid values: stdio, http."));
                continue;
            }

            if (transport == "stdio")
            {
                if (string.IsNullOrWhiteSpace(server.Command))
                    issues.Add(("error", $"{prefix}: command is required for stdio transport."));

                if (server.Headers.Count > 0)
                    issues.Add(("warning", $"{prefix}: headers is ignored for stdio transport (http only)."));
                if (server.OAuth is not null)
                    issues.Add(("error", $"{prefix}: oauth requires transport: http."));
                if (!server.TransportMode.Equals("auto", StringComparison.OrdinalIgnoreCase))
                    issues.Add(("warning", $"{prefix}: transportMode is ignored for stdio transport (http only)."));
            }
            else // http
            {
                if (string.IsNullOrWhiteSpace(server.Url))
                {
                    issues.Add(("error", $"{prefix}: url is required for http transport."));
                }
                else
                {
                    var expandedUrl = ProcessHelper.ExpandEnvTokens(server.Url);
                    if (!Uri.TryCreate(expandedUrl, UriKind.Absolute, out _))
                        issues.Add(("error", $"{prefix}: url '{server.Url}' is not a valid absolute URL."));
                }

                var mode = server.TransportMode.Trim().ToLowerInvariant();
                if (mode is not ("" or "auto" or "autodetect" or "streamable-http" or "streamablehttp" or "streamable" or "sse"))
                    issues.Add(("error",
                        $"{prefix}: unknown transportMode '{server.TransportMode}'. Valid values: auto, streamable-http, sse."));

                foreach (var (header, value) in server.Headers)
                    foreach (var envVar in ExtractEnvTokens(value))
                        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(envVar)))
                            issues.Add(("warning",
                                $"{prefix}: Header '{header}' references env var '{envVar}', which is not set in this shell."));

                if (server.Command is not null || server.Args.Count > 0 || server.Env.Count > 0 || server.WorkingDirectory is not null)
                    issues.Add(("warning", $"{prefix}: Command/Args/Env/WorkingDirectory are ignored for http transport (stdio only)."));

                if (server.OAuth is { } oauth)
                {
                    if (oauth.CallbackPort is <= 0 or > 65535)
                        issues.Add(("error",
                            $"{prefix}: OAuth.CallbackPort must be a valid TCP port (1-65535), got {oauth.CallbackPort}."));

                    if (oauth.ClientSecret is { } secret)
                        foreach (var envVar in ExtractEnvTokens(secret))
                            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(envVar)))
                                issues.Add(("warning",
                                    $"{prefix}: OAuth.ClientSecret references env var '{envVar}', which is not set in this shell."));

                    if (!string.IsNullOrWhiteSpace(oauth.ClientSecret) && string.IsNullOrWhiteSpace(oauth.ClientId))
                        issues.Add(("warning",
                            $"{prefix}: OAuth.ClientSecret is set without ClientId — this is almost always a mistake."));
                }
            }
        }
    }

    private static IEnumerable<string> ExtractEnvTokens(string value) =>
        System.Text.RegularExpressions.Regex.Matches(value, @"\$\{([^}]+)\}")
            .Select(m => m.Groups[1].Value);

    private static async Task ValidateMcpConnectivityAsync(
        OrchestrationConfig config,
        ValidateConfigSettings settings,
        List<(string Level, string Message)> issues)
    {
        if (settings.CheckConnectivity)
            await CheckConnectivityAsync(config, issues);
    }

    private static async Task<int> ReportResultsAsync(
        OrchestrationConfig config,
        ValidateConfigSettings settings,
        List<(string Level, string Message)> issues)
    {
        // Report static issues, then optionally run live connectivity checks.
        PrintIssues(issues);

        await ValidateMcpConnectivityAsync(config, settings, issues);

        var errorCount = issues.Count(x => x.Level == "error");
        var warnCount  = issues.Count(x => x.Level == "warning");

        if (errorCount == 0)
        {
            AnsiConsole.MarkupLine($"[green]✓ Valid[/]" +
                (warnCount > 0 ? $"  [yellow]{warnCount} warning(s)[/]" : string.Empty));

            if (settings.Diagram)
                PrintDiagram(config);

            if (settings.ShowPaths)
                PrintInterpolatedPaths(config, settings.SessionId);

            return 0;
        }

        AnsiConsole.MarkupLine($"[red]✗ {errorCount} error(s)[/]  [yellow]{warnCount} warning(s)[/]");

        if (settings.Diagram)
            PrintDiagram(config);

        if (settings.ShowPaths)
            PrintInterpolatedPaths(config, settings.SessionId);

        return 1;
    }

    /// <summary>
    /// Applies the Models registry alias lookup to a model config, mirroring the
    /// first step of <see cref="fuseraft.Infrastructure.Chat.ChatClientFactory.Resolve"/>.
    /// Per-agent Temperature/MaxTokens always take precedence over alias values.
    /// </summary>
    private static ModelConfig ResolveModelAlias(
        ModelConfig model,
        IReadOnlyDictionary<string, ModelConfig> registry)
    {
        if (registry.TryGetValue(model.ModelId, out var alias))
        {
            return alias with
            {
                Temperature     = model.Temperature ?? alias.Temperature,
                MaxTokens       = model.MaxTokens > 0 ? model.MaxTokens : alias.MaxTokens,
                ReasoningEffort = model.ReasoningEffort ?? alias.ReasoningEffort,
            };
        }
        return model;
    }

    // Provider prefixes that ChatClientFactory auto-detects — no explicit Endpoint required.
    private static readonly string[] KnownPrefixes =
    [
        "gpt", "o1", "o3", "o4", "grok-", "claude-",
        "gemini-", "learnlm-", "mistral-", "mixtral-", "codestral-", "pixtral-",
        "deepseek-", "llama", "phi", "qwen", "gemma", "codellama", "smollm"
    ];

    private static bool HasKnownProviderPrefix(string modelId) =>
        KnownPrefixes.Any(p => modelId.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    // Ollama models run locally and need no API key.
    private static readonly string[] OllamaPrefixes =
        ["llama", "phi", "qwen", "gemma", "codellama", "smollm"];

    private static bool IsOllamaModel(string modelId) =>
        OllamaPrefixes.Any(p => modelId.StartsWith(p, StringComparison.OrdinalIgnoreCase))
        || (modelId.Contains(':') && !modelId.Contains("://"));

    private static void ValidateMagenticSelection(
        OrchestrationConfig config,
        List<(string Level, string Message)> issues)
    {
        var mag = config.Selection.Magentic;

        if (mag?.Model is null) return;

        var resolved = ResolveModelAlias(mag.Model, config.Models);

        if (string.IsNullOrWhiteSpace(resolved.Endpoint)
            && !HasKnownProviderPrefix(resolved.ModelId)
            && !IsOllamaModel(resolved.ModelId))
            issues.Add(("error",
                $"flow.model: endpoint is empty and '{resolved.ModelId}' does not match " +
                "a known provider prefix. Set endpoint explicitly or use a model ID that starts with a " +
                "recognised prefix (grok-, gpt-, claude-, gemini-, etc.)."));

        if (string.IsNullOrWhiteSpace(resolved.ApiKeyEnvVar) && !IsOllamaModel(resolved.ModelId))
            issues.Add(("warning", "flow.model: no apiKeyEnvVar set."));
        else if (!string.IsNullOrWhiteSpace(resolved.ApiKeyEnvVar)
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(resolved.ApiKeyEnvVar)))
            issues.Add(("warning",
                $"flow.model: env var '{resolved.ApiKeyEnvVar}' is not set in this shell."));
    }

    private static async Task CheckConnectivityAsync(
        OrchestrationConfig config,
        List<(string Level, string Message)> issues)
    {
        AnsiConsole.MarkupLine("[dim]Checking API connectivity...[/]");
        AnsiConsole.WriteLine();

        using var factory = new ChatClientFactory(config.Models);

        // Collect (label, resolvedModel) pairs from every place a model can be configured.
        var candidates = new List<(string Label, ModelConfig Model)>();

        foreach (var agent in config.Agents)
        {
            if (string.IsNullOrWhiteSpace(agent.Model?.ModelId)) continue;
            try { candidates.Add((agent.Name, factory.Resolve(agent.Model))); }
            catch { /* unresolvable — already flagged by static checks */ }
        }


        if (config.Selection.Magentic?.Model is { ModelId.Length: > 0 } magModel)
        {
            try { candidates.Add(("flow.model", factory.Resolve(magModel))); }
            catch (Exception) { /* unresolvable — already flagged by static checks */ }
        }

        if (config.Compaction?.Model is { ModelId.Length: > 0 } compModel)
        {
            try { candidates.Add(("advanced.compaction.model", factory.Resolve(compModel))); }
            catch (Exception) { /* unresolvable — already flagged by static checks */ }
        }

        // Deduplicate by (endpoint, modelId, apiKey) — avoid hitting the same provider twice.
        var groups = new Dictionary<string, (ModelConfig Resolved, List<string> AgentNames)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var (label, resolved) in candidates)
        {
            var apiKey = ResolveApiKey(resolved);
            if (apiKey is null && !IsOllamaModel(resolved.ModelId)) continue;

            var dedupeKey = $"{resolved.Endpoint}||{resolved.ModelId}||{apiKey ?? ""}";
            if (!groups.TryGetValue(dedupeKey, out var group))
                groups[dedupeKey] = (resolved, [label]);
            else
                group.AgentNames.Add(label);
        }

        if (groups.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No connectable models found — API keys missing or models unresolvable.[/]");
            AnsiConsole.WriteLine();
            return;
        }

        foreach (var (_, (resolved, agentNames)) in groups)
        {
            var agents      = string.Join(", ", agentNames);
            var shortHost   = Uri.TryCreate(resolved.Endpoint, UriKind.Absolute, out var u) ? u.Host : resolved.Endpoint;
            var modelEsc    = Markup.Escape(resolved.ModelId);
            var hostEsc     = Markup.Escape(shortHost);
            var agentsEsc   = Markup.Escape(agents);

            try
            {
                var apiKey     = ResolveApiKey(resolved) ?? "";
                var testConfig = resolved with { ApiKey = apiKey, ApiKeyEnvVar = "" };
                var client     = factory.Create(testConfig);

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await client.GetResponseAsync(
                    [new ChatMessage(ChatRole.User, "hi")],
                    new ChatOptions { MaxOutputTokens = 1 },
                    cts.Token);

                AnsiConsole.MarkupLine(
                    $"[green]✓[/] {modelEsc} [dim]({hostEsc})[/] — key valid  [dim]agents: {agentsEsc}[/]");
            }
            catch (OperationCanceledException)
            {
                issues.Add(("error",
                    $"Connectivity timed out for '{resolved.ModelId}' at '{shortHost}' (agents: {agents})."));
                AnsiConsole.MarkupLine(
                    $"[red]✗[/] {modelEsc} [dim]({hostEsc})[/] — timed out after 15 s  [dim]agents: {agentsEsc}[/]");
            }
            catch (Exception ex)
            {
                var diagnosis = DiagnoseConnectivityError(ex);
                issues.Add(("error",
                    $"Connectivity check failed for '{resolved.ModelId}' at '{shortHost}': {diagnosis} (agents: {agents})."));
                AnsiConsole.MarkupLine(
                    $"[red]✗[/] {modelEsc} [dim]({hostEsc})[/] — {Markup.Escape(diagnosis)}  [dim]agents: {agentsEsc}[/]");
            }
        }

        AnsiConsole.WriteLine();
    }

    private static string? ResolveApiKey(ModelConfig resolved)
    {
        if (!string.IsNullOrEmpty(resolved.ApiKey)) return resolved.ApiKey;
        if (!string.IsNullOrEmpty(resolved.ApiKeyEnvVar))
            return Environment.GetEnvironmentVariable(resolved.ApiKeyEnvVar);
        return null;
    }

    private static string DiagnoseConnectivityError(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e.GetType().Name == "ClientResultException")
            {
                var status = e.GetType().GetProperty("Status")?.GetValue(e);
                if (status is int code)
                    return code switch
                    {
                        401 => "invalid API key (HTTP 401)",
                        403 => "access denied (HTTP 403)",
                        404 => "model not found (HTTP 404)",
                        429 => "rate limited — key is valid (HTTP 429)",
                        _   => $"HTTP {code}"
                    };
            }

            var msg = e.Message;
            if (msg.Contains("401",              StringComparison.Ordinal) ||
                msg.Contains("Unauthorized",     StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("invalid api key",  StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("authentication",   StringComparison.OrdinalIgnoreCase))
                return "invalid API key (HTTP 401)";

            if (msg.Contains("connection refused",  StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("no such host",        StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("name resolution",     StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("network unreachable", StringComparison.OrdinalIgnoreCase))
                return "endpoint unreachable — check network / endpoint URL";
        }

        var top = ex.Message;
        return top.Length > 100 ? top[..100] + "…" : top;
    }

    private static void PrintDiagram(OrchestrationConfig config)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]Mermaid diagram[/]");
        AnsiConsole.WriteLine();
        // Use Console.WriteLine directly to prevent Spectre from wrapping long edge lines.
        Console.WriteLine(WorkflowDiagramGenerator.ToMermaid(config));
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]Paste into https://mermaid.live to render.[/]");
    }

    private static void PrintInterpolatedPaths(OrchestrationConfig raw, string? sessionIdOverride)
    {
        var cwd        = Directory.GetCurrentDirectory();
        var slug       = fuseraft.Core.FuseraftPaths.ProjectSlug(cwd);
        var sessionId  = sessionIdOverride ?? "{session_id}";
        var expanded   = OrchestratorConfigLoader.InterpolateSessionId(raw, sessionId, slug);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]Interpolated paths[/]  [dim]project_slug={Markup.Escape(slug)}  session_id={Markup.Escape(sessionId)}[/]");
        AnsiConsole.WriteLine();

        var rows = new List<(string Label, string Template, string Resolved)>();

        void Add(string label, string? template, string? resolved)
        {
            if (template is null && resolved is null) return;
            rows.Add((label, template ?? "", resolved ?? ""));
        }

        // Session / state paths
        if (raw.Events is { } evRaw && expanded.Events is { } evExp)
            Add("events (built-in)", evRaw.Path, evExp.Path);

        if (raw.ChangeTracking is { } ctRaw && expanded.ChangeTracking is { } ctExp)
        {
            Add("change log (built-in)",        ctRaw.Path,                    ctExp.Path);
            Add("intent log (built-in)",        ctRaw.ResolveIntentLogPath(),  ctExp.IntentLogPath);
        }

        if (raw.EvidenceStore is { } esRaw && expanded.EvidenceStore is { } esExp)
            Add("evidence store (built-in)", esRaw.Path, esExp.Path);

        if (raw.Validation is { } vRaw && expanded.Validation is { } vExp)
        {
            Add("brief artifact",            vRaw.BriefPath,      vExp.BriefPath);
            Add("test report artifact",      vRaw.TestReportPath, vExp.TestReportPath);
            Add("change log read by gates",  vRaw.ChangeLogPath,  vExp.ChangeLogPath);
        }

        if (raw.Brownfield is { } bfRaw && expanded.Brownfield is { } bfExp)
        {
            Add("advanced.brownfield.discoveryBriefPath",    bfRaw.DiscoveryBriefPath,    bfExp.DiscoveryBriefPath);
            Add("advanced.brownfield.conventionProfilePath", bfRaw.ConventionProfilePath, bfExp.ConventionProfilePath);
        }

        if (raw.Chatroom is { } chRaw && expanded.Chatroom is { } chExp)
            Add("advanced.chatroom.path", chRaw.Path, chExp.Path);

        // Contracts — only path-bearing predicates
        var rawContracts = raw.Contracts ?? [];
        var expContracts = expanded.Contracts ?? [];
        for (int ci = 0; ci < rawContracts.Count; ci++)
        {
            var cr = rawContracts[ci];
            var ce = expContracts.Count > ci ? expContracts[ci] : cr;
            for (int pi = 0; pi < cr.Requires.Count; pi++)
            {
                var pr = cr.Requires[pi];
                var pe = ce.Requires.Count > pi ? ce.Requires[pi] : pr;
                var pfx = $"gate '{cr.Name}' [{pi}]";
                if (pr.Path        is not null) Add($"{pfx} path",           pr.Path,          pe.Path);
                if (pr.Source      is not null) Add($"{pfx} source",         pr.Source,        pe.Source);
                if (pr.PatternSource is not null) Add($"{pfx} command source", pr.PatternSource, pe.PatternSource);
            }
        }

        if (rows.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No path-bearing fields found in this config.[/]");
            AnsiConsole.WriteLine();
            return;
        }

        foreach (var (label, _, resolved) in rows)
        {
            Console.WriteLine(label);
            Console.WriteLine($"  {resolved}");
            Console.WriteLine();
        }

        AnsiConsole.WriteLine();
    }

    private static void PrintIssues(List<(string Level, string Message)> issues)
    {
        if (issues.Count == 0) return;

        foreach (var (level, msg) in issues)
        {
            var (icon, color) = level == "error" ? ("✗", "red") : ("⚠", "yellow");
            AnsiConsole.MarkupLine($"[{color}]{icon}[/] {Markup.Escape(msg)}");
        }

        AnsiConsole.WriteLine();
    }
}
