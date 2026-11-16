namespace fuseraft.Cli.Team;

/// <summary>A team file that can't be loaded; the message names the field and the fix.</summary>
public sealed class TeamConfigException(string message, Exception? inner = null)
    : InvalidOperationException(message, inner);
