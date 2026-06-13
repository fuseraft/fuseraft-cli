namespace fuseraft.Core.Models.Config;

/// <summary>
/// Configures the pluggable memory provider for an orchestration session.
/// When present, a <see cref="fuseraft.Infrastructure.Memory.MemoryManager"/> is built and wired
/// into the orchestrator's pre- and post-turn hooks.
/// </summary>
public record MemoryConfig
{
    /// <summary>
    /// Memory provider to activate.
    /// Built-in values: <c>local</c> (default — file-backed per-agent <c>MemoryStore</c>),
    /// <c>webhook</c> (generic HTTP endpoint).
    /// </summary>
    public string Provider { get; init; } = "local";

    /// <summary>
    /// Required when <see cref="Provider"/> is <c>webhook</c>.
    /// </summary>
    public WebhookMemoryConfig? Webhook { get; init; }
}

/// <summary>
/// HTTP endpoint configuration for the <c>webhook</c> memory provider.
/// </summary>
public record WebhookMemoryConfig
{
    /// <summary>
    /// URL to POST to when loading memory before a turn.
    /// Receives <c>{"agent": "&lt;name&gt;"}</c> and must return <c>{"block": "&lt;text&gt;"}</c>
    /// (or an empty body / null <c>block</c> when no memory applies).
    /// Omit to skip the load step.
    /// </summary>
    public string? LoadUrl { get; init; }

    /// <summary>
    /// URL to POST to when saving memory after a turn.
    /// Receives <c>{"agent": "&lt;name&gt;", "history": [...]}</c>.
    /// Omit to skip the save step.
    /// </summary>
    public string? SaveUrl { get; init; }

    /// <summary>
    /// HTTP headers merged into every request. Values support <c>${ENV_VAR}</c> expansion.
    /// Example: <c>{ "Authorization": "Bearer ${MY_MEMORY_TOKEN}" }</c>
    /// </summary>
    public Dictionary<string, string> Headers { get; init; } = [];

    /// <summary>
    /// Per-request timeout in seconds. Defaults to 10.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 10;

    /// <summary>
    /// Save only every Nth turn (1 = every turn, 5 = every 5th turn, etc.).
    /// Defaults to 10 to avoid flooding the endpoint on long sessions.
    /// </summary>
    public int SaveEveryNTurns { get; init; } = 10;
}
