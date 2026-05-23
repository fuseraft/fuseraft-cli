using System.ComponentModel;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Provides a single <c>compact_conversation</c> tool that lets an agent request a compaction
/// flush. The <see cref="fuseraft.Cli.SessionRunner"/> detects this tool call in the completed
/// turn and triggers the same <c>ApplyCompactionAsync</c> path as the automatic threshold trigger.
/// </summary>
public sealed class CompactionPlugin
{
    /// <summary>Name under which this plugin is registered in <see cref="PluginRegistry"/>.</summary>
    public const string PluginName = "Compaction";

    /// <summary>The function name exposed to the model (<c>compact_conversation</c>).</summary>
    public const string FunctionName = "compact_conversation";

    [Description("Compact conversation history to reduce context size.")]
    public string CompactConversation() => "COMPACT_REQUESTED";
}
