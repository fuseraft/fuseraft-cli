using Microsoft.Extensions.AI;
using fuseraft.Core.Models;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.KeyStore;
using fuseraft.Infrastructure.Plugins;
using fuseraft.Orchestration;

namespace fuseraft.Cli.Commands.Repl;

// Shared types used by ReplSession, ReplCommands, and ReplTurn.
internal enum CommandOutcome { Continue, Exit, SendInput }

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
    public readonly bool                Verbose;
    public SkillsPlugin?                SkillsPlugin { get; set; }
    public TodoPlugin?                  Todo         { get; set; }

    // Mutable provider state (may be replaced by /provider setup)
    private string _modelId = string.Empty;
    public string ModelId
    {
        get => _modelId;
        set
        {
            _modelId = value;
            ContextTokenBudget = ModelContextWindow.GetBudget(value);
        }
    }

    // Working token budget for history trimming (TrimHistory) and the /context, /compact,
    // and context-warning displays — derived from ModelId so a large-context model isn't
    // held to the same ceiling as a small-context local model. Recomputed automatically
    // whenever ModelId is (re)assigned, including on /provider setup, /model switch, and
    // session resume.
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
                   ModelConfig, Factory, ToolsByCategory.Count > 0, ReplTurn.StepIterationLimit);
        set => _stepClient = value;
    }

    // Tool state
    public readonly HashSet<string> DisabledCategories = new(StringComparer.OrdinalIgnoreCase);
    public ChatOptions? ChatOptions;

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

    // Safe mode
    public bool             SafeMode;
    public HashSet<string>? PreSafeDisabled;

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

    public ReplSessionContext(
        string cwd, string sessionId, DateTime startedAt, string modelId, ModelConfig modelConfig,
        UserConfig? userCfg, IChatClient client, ChatClientFactory factory,
        IApiKeyStore keyStore, EventEmitter emitter, string eventsPath,
        MemoryStore memoryStore, Dictionary<string, List<AIFunction>> toolsByCategory,
        string systemPrompt, bool pendingSave, bool verbose = false,
        SubAgentPlugin? subAgent = null, ConversationCompactor? compactor = null)
    {
        Cwd             = cwd;
        SessionId       = sessionId;
        StartedAt       = startedAt;
        ModelId         = modelId;
        ModelConfig     = modelConfig;
        UserCfg         = userCfg;
        Client          = client;
        Factory         = factory;
        KeyStore        = keyStore;
        Emitter         = emitter;
        EventsPath      = eventsPath;
        MemoryStore     = memoryStore;
        ToolsByCategory = toolsByCategory;
        SubAgent        = subAgent;
        PendingSave     = pendingSave;
        Verbose         = verbose;
        History         = [new ChatMessage(ChatRole.System, systemPrompt)];
        ChatOptions     = BuildChatOptions();
        Compactor       = compactor;
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
        .SelectMany(kv => kv.Value)];

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
        History.Sum(m => m.Contents.Sum(AgentFactory.EstimateContentChars) / 4) +
        GetActiveTools().Sum(t => t.JsonSchema.GetRawText().Length / 4);
}
