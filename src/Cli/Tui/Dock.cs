namespace fuseraft.Cli.Tui;

/// <summary>Where the full screen puts the prompt and status line: under the transcript, or above it.</summary>
internal enum Dock
{
    Bottom,
    Top,
}

internal static class Docks
{
    public static readonly IReadOnlyList<(Dock Dock, string Name, string Description)> All =
    [
        (Dock.Bottom, "bottom", "the prompt under the transcript"),
        (Dock.Top, "top", "the prompt at the top of the screen, the transcript under it"),
    ];

    public static string Name(Dock dock) => All.First(d => d.Dock == dock).Name;

    public static Dock? Named(string name) =>
        All.Where(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(d => (Dock?)d.Dock).FirstOrDefault();

    public static string Names => string.Join(", ", All.Select(d => d.Name));
}
