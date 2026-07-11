using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using fuseraft.Core;
using fuseraft.Core.Models;
using fuseraft.Infrastructure.Plugins;

namespace fuseraft.Cli;

/// <summary>
/// Assembles every agent's system prompt: base prompt (file/inline/embedded FUSERAFT.md),
/// spec-anchoring block, folder-orientation block, OS/shell block, .gitignore block, project-root
/// block, context-item summary, brownfield convention block, and test-selector block. Extracted
/// from <see cref="OrchestratorBuilder"/>'s <c>BuildSystemPrompt</c> — a pure prompt-assembly
/// responsibility distinct from orchestrator construction, called exactly once from
/// <see cref="OrchestratorBuilder.BuildAsync"/>.
/// </summary>
internal static class SystemPromptBuilder
{
    public static async Task<OrchestrationConfig> BuildSystemPrompt(
        OrchestrationConfig config,
        string configPath,
        string? sessionId,
        string? specContent,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        // Prepend the base system prompt to every agent's instructions.
        // Source priority: SystemPromptPath > SystemPrompt > embedded FUSERAFT.md.
        var basePrompt = ResolveBasePrompt(config, configPath);
        if (basePrompt is not null)
        {
            config = config with
            {
                Agents = config.Agents
                    .Select(a => a with
                    {
                        Instructions = basePrompt + "\n\n" + a.Instructions.TrimStart()
                    })
                    .ToList()
            };
        }

        // Inject the user-supplied spec into every agent's system prompt so all agents
        // remain anchored to it even after context compaction (spec-anchored SDD).
        if (!string.IsNullOrWhiteSpace(specContent))
        {
            var specBlock =
                "## Project Spec (authoritative)\n\n" +
                "The following specification is the single source of truth for this session. " +
                "All plans, brief.json, and implementation decisions must conform to it.\n\n" +
                specContent.Trim();
            config = config with
            {
                Agents = config.Agents
                    .Select(a => a with
                    {
                        Instructions = a.Instructions.TrimEnd() + "\n\n" + specBlock
                    })
                    .ToList()
            };
        }

        // Orient every agent to the local .fuseraft/ folder layout so they never
        // scan it with list_files to discover what is there — they already know.
        // Each agent only sees artifact paths for the plugins it actually has.
        config = config with
        {
            Agents = config.Agents
                .Select(a =>
                {
                    var artifacts = BuildPluginArtifacts(a.Plugins, config, sessionId);
                    var block = FuseraftPaths.BuildFolderOrientationBlock(sessionId ?? "default", pluginArtifacts: artifacts);
                    return a with { Instructions = a.Instructions.TrimEnd() + "\n\n" + block };
                })
                .ToList()
        };

        // Inject OS and recommended shell so agents never have to guess.
        var osBlock = FuseraftPaths.BuildOsEnvironmentBlock();
        config = config with
        {
            Agents = config.Agents
                .Select(a => a with
                {
                    Instructions = a.Instructions.TrimEnd() + "\n\n" + osBlock
                })
                .ToList()
        };

        // Inject .gitignore so agents know which paths to avoid writing to.
        var gitIgnoreBlock = BuildGitIgnoreBlock();
        if (gitIgnoreBlock is not null)
        {
            config = config with
            {
                Agents = config.Agents
                    .Select(a => a with
                    {
                        Instructions = a.Instructions.TrimEnd() + "\n\n" + gitIgnoreBlock
                    })
                    .ToList()
            };
        }

        // Project root orientation: when a sandbox root is configured, inject a prompt block
        // telling agents the canonical root path and warning against double-nested paths.
        // This is the primary prompt-level defence against the vsl/vsl/… path confusion
        // pattern observed in long sessions.
        if (config.Security?.FileSystemSandboxPath is { Length: > 0 } sbxForBlock)
        {
            var sandboxExpanded = FuseraftPaths.ExpandPath(sbxForBlock);
            var projectRootBlock = BuildProjectRootBlock(sandboxExpanded);
            config = config with
            {
                Agents = config.Agents
                    .Select(a => a with
                    {
                        Instructions = a.Instructions.TrimEnd() + "\n\n" + projectRootBlock
                    })
                    .ToList()
            };
        }

        // Inject context items into every agent's system prompt so agents know what
        // reference material is available without burning a tool call on discovery.
        var contextStore = new fuseraft.Infrastructure.Context.ContextStore();
        var contextSummary = await contextStore.BuildPromptSummaryAsync(cancellationToken);
        if (contextSummary is not null)
        {
            config = config with
            {
                Agents = config.Agents
                    .Select(a => a with
                    {
                        Instructions = a.Instructions.TrimEnd() + "\n\n" + contextSummary
                    })
                    .ToList()
            };
        }

        // Brownfield: when a convention profile exists on disk, inject its contents into
        // every agent's system prompt so agents follow project conventions automatically.
        if (config.Brownfield is { ConventionProfilePath: { } conventionPath }
            && File.Exists(conventionPath))
        {
            try
            {
                var profileJson    = await File.ReadAllTextAsync(conventionPath, cancellationToken);
                var profile        = JsonSerializer.Deserialize<ConventionProfile>(profileJson, OrchestratorBuilder.BrownfieldJsonOpts);
                var conventionBlock = BuildConventionBlock(profile);
                if (conventionBlock is not null)
                {
                    config = config with
                    {
                        Agents = config.Agents
                            .Select(a => a with
                            {
                                Instructions = a.Instructions.TrimEnd() + "\n\n" + conventionBlock
                            })
                            .ToList()
                    };
                }
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger(nameof(OrchestratorBuilder)).LogWarning(
                    "Could not load convention profile from '{Path}': {Message}",
                    conventionPath, ex.Message);
            }
        }

        // Brownfield: when TestSelector is configured, inject the discovery command template into
        // every agent's system prompt so agents run targeted tests without a tool call to find them.
        if (config.TestSelector is { FindRelatedCommand.Length: > 0 } tsCfg)
        {
            var tsBlock = BuildTestSelectorBlock(tsCfg);
            config = config with
            {
                Agents = config.Agents
                    .Select(a => a with
                    {
                        Instructions = a.Instructions.TrimEnd() + "\n\n" + tsBlock
                    })
                    .ToList()
            };
        }

        // Also emit a startup warning when a change envelope is declared without a sandbox —
        // the envelope is enforced by SandboxEnforcementFilter which requires a sandbox root.
        if (config.Security?.ChangeEnvelope is { Count: > 0 }
            && string.IsNullOrEmpty(config.Security.FileSystemSandboxPath))
        {
            loggerFactory.CreateLogger(nameof(OrchestratorBuilder)).LogWarning(
                "Security.ChangeEnvelope is configured but Security.FileSystemSandboxPath is not set. " +
                "The change envelope will not be enforced. Add a FileSystemSandboxPath to enable it.");
        }

        // Warn when FileSystemPermissions is configured without a sandbox root.
        if (config.Security?.FileSystemPermissions is not null
            && string.IsNullOrEmpty(config.Security.FileSystemSandboxPath))
        {
            loggerFactory.CreateLogger(nameof(OrchestratorBuilder)).LogWarning(
                "Security.FileSystemPermissions is configured but Security.FileSystemSandboxPath is not set. " +
                "Filesystem permission globs will not be enforced. Add a FileSystemSandboxPath to enable them.");
        }

        return config;
    }

    // Resolves the base system prompt prepended to every agent.
    // Priority: SystemPromptPath (file) > SystemPrompt (inline) > embedded FUSERAFT.md.
    private static string? ResolveBasePrompt(OrchestrationConfig config, string configPath)
    {
        if (!string.IsNullOrWhiteSpace(config.SystemPromptPath))
        {
            var configDir  = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".";
            var promptPath = Path.IsPathRooted(config.SystemPromptPath)
                ? config.SystemPromptPath
                : Path.GetFullPath(config.SystemPromptPath, configDir);
            return File.ReadAllText(promptPath).Trim();
        }

        if (!string.IsNullOrWhiteSpace(config.SystemPrompt))
            return config.SystemPrompt.Trim();

        // Fall back to the embedded FUSERAFT.md.
        var asm  = typeof(OrchestratorBuilder).Assembly;
        var name = asm.GetManifestResourceNames()
                      .FirstOrDefault(n => n.EndsWith("FUSERAFT.md", StringComparison.OrdinalIgnoreCase));
        if (name is null) return null;

        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    }

    private static string BuildTestSelectorBlock(TestSelectorConfig ts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("TEST SELECTOR (incremental test discovery — use this instead of running the full suite):");
        sb.AppendLine($"  FindRelatedCommand: {ts.FindRelatedCommand}");
        if (!string.IsNullOrWhiteSpace(ts.FullSuiteCommand))
            sb.AppendLine($"  FullSuiteCommand:   {ts.FullSuiteCommand}");
        sb.AppendLine();
        sb.Append("For each file you changed, substitute its path for {file} in FindRelatedCommand to discover related tests, then run those tests. Fall back to FullSuiteCommand when no related tests are found.");
        return sb.ToString();
    }

    private static string BuildProjectRootBlock(string sandboxRoot)
    {
        var dirName = Path.GetFileName(sandboxRoot.TrimEnd(Path.DirectorySeparatorChar));
        var sb = new StringBuilder();
        sb.AppendLine("## Project Root (Sandbox)");
        sb.AppendLine($"Sandbox root: {sandboxRoot}");
        sb.AppendLine("All file paths must be relative to this root or absolute. Never include the project directory name as a prefix in a relative path.");
        sb.AppendLine($"  Correct:  src/module/file.py  or  {dirName}/src/module/file.py (absolute)");
        sb.AppendLine($"  Wrong:    {dirName}/{dirName}/src/module/file.py  ← double-nested, file will not exist");
        sb.Append("Files you have already read this session are cached. If the file is unchanged you will see a hint instead of the full content — use grep_in_file for targeted lookup or pass startLine/maxLines for a specific section.");
        return sb.ToString();
    }

    /// <summary>
    /// Produces artifact path descriptors for the plugins an agent actually has, so the
    /// folder orientation block injected into that agent's system prompt only references
    /// paths it can meaningfully use.
    /// </summary>
    private static IEnumerable<(string Path, string Label)> BuildPluginArtifacts(
        List<string> pluginNames,
        OrchestrationConfig config,
        string? sessionId)
    {
        var sid = sessionId ?? "default";
        foreach (var name in pluginNames)
        {
            if (name.Equals("Changes", StringComparison.OrdinalIgnoreCase))
            {
                if (config.ChangeTracking?.Path is { } changesPath)
                    yield return (changesPath, ChangesPlugin.Label);
            }
            else if (name.Equals("SessionContext", StringComparison.OrdinalIgnoreCase))
            {
                yield return (FuseraftPaths.ExpandSessionId(FuseraftPaths.LocalSessionContext, sid), SessionContextPlugin.Label);
            }
            else if (name.Equals("Chatroom", StringComparison.OrdinalIgnoreCase))
            {
                yield return (FuseraftPaths.ExpandSessionId(config.Chatroom?.Path ?? FuseraftPaths.LocalChatroom, sid), ChatroomPlugin.Label);
            }
            else if (name.Equals("Scratchpad", StringComparison.OrdinalIgnoreCase))
            {
                var scratchPath = sessionId is { Length: > 0 }
                    ? FuseraftPaths.ExpandSessionId(FuseraftPaths.LocalSessionScratchpad, sessionId)
                    : FuseraftPaths.ExpandPath(config.Scratchpad?.BasePath ?? FuseraftPaths.GlobalScratchpad);
                yield return (scratchPath, ScratchpadPlugin.Label);
            }
        }
    }

    private static string? BuildGitIgnoreBlock()
    {
        var path = Path.Combine(Directory.GetCurrentDirectory(), ".gitignore");
        if (!File.Exists(path)) return null;

        const int maxLines = 100;
        var lines     = File.ReadAllLines(path);
        var truncated = lines.Length > maxLines;
        var content   = string.Join('\n', truncated ? lines[..maxLines] : lines);

        var sb = new StringBuilder();
        sb.AppendLine("## .gitignore");
        sb.AppendLine("Avoid writing to paths matched by these patterns. Treat matched paths as non-source (generated, vendored, or sensitive) — read them only when the task explicitly requires it.");
        if (truncated)
            sb.AppendLine($"(truncated to {maxLines} of {lines.Length} lines)");
        sb.AppendLine("```");
        sb.AppendLine(content);
        sb.Append("```");
        return sb.ToString();
    }

    private static string? BuildConventionBlock(ConventionProfile? profile)
    {
        if (profile is null) return null;

        var sb = new StringBuilder();
        sb.AppendLine("PROJECT CONVENTIONS (detected by Archaeologist — follow these in all code you write):");

        if (!string.IsNullOrWhiteSpace(profile.Language))
            sb.AppendLine($"  Language/ecosystem: {profile.Language}");

        if (!string.IsNullOrWhiteSpace(profile.BuildCommand))
            sb.AppendLine($"  Build command: {profile.BuildCommand}");

        if (!string.IsNullOrWhiteSpace(profile.TestCommand))
            sb.AppendLine($"  Test command:  {profile.TestCommand}");

        AppendList(sb, "  Naming:     ", profile.NamingPatterns);
        AppendList(sb, "  Error handling: ", profile.ErrorHandling);
        AppendList(sb, "  Forbidden:  ", profile.ForbiddenPatterns);
        AppendList(sb, "  Tests:      ", profile.TestPatterns);
        AppendList(sb, "  Structure:  ", profile.StructuralNotes);

        var result = sb.ToString().TrimEnd();
        return result.Length > "PROJECT CONVENTIONS (detected by Archaeologist — follow these in all code you write):".Length
            ? result
            : null;
    }

    private static void AppendList(StringBuilder sb, string label, IReadOnlyList<string> items)
    {
        if (items.Count == 0) return;
        foreach (var item in items)
            sb.AppendLine($"{label}{item}");
    }
}
