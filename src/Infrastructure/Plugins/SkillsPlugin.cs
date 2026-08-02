using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Exposes skills from the library as callable tools.
///
/// <para>
/// Skills follow the progressive-disclosure pattern: at session start the REPL
/// injects a catalog of skill names and descriptions into the system prompt so the
/// model knows what is available.  When the model decides to apply a skill it calls
/// <c>load_skill</c> to retrieve the full step-by-step SKILL.md body, then follows
/// those instructions using its other tools.  <c>read_skill_resource</c> is available
/// for skills that ship supplementary reference files (e.g. under <c>references/</c>)
/// alongside their SKILL.md, and <c>run_skill_script</c> for skills that ship
/// executable scripts.
/// </para>
/// </summary>
public sealed class SkillsPlugin
{
    // slug → directory that contains SKILL.md (and any scripts)
    private readonly IReadOnlyDictionary<string, string> _skillDirs;

    public int Count => _skillDirs.Count;

    public IEnumerable<string> Slugs => _skillDirs.Keys;

    public bool HasSkill(string slug) => _skillDirs.ContainsKey(slug);

    public SkillsPlugin(IReadOnlyDictionary<string, string> skillDirs)
    {
        _skillDirs = skillDirs;
    }

    [Description("Load full instructions for a skill by slug.")]
    public async Task<string> LoadSkillAsync(
        [Description("Skill slug, e.g. 'fetch-remote-api'.")] string name,
        CancellationToken cancellationToken = default)
    {
        if (!_skillDirs.TryGetValue(name, out var dir))
        {
            var known = string.Join(", ", _skillDirs.Keys.Take(10));
            return PluginResult.NotFound($"No skill '{name}'. Available: {known}");
        }

        var skillPath = Path.Combine(dir, "SKILL.md");
        if (!File.Exists(skillPath))
            return PluginResult.Error($"SKILL.md missing for '{name}'.");

        try
        {
            return await File.ReadAllTextAsync(skillPath, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PluginResult.Error($"Could not read skill '{name}': {ex.Message}");
        }
    }

    [Description("Read a reference/resource file bundled with a skill, e.g. a file under 'references/'.")]
    public async Task<string> ReadSkillResourceAsync(
        [Description("Skill slug.")] string skill,
        [Description("Resource path relative to the skill directory, e.g. 'references/style-guide.md'.")] string resourcePath,
        CancellationToken cancellationToken = default)
    {
        if (!_skillDirs.TryGetValue(skill, out var dir))
            return PluginResult.NotFound($"No skill '{skill}'.");

        if (string.IsNullOrWhiteSpace(resourcePath))
            return PluginResult.Error("Resource path must not be empty.");

        // Resolve against the skill directory and confirm the result stays inside it —
        // resourcePath comes from the model, so an absolute path or "../" sequence must
        // not be able to escape to arbitrary files on disk.
        var skillRoot = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
        var fullPath  = Path.GetFullPath(Path.Combine(dir, resourcePath));
        if (!fullPath.StartsWith(skillRoot, StringComparison.Ordinal))
            return PluginResult.Error($"'{resourcePath}' is outside the skill directory.");

        if (!File.Exists(fullPath))
            return PluginResult.NotFound($"Resource '{resourcePath}' not found in skill '{skill}'.");

        try
        {
            return await File.ReadAllTextAsync(fullPath, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PluginResult.Error($"Could not read resource '{resourcePath}': {ex.Message}");
        }
    }

    [Description("Run a script bundled with a skill.")]
    public async Task<string> RunSkillScriptAsync(
        [Description("Skill slug.")] string skill,
        [Description("Script filename inside the skill directory, e.g. 'transform.py'.")] string script,
        [Description("Space-separated arguments to pass to the script.")] string args = "",
        CancellationToken cancellationToken = default)
    {
        if (!_skillDirs.TryGetValue(skill, out var dir))
            return PluginResult.NotFound($"No skill '{skill}'.");

        // Resolve against the skill directory and confirm the result stays inside it — same
        // containment check as ReadSkillResourceAsync, since 'script' comes from the model.
        var skillRoot  = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
        var scriptPath = Path.GetFullPath(Path.Combine(dir, script));
        if (!scriptPath.StartsWith(skillRoot, StringComparison.Ordinal))
            return PluginResult.Error($"'{script}' is outside the skill directory.");

        if (!File.Exists(scriptPath))
            return PluginResult.NotFound($"Script '{script}' not found in skill '{skill}'.");

        var ext       = Path.GetExtension(scriptPath).ToLowerInvariant();
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var program   = ext switch
        {
            ".py" => isWindows ? "python" : "python3",
            ".sh" => "bash",
            ".js" => "node",
            _     => null,
        };
        if (program is null)
            return PluginResult.Error($"No runner registered for '{ext}' scripts.");

        var psi = new ProcessStartInfo
        {
            FileName               = program,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        psi.ArgumentList.Add(scriptPath);
        foreach (var a in args.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {program}");

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);
        await Task.WhenAll(stdoutTask, stderrTask);
        await proc.WaitForExitAsync(cancellationToken);

        var stdout = await stdoutTask;
        var stderr  = await stderrTask;
        return string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stdout}\nstderr: {stderr}";
    }
}
