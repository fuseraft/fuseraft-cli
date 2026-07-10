using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Cli.Diagram;
using fuseraft.Core.Models;
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

        ValidateAgents(config, settings, issues);

        // Selection strategy
        var selType = config.Selection.Type.ToLowerInvariant();
        if (selType is not (OrchestratorTypes.Sequential or OrchestratorTypes.RoundRobin or OrchestratorTypes.Llm or OrchestratorTypes.Keyword or OrchestratorTypes.Structured or OrchestratorTypes.Magentic or OrchestratorTypes.StateMachine or OrchestratorTypes.Graph or OrchestratorTypes.Workflow or OrchestratorTypes.Adversarial or OrchestratorTypes.MapReduce or OrchestratorTypes.ScatterGather))
            issues.Add(("error", $"Unknown selection type: '{config.Selection.Type}'."));

        if (selType == OrchestratorTypes.Llm && config.Selection.Model is null)
            issues.Add(("error", "LLM selection requires Selection.Model to be set."));

        if (selType == OrchestratorTypes.Keyword && (config.Selection.Routes is null || config.Selection.Routes.Count == 0))
            issues.Add(("error", "Keyword selection requires at least one entry in Routes."));

        if (selType == OrchestratorTypes.Structured)
            ValidateStructuredRoutes(config, issues);

        if (selType == OrchestratorTypes.Magentic)
            ValidateMagenticSelection(config, issues);

        if (selType == OrchestratorTypes.Graph)
            ValidateGraph(config, issues);

        if (selType == OrchestratorTypes.Workflow)
        {
            ValidateGraph(config, issues);
            ValidateWorkflowRestrictions(config, issues);
        }

        if (selType == OrchestratorTypes.MapReduce)
            ValidateMapReduce(config, issues);

        if (selType == OrchestratorTypes.ScatterGather)
            ValidateScatterGather(config, issues);

        if (selType == OrchestratorTypes.StateMachine)
            ValidateStateMachine(config, issues);

        if (selType == OrchestratorTypes.Adversarial)
            ValidateAdversarialSelection(config, issues);

        if (selType == OrchestratorTypes.Keyword && config.Selection.Routes is { Count: > 1 })
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

        ValidateCompactionConfig(config, issues);

        ValidateMemoryLayer(config, issues);

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

                if (agent.TrustScore is < 0.0 or > 1.0)
                    issues.Add(("error", $"Agent '{agent.Name}': TrustScore must be 0.0–1.0 (got {agent.TrustScore})."));

                if (agent.ContextWindow?.ContextCapFraction is < 0.0 or > 1.0)
                    issues.Add(("error", $"Agent '{agent.Name}': ContextCapFraction must be 0.0–1.0 (got {agent.ContextWindow.ContextCapFraction})."));

                var effort = agent.Model.ReasoningEffort?.ToLowerInvariant();
                if (effort is not null and not ("none" or "low" or "medium" or "high"))
                    issues.Add(("error", $"Agent '{agent.Name}': Model.ReasoningEffort '{agent.Model.ReasoningEffort}' is invalid. Valid values: none, low, medium, high."));

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
                    "ContextBudget.CutoverAt and ContextBudget.MaxSingleTurnInputTokens require " +
                    "a Compaction section. Add Compaction to enable automatic context trimming."));

            if (cb.WarnAt > 0 && cb.CutoverAt > 0 && cb.WarnAt >= cb.CutoverAt)
                issues.Add(("error",
                    $"ContextBudget.WarnAt ({cb.WarnAt:N0}) must be less than CutoverAt ({cb.CutoverAt:N0})."));

            if (config.WarnTurnTokens > 0 && cb.CutoverAt > 0 && config.WarnTurnTokens >= cb.CutoverAt)
                issues.Add(("warning",
                    $"WarnTurnTokens ({config.WarnTurnTokens:N0}) is >= ContextBudget.CutoverAt ({cb.CutoverAt:N0}). " +
                    "The per-turn warning fires in the same turn as compaction — lower WarnTurnTokens " +
                    "below CutoverAt to get an advance signal."));
        }

        if (config.Compaction?.AntiThrashMinSavingsRatio is < 0.0 or > 1.0)
            issues.Add(("error", $"Compaction.AntiThrashMinSavingsRatio must be 0.0–1.0 (got {config.Compaction.AntiThrashMinSavingsRatio})."));
    }

    private static void ValidateMemoryLayer(
        OrchestrationConfig config,
        List<(string Level, string Message)> issues)
    {
        // Telemetry
        if (config.Telemetry is { OtlpEndpoint: { } endpoint })
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out _))
                issues.Add(("error", $"Telemetry.OtlpEndpoint is not a valid URI: '{endpoint}'."));
        }
    }

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

        // MaxIterations: warn when explicitly using the maxiterations type with no cap,
        // or at depth 0 for non-composite strategies (composite delegates capping to children).
        if (t.MaxIterations <= 0 && (type == "maxiterations" || (depth == 0 && type != "composite")))
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

    private static void ValidateGraph(
        OrchestrationConfig config,
        List<(string Level, string Message)> issues)
    {
        var graph = config.Selection.Graph;
        if (graph is null)
        {
            issues.Add(("error", "Graph selection requires a 'Selection.Graph' configuration block."));
            return;
        }

        if (graph.Nodes.Count == 0)
        {
            issues.Add(("error", "Selection.Graph: at least one node is required."));
            return;
        }

        var agentNames = config.Agents.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Node IDs must be unique and reference valid agents. Mirrors
        // OrchestratorBuilder.ValidateAndSelectStrategy's per-node checks, including the
        // SubGraphId branch — without it, a valid sub-graph node (Agent left empty,
        // SubGraphId set) was reported as a false "Agent is required" error.
        var nodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < graph.Nodes.Count; i++)
        {
            var node   = graph.Nodes[i];
            var prefix = $"Selection.Graph.Nodes[{i}]";

            if (string.IsNullOrWhiteSpace(node.Id))
                issues.Add(("error", $"{prefix}: Id is required."));
            else if (!nodeIds.Add(node.Id))
                issues.Add(("error", $"{prefix}: Duplicate node Id '{node.Id}'."));

            bool isSubGraphNode = !string.IsNullOrWhiteSpace(node.SubGraphId);

            if (isSubGraphNode)
            {
                if (!string.IsNullOrWhiteSpace(node.Agent))
                {
                    issues.Add(("error", $"{prefix} (id='{node.Id}'): has both 'Agent' and 'SubGraphId' set. " +
                        "Use one or the other — leave 'Agent' empty when using 'SubGraphId'."));
                }

                if (graph.SubGraphs is null || !graph.SubGraphs.TryGetValue(node.SubGraphId!, out var subSpec))
                {
                    issues.Add(("error", $"{prefix} (id='{node.Id}'): references SubGraphId '{node.SubGraphId}' " +
                        "which is not defined in 'Selection.Graph.SubGraphs'."));
                }
                else if (!subSpec.IsValid)
                {
                    issues.Add(("error", $"SubGraph '{node.SubGraphId}' must set exactly one of 'Graph', 'MapReduce', or 'ScatterGather'."));
                }
                else if (subSpec.IsMapReduce)
                {
                    var mr = subSpec.MapReduce!;
                    if (string.IsNullOrWhiteSpace(mr.Splitter) || !agentNames.Contains(mr.Splitter))
                        issues.Add(("error", $"SubGraph '{node.SubGraphId}' MapReduce.Splitter '{mr.Splitter}' is not defined in Agents."));
                    if (string.IsNullOrWhiteSpace(mr.Mapper) || !agentNames.Contains(mr.Mapper))
                        issues.Add(("error", $"SubGraph '{node.SubGraphId}' MapReduce.Mapper '{mr.Mapper}' is not defined in Agents."));
                    if (string.IsNullOrWhiteSpace(mr.Reducer) || !agentNames.Contains(mr.Reducer))
                        issues.Add(("error", $"SubGraph '{node.SubGraphId}' MapReduce.Reducer '{mr.Reducer}' is not defined in Agents."));
                    if (mr.MaxConcurrency < 0)
                        issues.Add(("error", $"SubGraph '{node.SubGraphId}' MapReduce.MaxConcurrency must be >= 0 (got {mr.MaxConcurrency})."));
                    if (mr.MaxSplitterRetries < 1)
                        issues.Add(("error", $"SubGraph '{node.SubGraphId}' MapReduce.MaxSplitterRetries must be at least 1 (got {mr.MaxSplitterRetries})."));
                    if (string.IsNullOrWhiteSpace(mr.ItemsJsonPath))
                        issues.Add(("error", $"SubGraph '{node.SubGraphId}' MapReduce.ItemsJsonPath must be a non-empty string."));
                }
                else if (subSpec.IsScatterGather)
                {
                    var sg = subSpec.ScatterGather!;
                    if (sg.Participants.Count == 0)
                        issues.Add(("error", $"SubGraph '{node.SubGraphId}' ScatterGather.Participants must contain at least one agent name."));
                    foreach (var p in sg.Participants)
                        if (string.IsNullOrWhiteSpace(p) || !agentNames.Contains(p))
                            issues.Add(("error", $"SubGraph '{node.SubGraphId}' ScatterGather.Participants contains '{p}' which is not defined in Agents."));
                    if (string.IsNullOrWhiteSpace(sg.Synthesizer) || !agentNames.Contains(sg.Synthesizer))
                        issues.Add(("error", $"SubGraph '{node.SubGraphId}' ScatterGather.Synthesizer '{sg.Synthesizer}' is not defined in Agents."));
                    if (sg.MaxConcurrency < 0)
                        issues.Add(("error", $"SubGraph '{node.SubGraphId}' ScatterGather.MaxConcurrency must be >= 0 (got {sg.MaxConcurrency})."));
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(node.Agent))
                    issues.Add(("error", $"{prefix} (id='{node.Id}'): Agent is required."));
                else if (!agentNames.Contains(node.Agent))
                    issues.Add(("error", $"{prefix} (id='{node.Id}'): Agent '{node.Agent}' is not defined in Agents."));
            }
        }

        // EntryNode must resolve to a declared node.
        if (graph.EntryNode is { Length: > 0 } entry && !nodeIds.Contains(entry))
            issues.Add(("error", $"Selection.Graph.EntryNode '{entry}' does not match any declared node Id."));

        // MaxRetries must be positive.
        if (graph.MaxRetries <= 0)
            issues.Add(("warning", $"Selection.Graph.MaxRetries should be > 0 (got {graph.MaxRetries})."));

        // Edges must reference valid node IDs.
        for (int i = 0; i < graph.Edges.Count; i++)
        {
            var edge   = graph.Edges[i];
            var prefix = $"Selection.Graph.Edges[{i}]";

            if (string.IsNullOrWhiteSpace(edge.From))
                issues.Add(("error", $"{prefix}: From is required."));
            else if (!nodeIds.Contains(edge.From))
                issues.Add(("error", $"{prefix}: From '{edge.From}' does not match any declared node Id."));

            if (string.IsNullOrWhiteSpace(edge.To))
                issues.Add(("error", $"{prefix}: To is required."));
            else if (!nodeIds.Contains(edge.To))
                issues.Add(("error", $"{prefix}: To '{edge.To}' does not match any declared node Id."));

            if (edge.SourceAgents is { Count: > 0 })
                foreach (var src in edge.SourceAgents)
                    if (!agentNames.Contains(src))
                        issues.Add(("warning", $"{prefix}: SourceAgent '{src}' is not defined in Agents."));

            if (!string.IsNullOrWhiteSpace(edge.RecoveryAgent) && !agentNames.Contains(edge.RecoveryAgent))
                issues.Add(("warning", $"{prefix}: RecoveryAgent '{edge.RecoveryAgent}' is not defined in Agents."));
        }
    }

    // Mirrors OrchestratorBuilder.ValidateAndSelectStrategy's Selection.MapReduce checks.
    private static void ValidateMapReduce(
        OrchestrationConfig config,
        List<(string Level, string Message)> issues)
    {
        var mr = config.Selection.MapReduce;
        if (mr is null)
        {
            issues.Add(("error", "Selection.Type 'mapreduce' requires a 'Selection.MapReduce' configuration block."));
            return;
        }

        var agentNames = config.Agents.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(mr.Splitter))
            issues.Add(("error", "Selection.MapReduce.Splitter must be a non-empty agent name."));
        else if (!agentNames.Contains(mr.Splitter))
            issues.Add(("error", $"Selection.MapReduce.Splitter '{mr.Splitter}' is not defined in 'Orchestration.Agents'."));

        if (string.IsNullOrWhiteSpace(mr.Mapper))
            issues.Add(("error", "Selection.MapReduce.Mapper must be a non-empty agent name."));
        else if (!agentNames.Contains(mr.Mapper))
            issues.Add(("error", $"Selection.MapReduce.Mapper '{mr.Mapper}' is not defined in 'Orchestration.Agents'."));

        if (string.IsNullOrWhiteSpace(mr.Reducer))
            issues.Add(("error", "Selection.MapReduce.Reducer must be a non-empty agent name."));
        else if (!agentNames.Contains(mr.Reducer))
            issues.Add(("error", $"Selection.MapReduce.Reducer '{mr.Reducer}' is not defined in 'Orchestration.Agents'."));

        if (mr.MaxConcurrency < 0)
            issues.Add(("error", $"Selection.MapReduce.MaxConcurrency must be >= 0 (got {mr.MaxConcurrency}). Use 0 for unlimited."));

        if (mr.MaxSplitterRetries < 1)
            issues.Add(("error", $"Selection.MapReduce.MaxSplitterRetries must be at least 1 (got {mr.MaxSplitterRetries})."));

        if (string.IsNullOrWhiteSpace(mr.ItemsJsonPath))
            issues.Add(("error", "Selection.MapReduce.ItemsJsonPath must be a non-empty string."));
    }

    // Mirrors OrchestratorBuilder.ValidateAndSelectStrategy's Selection.ScatterGather checks.
    private static void ValidateScatterGather(
        OrchestrationConfig config,
        List<(string Level, string Message)> issues)
    {
        var sg = config.Selection.ScatterGather;
        if (sg is null)
        {
            issues.Add(("error", "Selection.Type 'scattergather' requires a 'Selection.ScatterGather' configuration block."));
            return;
        }

        var agentNames = config.Agents.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (sg.Participants.Count == 0)
            issues.Add(("error", "Selection.ScatterGather.Participants must contain at least one agent name."));

        foreach (var p in sg.Participants)
            if (string.IsNullOrWhiteSpace(p) || !agentNames.Contains(p))
                issues.Add(("error", $"Selection.ScatterGather.Participants contains '{p}' which is not defined in 'Orchestration.Agents'."));

        if (string.IsNullOrWhiteSpace(sg.Synthesizer))
            issues.Add(("error", "Selection.ScatterGather.Synthesizer must be a non-empty agent name."));
        else if (!agentNames.Contains(sg.Synthesizer))
            issues.Add(("error", $"Selection.ScatterGather.Synthesizer '{sg.Synthesizer}' is not defined in 'Orchestration.Agents'."));

        if (sg.MaxConcurrency < 0)
            issues.Add(("error", $"Selection.ScatterGather.MaxConcurrency must be >= 0 (got {sg.MaxConcurrency}). Use 0 for unlimited."));
    }

    // Selection.Type 'workflow' reuses the same Selection.Graph block as 'graph' (checked by
    // ValidateGraph above) but is a v1 implementation that rejects Parallel, SubGraphId,
    // RequireHumanApproval, RecoveryAgent, and no-keyword edges, and requires every node's
    // agent to have the Handoff plugin (routing is tool-call-only, no text-keyword fallback).
    // Mirrors the same checks OrchestratorBuilder.ValidateAndSelectStrategy enforces at run
    // time, so 'fuseraft validate' surfaces them without needing to actually run a session.
    private static void ValidateWorkflowRestrictions(
        OrchestrationConfig config,
        List<(string Level, string Message)> issues)
    {
        var graph = config.Selection.Graph;
        if (graph is null) return;

        var agentByName = config.Agents.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < graph.Nodes.Count; i++)
        {
            var node   = graph.Nodes[i];
            var prefix = $"Selection.Graph.Nodes[{i}] (id='{node.Id}')";

            if (!string.IsNullOrWhiteSpace(node.SubGraphId))
                issues.Add(("error", $"{prefix}: 'SubGraphId' is not supported under Selection.Type 'workflow'. Use 'graph' instead."));

            if (node.Parallel)
                issues.Add(("error", $"{prefix}: 'Parallel: true' is not supported under Selection.Type 'workflow'. Use 'graph' instead."));

            if (!string.IsNullOrWhiteSpace(node.Agent) && agentByName.TryGetValue(node.Agent, out var agentCfg)
                && !agentCfg.Plugins.Contains(HandoffPlugin.PluginName, StringComparer.OrdinalIgnoreCase))
                issues.Add(("error", $"{prefix}: agent '{node.Agent}' must have '{HandoffPlugin.PluginName}' in Plugins — 'workflow' routes exclusively via handoff(route_keyword: ...) tool calls."));
        }

        for (int i = 0; i < graph.Edges.Count; i++)
        {
            var edge   = graph.Edges[i];
            var prefix = $"Selection.Graph.Edges[{i}] (From='{edge.From}' To='{edge.To}')";

            if (string.IsNullOrEmpty(edge.Keyword))
                issues.Add(("error", $"{prefix}: 'Keyword' is required under Selection.Type 'workflow' — unconditional edges are not supported. Use 'graph' instead."));

            if (edge.RequireHumanApproval)
                issues.Add(("error", $"{prefix}: 'RequireHumanApproval' is not supported under Selection.Type 'workflow'. Use 'graph' instead."));

            if (edge.RecoveryAgent is not null)
                issues.Add(("error", $"{prefix}: 'RecoveryAgent' is not supported under Selection.Type 'workflow'. Use 'graph' instead."));
        }
    }

    private static void ValidateAdversarialSelection(
        OrchestrationConfig config,
        List<(string Level, string Message)> issues)
    {
        var adv = config.Selection.Adversarial;

        if (adv is null)
        {
            issues.Add(("error",
                "Selection.Type 'adversarial' requires a 'Selection.Adversarial' configuration block."));
            return;
        }

        if (adv.Stages.Count == 0)
        {
            issues.Add(("error", "Selection.Adversarial.Stages must contain at least one stage."));
            return;
        }

        if (adv.Rounds < 1)
            issues.Add(("error",
                $"Selection.Adversarial.Rounds must be at least 1 (got {adv.Rounds})."));

        if (string.IsNullOrWhiteSpace(adv.PassKeyword))
            issues.Add(("error", "Selection.Adversarial.PassKeyword must be a non-empty string."));

        var agentNames = config.Agents.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int si = 0; si < adv.Stages.Count; si++)
        {
            var stage  = adv.Stages[si];
            var prefix = $"Selection.Adversarial.Stages[{si}]";

            if (string.IsNullOrWhiteSpace(stage.Generator))
                issues.Add(("error", $"{prefix}: Generator is required."));
            else if (!agentNames.Contains(stage.Generator))
                issues.Add(("error", $"{prefix}.Generator '{stage.Generator}' is not defined in Orchestration.Agents."));

            if (string.IsNullOrWhiteSpace(stage.Critic))
                issues.Add(("error", $"{prefix}: Critic is required."));
            else if (!agentNames.Contains(stage.Critic))
                issues.Add(("error", $"{prefix}.Critic '{stage.Critic}' is not defined in Orchestration.Agents."));

            if (!string.IsNullOrWhiteSpace(stage.Generator) && !string.IsNullOrWhiteSpace(stage.Critic) &&
                string.Equals(stage.Generator, stage.Critic, StringComparison.OrdinalIgnoreCase))
                issues.Add(("warning",
                    $"{prefix}: Generator and Critic are the same agent ('{stage.Generator}'). " +
                    "Self-critique defeats the context firewall — use two distinct agents."));
        }
    }

    private static void ValidateStateMachine(
        OrchestrationConfig config,
        List<(string Level, string Message)> issues)
    {
        var sm = config.Selection.StateMachine;
        if (sm is null)
        {
            issues.Add(("error", "StateMachine selection requires a 'Selection.StateMachine' configuration block."));
            return;
        }

        if (sm.States.Count == 0)
        {
            issues.Add(("error", "Selection.StateMachine: at least one state is required."));
            return;
        }

        var agentNames = config.Agents.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stateNames = sm.States.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Initial state must exist.
        if (string.IsNullOrWhiteSpace(sm.Initial))
            issues.Add(("error", "Selection.StateMachine.Initial is required."));
        else if (!stateNames.Contains(sm.Initial))
            issues.Add(("error", $"Selection.StateMachine.Initial '{sm.Initial}' does not match any declared state."));

        foreach (var (name, state) in sm.States)
        {
            var prefix = $"Selection.StateMachine.States['{name}']";

            if (string.IsNullOrWhiteSpace(state.Agent))
                issues.Add(("error", $"{prefix}: Agent is required."));
            else if (!agentNames.Contains(state.Agent))
                issues.Add(("error", $"{prefix}: Agent '{state.Agent}' is not defined in Agents."));

            for (int ti = 0; ti < state.Transitions.Count; ti++)
            {
                var t      = state.Transitions[ti];
                var tpfx   = $"{prefix}.Transitions[{ti}]";

                if (string.IsNullOrWhiteSpace(t.To))
                    issues.Add(("error", $"{tpfx}: To is required."));
                else if (!stateNames.Contains(t.To))
                    issues.Add(("error", $"{tpfx}: To '{t.To}' does not match any declared state."));

                if (t.SourceAgents is { Count: > 0 })
                    foreach (var src in t.SourceAgents)
                        if (!agentNames.Contains(src))
                            issues.Add(("warning", $"{tpfx}: SourceAgent '{src}' is not defined in Agents."));

                if (!string.IsNullOrWhiteSpace(t.RecoveryAgent) && !agentNames.Contains(t.RecoveryAgent))
                    issues.Add(("warning", $"{tpfx}: RecoveryAgent '{t.RecoveryAgent}' is not defined in Agents."));

                // Parallel transition checks.
                if (t.Parallel)
                {
                    if (t.Targets is null or { Count: 0 })
                    {
                        issues.Add(("error",
                            $"{tpfx}: Parallel transition requires at least one entry in Targets."));
                    }
                    else
                    {
                        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var target in t.Targets)
                        {
                            if (!seenTargets.Add(target))
                                issues.Add(("warning", $"{tpfx}: Duplicate target state '{target}' in Targets."));

                            if (!stateNames.Contains(target))
                                issues.Add(("error",
                                    $"{tpfx}: Targets['{target}'] does not match any declared state."));
                            else if (string.Equals(target, t.To, StringComparison.OrdinalIgnoreCase))
                                issues.Add(("warning",
                                    $"{tpfx}: Target state '{target}' is the same as the join state (To). " +
                                    "Branch targets and the join state should be distinct."));
                        }
                    }

                    // Merge agent is required for Ranked and SemanticDiff.
                    if (t.Merge is { Strategy: MergeStrategy.Ranked or MergeStrategy.SemanticDiff })
                    {
                        if (string.IsNullOrWhiteSpace(t.Merge.Agent))
                            issues.Add(("error",
                                $"{tpfx}: Merge.Strategy '{t.Merge.Strategy}' requires Merge.Agent to be set."));
                        else if (!agentNames.Contains(t.Merge.Agent))
                            issues.Add(("error",
                                $"{tpfx}: Merge.Agent '{t.Merge.Agent}' is not defined in Agents."));
                    }

                    // RecoveryAgent is meaningless on a parallel transition (no contract evaluation).
                    if (!string.IsNullOrWhiteSpace(t.RecoveryAgent))
                        issues.Add(("warning",
                            $"{tpfx}: RecoveryAgent is ignored on parallel transitions."));
                }
                else
                {
                    // Targets without Parallel: true is almost certainly a config mistake.
                    if (t.Targets is { Count: > 0 })
                        issues.Add(("warning",
                            $"{tpfx}: Targets is set but Parallel is false — Targets will be ignored. " +
                            "Set 'Parallel: true' to enable fan-out."));

                    // Merge without Parallel: true is ignored.
                    if (t.Merge is not null)
                        issues.Add(("warning",
                            $"{tpfx}: Merge is set but Parallel is false — it will be ignored."));
                }
            }

            // Terminal states should have no transitions — they're unreachable.
            if (state.Terminal && state.Transitions.Count > 0)
                issues.Add(("warning", $"{prefix}: Terminal state declares {state.Transitions.Count} transition(s) that will never fire."));
        }
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

    private static void PrintInterpolatedPaths(OrchestrationConfig raw, string? sessionIdOverride)
    {
        var cwd        = Directory.GetCurrentDirectory();
        var slug       = fuseraft.Core.FuseraftPaths.ProjectSlug(cwd);
        var sessionId  = sessionIdOverride ?? "{session_id}";
        var expanded   = OrchestratorBuilder.InterpolateSessionId(raw, sessionId, slug);

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
            Add("Events.Path", evRaw.Path, evExp.Path);

        if (raw.ChangeTracking is { } ctRaw && expanded.ChangeTracking is { } ctExp)
        {
            Add("ChangeTracking.Path",          ctRaw.Path,                    ctExp.Path);
            Add("ChangeTracking.IntentLogPath", ctRaw.ResolveIntentLogPath(),  ctExp.IntentLogPath);
        }

        if (raw.EvidenceStore is { } esRaw && expanded.EvidenceStore is { } esExp)
            Add("EvidenceStore.Path", esRaw.Path, esExp.Path);

        if (raw.Validation is { } vRaw && expanded.Validation is { } vExp)
        {
            Add("Validation.BriefPath",      vRaw.BriefPath,      vExp.BriefPath);
            Add("Validation.TestReportPath", vRaw.TestReportPath, vExp.TestReportPath);
            Add("Validation.ChangeLogPath",  vRaw.ChangeLogPath,  vExp.ChangeLogPath);
        }

        if (raw.Brownfield is { } bfRaw && expanded.Brownfield is { } bfExp)
        {
            Add("Brownfield.DiscoveryBriefPath",    bfRaw.DiscoveryBriefPath,    bfExp.DiscoveryBriefPath);
            Add("Brownfield.ConventionProfilePath", bfRaw.ConventionProfilePath, bfExp.ConventionProfilePath);
        }

        if (raw.Chatroom is { } chRaw && expanded.Chatroom is { } chExp)
            Add("Chatroom.Path", chRaw.Path, chExp.Path);

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
                var pfx = $"Contracts[{cr.Name}].Requires[{pi}]";
                if (pr.Path        is not null) Add($"{pfx}.Path",          pr.Path,          pe.Path);
                if (pr.Source      is not null) Add($"{pfx}.Source",        pr.Source,        pe.Source);
                if (pr.PatternSource is not null) Add($"{pfx}.PatternSource", pr.PatternSource, pe.PatternSource);
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
