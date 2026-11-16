using System.Text.Json;

namespace fuseraft.Core.Models.Team;

/// <summary>Root of a v2 team file: agents, the artifacts they exchange, and the flow between them.</summary>
public sealed record TeamConfig
{
    /// <summary>Team name, shown in session output.</summary>
    public string Team { get; init; } = string.Empty;

    /// <summary>Optional one-line summary of what the team does.</summary>
    public string? Description { get; init; }

    /// <summary>Named model aliases that agents and patterns refer to by name.</summary>
    public Dictionary<string, ModelConfig> Models { get; init; } = [];

    /// <summary>Files agents hand each other, declared once and referenced by name.</summary>
    public Dictionary<string, ArtifactConfig> Artifacts { get; init; } = [];

    /// <summary>Agents keyed by name.</summary>
    public Dictionary<string, TeamAgentConfig> Agents { get; init; } = [];

    /// <summary>Either <c>Start</c> + <c>Steps</c>, or a <c>Pattern</c> with its fields.</summary>
    public JsonElement Flow { get; init; }

    /// <summary>Hard caps on a run.</summary>
    public LimitsConfig? Limits { get; init; }

    /// <summary>Sandbox, permissions and approval settings.</summary>
    public SecurityConfig? Security { get; init; }

    /// <summary>MCP servers whose tools agents can list in <c>Tools</c>.</summary>
    public List<McpServerConfig>? McpServers { get; init; }

    /// <summary>Opt-in layers (compaction, verifier, telemetry, ...), bound to their existing records.</summary>
    public JsonElement? Advanced { get; init; }
}

/// <summary>A file agents exchange, with an optional JSON shape that gates check.</summary>
public sealed record ArtifactConfig
{
    /// <summary>Path relative to the working directory.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Compact type sketch: <c>string</c>, <c>int</c>, <c>number</c>, <c>bool</c>, <c>A|B</c>, <c>[T]</c>, or a nested map.</summary>
    public JsonElement? Shape { get; init; }
}

/// <summary>One agent in a v2 team file.</summary>
public sealed record TeamAgentConfig
{
    /// <summary>Optional YAML file holding this agent's fields; inline fields win.</summary>
    public string? AgentFile { get; init; }

    /// <summary>One-line description of what the agent does.</summary>
    public string? Role { get; init; }

    /// <summary>A model alias, or a full model object.</summary>
    public JsonElement? Model { get; init; }

    /// <summary>Tool groups (plugins and MCP server names).</summary>
    public List<string> Tools { get; init; } = [];

    /// <summary>Artifacts this agent reads.</summary>
    public List<string> Reads { get; init; } = [];

    /// <summary>Artifacts this agent writes.</summary>
    public List<string> Writes { get; init; } = [];

    /// <summary><c>handoff</c> (default), <c>conversation</c> or <c>fork</c>.</summary>
    public string? Sees { get; init; }

    /// <summary>The agent's system instructions.</summary>
    public string Instructions { get; init; } = string.Empty;

    /// <summary>Remote A2A agent; when set, Model and Tools don't apply.</summary>
    public RemoteAgentConfig? RemoteAgent { get; init; }

    /// <summary>Per-agent knobs, bound to the matching <see cref="AgentConfig"/> fields.</summary>
    public JsonElement? Advanced { get; init; }
}

/// <summary>Hard caps on a run.</summary>
public sealed record LimitsConfig
{
    /// <summary>Maximum agent turns before the run stops.</summary>
    public int MaxTurns { get; init; } = 40;

    /// <summary>Maximum total tokens before the run stops.</summary>
    public int? MaxTokens { get; init; }
}
