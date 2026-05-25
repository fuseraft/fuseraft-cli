using fuseraft.Infrastructure;

namespace fuseraft.Cli.Commands.Context;

internal static class ContextHelpers
{
    internal static string ResolveContextDir(string? dir)
    {
        var baseDir = string.IsNullOrWhiteSpace(dir)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(dir);
        return Path.Combine(baseDir, ContextStore.DefaultContextDir);
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1_024         => $"{bytes} B",
        < 1_048_576     => $"{bytes / 1_024.0:F1} KB",
        < 1_073_741_824 => $"{bytes / 1_048_576.0:F1} MB",
        _               => $"{bytes / 1_073_741_824.0:F1} GB",
    };
}
