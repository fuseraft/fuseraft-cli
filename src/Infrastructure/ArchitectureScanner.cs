using System.Text.RegularExpressions;
using fuseraft.Core.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace fuseraft.Infrastructure;

/// <summary>
/// Loads an <see cref="ArchitectureManifest"/> from YAML and scans source files for
/// layer violations: <c>using</c> directives that cross a disallowed layer boundary.
/// </summary>
public static class ArchitectureScanner
{
    private static readonly Regex UsingDirective = new(
        @"^\s*using\s+([\w.]+)\s*;",
        RegexOptions.Compiled);

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
    /// Scans all <c>.cs</c> files under <paramref name="projectRoot"/> and returns every
    /// <see cref="ArchitectureViolation"/> found relative to the given manifest.
    /// </summary>
    public static async Task<IReadOnlyList<ArchitectureViolation>> ScanAsync(
        ArchitectureManifest manifest,
        string projectRoot,
        CancellationToken ct = default)
    {
        projectRoot = Path.GetFullPath(projectRoot);

        // Build effective namespace prefixes per layer; default to "fuseraft.<LayerName>".
        var layerNamespaces = manifest.Layers.ToDictionary(
            l => l.Name,
            l => l.Namespaces.Count > 0 ? l.Namespaces : [$"fuseraft.{l.Name}"],
            StringComparer.OrdinalIgnoreCase);

        var violations = new List<ArchitectureViolation>();

        var files = Directory.EnumerateFiles(projectRoot, "*.cs", SearchOption.AllDirectories)
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
                var match = UsingDirective.Match(lines[i]);
                if (!match.Success) continue;

                var ns          = match.Groups[1].Value;
                var targetLayer = FindLayerForNamespace(layerNamespaces, ns);
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
            }
        }

        return violations;
    }

    // Helpers

    private static bool IsGeneratedPath(string fullPath)
    {
        var sep = Path.DirectorySeparatorChar;
        return fullPath.Contains($"{sep}obj{sep}", StringComparison.Ordinal)
            || fullPath.Contains($"{sep}bin{sep}", StringComparison.Ordinal);
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
        string ns)
    {
        foreach (var (layerName, prefixes) in layerNamespaces)
        {
            foreach (var prefix in prefixes)
            {
                if (ns.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                    || ns.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase))
                    return layerName;
            }
        }
        return null;
    }
}
