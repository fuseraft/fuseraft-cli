namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Implemented by plugins that own a runtime artifact path under ~/.fuseraft/.
/// The path and label are injected into the agent system prompt so the agent
/// can reference the file directly without scanning the directory.
/// </summary>
internal interface IHasArtifact
{
    /// <summary>Absolute path to the artifact file or directory this plugin manages.</summary>
    string ArtifactPath { get; }

    /// <summary>Short description appended after the path in the orientation block.</summary>
    string ArtifactLabel { get; }
}
