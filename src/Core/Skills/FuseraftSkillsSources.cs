using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace fuseraft.Core.Skills;

/// <summary>
/// Shared plumbing for wiring up Microsoft.Agents.AI's Agent Skills feature
/// (<see cref="AgentFileSkillsSource"/>/<see cref="AgentSkillsProvider"/>) — the single
/// implementation both the REPL (<c>fuseraft repl</c>) and orchestration (<c>fuseraft run</c>)
/// use for skill discovery, frontmatter parsing/validation, and progressive disclosure. Nothing
/// in this file parses or validates SKILL.md content; that is entirely Microsoft's
/// <see cref="AgentFileSkillsSource"/>/<see cref="AgentSkillFrontmatter"/>. This file only
/// supplies the two things the library deliberately leaves to the host: where to search, and
/// how to execute a script file on this OS.
/// </summary>
public static class FuseraftSkillsSources
{
    /// <summary>
    /// Priority-ordered directories both the REPL and orchestration scan for skills
    /// (project-native → project cross-client → user-native → user cross-client → built-in).
    /// Non-existent directories are skipped by <see cref="AgentFileSkillsSource"/> itself.
    /// </summary>
    public static string[] GetDefaultSearchDirs()
    {
        var cwd  = Directory.GetCurrentDirectory();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return
        [
            Path.Combine(cwd,  ".fuseraft", "skills"),
            Path.Combine(cwd,  ".agents",   "skills"),
            fuseraft.Core.FuseraftPaths.GlobalSkills,
            Path.Combine(home, ".agents",   "skills"),
            Path.Combine(AppContext.BaseDirectory, "skills"),
        ];
    }

    /// <summary>
    /// Applies the provider options fuseraft needs regardless of caller: fuseraft has no
    /// <c>Microsoft.Agents.AI</c> <c>ToolApprovalAgentOptions</c> pipeline wired up anywhere —
    /// neither the REPL (which drives <see cref="IChatClient"/> directly via
    /// <c>UseFunctionInvocation()</c>, a plain <c>Microsoft.Extensions.AI</c> concept with no
    /// approval semantics) nor orchestration (which has its own, unrelated
    /// <c>IHumanApprovalService</c> for shell commands, never wired to skill tools). Leaving
    /// <see cref="AgentSkillsProvider"/>'s default (approval required for all three tools) would
    /// make <c>load_skill</c>/<c>read_skill_resource</c>/<c>run_skill_script</c> silently
    /// non-functional rather than "safely gated" — a model's attempt to call them would come
    /// back as an unresolved approval request that nothing in fuseraft ever grants.
    /// </summary>
    public static void DisableApproval(AgentSkillsProviderOptions options)
    {
        options.DisableLoadSkillApproval        = true;
        options.DisableReadSkillResourceApproval = true;
        options.DisableRunSkillScriptApproval    = true;
    }

    /// <summary>
    /// Runs a file-based skill script as a local subprocess. Ported from Microsoft's own
    /// reference implementation (<c>samples/02-agents/AgentSkills/SubprocessScriptRunner.cs</c>
    /// in the agent-framework repo, referenced directly from <see cref="AgentSkillsProviderBuilder"/>'s
    /// own XML doc example) rather than reimplemented, since the framework does not ship a
    /// default script runner — <see cref="AgentFileSkillScriptRunner"/> is an intentional
    /// extension point the host must supply.
    /// </summary>
    public static async Task<object?> RunScriptAsync(
        AgentFileSkill skill,
        AgentFileSkillScript script,
        JsonElement? arguments,
        IServiceProvider? serviceProvider,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(script.FullPath))
            return $"Error: Script file not found: {script.FullPath}";

        var extension  = Path.GetExtension(script.FullPath);
        var isWindows  = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        string? interpreter = extension switch
        {
            ".py"  => isWindows ? "python" : "python3",
            ".js"  => "node",
            ".sh"  => "bash",
            ".ps1" => "pwsh",
            _      => null,
        };

        var startInfo = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            WorkingDirectory       = Path.GetDirectoryName(script.FullPath) ?? ".",
        };

        if (interpreter is not null)
        {
            startInfo.FileName = interpreter;
            startInfo.ArgumentList.Add(script.FullPath);
        }
        else
        {
            startInfo.FileName = script.FullPath;
        }

        if (arguments is { ValueKind: JsonValueKind.Array } json)
        {
            foreach (var element in json.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String)
                    throw new InvalidOperationException(
                        $"File-based skill scripts only accept string CLI arguments but received a JSON element of kind '{element.ValueKind}'.");
                startInfo.ArgumentList.Add(element.GetString()!);
            }
        }
        else if (arguments is not null && arguments.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            throw new InvalidOperationException(
                $"Expected a JSON array of CLI arguments but received {arguments.Value.ValueKind}.");
        }

        Process? process = null;
        try
        {
            process = Process.Start(startInfo);
            if (process is null)
                return $"Error: Failed to start process for script '{script.Name}'.";

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask  = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            var output = await outputTask.ConfigureAwait(false);
            var error  = await errorTask.ConfigureAwait(false);

            if (!string.IsNullOrEmpty(error))
                output += $"\nStderr:\n{error}";
            if (process.ExitCode != 0)
                output += $"\nScript exited with code {process.ExitCode}";

            return string.IsNullOrEmpty(output) ? "(no output)" : output.Trim();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            process?.Kill(entireProcessTree: true);
            throw;
        }
        catch (Exception ex)
        {
            return $"Error: Failed to execute script '{script.Name}': {ex.Message}";
        }
        finally
        {
            process?.Dispose();
        }
    }
}
