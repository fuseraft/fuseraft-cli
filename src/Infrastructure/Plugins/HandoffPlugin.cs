using System.ComponentModel;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Provides a single generic <c>handoff</c> tool for explicit, type-safe routing.
///
/// <para>
/// Agents call <c>handoff(route_keyword: "HANDOFF TO DEVELOPER")</c> instead of emitting
/// the keyword in free text. Tool-call arguments are parsed by the model's function-calling
/// infrastructure, which is far more reliable than expecting an exact string on its own line
/// in an open-ended prose response.
/// </para>
///
/// <para>
/// Both <see cref="fuseraft.Orchestration.GraphOrchestrator"/> and
/// <see cref="fuseraft.Orchestration.Strategies.KeywordSelectionStrategy"/> inspect
/// <c>FunctionCallContent</c> arguments directly from the response or history and use them
/// as the primary routing signal — before any text-based keyword scanning fires.
/// </para>
///
/// <para>
/// The tool itself is a no-op: it returns <paramref name="route_keyword"/> verbatim so that
/// the legacy tool-result scanning paths also detect it as a fallback.
/// </para>
///
/// <para>
/// The optional <paramref name="goal"/>/<paramref name="background"/>/<paramref name="constraints"/>
/// arguments let the handing-off agent synthesize a self-contained directive for the receiving
/// agent instead of relying on it to infer intent from the shared transcript. Orchestrators read
/// these directly off the <c>FunctionCallContent</c> and build an
/// <see cref="fuseraft.Core.Models.Agents.AgentDirective"/> for the next turn. When omitted, the
/// receiving agent falls back to whatever its <see cref="fuseraft.Core.Models.Agents.AgentIsolation"/>
/// mode otherwise provides.
/// </para>
/// </summary>
public sealed class HandoffPlugin
{
    /// <summary>Name under which this plugin is registered in <see cref="PluginRegistry"/>.</summary>
    public const string PluginName = "Handoff";

    /// <summary>The function name exposed to the model (<c>handoff</c>).</summary>
    public const string FunctionName = "handoff";

    /// <summary>The argument name the model must supply (<c>route_keyword</c>).</summary>
    public const string ArgumentName = "route_keyword";

    /// <summary>The optional structured-directive argument names, for orchestrators reading raw <c>FunctionCallContent</c>.</summary>
    public const string GoalArgumentName        = "goal";
    public const string BackgroundArgumentName   = "background";
    public const string ConstraintsArgumentName  = "constraints";

    [Description("Signal completion and hand off to the next workflow step. Must be the last tool call.")]
    public string Handoff(
        [Description("Exact routing keyword for the intended handoff.")]
        string route_keyword,
        [Description("What the receiving agent must accomplish this turn. Recommended: always set this — it becomes the receiving agent's task when it runs in isolated (Fresh) mode and cannot see this conversation.")]
        string? goal = null,
        [Description("What you already learned, tried, or ruled out that the receiving agent needs to know. Do not assume it can see your reasoning.")]
        string? background = null,
        [Description("Explicit constraints the receiving agent must respect, one per line.")]
        string? constraints = null)
        => route_keyword;
}
