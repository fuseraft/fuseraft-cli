using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Spectre.Console;
using Spectre.Console.Cli;
using fuseraft.Core;
using fuseraft.Infrastructure.KeyStore;

namespace fuseraft.Cli.Commands.Uninstall;

public sealed class UninstallSettings : CommandSettings
{
    [CommandOption("--purge")]
    [Description("Also delete your chats, settings, skills and memory (~/.fuseraft) and every fuseraft entry in the OS keychain.")]
    public bool Purge { get; set; }

    [CommandOption("-y|--yes")]
    [Description("Don't ask for confirmation.")]
    public bool Yes { get; set; }

    [CommandOption("--dry-run")]
    [Description("Show what would be removed, and change nothing.")]
    public bool DryRun { get; set; }
}

/// <summary>Removes the installed CLI and, with <c>--purge</c>, everything it keeps on this machine.</summary>
public sealed class UninstallCommand : AsyncCommand<UninstallSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, UninstallSettings settings, CancellationToken cancellationToken)
    {
        var windows = OperatingSystem.IsWindows();
        var underSudo = !windows && UninstallPlan.UnderSudo(Environment.UserName, Environment.GetEnvironmentVariable("SUDO_USER"));
        if (settings.Purge && underSudo)
        {
            AnsiConsole.MarkupLine("[red]✗ Don't run --purge under sudo: it would delete root's fuseraft data and keys, not yours.[/]");
            AnsiConsole.MarkupLine("[dim]Run [bold]fuseraft uninstall --purge[/] as yourself; it tells you the sudo command for the binary if one is needed.[/]");
            return 1;
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var binary  = UninstallPlan.InstalledBinary(Environment.ProcessPath);
        var binDir  = binary is null ? null : Path.GetDirectoryName(binary)!;
        List<string> files = binary is null ? [] : [.. UninstallPlan.InstallFiles(binary, windows).Where(File.Exists)];

        // Only a folder holding nothing but fuseraft comes off PATH or gets deleted: a custom -InstallDir may be shared with other tools.
        var onlyOurs = binDir is not null && Directory.EnumerateFileSystemEntries(binDir)
            .All(e => files.Contains(e, StringComparer.OrdinalIgnoreCase));
        IReadOnlyList<string> installDirs = windows && binary is not null && onlyOurs ? UninstallPlan.InstallDirs(binary) : [];
        var pathEntry   = windows && onlyOurs && binDir is not null && WindowsUserPath.Contains(binDir);
        var needsSudo   = !windows && binDir is not null && !CanWrite(binDir);
        var extractDir  = RuntimeCache.Root;
        var runtimeCache = (binary is not null || settings.Purge) && Directory.Exists(extractDir) ? extractDir : null;

        var root       = FuseraftPaths.GlobalRoot;
        var rootExists = Directory.Exists(root);
        var defaultRoot = Path.Combine(profile, ".fuseraft");
        var otherRoot  = !string.Equals(Path.GetFullPath(root), Path.GetFullPath(defaultRoot), StringComparison.OrdinalIgnoreCase)
                         && Directory.Exists(defaultRoot) ? defaultRoot : null;
        var refusal    = settings.Purge && rootExists ? UninstallPlan.PurgeRefusal(PurgeTarget(root), profile) : null;
        IReadOnlyList<string> projects = rootExists ? UninstallPlan.ProjectFolders(root) : [];
        var schedules  = Directory.Exists(FuseraftPaths.GlobalSchedule)
            ? Directory.EnumerateFiles(FuseraftPaths.GlobalSchedule, "*.y*ml").Count()
            : 0;

        var store    = ApiKeyStoreFactory.Create();
        var (accounts, keychainError) = store.IsAvailable && !underSudo ? await ListAccountsAsync(store) : ([], null);
        var temps    = TempItems();
        IReadOnlyList<string> cron  = windows ? [] : CronLines();
        IReadOnlyList<string> units = windows ? [] : SystemdUnits(profile);
        var running  = OtherFuseraftProcesses();

        // ── The plan ─────────────────────────────────────────────────────────────
        var remove = new Grid().AddColumn(new GridColumn().NoWrap().PadLeft(2).PadRight(2)).AddColumn();
        void Row(string label, string value) => remove.AddRow(new Markup($"• {label}"), new Markup($"[dim]{value}[/]"));

        if (binary is null)
            Row("CLI", "none: this is a development build run through dotnet");
        else
        {
            Row("CLI", Markup.Escape(Tilde(binary)) + (needsSudo ? " [yellow](needs sudo)[/]" : ""));
            foreach (var f in files.Skip(1)) Row(Markup.Escape(Path.GetFileName(f)), Markup.Escape(Tilde(f)));
            if (pathEntry) Row("PATH entry", Markup.Escape(binDir!));
        }
        if (runtimeCache is not null)
            Row("runtime cache", $"{Markup.Escape(Tilde(runtimeCache))} ({Size(runtimeCache)}, native libraries unpacked by each version)");

        if (settings.Purge)
        {
            if (rootExists) Row("your data", $"{Markup.Escape(Tilde(root))} ({DescribeHome(root)})");
            if (!store.IsAvailable || keychainError is not null)
                Row("keychain", $"[yellow]can't check it: {Markup.Escape(keychainError ?? store.StoreName)}[/]");
            else if (accounts.Count > 0)
                Row($"keychain ({accounts.Count})", Markup.Escape(string.Join('\n', accounts.Select(UninstallPlan.KeychainLabel))) +
                    $"\n[italic]{Markup.Escape(store.StoreName)}[/]");
            if (temps.Count > 0) Row("temp files", Markup.Escape(string.Join('\n', temps.Select(Tilde))));
        }

        AnsiConsole.MarkupLine(settings.DryRun ? "Would remove:" : "This removes:");
        AnsiConsole.Write(remove);

        var kept = new List<string>();
        if (!settings.Purge && !underSudo)
        {
            if (rootExists) kept.Add($"your chats, settings and skills in {Tilde(root)} ({DescribeHome(root)})");
            if (accounts.Count > 0) kept.Add($"{accounts.Count} keychain entr{(accounts.Count == 1 ? "y" : "ies")} (API keys, MCP sign-ins)");
        }
        if (otherRoot is not null) kept.Add($"{Tilde(otherRoot)}, because FUSERAFT_HOME points somewhere else");
        foreach (var p in projects) kept.Add($"{Tilde(Path.Combine(p, ".fuseraft"))}: project files you may have written and committed");
        foreach (var c in cron) kept.Add($"your crontab line: {c}");
        foreach (var u in units) kept.Add($"{Tilde(u)}: a systemd unit that runs fuseraft");
        if (schedules > 0 && cron.Count == 0 && units.Count == 0)
            kept.Add($"whatever runs `fuseraft schedule run` for your {schedules} scheduled job{(schedules == 1 ? "" : "s")} (cron, a systemd timer, Task Scheduler)");

        if (kept.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("Not removed:");
            var keptGrid = new Grid().AddColumn(new GridColumn().NoWrap().PadLeft(2).PadRight(2)).AddColumn();
            foreach (var k in kept) keptGrid.AddRow(new Markup("•"), new Markup($"[dim]{Markup.Escape(k)}[/]"));
            AnsiConsole.Write(keptGrid);
            if (!settings.Purge && (rootExists || accounts.Count > 0))
                AnsiConsole.MarkupLine("  Add [bold]--purge[/] to remove your data and keychain entries too.");
        }
        AnsiConsole.WriteLine();

        // ── Blockers ─────────────────────────────────────────────────────────────
        if (refusal is not null)
        {
            AnsiConsole.MarkupLine($"[red]✗ Not purging: {Markup.Escape(refusal)}[/]");
            AnsiConsole.MarkupLine("[dim]Check FUSERAFT_HOME, or delete what you want gone yourself.[/]");
            return 1;
        }
        if (binary is null && !settings.Purge)
        {
            AnsiConsole.MarkupLine("[yellow]Nothing to remove without --purge.[/]");
            return 1;
        }
        // Windows can't delete a running exe, and a purge would be refilled by anything still running.
        if (running.Count > 0 && (settings.Purge || (windows && binary is not null)))
        {
            AnsiConsole.MarkupLine("[red]✗ fuseraft is still running:[/]");
            foreach (var r in running) AnsiConsole.MarkupLine($"  [dim]• {Markup.Escape(r)}[/]");
            AnsiConsole.MarkupLine("[dim]Close your fuseraft chats (including VS Code's) and stop any `fuseraft serve`, then run this again.[/]");
            return 1;
        }
        if (running.Count > 0)
            AnsiConsole.MarkupLine($"[yellow]Note: {running.Count} other fuseraft process{(running.Count == 1 ? " keeps" : "es keep")} running until closed.[/]");

        if (settings.DryRun)
        {
            AnsiConsole.MarkupLine("[dim]Dry run: nothing was changed.[/]");
            return 0;
        }

        if (!settings.Yes)
        {
            if (Console.IsInputRedirected)
            {
                AnsiConsole.MarkupLine("[red]✗ Can't ask for confirmation here. Pass --yes to uninstall.[/]");
                return 1;
            }
            if (!AnsiConsole.Confirm(settings.Purge ? "Remove fuseraft and all of its data?" : "Remove fuseraft?", defaultValue: false))
            {
                AnsiConsole.MarkupLine("[dim]Nothing was changed.[/]");
                return 1;
            }
        }

        // ── Do it: data first, so a binary that needs sudo doesn't stop the rest ──
        var failed = false;

        if (settings.Purge)
        {
            foreach (var a in accounts)
            {
                try { await store.DeleteAsync(a); } catch { /* the re-list below reports it */ }
            }
            var (left, recheckError) = accounts.Count > 0 ? await ListAccountsAsync(store) : ([], null);
            if (left.Count > 0 || recheckError is not null)
            {
                failed = true;
                AnsiConsole.MarkupLine(left.Count > 0
                    ? $"[red]✗ {left.Count} keychain entr{(left.Count == 1 ? "y is" : "ies are")} still there.[/] [dim]Remove {(left.Count == 1 ? "it" : "them")} with: {Markup.Escape(ManualKeychainCommand())}[/]"
                    : $"[red]✗ Couldn't check the keychain entries were removed:[/] [dim]{Markup.Escape(recheckError!)}. Check with: {Markup.Escape(ManualKeychainCommand())}[/]");
            }
            else if (keychainError is not null)
            {
                failed = true;
                AnsiConsole.MarkupLine($"[yellow]! Couldn't check the keychain, so any fuseraft entries in it are still there.[/] [dim]Remove them with: {Markup.Escape(ManualKeychainCommand())}[/]");
            }
            else if (accounts.Count > 0)
                AnsiConsole.MarkupLine($"[green]✓[/] Removed {accounts.Count} keychain entr{(accounts.Count == 1 ? "y" : "ies")}");

            if (rootExists)
            {
                if (TryDeleteHome(root, out var error))
                    AnsiConsole.MarkupLine($"[green]✓[/] Removed {Markup.Escape(Tilde(root))}");
                else
                {
                    failed = true;
                    AnsiConsole.MarkupLine($"[red]✗ Couldn't remove all of {Markup.Escape(root)}:[/] [dim]{Markup.Escape(error)}[/]");
                }
            }

            foreach (var t in temps)
            {
                try
                {
                    if (Directory.Exists(t)) Directory.Delete(t, recursive: true);
                    else File.Delete(t);
                }
                catch { AnsiConsole.MarkupLine($"[dim]Left {Markup.Escape(t)} (it's in use or not yours).[/]"); }
            }
        }

        if (runtimeCache is not null && !windows)
        {
            try
            {
                Directory.Delete(runtimeCache, recursive: true);
                AnsiConsole.MarkupLine($"[green]✓[/] Removed {Markup.Escape(Tilde(runtimeCache))}");
            }
            catch (Exception ex)
            {
                failed = true;
                AnsiConsole.MarkupLine($"[red]✗ Couldn't remove {Markup.Escape(Tilde(runtimeCache))}:[/] [dim]{Markup.Escape(ex.Message)}[/]");
            }
        }

        if (binary is not null)
            failed |= !(windows ? RemoveOnWindows(binary, files, installDirs, pathEntry, runtimeCache) : RemoveOnUnix(files));
        else if (windows && runtimeCache is not null)
            failed |= !ScheduleWindowsCleanup([], [runtimeCache], [], what: Tilde(runtimeCache));

        // ── What's left for the user ─────────────────────────────────────────────
        var another = OtherBinariesOnPath(binary);
        foreach (var b in another)
            AnsiConsole.MarkupLine($"[yellow]Another fuseraft is on your PATH:[/] {Markup.Escape(b)} [dim](run [bold]{Markup.Escape(b)} uninstall[/] to remove it too)[/]");
        if (VsCodeExtensionInstalled(profile))
            AnsiConsole.MarkupLine("[dim]The VS Code extension is still installed: remove it from the Extensions view, or run [bold]code --uninstall-extension fuseraft.fuseraft[/].[/]");

        return failed ? 1 : 0;
    }

    // A home that is a symlink is purged at its target, or deleting it would only remove the link.
    private static string PurgeTarget(string root)
    {
        try { return new DirectoryInfo(root).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? root; }
        catch { return root; }
    }

    private static bool TryDeleteHome(string root, out string error)
    {
        error = string.Empty;
        try
        {
            var target = PurgeTarget(root);
            DeleteTree(target);
            if (!string.Equals(target, root, StringComparison.Ordinal) && Path.Exists(root)) Directory.Delete(root);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void DeleteTree(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        {
            // Git checkouts under skills/ have read-only files, which Windows won't delete.
            var all = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var f in Directory.EnumerateFiles(dir, "*", all)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(dir, recursive: true);
        }
    }

    private static bool RemoveOnUnix(IReadOnlyList<string> files)
    {
        var left = new List<string>();
        foreach (var f in files)
        {
            try { File.Delete(f); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { left.Add(f); }
        }
        if (left.Count == 0)
        {
            AnsiConsole.MarkupLine($"[green]✓[/] Removed {Markup.Escape(Tilde(files[0]))}");
            return true;
        }
        AnsiConsole.MarkupLine($"[red]✗ You don't have permission to remove {Markup.Escape(left[0])}.[/] [dim]Run: sudo rm {Markup.Escape(string.Join(' ', left.Select(Quote)))}[/]");
        return false;
    }

    private static bool RemoveOnWindows(string binary, IReadOnlyList<string> files, IReadOnlyList<string> dirs, bool pathEntry, string? runtimeCache)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var ok = true;

        if (pathEntry)
        {
            try
            {
                if (WindowsUserPath.Remove(Path.GetDirectoryName(binary)!))
                    AnsiConsole.MarkupLine("[green]✓[/] Removed fuseraft from your PATH [dim](terminals open now keep the old PATH until reopened)[/]");
            }
            catch (Exception ex)
            {
                ok = false;
                AnsiConsole.MarkupLine($"[red]✗ Couldn't edit your PATH:[/] [dim]{Markup.Escape(ex.Message)}. Remove {Markup.Escape(Path.GetDirectoryName(binary)!)} from it in System Properties → Environment Variables.[/]");
            }
        }

        // The running exe, and anything else still locked, goes once this process exits.
        var later = new List<string> { binary };
        foreach (var f in files.Skip(1))
        {
            try { File.Delete(f); }
            catch { later.Add(f); }
        }

        return ScheduleWindowsCleanup(later, runtimeCache is null ? [] : [runtimeCache], dirs, what: Path.GetFileName(binary)) && ok;
    }

    private static bool ScheduleWindowsCleanup(IReadOnlyList<string> files, IReadOnlyList<string> trees, IReadOnlyList<string> dirs, string what)
    {
        var script = UninstallPlan.WindowsCleanupScript(Environment.ProcessId, files, trees, dirs);
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = "powershell.exe",
                Arguments       = "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " +
                                  Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
                UseShellExecute = false,
                CreateNoWindow  = true,
            });
            AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(what)} is removed a few seconds after this exits");
            return true;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ Couldn't schedule removing {Markup.Escape(what)}:[/] [dim]{Markup.Escape(ex.Message)}. Delete it once this exits.[/]");
            return false;
        }
    }

    private static async Task<(IReadOnlyList<string> Accounts, string? Error)> ListAccountsAsync(IApiKeyStore store)
    {
        try { return (await store.ListAccountsAsync(), null); }
        catch (Exception ex) { return ([], ex.Message); }
    }

    private static string ManualKeychainCommand() =>
        OperatingSystem.IsWindows() ? "Credential Manager → Windows Credentials → entries starting fuseraft-cli/"
        : OperatingSystem.IsMacOS() ? "security delete-generic-password -s fuseraft-cli (repeat until it says none found)"
        : "secret-tool clear service fuseraft-cli";

    private static string DescribeHome(string root)
    {
        int Count(string sub) =>
            Directory.Exists(Path.Combine(root, sub)) ? Directory.EnumerateFiles(Path.Combine(root, sub), "*.json").Count() : 0;

        var chats = Count("repl-sessions") + Count("sessions");
        return chats > 0 ? $"{chats} chat{(chats == 1 ? "" : "s")}, {Size(root)}" : Size(root);
    }

    private static string Size(string dir) => RuntimeCache.FormatBytes(RuntimeCache.Size(dir));

    private static List<string> TempItems()
    {
        var temp  = Path.GetTempPath();
        var items = new List<string>();
        if (Directory.Exists(FuseraftPaths.SystemTempRoot)) items.Add(FuseraftPaths.SystemTempRoot);
        foreach (var pattern in new[] { "fuseraft-draft-*.md", "fuseraft-run-*.txt", "fuseraft-migrate-*.yaml" })
        {
            try { items.AddRange(Directory.EnumerateFiles(temp, pattern)); }
            catch { /* unreadable temp dir */ }
        }
        return items;
    }

    private static IReadOnlyList<string> CronLines()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("crontab", "-l")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
            });
            if (p is null) return [];
            var stdout = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } return []; }
            return p.ExitCode == 0 ? UninstallPlan.CronLines(stdout.Result) : [];
        }
        catch { return []; }
    }

    private static IReadOnlyList<string> SystemdUnits(string profile)
    {
        var dir = Path.Combine(profile, ".config", "systemd", "user");
        if (!Directory.Exists(dir)) return [];
        try
        {
            return Directory.EnumerateFiles(dir)
                .Where(f => f.EndsWith(".service", StringComparison.Ordinal) || f.EndsWith(".timer", StringComparison.Ordinal))
                .Where(f => File.ReadAllText(f).Contains("fuseraft", StringComparison.Ordinal))
                .ToList();
        }
        catch { return []; }
    }

    private static IReadOnlyList<string> OtherFuseraftProcesses()
    {
        var result = new List<string>();
        foreach (var p in Process.GetProcessesByName("fuseraft"))
        {
            using (p)
            {
                if (p.Id == Environment.ProcessId) continue;
                var cmd = string.Empty;
                try
                {
                    if (OperatingSystem.IsLinux())
                    {
                        var argv = File.ReadAllText($"/proc/{p.Id}/cmdline").Split('\0', StringSplitOptions.RemoveEmptyEntries);
                        if (argv.Length > 0) cmd = string.Join(' ', [Path.GetFileName(argv[0]), .. argv[1..]]);
                    }
                }
                catch { /* gone, or not ours to read */ }
                if (cmd.Length > 80) cmd = cmd[..77] + "…";
                result.Add(cmd.Length > 0 ? $"pid {p.Id}: {cmd}" : $"pid {p.Id}");
            }
        }
        return result;
    }

    private static IReadOnlyList<string> OtherBinariesOnPath(string? removed)
    {
        var name = OperatingSystem.IsWindows() ? "fuseraft.exe" : "fuseraft";
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => { try { return Path.GetFullPath(Path.Combine(d.Trim().Trim('"'), name)); } catch { return null; } })
            .OfType<string>()
            .Where(f => File.Exists(f) && !string.Equals(f, removed, StringComparison.OrdinalIgnoreCase))
            .Distinct(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool VsCodeExtensionInstalled(string profile)
    {
        foreach (var host in new[] { ".vscode", ".vscode-insiders", ".vscode-oss", ".cursor", ".windsurf" })
        {
            var dir = Path.Combine(profile, host, "extensions");
            try
            {
                if (Directory.Exists(dir) && Directory.EnumerateDirectories(dir, "fuseraft.fuseraft-*").Any()) return true;
            }
            catch { /* unreadable */ }
        }
        return false;
    }

    private static string Tilde(string path)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.StartsWith(profile + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? "~" + path[profile.Length..] : path;
    }

    private static bool CanWrite(string dir) => access(dir, 2) == 0;

    private static string Quote(string s) => s.Contains(' ') || s.Contains('\'') ? "'" + s.Replace("'", "'\\''") + "'" : s;

    [DllImport("libc", SetLastError = true)]
    private static extern int access(string path, int mode);
}
