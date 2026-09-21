using fuseraft.Core.Models.Config;

namespace fuseraft.Cli.Commands.Repl;

/// <summary>The REPL's tunable thresholds from <see cref="ReplDefaultsConfig"/>, clamped so a hand-edited file can't disable a safety rail.</summary>
internal sealed record ReplLimits
{
    internal const double DefaultAutoCompactThreshold = 0.75;
    internal const double DefaultPreserveTailRatio    = 0.20;
    internal const int    DefaultMaxStreamRetries     = 2;

    internal const double AutoCompactThresholdMin = 0.50, AutoCompactThresholdMax = 0.95;
    internal const double PreserveTailRatioMin    = 0.05, PreserveTailRatioMax    = 0.50;
    internal const int    ToolFailuresMin         = 2,    ToolFailuresMax         = 50;
    internal const int    IdenticalCallsMin       = 3,    IdenticalCallsMax       = 50;
    internal const int    WarnIdenticalCallsMin   = 2;
    internal const int    StreamRetriesMax        = 5;

    internal static ReplLimits Default { get; } = new();

    internal double AutoCompactThreshold    { get; init; } = DefaultAutoCompactThreshold;
    internal double PreserveTailRatio       { get; init; } = DefaultPreserveTailRatio;
    internal int    MaxConsecutiveToolFailures { get; init; } = ReplTurn.MaxConsecutiveToolFailures;
    internal int    MaxIdenticalToolCalls   { get; init; } = ReplTurn.MaxConsecutiveIdenticalToolCalls;
    internal int    WarnIdenticalToolCalls  { get; init; } = ReplTurn.SoftRepeatedToolCallThreshold;
    internal int    MaxStreamRetries        { get; init; } = DefaultMaxStreamRetries;

    internal static ReplLimits From(ReplDefaultsConfig? cfg)
    {
        if (cfg is null) return Default;

        var hard = cfg.MaxIdenticalToolCalls is { } h
            ? Math.Clamp(h, IdenticalCallsMin, IdenticalCallsMax)
            : ReplTurn.MaxConsecutiveIdenticalToolCalls;

        // The warning has to land before the cutoff to be of any use, so it can't reach it however the two are set.
        var warn = Math.Min(
            cfg.WarnIdenticalToolCalls is { } w ? Math.Max(w, WarnIdenticalCallsMin) : ReplTurn.SoftRepeatedToolCallThreshold,
            hard - 1);

        return new ReplLimits
        {
            AutoCompactThreshold = cfg.AutoCompactThreshold is { } t
                ? Math.Clamp(t, AutoCompactThresholdMin, AutoCompactThresholdMax) : DefaultAutoCompactThreshold,
            PreserveTailRatio = cfg.CompactPreserveTailRatio is { } r
                ? Math.Clamp(r, PreserveTailRatioMin, PreserveTailRatioMax) : DefaultPreserveTailRatio,
            MaxConsecutiveToolFailures = cfg.MaxConsecutiveToolFailures is { } f
                ? Math.Clamp(f, ToolFailuresMin, ToolFailuresMax) : ReplTurn.MaxConsecutiveToolFailures,
            MaxIdenticalToolCalls  = hard,
            WarnIdenticalToolCalls = warn,
            MaxStreamRetries = cfg.MaxStreamRetries is { } s
                ? Math.Clamp(s, 0, StreamRetriesMax) : DefaultMaxStreamRetries,
        };
    }
}
