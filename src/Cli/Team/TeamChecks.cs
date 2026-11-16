using System.Text.Json;
using fuseraft.Core.Models;
using fuseraft.Core.Models.Team;
using fuseraft.Orchestration;

namespace fuseraft.Cli.Team;

/// <summary>A compiled gate: the predicates the engine runs and the sentence the agent is told.</summary>
internal sealed record CompiledCheck(List<ContractPredicate> Predicates, string Description);

/// <summary>The built-in checks a <c>Requires:</c> list accepts, and how each compiles to engine predicates.</summary>
internal static class TeamChecks
{
    private static readonly Dictionary<string, string[]> Options = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fileExists"]               = [],
        ["filesWritten"]             = [],
        ["checklistComplete"]        = [],
        ["commandSucceeded"]         = ["fallback"],
        ["testReport"]               = ["noFailures", "hasAssertions"],
        ["relatedTestsPass"]         = [],
        ["anyFileWritten"]           = ["fallback"],
        ["reviewJudgement"]          = [],
        ["acceptanceCriteriaPassed"] = [],
        ["architecture"]             = [],
        ["sessionContextWritten"]    = [],
        ["noRepeatedShellFailures"]  = [],
    };

    /// <summary>Names of every built-in check, for error messages and docs.</summary>
    public static IEnumerable<string> Names => Options.Keys;

    /// <summary>True when <paramref name="name"/> is a built-in check.</summary>
    public static bool IsCheck(string name) => Options.ContainsKey(name);

    /// <summary>Compiles one <c>Requires:</c> item. <paramref name="at"/> locates it for error messages.</summary>
    public static CompiledCheck Compile(JsonElement item, TeamConfig team, string at, TeamCompileState state)
    {
        if (item.ValueKind == JsonValueKind.String)
        {
            var name = item.GetString()!;
            if (team.Artifacts.TryGetValue(name, out var artifact))
                return ArtifactCheck(name, artifact);
            if (IsCheck(name))
                return Build(Canonical(name), arg: null, item, team, at, state);
            throw Unknown(name, team, at);
        }

        if (item.ValueKind != JsonValueKind.Object)
            throw new TeamConfigException($"{at}: each requires item is an artifact name, a check name, or a map like 'filesWritten: brief.files_to_change'.");

        var checks = item.EnumerateObject().Where(p => IsCheck(p.Name)).ToList();
        if (checks.Count != 1)
        {
            var first = item.EnumerateObject().Select(p => p.Name).FirstOrDefault() ?? "?";
            if (checks.Count == 0) throw Unknown(first, team, at);
            throw new TeamConfigException($"{at}: one check per item; split {string.Join(" and ", checks.Select(c => c.Name))} into separate items.");
        }

        var check = Canonical(checks[0].Name);
        foreach (var p in item.EnumerateObject().Where(p => !IsCheck(p.Name)))
            if (!Options[check].Contains(p.Name, StringComparer.OrdinalIgnoreCase))
                throw new TeamConfigException(
                    $"{at}: '{p.Name}' isn't an option of {check}." +
                    (Options[check].Length > 0 ? $" Options: {string.Join(", ", Options[check])}." : " It takes no options."));

        return Build(check, checks[0].Value, item, team, at, state);
    }

    private static CompiledCheck ArtifactCheck(string name, ArtifactConfig artifact)
    {
        var predicates = new List<ContractPredicate> { new() { Type = "FileExists", Path = artifact.Path } };
        if (artifact.Shape is { } shape)
            predicates.Add(new() { Type = "Shape", Path = artifact.Path, Shape = shape.GetRawText() });
        return new(predicates, artifact.Shape is null
            ? $"`{name}` exists at {artifact.Path}"
            : $"`{name}` exists at {artifact.Path} and matches its shape");
    }

    private static CompiledCheck Build(string check, JsonElement? arg, JsonElement item, TeamConfig team, string at, TeamCompileState state)
    {
        string? Text() => arg is { ValueKind: JsonValueKind.String } s ? s.GetString() : null;
        string? Opt(string key)
        {
            if (item.ValueKind != JsonValueKind.Object) return null;
            foreach (var p in item.EnumerateObject())
                if (p.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
                    return p.Value.ValueKind switch
                    {
                        JsonValueKind.String => p.Value.GetString(),
                        JsonValueKind.True   => "true",
                        JsonValueKind.False  => "false",
                        _                    => p.Value.GetRawText(),
                    };
            return null;
        }
        bool OptBool(string key, bool fallback) => Opt(key) is { } v ? v.Equals("true", StringComparison.OrdinalIgnoreCase) : fallback;

        switch (check)
        {
            case "fileExists":
            {
                var path = Required(Text(), check, "a path", at);
                return new([new() { Type = "FileExists", Path = path }], $"{path} exists");
            }
            case "filesWritten":
            case "checklistComplete":
            {
                var (artName, artifact, field) = FieldRef(Required(Text(), check, "an artifact field like brief.files_to_change", at), team, at, check);
                return new([new() { Type = PredicateType(check), Source = artifact.Path, Field = field }],
                    check == "filesWritten"
                        ? $"every path listed in {artName}.{field} has been written"
                        : $"every item in {artName}.{field} is checked off");
            }
            case "commandSucceeded":
            {
                var spec = Required(Text(), check, "an artifact field like brief.verify_command, or a command pattern", at);
                var fallback = Opt("fallback");
                if (TryFieldRef(spec, team, out var artName, out var artifact, out var field))
                    return new([new() { Type = PredicateType(check), PatternSource = artifact!.Path, PatternField = field, Pattern = fallback }],
                        $"the command in {artName}.{field} ran in a shell tool and succeeded");
                return new([new() { Type = PredicateType(check), Pattern = spec }],
                    $"a shell command matching `{spec}` ran and succeeded");
            }
            case "testReport":
            {
                var artName = Required(Text(), check, "the test report's artifact name", at);
                if (!team.Artifacts.TryGetValue(artName, out var artifact))
                    throw new TeamConfigException($"{at}: testReport names '{artName}', which isn't declared under artifacts.");
                state.NoteTestReport(artifact.Path, at);
                var noFailures    = OptBool("noFailures", true);
                var hasAssertions = OptBool("hasAssertions", true);
                return new([new() { Type = PredicateType(check), Path = artifact.Path, NoFailures = noFailures, HasAssertions = hasAssertions }],
                    $"`{artName}` exists" + (noFailures ? ", reports no failures" : "") +
                    (hasAssertions ? ", and every PASS cites a command you actually ran" : ""));
            }
            case "relatedTestsPass":
                state.NeedsTestSelector(at);
                return new([new() { Type = PredicateType(check) }], "the tests related to the files you changed pass");
            case "anyFileWritten":
                return Validator(ValidatorNames.RequireWriteFile, Opt("fallback"), "you wrote at least one file this turn");
            case "reviewJudgement":
                if (Text() is { } reviewArt) NoteBriefArtifact(reviewArt, team, at, state, check);
                return Validator(ValidatorNames.RequireReviewJudgement, null, "your review states a verdict backed by evidence");
            case "acceptanceCriteriaPassed":
            {
                var art = Required(Text(), check, "the brief's artifact name", at);
                NoteBriefArtifact(art, team, at, state, check);
                return Validator(ValidatorNames.RequireAcceptanceCriteriaPassed, null, $"every acceptance criterion in `{art}` has passed");
            }
            case "architecture":
                return Validator(ValidatorNames.ArchitectureValidator, null, "the change respects the project's architecture rules");
            case "sessionContextWritten":
                return Validator(ValidatorNames.RequireSessionContextWrite, null, "you wrote a session context summary with session_context_write");
            case "noRepeatedShellFailures":
                return Validator(ValidatorNames.BlockOnConsecutiveFail, Text(), "your last shell runs are not failing repeatedly");
        }
        throw new TeamConfigException($"{at}: unknown check '{check}'.");
    }

    private static CompiledCheck Validator(string name, string? pattern, string description) =>
        new([new() { Type = "Validator", Validator = name, Pattern = pattern }], description);

    private static void NoteBriefArtifact(string artName, TeamConfig team, string at, TeamCompileState state, string check)
    {
        if (!team.Artifacts.TryGetValue(artName, out var artifact))
            throw new TeamConfigException($"{at}: {check} names '{artName}', which isn't declared under artifacts.");
        state.NoteBrief(artName, artifact.Path, at);
    }

    private static string Required(string? value, string check, string what, string at) =>
        value is { Length: > 0 } v ? v : throw new TeamConfigException($"{at}: {check} needs {what}.");

    private static (string Name, ArtifactConfig Artifact, string Field) FieldRef(string spec, TeamConfig team, string at, string check) =>
        TryFieldRef(spec, team, out var name, out var artifact, out var field)
            ? (name!, artifact!, field!)
            : throw new TeamConfigException(
                $"{at}: {check} expects '<artifact>.<field>', and '{spec}' doesn't name a declared artifact. " +
                $"Declared: {string.Join(", ", team.Artifacts.Keys)}.");

    private static bool TryFieldRef(string spec, TeamConfig team, out string? name, out ArtifactConfig? artifact, out string? field)
    {
        name = null; artifact = null; field = null;
        var dot = spec.IndexOf('.');
        if (dot <= 0 || dot == spec.Length - 1) return false;
        var candidate = spec[..dot];
        if (!team.Artifacts.TryGetValue(candidate, out artifact)) return false;
        name  = candidate;
        field = spec[(dot + 1)..];
        return true;
    }

    // Runtime predicate types keep their engine names (FilesWritten), whatever case the file used.
    private static string PredicateType(string check) => char.ToUpperInvariant(check[0]) + check[1..];

    internal static string Canonical(string name) => Options.Keys.First(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static TeamConfigException Unknown(string name, TeamConfig team, string at) =>
        new($"{at}: '{name}' is neither a declared artifact nor a check. " +
            (team.Artifacts.Count > 0 ? $"Artifacts: {string.Join(", ", team.Artifacts.Keys)}. " : "") +
            $"Checks: {string.Join(", ", Names)}.");
}
