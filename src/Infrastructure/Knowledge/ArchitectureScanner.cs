using System.Text.RegularExpressions;
using fuseraft.Core.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace fuseraft.Infrastructure.Knowledge;

/// <summary>
/// Loads an <see cref="ArchitectureManifest"/> from YAML and scans source files for
/// layer violations: import statements that cross a disallowed layer boundary.
/// Built-in profiles are provided for C#, Python, Java, TypeScript, JavaScript,
/// Go, Rust, and Ruby. The active profile is selected by
/// <see cref="ArchitectureManifest.Language"/>; unknown values fall back to C#.
/// </summary>
public static class ArchitectureScanner
{
    // -------------------------------------------------------------------------
    // Language profiles
    // -------------------------------------------------------------------------

    /// <summary>
    /// Describes how to find source files and extract imported module/namespace
    /// names for a specific language.
    /// </summary>
    private sealed record LanguageProfile(
        /// <summary>Glob patterns passed to <see cref="Directory.EnumerateFiles"/>.</summary>
        IReadOnlyList<string> FileGlobs,
        /// <summary>
        /// One or more regexes whose capture group 1 contains the imported
        /// module or namespace path. All patterns are tried per line; the first
        /// match wins.
        /// </summary>
        IReadOnlyList<Regex> ImportPatterns,
        /// <summary>
        /// Token that separates namespace segments (e.g. <c>"."</c>, <c>"::"</c>,
        /// <c>"/"</c>). Used for prefix matching in layer assignment.
        /// </summary>
        string NamespaceSeparator,
        /// <summary>
        /// Given a layer name, returns the default namespace/module prefixes when
        /// the manifest omits <c>Namespaces</c>. Return an empty list to require
        /// explicit declarations.
        /// </summary>
        Func<string, List<string>> DefaultNamespaces,
        /// <summary>
        /// Optional predicate that suppresses an extracted namespace (e.g. to
        /// skip relative imports such as <c>"./foo"</c>).
        /// </summary>
        Func<string, bool>? SkipNamespace = null);

    private static readonly Dictionary<string, LanguageProfile> Profiles =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["csharp"] = new(
                FileGlobs: ["*.cs"],
                ImportPatterns: [
                    new Regex(@"^\s*using\s+([\w.]+)\s*;", RegexOptions.Compiled),
                ],
                NamespaceSeparator: ".",
                DefaultNamespaces: name => [$"fuseraft.{name}"]),

            ["python"] = new(
                FileGlobs: ["*.py"],
                ImportPatterns: [
                    new Regex(@"^\s*import\s+([\w.]+)",          RegexOptions.Compiled),
                    new Regex(@"^\s*from\s+([\w.]+)\s+import\s+", RegexOptions.Compiled),
                ],
                NamespaceSeparator: ".",
                DefaultNamespaces: _ => []),

            ["java"] = new(
                FileGlobs: ["*.java"],
                ImportPatterns: [
                    // import com.example.Foo;  /  import static com.example.Foo;
                    // import com.example.*;  — [\w.]+ stops at *, trailing dot is harmless
                    new Regex(@"^\s*import\s+(?:static\s+)?([\w.]+)", RegexOptions.Compiled),
                ],
                NamespaceSeparator: ".",
                DefaultNamespaces: _ => []),

            ["typescript"] = new(
                FileGlobs: ["*.ts", "*.tsx"],
                ImportPatterns: [
                    new Regex(@"from\s+['""]([^'""]+)['""]",                        RegexOptions.Compiled),
                    new Regex(@"^\s*import\s+['""]([^'""]+)['""]",                  RegexOptions.Compiled),
                    new Regex(@"require\s*\(\s*['""]([^'""]+)['""]\s*\)",           RegexOptions.Compiled),
                ],
                NamespaceSeparator: "/",
                DefaultNamespaces: _ => [],
                SkipNamespace: ns => ns.StartsWith('.')),

            ["javascript"] = new(
                FileGlobs: ["*.js", "*.jsx"],
                ImportPatterns: [
                    new Regex(@"from\s+['""]([^'""]+)['""]",                        RegexOptions.Compiled),
                    new Regex(@"^\s*import\s+['""]([^'""]+)['""]",                  RegexOptions.Compiled),
                    new Regex(@"require\s*\(\s*['""]([^'""]+)['""]\s*\)",           RegexOptions.Compiled),
                ],
                NamespaceSeparator: "/",
                DefaultNamespaces: _ => [],
                SkipNamespace: ns => ns.StartsWith('.')),

            ["go"] = new(
                FileGlobs: ["*.go"],
                ImportPatterns: [
                    // import "pkg"  /  import alias "pkg"
                    new Regex(@"^\s*import\s+(?:[\w_]+\s+)?""([^""]+)""", RegexOptions.Compiled),
                    // lines inside an import ( ... ) block
                    new Regex(@"^\s+(?:[\w_]+\s+)?""([^""]+)""",          RegexOptions.Compiled),
                ],
                NamespaceSeparator: "/",
                DefaultNamespaces: _ => []),

            ["rust"] = new(
                FileGlobs: ["*.rs"],
                ImportPatterns: [
                    // use foo::bar::Baz;  /  use foo::bar::{A,B};  /  use foo::bar::*;
                    // ([\w:]+?) stops before { or * leaving clean path)
                    new Regex(@"^\s*use\s+((?:\w+::)*\w+)", RegexOptions.Compiled),
                ],
                NamespaceSeparator: "::",
                DefaultNamespaces: _ => []),

            ["ruby"] = new(
                FileGlobs: ["*.rb"],
                ImportPatterns: [
                    new Regex(@"^\s*require\s+['""]([^'""]+)['""]", RegexOptions.Compiled),
                ],
                NamespaceSeparator: "/",
                DefaultNamespaces: _ => [],
                SkipNamespace: ns => ns.StartsWith('.')),
        };

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Loads the manifest at <paramref name="manifestPath"/> and returns null if the file
    /// does not exist or cannot be parsed.
    /// </summary>
    public static ArchitectureManifest? TryLoadManifest(string manifestPath)
    {
        if (!File.Exists(manifestPath)) return null;

        try
        {
            var yaml = File.ReadAllText(manifestPath);
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(PascalCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            return deserializer.Deserialize<ArchitectureManifest>(yaml);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Scans source files under <paramref name="projectRoot"/> for layer violations
    /// using the language profile declared in <paramref name="manifest"/>.
    /// Unknown <c>Language</c> values fall back to the C# profile.
    /// </summary>
    public static async Task<IReadOnlyList<ArchitectureViolation>> ScanAsync(
        ArchitectureManifest manifest,
        string projectRoot,
        CancellationToken ct = default)
    {
        projectRoot = Path.GetFullPath(projectRoot);

        var profile = Profiles.GetValueOrDefault(manifest.Language) ?? Profiles["csharp"];

        var layerNamespaces = manifest.Layers.ToDictionary(
            l => l.Name,
            l => l.Namespaces.Count > 0 ? l.Namespaces : profile.DefaultNamespaces(l.Name),
            StringComparer.OrdinalIgnoreCase);

        var violations = new List<ArchitectureViolation>();

        var files = profile.FileGlobs
            .SelectMany(glob => Directory.EnumerateFiles(projectRoot, glob, SearchOption.AllDirectories))
            .Where(f => !IsGeneratedPath(f));

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var relPath     = Path.GetRelativePath(projectRoot, file).Replace('\\', '/');
            var sourceLayer = FindLayerForPath(manifest.Layers, relPath);
            if (sourceLayer is null) continue;

            var lines = await File.ReadAllLinesAsync(file, ct);

            for (int i = 0; i < lines.Length; i++)
            {
                foreach (var pattern in profile.ImportPatterns)
                {
                    var match = pattern.Match(lines[i]);
                    if (!match.Success) continue;

                    var ns = match.Groups[1].Value;
                    if (profile.SkipNamespace?.Invoke(ns) == true) continue;

                    var targetLayer = FindLayerForNamespace(layerNamespaces, ns, profile.NamespaceSeparator);
                    if (targetLayer is null) continue;
                    if (string.Equals(targetLayer, sourceLayer.Name, StringComparison.OrdinalIgnoreCase)) continue;

                    if (!sourceLayer.MayDependOn.Contains(targetLayer, StringComparer.OrdinalIgnoreCase))
                    {
                        violations.Add(new ArchitectureViolation
                        {
                            SourceLayer = sourceLayer.Name,
                            TargetLayer = targetLayer,
                            File        = relPath,
                            Line        = i + 1,
                            Namespace   = ns,
                        });
                    }

                    break; // one match per line is enough
                }
            }
        }

        return violations;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static bool IsGeneratedPath(string fullPath)
    {
        var sep = Path.DirectorySeparatorChar;
        return fullPath.Contains($"{sep}obj{sep}",           StringComparison.Ordinal)  // C# build
            || fullPath.Contains($"{sep}bin{sep}",           StringComparison.Ordinal)  // C# build
            || fullPath.Contains($"{sep}__pycache__{sep}",   StringComparison.Ordinal)  // Python
            || fullPath.Contains($"{sep}.venv{sep}",         StringComparison.Ordinal)  // Python venv
            || fullPath.Contains($"{sep}venv{sep}",          StringComparison.Ordinal)  // Python venv
            || fullPath.Contains($"{sep}site-packages{sep}", StringComparison.Ordinal)  // Python packages
            || fullPath.Contains($"{sep}node_modules{sep}",  StringComparison.Ordinal)  // JS/TS
            || fullPath.Contains($"{sep}target{sep}",        StringComparison.Ordinal)  // Rust / Maven
            || fullPath.Contains($"{sep}vendor{sep}",        StringComparison.Ordinal)  // Go / Ruby
            || fullPath.Contains($"{sep}.next{sep}",         StringComparison.Ordinal); // Next.js
    }

    private static ArchitectureLayer? FindLayerForPath(
        IReadOnlyList<ArchitectureLayer> layers,
        string relPath)
    {
        foreach (var layer in layers)
        {
            foreach (var p in layer.Paths)
            {
                var prefix = p.Replace('\\', '/').TrimEnd('/') + '/';
                if (relPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return layer;
            }
        }
        return null;
    }

    private static string? FindLayerForNamespace(
        Dictionary<string, List<string>> layerNamespaces,
        string ns,
        string separator)
    {
        foreach (var (layerName, prefixes) in layerNamespaces)
        {
            foreach (var prefix in prefixes)
            {
                if (ns.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                    || ns.StartsWith(prefix + separator, StringComparison.OrdinalIgnoreCase))
                    return layerName;
            }
        }
        return null;
    }
}
