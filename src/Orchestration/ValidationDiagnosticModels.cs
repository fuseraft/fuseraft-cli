namespace fuseraft.Orchestration;

// Minimal projections of the change log schema used only by ValidationDiagnosticHook
// to deserialize the most recent entry for diagnostic context injection.

internal sealed class ChangeLogSnapshot
{
    public List<ChangeEntrySnapshot>? Entries { get; init; }
}

internal sealed class ChangeEntrySnapshot
{
    public string?       Agent        { get; init; }
    public int           TurnIndex    { get; init; }
    public List<string>? FilesWritten { get; init; }
    public List<string>? FilesDeleted { get; init; }
    public List<CommandSnapshot>? CommandsRun { get; init; }
    public List<string>? GitCommits   { get; init; }
}

internal sealed class CommandSnapshot
{
    public string Command   { get; init; } = string.Empty;
    public bool   Succeeded { get; init; }
}
