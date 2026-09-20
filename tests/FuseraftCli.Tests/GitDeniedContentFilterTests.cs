using fuseraft.Core.Models.Config;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// Pure tests for <see cref="GitDeniedContentFilter"/>: patch text in, patch text out. The header
/// shapes here are the awkward ones — renames, binary payloads, quoted non-ASCII paths, spaces,
/// combined diffs, and a following commit's header that must survive.
/// </summary>
public sealed class GitDeniedContentFilterTests
{
    private const string Top = "/repo";

    private static string Filter(string patch, params string[] extraDeny)
    {
        var deny = DefaultSecurityPolicy.MergeFileSystemDeny(new FileSystemPermissions { Deny = [.. extraDeny] });
        return GitDeniedContentFilter.HideDeniedSections(
            patch, Top, FileSystemSandbox.BuildDenyMatcher(deny), Top, out _);
    }

    private static IReadOnlyList<string> Hidden(string patch)
    {
        GitDeniedContentFilter.HideDeniedSections(
            patch, Top, FileSystemSandbox.BuildDenyMatcher(DefaultSecurityPolicy.MergeFileSystemDeny(null)), Top, out var hidden);
        return hidden;
    }

    private const string Readme = """
        diff --git a/README.md b/README.md
        index 1111111..2222222 100644
        --- a/README.md
        +++ b/README.md
        @@ -1 +1,2 @@
         hello
        +visible-readme-line
        """;

    private const string EnvSection = """
        diff --git a/backend/.env b/backend/.env
        index 3333333..4444444 100644
        --- a/backend/.env
        +++ b/backend/.env
        @@ -1 +1 @@
        -KEY=old-secret-value
        +KEY=new-secret-value
        """;

    private const string App = """
        diff --git a/src/app.py b/src/app.py
        index 5555555..6666666 100644
        --- a/src/app.py
        +++ b/src/app.py
        @@ -1 +1 @@
        -print('a')
        +print('visible-app-line')
        """;

    [Fact]
    public void OnlyTheDeniedFilesSectionIsHidden_TheRestIsUntouched()
    {
        var result = Filter($"{Readme}\n{EnvSection}\n{App}\n");

        Assert.DoesNotContain("old-secret-value", result);
        Assert.DoesNotContain("new-secret-value", result);
        Assert.Contains("+visible-readme-line", result);
        Assert.Contains("+print('visible-app-line')", result);
    }

    [Fact]
    public void TheDeniedSectionKeepsItsHeader_AndSaysWhyItsBodyIsGone()
    {
        var result = Filter($"{Readme}\n{EnvSection}\n{App}\n");

        Assert.Contains("diff --git a/backend/.env b/backend/.env", result);
        Assert.Contains("[content hidden: 'backend/.env' matches a FileSystem deny rule]", result);
        Assert.DoesNotContain("@@ -1 +1 @@\n-KEY", result);
    }

    [Fact]
    public void NonDeniedSections_AreByteForByteUnchanged()
    {
        var result = Filter($"{Readme}\n{EnvSection}\n{App}\n");

        Assert.Contains(Readme.ReplaceLineEndings("\n"), result);
        Assert.Contains(App.ReplaceLineEndings("\n"), result);
    }

    [Fact]
    public void ReportsWhichPathsWereHidden() =>
        Assert.Equal(["backend/.env"], Hidden($"{Readme}\n{EnvSection}\n{App}\n"));

    [Fact]
    public void ACommitHeaderAfterADeniedSection_IsNotSwallowedWithIt()
    {
        var log = """
            commit 1111111111111111111111111111111111111111
            Author: T <t@e.x>
            Date:   Mon Jan 1 00:00:00 2024 +0000

                add env

            diff --git a/.env b/.env
            new file mode 100644
            index 0000000..1111111
            --- /dev/null
            +++ b/.env
            @@ -0,0 +1 @@
            +SECRET=abc-secret

            commit 2222222222222222222222222222222222222222
            Author: T <t@e.x>
            Date:   Mon Jan 1 00:00:01 2024 +0000

                change readme

            diff --git a/README.md b/README.md
            index 1..2 100644
            --- a/README.md
            +++ b/README.md
            @@ -1 +1 @@
            +visible-line
            """.ReplaceLineEndings("\n");

        var result = Filter(log);

        Assert.DoesNotContain("abc-secret", result);
        Assert.Contains("commit 2222222222222222222222222222222222222222", result);
        Assert.Contains("    change readme", result);
        Assert.Contains("    add env", result);
        Assert.Contains("+visible-line", result);
    }

    [Fact]
    public void ANewlyAddedDeniedFile_IsHidden_IncludingItsNewFileModeLine()
    {
        var patch = """
            diff --git a/.env b/.env
            new file mode 100644
            index 0000000..abcdefg
            --- /dev/null
            +++ b/.env
            @@ -0,0 +1,2 @@
            +A=first-secret
            +B=second-secret
            \ No newline at end of file
            """;

        var result = Filter(patch);

        Assert.DoesNotContain("first-secret", result);
        Assert.DoesNotContain("second-secret", result);
        Assert.DoesNotContain("No newline", result);
    }

    [Fact]
    public void ADeletedDeniedFile_IsHidden()
    {
        var patch = """
            diff --git a/.env b/.env
            deleted file mode 100644
            index abcdefg..0000000
            --- a/.env
            +++ /dev/null
            @@ -1 +0,0 @@
            -DELETED=gone-secret
            """;

        Assert.DoesNotContain("gone-secret", Filter(patch));
    }

    [Theory]
    [InlineData("README.md", ".env")]     // renamed TO a protected name
    [InlineData(".env", "old.txt")]       // renamed FROM one
    public void ARenameInvolvingAProtectedName_IsHidden(string from, string to)
    {
        var patch = $"""
            diff --git a/{from} b/{to}
            similarity index 90%
            rename from {from}
            rename to {to}
            index 1111111..2222222 100644
            --- a/{from}
            +++ b/{to}
            @@ -1 +1 @@
            -rename-old-secret
            +rename-new-secret
            """;

        var result = Filter(patch);

        Assert.DoesNotContain("rename-old-secret", result);
        Assert.DoesNotContain("rename-new-secret", result);
    }

    [Fact]
    public void ACopyToAProtectedName_IsHidden()
    {
        var patch = """
            diff --git a/template.txt b/.env
            similarity index 100%
            copy from template.txt
            copy to .env
            """;

        Assert.Equal([".env"], Hidden(patch).Distinct().ToList());
    }

    [Fact]
    public void AModeOnlyChangeToAProtectedFile_IsHidden()
    {
        var patch = """
            diff --git a/deploy/id_rsa b/deploy/id_rsa
            old mode 100644
            new mode 100755
            """;

        Assert.Equal(["deploy/id_rsa"], Hidden(patch).Distinct().ToList());
    }

    [Fact]
    public void ABinaryDiffOfAProtectedFile_IsHidden_AndItsPayloadDoesNotLeakIntoTheNextSection()
    {
        var patch = $"""
            diff --git a/.env b/.env
            index 1111111..2222222 100644
            GIT binary patch
            literal 12
            zcmXXbase85payload

            literal 0
            HcmV?d00001

            {Readme}
            """;

        var result = Filter(patch);

        Assert.DoesNotContain("base85payload", result);
        Assert.Contains("+visible-readme-line", result);   // the next section is intact
    }

    [Fact]
    public void ABinaryFilesDifferLine_ForAProtectedFile_IsHidden()
    {
        var patch = """
            diff --git a/.env b/.env
            index 1111111..2222222 100644
            Binary files a/.env and b/.env differ
            """;

        Assert.Equal([".env"], Hidden(patch).Distinct().ToList());
    }

    [Fact]
    public void AQuotedNonAsciiPath_IsUnquotedBeforeItIsMatched()
    {
        // git prints "d\303\251/.env" for d?/.env when core.quotePath is on (the default).
        var patch = """
            diff --git "a/d\303\251/.env" "b/d\303\251/.env"
            index 1111111..2222222 100644
            --- "a/d\303\251/.env"
            +++ "b/d\303\251/.env"
            @@ -1 +1 @@
            -quoted-old-secret
            +quoted-new-secret
            """;

        var result = Filter(patch);

        Assert.DoesNotContain("quoted-old-secret", result);
        Assert.DoesNotContain("quoted-new-secret", result);
    }

    [Fact]
    public void APathContainingSpaces_IsStillMatched()
    {
        var patch = """
            diff --git a/my project/.env b/my project/.env
            index 1111111..2222222 100644
            --- a/my project/.env	
            +++ b/my project/.env	
            @@ -1 +1 @@
            -spaced-old-secret
            +spaced-new-secret
            """;

        var result = Filter(patch);

        Assert.DoesNotContain("spaced-old-secret", result);
        Assert.DoesNotContain("spaced-new-secret", result);
    }

    [Fact]
    public void ACombinedDiff_OfAProtectedFile_IsHidden()
    {
        var patch = """
            diff --cc backend/.env
            index 111,222..333
            --- a/backend/.env
            +++ b/backend/.env
            @@@ -1,1 -1,1 +1,1 @@@
            - ours-secret
             -theirs-secret
            ++merged-secret
            """;

        var result = Filter(patch);

        Assert.DoesNotContain("merged-secret", result);
        Assert.DoesNotContain("ours-secret", result);
    }

    [Theory]
    [InlineData("diff --git .env .env")]                       // --no-prefix
    [InlineData("diff --git i/.env w/.env")]                   // diff.mnemonicPrefix
    [InlineData("diff --git c/backend/.env w/backend/.env")]
    public void UnusualPrefixConventions_AreStillMatched(string header)
    {
        var patch = $"""
            {header}
            index 1111111..2222222 100644
            @@ -1 +1 @@
            -prefix-secret
            """;

        Assert.DoesNotContain("prefix-secret", Filter(patch));
    }

    [Theory]
    [InlineData(".env")]
    [InlineData("backend/.env")]
    [InlineData("apps/web/.env.local")]
    [InlineData(".env.production")]
    [InlineData("deploy/id_rsa")]
    [InlineData("deploy/id_ed25519")]
    [InlineData("home/u/.aws/credentials")]
    [InlineData(".netrc")]
    [InlineData("tools/.pgpass")]
    [InlineData("x/.git-credentials")]
    public void EveryProtectedNameIsHidden_AtAnyDepth(string path)
    {
        var patch = $"""
            diff --git a/{path} b/{path}
            index 1111111..2222222 100644
            --- a/{path}
            +++ b/{path}
            @@ -1 +1 @@
            -protected-old
            +protected-new
            """;

        var result = Filter(patch);

        Assert.DoesNotContain("protected-old", result);
        Assert.DoesNotContain("protected-new", result);
        Assert.Contains($"[content hidden: '{path}'", result);
    }

    [Theory]
    [InlineData("deploy/id_rsa.pub")]
    [InlineData("deploy/id_ed25519.pub")]
    [InlineData(".envrc")]
    [InlineData("docs/env.md")]
    [InlineData("docs/credentials.md")]
    [InlineData(".aws/config")]
    [InlineData("src/environment.py")]
    public void PublicKeysAndLookalikes_AreNotHidden(string path)
    {
        var patch = $"""
            diff --git a/{path} b/{path}
            index 1111111..2222222 100644
            --- a/{path}
            +++ b/{path}
            @@ -1 +1 @@
            -visible-old
            +visible-new
            """;

        var result = Filter(patch);

        Assert.Contains("+visible-new", result);
        Assert.DoesNotContain("content hidden", result);
    }

    [Fact]
    public void AConfiguredDenyGlob_IsHonouredToo()
    {
        var patch = """
            diff --git a/secrets/api.txt b/secrets/api.txt
            index 1111111..2222222 100644
            --- a/secrets/api.txt
            +++ b/secrets/api.txt
            @@ -1 +1 @@
            -configured-old
            +configured-new
            """;

        Assert.DoesNotContain("configured-new", Filter(patch, "secrets/**"));
    }

    [Fact]
    public void AnAddedLineThatLooksLikeADiffHeader_DoesNotHideOrTruncateAnythingElse()
    {
        // README documents git internals; its added lines include text that resembles a header.
        var patch = """
            diff --git a/README.md b/README.md
            index 1111111..2222222 100644
            --- a/README.md
            +++ b/README.md
            @@ -1 +1,3 @@
             intro
            +diff --git a/.env b/.env
            +still-in-readme-line
            """;

        var result = Filter($"{patch}\n{App}\n");

        Assert.Contains("+diff --git a/.env b/.env", result);
        Assert.Contains("+still-in-readme-line", result);
        Assert.Contains("+print('visible-app-line')", result);
        Assert.DoesNotContain("content hidden", result);
    }

    [Fact]
    public void OutputWithoutAPatch_IsReturnedUnchanged()
    {
        const string text = "commit abc123\nAuthor: x\n\n    just a message mentioning .env\n";
        var deny = FileSystemSandbox.BuildDenyMatcher(DefaultSecurityPolicy.MergeFileSystemDeny(null));

        var result = GitDeniedContentFilter.HideDeniedSections(text, Top, deny, Top, out var hidden);

        Assert.Same(text, result);
        Assert.Empty(hidden);
    }

    [Fact]
    public void WithNoMatcher_NothingIsHidden()
    {
        var patch = $"{EnvSection}\n";

        var result = GitDeniedContentFilter.HideDeniedSections(patch, Top, null, Top, out var hidden);

        Assert.Same(patch, result);
        Assert.Empty(hidden);
    }

    [Fact]
    public void ATrailingNewline_IsPreserved()
    {
        var result = Filter($"{EnvSection}\n");

        Assert.EndsWith("\n", result);
    }

    [Fact]
    public void CrLfLinesElsewhere_KeepTheirCarriageReturns()
    {
        var patch = "diff --git a/README.md b/README.md\r\nindex 1..2 100644\r\n--- a/README.md\r\n+++ b/README.md\r\n@@ -1 +1 @@\r\n+windows-line\r\n";

        Assert.Equal(patch, Filter(patch));
    }

    [Theory]
    [InlineData("diff --git")]
    [InlineData("diff --git ")]
    [InlineData("diff --git a/x")]
    [InlineData("diff --git \"unterminated")]
    [InlineData("diff --cc")]
    [InlineData("diff --git a/\0bad b/\0bad")]
    public void MalformedHeaders_DoNotThrow(string header) =>
        _ = Filter($"{header}\n@@ -1 +1 @@\n-x\n");

    [Fact]
    public void ContainsPatch_IsAFastCheckForDiffLines()
    {
        Assert.True(GitDeniedContentFilter.ContainsPatch("diff --git a/x b/x\n"));
        Assert.True(GitDeniedContentFilter.ContainsPatch("commit x\n\ndiff --cc y\n"));
        Assert.False(GitDeniedContentFilter.ContainsPatch("commit x\n\n    message\n"));
        Assert.False(GitDeniedContentFilter.ContainsPatch(""));
    }
}
