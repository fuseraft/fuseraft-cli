using Microsoft.Extensions.AI;
using fuseraft.Core.Models;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.KeyStore;
using fuseraft.Orchestration;

namespace fuseraft.Cli.Commands.Repl;

// Shared types used by ReplSession, ReplCommands, and ReplTurn.
internal sealed record PlanStep(int Step, string Description, string? Tool, string? Creates);

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
    public readonly string              SessionId;
    public readonly string              EventsPath;
    public readonly EventEmitter        Emitter;
    public readonly MemoryStore         MemoryStore;
    public readonly ChatClientFactory   Factory;
    public readonly IApiKeyStore        KeyStore;
    public readonly Dictionary<string, List<AIFunction>> ToolsByCategory;
    public readonly bool                Verbose;

    // Mutable provider state (may be replaced by /provider setup)
    public string      ModelId     { get; set; }
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

    // Plan/execution
    public PlanStep[]?                                CurrentPlan;
    public readonly Queue<(PlanStep Step, int Total)> ExecutionQueue = new();

    // Halted plan state — set when a step fails, cleared by /recover or /resume
    public (PlanStep Step, int Total)?                HaltedAt;
    public readonly Queue<(PlanStep Step, int Total)> HaltedRemaining = new();
    public List<string>                               HaltedToolCalls = [];
    public string?                                    RecoveryHint;

    // Safe mode
    public bool             SafeMode;
    public HashSet<string>? PreSafeDisabled;

    // Context growth tracking
    public int              PrevCtxEstimate;
    public readonly List<int> TurnTokenDeltas = [];
    public int              PrevTurnTokenEstimate;

    // Session lifecycle
    public int  TurnIndex              = 0;
    public int  LastExtractedTurnIndex = -1;
    public bool PendingSave;

    // Ctrl+C interception for in-flight requests only
    public CancellationTokenSource? ActiveCts;

    public ReplSessionContext(
        string cwd, string sessionId, string modelId, ModelConfig modelConfig,
        UserConfig? userCfg, IChatClient client, ChatClientFactory factory,
        IApiKeyStore keyStore, EventEmitter emitter, string eventsPath,
        MemoryStore memoryStore, Dictionary<string, List<AIFunction>> toolsByCategory,
        string systemPrompt, bool pendingSave, bool verbose = false)
    {
        Cwd             = cwd;
        SessionId       = sessionId;
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
        PendingSave     = pendingSave;
        Verbose         = verbose;
        History         = [new ChatMessage(ChatRole.System, systemPrompt)];
        ChatOptions     = BuildChatOptions();
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
        return active.Count > 0
            ? new ChatOptions { Tools = active.Cast<AITool>().ToList() }
            : null;
    }

    public int EstimateTokens() =>
        History.Sum(m => (m.Text?.Length ?? 0) / 4) +
        GetActiveTools().Sum(t => t.JsonSchema.GetRawText().Length / 4);
}
