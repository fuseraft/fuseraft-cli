using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Cli.Diagram;
using fuseraft.Core.Models;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.Plugins;

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

    [CommandOption("--check-connectivity|-c")]
    [Description("Make a minimal test call to each unique provider endpoint to verify the API key is valid and the endpoint is reachable. Incurs a small API cost (~1 token per unique endpoint).")]
    public bool CheckConnectivity { get; set; }
}

/// <summary>
/// Validates an orchestration config file and reports all issues found.
/// </summary>
public sealed class ValidateConfigCommand(PluginRegistry pluginRegistry) : AsyncCommand<ValidateConfigSettings>
{

    internal Task<int> ExecuteAsync(CommandContext context, ValidateConfigSettings settings) =>
        ExecuteAsync(context, settings, CancellationToken.None);

    protected override async Task<int> ExecuteAsync(CommandContext context, ValidateConfigSettings settings, CancellationToken cancellationToken)
    {
        var issues = new List<(string Level, string Message)>();

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
            config = OrchestratorBuilder.LoadConfig(settings.Path);
        }
        catch (Exception ex)
        {
            issues.Add(("error", $"Config binding failed: {ex.Message}"));
            PrintIssues(issues);
            return 1;
        }

        // Semantic checks
        if (string.IsNullOrWhiteSpace(config.Name))
            issues.Add(("warning", "Orchestration.Name is empty."));

        if (!string.IsNullOrWhiteSpace(config.SystemPromptPath))
        {
            var configDir  = Path.GetDirectoryName(Path.GetFullPath(settings.Path)) ?? ".";
            var promptPath = Path.IsPathRooted(config.SystemPromptPath)
                ? config.SystemPromptPath
                : Path.GetFullPath(config.SystemPromptPath, configDir);
            if (!File.Exists(promptPath))
                issues.Add(("error", $"SystemPromptPath file not found: {promptPath}"));
        }

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
                    issues.Add(("warning", $"Agent '{agent.Name}' has no Instructions."));

                // Remote A2A agent: validate URL; skip local model checks.
                if (agent.RemoteAgent is not null)
                {
                    if (string.IsNullOrWhiteSpace(agent.RemoteAgent.Url))
                        issues.Add(("error", $"Agent '{agent.Name}': RemoteAgent.Url is required when RemoteAgent is set."));
                    else if (!Uri.TryCreate(agent.RemoteAgent.Url, UriKind.Absolute, out _))
                        issues.Add(("error", $"Agent '{agent.Name}': RemoteAgent.Url '{agent.RemoteAgent.Url}' is not a valid absolute URL."));

                    if (!string.IsNullOrWhiteSpace(agent.Model.ModelId))
                        issues.Add(("warning", $"Agent '{agent.Name}': Model.ModelId is ignored when RemoteAgent is set."));

                    if (agent.Plugins.Count > 0)
                        issues.Add(("warning", $"Agent '{agent.Name}': Plugins are ignored when RemoteAgent is set."));
                }
                else
                {
                if (string.IsNullOrWhiteSpace(agent.Model.ModelId))
                    issues.Add(("error", $"Agent '{agent.Name}': ModelId is empty."));

                // Resolve the model against the Models registry before checking connection
                // fields — an alias fills in Endpoint and ApiKeyEnvVar at runtime.
                var resolved = ResolveModelAlias(agent.Model, config.Models);

                if (string.IsNullOrWhiteSpace(resolved.Endpoint)
                    && !HasKnownProviderPrefix(resolved.ModelId)
                    && !IsOllamaModel(resolved.ModelId))
                    issues.Add(("error", $"Agent '{agent.Name}': Model.Endpoint is empty and '{resolved.ModelId}' does not match a known provider prefix. Set Endpoint explicitly or use a model ID that starts with a recognised prefix (grok-, gpt-, claude-, gemini-, etc.)."));

                if (string.IsNullOrWhiteSpace(resolved.ApiKeyEnvVar)
                    && !IsOllamaModel(resolved.ModelId))
                    issues.Add(("warning", $"Agent '{agent.Name}': No ApiKeyEnvVar set."));
                else if (!string.IsNullOrWhiteSpace(resolved.ApiKeyEnvVar)
                    && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(resolved.ApiKeyEnvVar)))
                    issues.Add(("warning", $"Agent '{agent.Name}': Env var '{resolved.ApiKeyEnvVar}' is not set in this shell."));
                }

                if (agent.FunctionChoice.ToLowerInvariant() is not ("auto" or "required" or "none"))
                    issues.Add(("error", $"Agent '{agent.Name}': FunctionChoice '{agent.FunctionChoice}' is invalid. Valid values: auto, required, none."));

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

        // Selection strategy
        var selType = config.Selection.Type.ToLowerInvariant();
        if (selType is not ("sequential" or "roundrobin" or "llm" or "keyword" or "structured" or "magentic" or "statemachine"))
            issues.Add(("error", $"Unknown selection type: '{config.Selection.Type}'."));

        if (selType == "llm" && config.Selection.Model is null)
            issues.Add(("error", "LLM selection requires Selection.Model to be set."));

        if (selType == "keyword" && (config.Selection.Routes is null || config.Selection.Routes.Count == 0))
            issues.Add(("error", "Keyword selection requires at least one entry in Routes."));

        if (selType == "structured")
            ValidateStructuredRoutes(config, issues);

        if (selType == "magentic")
            ValidateMagenticSelection(config, issues);

        if (selType == "keyword" && config.Selection.Routes is { Count: > 1 })
        {
            // Detect routes that share the same keyword and SourceAgents but have different
            // validators. Because selection uses first-match-wins, the second route's validator
            // is permanently unreachable — this is almost always a misconfiguration. The intent
            // is usually AND semantics (both validators must pass), which requires a single route
            // with a Validators[] array instead of two separate routes.
            //
            // Exception: routes that carry a Condition are disambiguated at runtime by the JSON
            // value of the condition field — they are intentionally parallel branches of the same
            // keyword and must not be flagged as unreachable.
            var routeSignatures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int ri = 0; ri < config.Selection.Routes.Count; ri++)
            {
                var r = config.Selection.Routes[ri];
                var sourceKey = r.SourceAgents is { Count: > 0 }
                    ? string.Join(",", r.SourceAgents.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
                    : "*";

                // Include the condition in the signature so condition-differentiated routes
                // on the same keyword do not trigger the unreachable-route warning.
                var condKey = r.Condition is { } c
                    ? $"|cond:{c.Field}:{c.Is}{c.IsNot}{c.Contains}{c.Exists}"
                    : string.Empty;

                var sig = $"{r.Keyword}::{sourceKey}{condKey}";

                if (routeSignatures.TryGetValue(sig, out var firstIndex))
                    issues.Add(("warning",
                        $"Routes[{firstIndex}] and Routes[{ri}] share keyword '{r.Keyword}' " +
                        $"and SourceAgents '{sourceKey}'. The second route's validator is " +
                        $"unreachable (first-match wins). To require both validators, merge them " +
                        $"into a single route using a \"Validators\": [] array."));
                else
                    routeSignatures[sig] = ri;
            }
        }

        // Termination strategy — only validate when the section was explicitly configured.
        if (config.Termination is not null)
            ValidateTermination(config.Termination, config.Agents, issues);

        // Telemetry
        if (config.Telemetry is { OtlpEndpoint: { } endpoint })
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out _))
                issues.Add(("error", $"Telemetry.OtlpEndpoint is not a valid URI: '{endpoint}'."));
        }

        // Report static issues, then optionally run live connectivity checks.
        PrintIssues(issues);

        if (settings.CheckConnectivity)
            await CheckConnectivityAsync(config, issues);

        var errorCount = issues.Count(x => x.Level == "error");
        var warnCount  = issues.Count(x => x.Level == "warning");

        if (errorCount == 0)
        {
            AnsiConsole.MarkupLine($"[green]✓ Valid[/]" +
                (warnCount > 0 ? $"  [yellow]{warnCount} warning(s)[/]" : string.Empty));

            if (settings.Diagram)
                PrintDiagram(config);

            return 0;
        }

        AnsiConsole.MarkupLine($"[red]✗ {errorCount} error(s)[/]  [yellow]{warnCount} warning(s)[/]");

        if (settings.Diagram)
            PrintDiagram(config);

        return 1;
    }

    /// <summary>
    /// Applies the Models registry alias lookup to a model config, mirroring the
    /// first step of <see cref="fuseraft.Infrastructure.ChatClientFactory.Resolve"/>.
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
                Temperature = model.Temperature ?? alias.Temperature,
                MaxTokens   = model.MaxTokens > 0 ? model.MaxTokens : alias.MaxTokens
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

    private static void ValidateTermination(
        TerminationStrategyConfig t,
        List<AgentConfig> agents,
        List<(string, string)> issues,
        int depth = 0)
    {
        var prefix = depth > 0 ? "  Nested termination: " : "Termination: ";
        var type = t.Type.ToLowerInvariant();

        if (type is not ("regex" or "maxiterations" or "composite"))
            issues.Add(("error", $"{prefix}Unknown type '{t.Type}'."));

        if (type == "regex" && string.IsNullOrWhiteSpace(t.Pattern))
            issues.Add(("error", $"{prefix}Regex strategy requires a Pattern."));

        // MaxIterations is only a meaningful cap at the top-level or for maxiterations-type
        // strategies; nested child strategies within a composite rely on the outer cap.
        if (t.MaxIterations <= 0 && (depth == 0 || type == "maxiterations"))
            issues.Add(("warning", $"{prefix}MaxIterations should be > 0 (got {t.MaxIterations})."));

        if (t.AgentNames is { Length: > 0 })
        {
            var agentNames = agents.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var name in t.AgentNames)
                if (!agentNames.Contains(name))
                    issues.Add(("warning", $"{prefix}AgentName '{name}' doesn't match any defined agent."));
        }

        if (type == "composite")
        {
            if (t.Strategies is not { Count: > 0 })
                issues.Add(("error", $"{prefix}Composite strategy requires at least one child strategy."));
            else
                foreach (var child in t.Strategies)
                    ValidateTermination(child, agents, issues, depth + 1);
        }
    }

    private static void ValidateMagenticSelection(
        OrchestrationConfig config,
        List<(string Level, string Message)> issues)
    {
        var mag = config.Selection.Magentic;

        if (mag is null || mag.Model is null)
        {
            issues.Add(("error",
                "Selection.Type 'magentic' requires a 'Selection.Magentic.Model' configuration block."));
            return;
        }

        var resolved = ResolveModelAlias(mag.Model, config.Models);

        if (string.IsNullOrWhiteSpace(resolved.Endpoint)
            && !HasKnownProviderPrefix(resolved.ModelId)
            && !IsOllamaModel(resolved.ModelId))
            issues.Add(("error",
                $"Selection.Magentic.Model: Endpoint is empty and '{resolved.ModelId}' does not match " +
                "a known provider prefix. Set Endpoint explicitly or use a model ID that starts with a " +
                "recognised prefix (grok-, gpt-, claude-, gemini-, etc.)."));

        if (string.IsNullOrWhiteSpace(resolved.ApiKeyEnvVar) && !IsOllamaModel(resolved.ModelId))
            issues.Add(("warning", "Selection.Magentic.Model: No ApiKeyEnvVar set."));
        else if (!string.IsNullOrWhiteSpace(resolved.ApiKeyEnvVar)
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(resolved.ApiKeyEnvVar)))
            issues.Add(("warning",
                $"Selection.Magentic.Model: Env var '{resolved.ApiKeyEnvVar}' is not set in this shell."));

        if (mag.MaxRoundCount < 1)
            issues.Add(("error",
                $"Selection.Magentic.MaxRoundCount must be at least 1 (got {mag.MaxRoundCount}). " +
                "A value of 0 would exit immediately without invoking any participant agents."));

        if (mag.MaxStallCount < 1)
            issues.Add(("error",
                $"Selection.Magentic.MaxStallCount must be at least 1 (got {mag.MaxStallCount}). " +
                "A value of 0 would trigger a replan on every single round."));

        if (mag.MaxResetCount < 0)
            issues.Add(("error",
                $"Selection.Magentic.MaxResetCount must be >= 0 (got {mag.MaxResetCount}). " +
                "Use 0 to disable replanning entirely."));

        // Warn when a Termination section is configured — it is silently ignored for Magentic.
        var t = config.Termination;
        bool hasNonDefaultTermination = t is not null && (
            !t.Type.Equals("composite", StringComparison.OrdinalIgnoreCase) ||
            t.Pattern    is not null ||
            t.Strategies is { Count: > 0 });
        if (hasNonDefaultTermination)
            issues.Add(("warning",
                "The 'Termination' section is ignored for Selection.Type 'magentic'. " +
                "Session termination is controlled by MaxRoundCount, MaxStallCount, and MaxResetCount " +
                "in the 'Selection.Magentic' block."));
    }

    private static void ValidateStructuredRoutes(
        OrchestrationConfig config,
        List<(string Level, string Message)> issues)
    {
        if (config.Selection.StructuredRoutes is not { Count: > 0 })
        {
            issues.Add(("error", "Structured selection requires at least one entry in StructuredRoutes."));
            return;
        }

        var agentNames = config.Agents.Select(a => a.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < config.Selection.StructuredRoutes.Count; i++)
        {
            var route = config.Selection.StructuredRoutes[i];
            var prefix = $"StructuredRoutes[{i}]";

            if (string.IsNullOrWhiteSpace(route.Agent))
                issues.Add(("error", $"{prefix}: Agent is empty."));
            else if (!agentNames.Contains(route.Agent))
                issues.Add(("error", $"{prefix}: Agent '{route.Agent}' is not defined in Agents."));

            if (route.SourceAgents is { Count: > 0 })
                foreach (var src in route.SourceAgents)
                    if (!agentNames.Contains(src))
                        issues.Add(("warning", $"{prefix}: SourceAgent '{src}' is not defined in Agents."));

            // Condition validation
            if (string.IsNullOrWhiteSpace(route.Condition.Field))
            {
                issues.Add(("error", $"{prefix}: Condition.Field is required."));
                continue;
            }

            int operatorCount =
                (route.Condition.Is    is not null ? 1 : 0) +
                (route.Condition.IsNot is not null ? 1 : 0) +
                (route.Condition.Contains  is not null ? 1 : 0) +
                (route.Condition.Exists    is not null ? 1 : 0);

            if (operatorCount == 0)
                issues.Add(("error",
                    $"{prefix}: Condition must specify at least one operator " +
                    "(Is, IsNot, Contains, or Exists)."));
            else if (operatorCount > 1)
                issues.Add(("warning",
                    $"{prefix}: Condition specifies {operatorCount} operators — only the first one evaluated " +
                    "will apply (Equals → NotEquals → Contains → Exists)."));
        }
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

        if (config.Selection.Model is { ModelId.Length: > 0 } selModel)
        {
            try { candidates.Add(("Selection.Model", factory.Resolve(selModel))); }
            catch (Exception) { /* unresolvable — already flagged by static checks */ }
        }

        if (config.Selection.Magentic?.Model is { ModelId.Length: > 0 } magModel)
        {
            try { candidates.Add(("Selection.Magentic", factory.Resolve(magModel))); }
            catch (Exception) { /* unresolvable — already flagged by static checks */ }
        }

        if (config.Compaction?.Model is { ModelId.Length: > 0 } compModel)
        {
            try { candidates.Add(("Compaction", factory.Resolve(compModel))); }
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
