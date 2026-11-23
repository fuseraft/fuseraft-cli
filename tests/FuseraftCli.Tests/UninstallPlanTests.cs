using fuseraft.Cli.Commands.Uninstall;

namespace FuseraftCli.Tests;

public sealed class UninstallPlanTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"fuseraft-uninstall-test-{Guid.NewGuid():N}");

    public UninstallPlanTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string Dir(params string[] parts)
    {
        var path = Path.Combine([_dir, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    // ── binary ──────────────────────────────────────────────────────────────

    [Fact]
    public void InstalledBinary_IsTheProcessPath_OnlyWhenItIsFuseraft()
    {
        var cli = Path.Combine(_dir, "fuseraft");
        Assert.Equal(cli, UninstallPlan.InstalledBinary(cli));
        Assert.Equal(cli + ".exe", UninstallPlan.InstalledBinary(cli + ".exe"));
        Assert.Null(UninstallPlan.InstalledBinary(Path.Combine(_dir, "dotnet")));
        Assert.Null(UninstallPlan.InstalledBinary(null));
    }

    [Fact]
    public void InstallFiles_OnWindows_IncludesTheUpdaterAndUpdateLeftovers()
    {
        var exe   = Path.Combine(_dir, "fuseraft.exe");
        var files = UninstallPlan.InstallFiles(exe, windows: true);

        Assert.Equal(exe, files[0]);
        Assert.Contains(Path.Combine(_dir, UninstallPlan.UpdaterName), files);
        Assert.Contains(exe + ".pending", files);
        Assert.Contains(exe + ".backup", files);
        Assert.Equal([Path.Combine(_dir, "fuseraft"), Path.Combine(_dir, "fuseraft") + ".new"],
            UninstallPlan.InstallFiles(Path.Combine(_dir, "fuseraft"), windows: false));
    }

    [Fact]
    public void InstallDirs_AreOnlyTheOnesInstallPs1Made()
    {
        var bin = Path.Combine(_dir, "fuseraft", "bin");
        Assert.Equal([bin, Path.Combine(_dir, "fuseraft")], UninstallPlan.InstallDirs(Path.Combine(bin, "fuseraft.exe")));

        // ~/.local/bin and a custom -InstallDir may hold other tools.
        Assert.Empty(UninstallPlan.InstallDirs(Path.Combine(_dir, ".local", "bin", "fuseraft")));
        Assert.Empty(UninstallPlan.InstallDirs(Path.Combine(_dir, "Tools", "fuseraft.exe")));
    }

    // ── PATH ────────────────────────────────────────────────────────────────

    private static string Expand(string s) => s.Replace("%LOCALAPPDATA%", @"C:\Users\u\AppData\Local");

    [Fact]
    public void RemovePathEntry_MatchesAfterExpandingVariables_AndKeepsTheRestAsWritten()
    {
        var (value, removed) = UninstallPlan.RemovePathEntry(
            @"%USERPROFILE%\bin;%LOCALAPPDATA%\fuseraft\bin;C:\Tools", @"C:\Users\u\AppData\Local\fuseraft\bin", Expand);

        Assert.True(removed);
        Assert.Equal(@"%USERPROFILE%\bin;C:\Tools", value);
    }

    [Fact]
    public void RemovePathEntry_IgnoresCaseTrailingSlashesAndQuotes()
    {
        var (value, removed) = UninstallPlan.RemovePathEntry(
            @"C:\A;""c:\users\u\appdata\local\FUSERAFT\bin\"";;C:\B", @"C:\Users\u\AppData\Local\fuseraft\bin", Expand);

        Assert.True(removed);
        Assert.Equal(@"C:\A;C:\B", value);
    }

    [Fact]
    public void RemovePathEntry_LeavesAPathWithoutTheEntryUntouched()
    {
        const string path = @"C:\A;;C:\fuseraft\bin-old";
        Assert.Equal((path, false), UninstallPlan.RemovePathEntry(path, @"C:\fuseraft\bin", Expand));
    }

    // ── purge guard ─────────────────────────────────────────────────────────

    [Fact]
    public void PurgeRefusal_RefusesTheHomeFolderAndAnythingAboveIt()
    {
        var home = Dir("home", "u");
        Assert.Contains("home folder", UninstallPlan.PurgeRefusal(home, home));
        Assert.Contains("home folder", UninstallPlan.PurgeRefusal(Path.GetDirectoryName(home)!, home));
        Assert.Contains("root of a drive", UninstallPlan.PurgeRefusal(Path.GetPathRoot(_dir)!, home));
    }

    [Fact]
    public void PurgeRefusal_RefusesAFolderThatIsntAFuseraftHome()
    {
        var other = Dir("thesis");
        File.WriteAllText(Path.Combine(other, "thesis.docx"), "x");

        Assert.Contains("doesn't look like", UninstallPlan.PurgeRefusal(other, Dir("home")));
    }

    [Fact]
    public void PurgeRefusal_AllowsAFuseraftHome_WhereverItIs()
    {
        var home   = Dir("home");
        var custom = Dir("share", "ai");
        Dir("share", "ai", "repl-sessions");
        var dotted = Dir("home", ".fuseraft");
        File.WriteAllText(Path.Combine(dotted, "anything"), "x");

        Assert.Null(UninstallPlan.PurgeRefusal(custom, home));
        Assert.Null(UninstallPlan.PurgeRefusal(dotted, home));
        Assert.Null(UninstallPlan.PurgeRefusal(Dir("empty"), home));
        Assert.Null(UninstallPlan.PurgeRefusal(Path.Combine(_dir, "missing"), home));
    }

    [Theory]
    [InlineData("root", "scott", true)]
    [InlineData("root", null, false)]
    [InlineData("root", "root", false)]
    [InlineData("scott", "scott", false)]
    public void UnderSudo_IsRootActingForSomeoneElse(string user, string? sudoUser, bool expected) =>
        Assert.Equal(expected, UninstallPlan.UnderSudo(user, sudoUser));

    // ── what's kept ─────────────────────────────────────────────────────────

    [Fact]
    public void ProjectFolders_ListsWhereChatsAndSessionsRan_ThatStillHaveAFuseraftFolder()
    {
        var home    = Dir("home");
        var root    = Dir("home", ".fuseraft");
        var withDir = Dir("proj-a");
        Dir("proj-a", ".fuseraft");
        var without = Dir("proj-b");
        var session = Dir("proj-c");
        Dir("proj-c", ".fuseraft");

        var chats = Dir("home", ".fuseraft", "repl-sessions");
        File.WriteAllText(Path.Combine(chats, "a.json"), $$"""{"SessionId":"a","Cwd":{{Json(withDir)}},"History":[]}""");
        File.WriteAllText(Path.Combine(chats, "b.json"), $$"""{"SessionId":"b","Cwd":{{Json(without)}}}""");
        // A chat started in the home folder points at the fuseraft home itself, which isn't a project.
        File.WriteAllText(Path.Combine(chats, "c.json"), $$"""{"SessionId":"c","Cwd":{{Json(home)}}}""");
        File.WriteAllText(Path.Combine(chats, "broken.json"), "{not json");
        File.WriteAllText(Path.Combine(Dir("home", ".fuseraft", "sessions"), "s.json"),
            $$"""{"SessionId":"s","Task":"t","WorkingDirectory":{{Json(session)}},"Messages":[]}""");

        Assert.Equal([withDir, session], UninstallPlan.ProjectFolders(root));
    }

    [Fact]
    public void ReadTopLevelString_SkipsNestedPropertiesOfTheSameName()
    {
        var file = Path.Combine(_dir, "s.json");
        File.WriteAllText(file, """{"History":[{"Cwd":"/nested"}],"Meta":{"Cwd":"/deeper"},"Cwd":"/top"}""");

        Assert.Equal("/top", UninstallPlan.ReadTopLevelString(file, "Cwd"));
        Assert.Null(UninstallPlan.ReadTopLevelString(file, "Missing"));
    }

    private static string Json(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    [Theory]
    [InlineData("default", "API key (saved before")]
    [InlineData("provider:https://api.x.ai:443", "API key for https://api.x.ai:443")]
    [InlineData("mcp-oauth-Live-OAuth-1fcb1e31", "MCP sign-in for Live-OAuth")]
    [InlineData("something-else", "something-else")]
    public void KeychainLabel_SaysWhatTheEntryHolds(string account, string expected) =>
        Assert.Contains(expected, UninstallPlan.KeychainLabel(account));

    [Fact]
    public void CronLines_AreTheActiveOnesThatRunFuseraft()
    {
        const string crontab = "# * * * * * fuseraft schedule run\n* * * * * /home/u/.local/bin/fuseraft schedule run\n0 3 * * * backup.sh\n";

        Assert.Equal(["* * * * * /home/u/.local/bin/fuseraft schedule run"], UninstallPlan.CronLines(crontab));
    }

    [Fact]
    public void WindowsCleanupScript_WaitsForTheProcess_AndQuotesPaths()
    {
        var script = UninstallPlan.WindowsCleanupScript(
            4242, [@"C:\Users\o'brien\fuseraft.exe"], [@"C:\Temp\.net\fuseraft"], [@"C:\x\fuseraft\bin"]);

        Assert.Contains("Wait-Process -Id 4242", script);
        Assert.Contains(@"'C:\Users\o''brien\fuseraft.exe'", script);
        Assert.Contains(@"'C:\Temp\.net\fuseraft'", script);
        Assert.Contains("-Recurse", script);
        Assert.Contains(@"'C:\x\fuseraft\bin'", script);
    }
}
