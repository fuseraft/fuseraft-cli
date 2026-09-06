using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using fuseraft.Core;
using fuseraft.Core.Models;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.KeyStore;
using fuseraft.Infrastructure.Mcp;
using fuseraft.Infrastructure.Plugins;
using fuseraft.Orchestration;

namespace fuseraft.Cli.Commands.Repl;

// Shared types used by ReplSession, ReplCommands, and ReplTurn.
internal enum CommandOutcome { Continue, Exit, SendInput }

/// <summary>
/// Mutable holder for REPL HITL mode's on/off flag, shared between the ShellPlugin approver
/// closure (built in ReplCommand.cs before a ReplSessionContext exists) and
/// <see cref="ReplSessionContext.HitlMode"/> (toggled by <c>/hitl</c>). A plain bool can't be
/// passed by reference across that gap the way this one shared instance can.
/// </summary>
internal sealed class HitlModeState
{
    public bool Enabled;
}

internal readonly record struct CommandResult(
    CommandOutcome Outcome,
    string?        InputOverride = null,
    bool           CapturePlan   = false)
{
    public static readonly CommandResult Continue = new(CommandOutcome.Continue);
    public static readonly CommandResult Exit     = new(CommandOutcome.Exit);
    public static CommandResult Send(string input, bool capturePlan = false) =>
        new(CommandOutcome.SendInput, input, capturePlan);
}

/// <summary>
/// Holds all mutable state for a REPL session. Passed explicitly to command handlers
/// and turn execution so those can be static methods with no hidden state.
/// </summary>
internal sealed class ReplSessionContext
{
    // Immutable deps
    public readonly string              Cwd;
    public string                       SessionId  { get; set; }
    public readonly string              EventsPath;
    public readonly EventEmitter        Emitter;
    public readonly MemoryStore         MemoryStore;
    public readonly ChatClientFactory   Factory;
    public readonly IApiKeyStore        KeyStore;
    public readonly Dictionary<string, List<AIFunction>> ToolsByCategory;
    public readonly SubAgentPlugin?     SubAgent;
    public readonly UndoSnapshotStore?  UndoStore;
    public readonly bool                Verbose;

    // Shared with every IChatClient this session builds via ReplFactory.BuildClient (including
    // the ones built before this ReplSessionContext existed, so the same instance is passed in
    // here rather than created fresh), so a provider call that only survived via adaptive
    // context-trim can force a real /compact before the next turn — see
    // AgentMiddlewareBuilder's adaptive-retry loop and ReplTurn's post-turn ConsumeTrim check.
    // Mirrors CompactionCoordinator's role in `fuseraft run`.
    public readonly AdaptiveTrimTracker AdaptiveTrimTracker;
    public IReadOnlyList<AgentSkill>    Skills       { get; set; } = [];
    public TodoPlugin?                  Todo         { get; set; }

    // Owns any MCP server connections added this session via /mcp add. Created lazily on
    // first use (either loading saved servers at startup or the first /mcp add call) and
    // disposed once, on REPL exit — see ReplCommand.cs.
    public McpSessionManager? McpManager { get; set; }

    // Mutable provider state (may be replaced by /provider setup)
    private string _modelId = string.Empty;
    public string ModelId
    {
        get => _modelId;
        set
        {
            _modelId = value;
            ContextTokenBudget = ModelContextWindow.GetBudget(value, UserCfg?.ReplContextBudget);
        }
    }

    // Working token budget for history trimming (TrimHistory) and the /context, /compact,
    // and context-warning displays — derived from ModelId (and UserCfg.ReplContextBudget, if set)
    // so a large-context model isn't held to the same ceiling as a small-context local model.
    // Recomputed automatically whenever ModelId is (re)assigned, including on /provider setup,
    // /model switch, and session resume. Relies on UserCfg already being current at that point
    // — the constructor below sets UserCfg before ModelId for this reason, and every later
    // reassignment site that changes both (e.g. /provider setup) must preserve that order.
    public int ContextTokenBudget { get; private set; } = ModelContextWindow.DefaultBudget;

    public ModelConfig ModelConfig { get; set; }
    public UserConfig? UserCfg     { get; set; }
    public IChatClient Client      { get; set; }

    // Low-iteration client used during /execute steps. Initialized on first use so the
    // extra factory.Create() call is skipped in sessions that never run /execute.
    private IChatClient? _stepClient;
    public  IChatClient  StepClient
    {
        get => _stepClient ??= ReplFactory.BuildClient(
                   ModelConfig, Factory, ToolsByCategory.Count > 0,
                   AdaptiveTrimTracker, Emitter, ReplTurn.StepIterationLimit);
        set => _stepClient = value;
    }

    // Tool state
    public readonly HashSet<string> DisabledCategories = new(StringComparer.OrdinalIgnoreCase);
    public ChatOptions? ChatOptions;

    // Per-plugin capability restrictions set via /tools restrict, using the same
    // PluginCapabilityMap vocabulary (read/write/delete/run/...) and the same enforcement
    // function (PluginCapabilityMap.IsAllowed) as AgentConfig.Capabilities in orchestration.
    // Keys are plugin names ("FileSystem", "Shell", "Git", "Http", ...); values are the
    // capability tags still allowed for that plugin. Filtering is done per-tool by
    // PluginCapabilityMap.GetPlugin(toolName) rather than by which REPL category dictionary
    // key currently holds the tool — so restricting "Git" also covers Git tools sitting in
    // the "Extended" category bucket, unlike /safe-mode's category-key-only disable.
    public readonly Dictionary<string, List<string>> CapabilityRestrictions =
        new(StringComparer.OrdinalIgnoreCase);

    // Conversation
    public readonly List<ChatMessage> History;
    public readonly ConversationCompactor? Compactor;

    // Plan/execution
    public PlanStep[]?                                CurrentPlan;
    public readonly Queue<(PlanStep Step, int Total)> ExecutionQueue = new();

    // Halted plan state — set when a step fails, cleared by /recover or /resume
    public (PlanStep Step, int Total)?                HaltedAt;
    public readonly Queue<(PlanStep Step, int Total)> HaltedRemaining = new();
    public List<string>                               HaltedToolCalls = [];
    public string?                                    RecoveryHint;

    // JSON bridge mode (set when running inside VS Code webview panel)
    public bool JsonMode;

    // Startup display options, captured once so /clear can redraw the same header
    // (see MessageRenderer.RenderReplHeader) it printed at launch.
    public bool NoBanner;
    public int  MemoryCount;

    // Safe mode
    public bool             SafeMode;
    public HashSet<string>? PreSafeDisabled;

    // HITL (human-in-the-loop) mode — when on, every shell command asks for y/N approval via
    // the same IHumanApprovalService.PromptShellCommandAsync gate `fuseraft run --hitl` already
    // uses (see OrchestratorBuilder.ResolveSecurityConfig). The flag lives in a separate shared
    // object rather than a plain bool here because ShellPlugin is constructed before this
    // ReplSessionContext exists (see ReplCommand.cs) — its approver closure captures Hitl
    // directly, and this property just proxies to the same storage so /hitl can toggle it live.
    public readonly HitlModeState Hitl;
    public bool HitlMode
    {
        get => Hitl.Enabled;
        set => Hitl.Enabled = value;
    }

    // Adversarial mode — critic agent reviews each /execute step result
    public bool AdversarialMode;

    // Set by HandleStepResult when a step passed using only inspect (read-only) tools.
    // RunLoopAsync uses these to inject tool outputs into history so subsequent steps can see them.
    public bool LastStepWasInspectOnly;
    public List<(string ToolName, string Output)>? LastStepInspectResults;

    // Max output tokens (0 = provider default)
    public int MaxOutputTokens;

    // Context growth tracking
    public int              PrevCtxEstimate;
    public readonly List<int> TurnTokenDeltas = [];
    public int              PrevTurnTokenEstimate;

    // Actual provider-reported token usage, summed across every LLM round trip for the life
    // of this process (including tool-call continuations within a turn). Reflects real billed
    // usage, so unlike the estimates above it is never reset by /clear, /rewind, or /compact.
    public long CumulativeInputTokens;
    public long CumulativeOutputTokens;

    // Real input-token count reported by the provider for the *first* LLM call of the most
    // recently completed turn (i.e. before that turn's own tool round trips inflated the
    // request) — the exact size of everything sent to the model as that turn began. Set to
    // null whenever a turn completes without any UsageContent (provider doesn't report usage,
    // e.g. Ollama), so /context falls back cleanly to the char-based estimate rather than
    // showing a stale number from an earlier turn.
    public int? LastActualContextTokens;

    // Session lifecycle
    public DateTime StartedAt { get; set; }
    public int  TurnIndex              = 0;
    public int  LastExtractedTurnIndex = -1;
    public bool PendingSave;

    // Whether the current API key was actually persisted to an OS keychain (true unless the
    // wizard ran with no keychain available, in which case the key is memory-only for this
    // process and ReplTurn's deferred-save message must not claim otherwise).
    public bool KeyStored = true;

    // One-time context-warning flag; reset by /clear and /compact so the hint
    // fires once again if the user compacts and then fills context again.
    public bool ContextWarningShown;

    // Ctrl+C interception for in-flight requests only
    public CancellationTokenSource? ActiveCts;

    // History-aware line reader (shared across turns so history persists)
    public readonly ReplLineReader LineReader = new();
    
    // Turn-scoped plugin state that must be cleared before each new REPL turn.
    public readonly List<ITurnResettable> TurnResettables = [];

    public ReplSessionContext(
        string cwd, string sessionId, DateTime startedAt, string modelId, ModelConfig modelConfig,
        UserConfig? userCfg, IChatClient client, ChatClientFactory factory,
        IApiKeyStore keyStore, EventEmitter emitter, string eventsPath,
        MemoryStore memoryStore, Dictionary<string, List<AIFunction>> toolsByCategory,
        string systemPrompt, bool pendingSave, AdaptiveTrimTracker adaptiveTrimTracker,
        bool verbose = false,
        SubAgentPlugin? subAgent = null, ConversationCompactor? compactor = null,
        UndoSnapshotStore? undoStore = null, HitlModeState? hitlState = null)
    {
        Hitl            = hitlState ?? new HitlModeState();
        Cwd             = cwd;
        SessionId       = sessionId;
        StartedAt       = startedAt;
        UserCfg         = userCfg;
        ModelId         = modelId;
        ModelConfig     = modelConfig;
        Client          = client;
        Factory         = factory;
        KeyStore        = keyStore;
        Emitter         = emitter;
        EventsPath      = eventsPath;
        MemoryStore     = memoryStore;
        ToolsByCategory = toolsByCategory;
        SubAgent        = subAgent;
        UndoStore       = undoStore;
        PendingSave     = pendingSave;
        Verbose         = verbose;
        History         = [new ChatMessage(ChatRole.System, systemPrompt)];
        ChatOptions     = BuildChatOptions();
        Compactor       = compactor;
        AdaptiveTrimTracker = adaptiveTrimTracker;
    }

    public void ResetPlanState()
    {
        ExecutionQueue.Clear();
        CurrentPlan = null;
        HaltedAt = null;
        HaltedRemaining.Clear();
        HaltedToolCalls.Clear();
        RecoveryHint = null;
    }

    public List<AIFunction> GetActiveTools() => [.. ToolsByCategory
        .Where(kv => !DisabledCategories.Contains(kv.Key))
        .SelectMany(kv => kv.Value)
        .Where(f => PassesCapabilityRestriction(f.Name))];

    public bool PassesCapabilityRestriction(string toolName)
    {
        if (CapabilityRestrictions.Count == 0) return true;
        var plugin = PluginCapabilityMap.GetPlugin(toolName);
        // No capability-map entry (MCP tools, plugins with no fine-grained tags) — not
        // restrictable, so it's unaffected by any /tools restrict declared so far.
        if (plugin is null) return true;
        // This tool's owning plugin has no restriction declared — pass through.
        if (!CapabilityRestrictions.TryGetValue(plugin, out var allowed)) return true;
        return PluginCapabilityMap.IsAllowed(toolName, allowed);
    }

    public void BeginTurn()
    {
        foreach (var resettable in TurnResettables)
            resettable.BeginTurn();
    }

    public ChatOptions? BuildChatOptions()
    {
        var active = GetActiveTools();
        var hasTools = active.Count > 0;
        var hasMax   = MaxOutputTokens > 0;
        if (!hasTools && !hasMax) return null;
        var opts = new ChatOptions();
        if (hasTools) opts.Tools = active.Cast<AITool>().ToList();
        if (hasMax)   opts.MaxOutputTokens = MaxOutputTokens;
        return opts;
    }

    public int EstimateTokens() =>
        History.Sum(m => TokenEstimator.EstimateTokens(m.Contents.Sum(AgentContextCompactionFilters.EstimateContentChars))) +
        GetActiveTools().Sum(t => TokenEstimator.EstimateTokens(t.JsonSchema.GetRawText().Length));
}
