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
/// </summary>
public sealed class HandoffPlugin
{
    /// <summary>Name under which this plugin is registered in <see cref="PluginRegistry"/>.</summary>
    public const string PluginName = "Handoff";

    /// <summary>The function name exposed to the model (<c>handoff</c>).</summary>
    public const string FunctionName = "handoff";

    /// <summary>The argument name the model must supply (<c>route_keyword</c>).</summary>
    public const string ArgumentName = "route_keyword";

    [Description("Signal completion and hand off to the next workflow step. Must be the last tool call.")]
    public string Handoff(
        [Description("Exact routing keyword for the intended handoff.")] string route_keyword)
        => route_keyword;
}
