using fuseraft.Core.Models.Config;
using fuseraft.Infrastructure.Plugins;

namespace FuseraftCli.Tests;

/// <summary>
/// Deny rules stop the file tools from reading <c>.env</c>, and the shell policy stops a command that
/// names it — but <c>cat .e*</c>, <c>grep -r API_KEY .</c> or a script that opens the file itself
/// print its contents anyway. Probed against the real ShellPlugin: all three came back with the raw
/// secret. Masking by value is the layer that doesn't depend on how the command was spelled.
/// </summary>
public sealed class KnownSecretFilesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("fuseraft_knownsecrets_").FullName;

    private static readonly string StripeKey = "sk_live_" + Guid.NewGuid().ToString("N");
    private static readonly string DbPassword = "pw-" + Guid.NewGuid().ToString("N");

    public KnownSecretFilesTests() => KnownSecretFiles.Invalidate();

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        KnownSecretFiles.Invalidate();
    }

    private void Put(string relative, string content)
    {
        var full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private ShellPlugin Shell(bool blockCredentialFiles = true) => new(
        _root, blockCredentialFiles: blockCredentialFiles,
        denyPatterns: DefaultSecurityPolicy.MergeFileSystemDeny(null, blockCredentialFiles));

    private void PutDotEnv() => Put(".env",
        $"""
         # production settings
         PORT=3000
         NODE_ENV=production
         STRIPE_SECRET_KEY={StripeKey}
         DATABASE_URL=postgres://app:{DbPassword}@db.internal:5432/app
         """);

    // -- extraction -----------------------------------------------------------------------------

    [Fact]
    public void ExtractSecrets_TakesSecretNamedValues_AndIgnoresOrdinaryOnes()
    {
        var found = KnownSecretFiles.ExtractSecrets(".env",
            "PORT=3000\nNODE_ENV=production\nAPI_KEY=abcdef123456\nexport JWT_SECRET='quoted-secret-value'\nDEBUG=true\n").ToList();

        Assert.Contains("abcdef123456", found);
        Assert.Contains("quoted-secret-value", found);
        Assert.DoesNotContain("production", found);
        Assert.DoesNotContain("3000", found);
    }

    [Theory]
    [InlineData("DB_PASS=hunter2hunter2", "hunter2hunter2")]
    [InlineData("MONGO_PWD=mongo-password-1", "mongo-password-1")]
    [InlineData("SENTRY_DSN=https-dsn-value-1", "https-dsn-value-1")]
    [InlineData("password: yaml-password-1", "yaml-password-1")]
    [InlineData("\"api_key\": \"json-api-key-1\",", "json-api-key-1")]
    [InlineData("TOKEN = spaced-token-value # a comment", "spaced-token-value")]
    [InlineData("aws_secret_access_key = aws-secret-value-1", "aws-secret-value-1")]
    public void ExtractSecrets_RecognisesTheCommonSpellings(string line, string expected) =>
        Assert.Contains(expected, KnownSecretFiles.ExtractSecrets("config", line + "\n"));

    [Fact]
    public void ExtractSecrets_PointerNamesAreNotSecrets() =>
        Assert.Empty(KnownSecretFiles.ExtractSecrets(".env",
            "AWS_ACCESS_KEY_ID=AKIAIOSFODNN7EXAMPLE\nTOKEN_URL=https://auth.example.com/token\nSECRET_FILE=/run/secrets/x\n"));

    [Fact]
    public void ExtractSecrets_TakesThePasswordOutOfAUrl_WhateverTheKeyIsCalled()
    {
        var found = KnownSecretFiles.ExtractSecrets(".git-credentials",
            "https://octocat:ghp_abcdefghijklmnop@github.com\nhttps://plain@example.com\n").ToList();

        Assert.Equal(["ghp_abcdefghijklmnop"], found);
    }

    [Fact]
    public void ExtractSecrets_TakesPemBodyLines_AndTheJoinedBody()
    {
        const string pem = "-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAABG5vbmU\nAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\n-----END OPENSSH PRIVATE KEY-----\n";
        var found = KnownSecretFiles.ExtractSecrets("id_ed25519", pem).ToList();

        Assert.Contains("b3BlbnNzaC1rZXktdjEAAAAABG5vbmU", found);
        Assert.Contains("b3BlbnNzaC1rZXktdjEAAAAABG5vbmU" + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", found);
        Assert.DoesNotContain(found, v => v.Contains("BEGIN"));
    }

    [Fact]
    public void ExtractSecrets_PemHeaderLinesAreNotKeyMaterial()
    {
        const string pem = "-----BEGIN RSA PRIVATE KEY-----\nProc-Type: 4,ENCRYPTED\nDEK-Info: AES-128-CBC,0123456789ABCDEF\n\nMIIEowIBAAKCAQEAxxxxxxxxxxxxxxxx\n-----END RSA PRIVATE KEY-----\n";
        var found = KnownSecretFiles.ExtractSecrets("id_rsa", pem).ToList();

        Assert.DoesNotContain(found, v => v.Contains("Proc-Type") || v.Contains("DEK-Info"));
        Assert.Contains("MIIEowIBAAKCAQEAxxxxxxxxxxxxxxxx", found);
    }

    [Fact]
    public void ExtractSecrets_NetrcAndPgpass_AreParsedByFileName()
    {
        Assert.Contains("netrc-password-1", KnownSecretFiles.ExtractSecrets(".netrc",
            "machine example.com login me password netrc-password-1\n"));
        Assert.Contains("pgpass-password-1", KnownSecretFiles.ExtractSecrets(".pgpass",
            "db.internal:5432:app:appuser:pgpass-password-1\n"));

        // The same text in a file with another name is not a pgpass line.
        Assert.DoesNotContain("pgpass-password-1", KnownSecretFiles.ExtractSecrets("notes.txt",
            "db.internal:5432:app:appuser:pgpass-password-1\n"));
    }

    [Fact]
    public void ExtractSecrets_ShortValuesAreSkipped() =>
        Assert.Empty(KnownSecretFiles.ExtractSecrets(".env", "API_KEY=short\nPASSWORD=1234567\n"));

    // -- scanning + masking ---------------------------------------------------------------------

    [Fact]
    public void Track_FindsSecretsInNestedDeniedFiles()
    {
        Put("backend/services/api/.env", $"STRIPE_SECRET_KEY={StripeKey}\n");
        KnownSecretFiles.Track(_root, DefaultSecurityPolicy.MergeFileSystemDeny(null), includeHomeCredentials: false);
        KnownSecretFiles.Invalidate();

        Assert.Contains(StripeKey, KnownSecretFiles.Values());
    }

    [Fact]
    public void Track_IgnoresFilesTheDenyRulesDoNotProtect()
    {
        Put("config/settings.ini", $"api_key={StripeKey}\n");
        KnownSecretFiles.Track(_root, DefaultSecurityPolicy.MergeFileSystemDeny(null), includeHomeCredentials: false);
        KnownSecretFiles.Invalidate();

        Assert.DoesNotContain(StripeKey, KnownSecretFiles.Values());
    }

    [Fact]
    public void Track_DoesNotDescendIntoDependencyDirectories()
    {
        Put("node_modules/pkg/.env", $"STRIPE_SECRET_KEY={StripeKey}\n");
        KnownSecretFiles.Track(_root, DefaultSecurityPolicy.MergeFileSystemDeny(null), includeHomeCredentials: false);
        KnownSecretFiles.Invalidate();

        Assert.DoesNotContain(StripeKey, KnownSecretFiles.Values());
    }

    [Fact]
    public void Mask_HidesAFileSecret_OnceASourceIsTracked()
    {
        Put(".env", $"STRIPE_SECRET_KEY={StripeKey}\n");
        KnownSecretFiles.Track(_root, DefaultSecurityPolicy.MergeFileSystemDeny(null), includeHomeCredentials: false);
        KnownSecretFiles.Invalidate();

        var masked = EnvSecretMasker.Mask($"key is {StripeKey}!");

        Assert.DoesNotContain(StripeKey, masked);
        Assert.Contains(EnvSecretMasker.Placeholder, masked);
    }

    [Fact]
    public void Scan_PicksUpAFileWrittenAfterTheFirstScan_OnceTheCacheExpires()
    {
        KnownSecretFiles.Track(_root, DefaultSecurityPolicy.MergeFileSystemDeny(null), includeHomeCredentials: false);
        KnownSecretFiles.Invalidate();
        Assert.DoesNotContain(StripeKey, KnownSecretFiles.Values());

        Put(".env", $"STRIPE_SECRET_KEY={StripeKey}\n");
        Assert.DoesNotContain(StripeKey, KnownSecretFiles.Values());   // still inside the cache window

        KnownSecretFiles.Invalidate();
        Assert.Contains(StripeKey, KnownSecretFiles.Values());
    }

    [Fact]
    public void Track_WithNoPatternsAndNoHome_RegistersNothing()
    {
        Put(".env", $"STRIPE_SECRET_KEY={StripeKey}\n");
        KnownSecretFiles.Track(_root, null, includeHomeCredentials: false);
        KnownSecretFiles.Invalidate();

        Assert.DoesNotContain(StripeKey, KnownSecretFiles.Values());
    }

    [Fact]
    public void ScanHome_ReadsTheWellKnownCredentialFiles_AndNotPublicKeys()
    {
        Put("home/.aws/credentials", "[default]\naws_access_key_id = AKIAIOSFODNN7EXAMPLE\naws_secret_access_key = aws-home-secret-1\n");
        Put("home/.netrc", "machine example.com login me password netrc-home-secret-1\n");
        Put("home/.pgpass", "db:5432:app:me:pgpass-home-secret-1\n");
        Put("home/.git-credentials", "https://me:gitcred-home-secret-1@github.com\n");
        Put("home/.ssh/id_ed25519", "-----BEGIN OPENSSH PRIVATE KEY-----\nssh-private-key-body-line-1\n-----END OPENSSH PRIVATE KEY-----\n");
        Put("home/.ssh/id_ed25519.pub", "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5-public-key-material me@host\n");

        var values = new HashSet<string>();
        KnownSecretFiles.ScanHome(Path.Combine(_root, "home"), values);

        Assert.Contains("aws-home-secret-1", values);
        Assert.Contains("netrc-home-secret-1", values);
        Assert.Contains("pgpass-home-secret-1", values);
        Assert.Contains("gitcred-home-secret-1", values);
        Assert.Contains("ssh-private-key-body-line-1", values);
        Assert.DoesNotContain(values, v => v.Contains("public-key-material"));
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", values);           // an access key ID is not the secret
    }

    [Fact]
    public void ScanHome_AMissingHomeOrFiles_IsNotAnError()
    {
        var values = new HashSet<string>();

        KnownSecretFiles.ScanHome(Path.Combine(_root, "does-not-exist"), values);
        KnownSecretFiles.ScanHome("", values);

        Assert.Empty(values);
    }

    [Fact]
    public void Track_WithHomeCredentials_CoversTheHomeDirectoryToo()
    {
        // Outside _root on purpose: inside it, the sandbox walk would find the file and hide a broken home scan.
        var home = Directory.CreateTempSubdirectory("fuseraft_fakehome_").FullName;
        File.WriteAllText(Path.Combine(home, ".netrc"), $"machine example.com login me password {StripeKey}\n");
        var original = KnownSecretFiles.UserHome;
        KnownSecretFiles.UserHome = () => home;
        try
        {
            KnownSecretFiles.Track(_root, DefaultSecurityPolicy.MergeFileSystemDeny(null), includeHomeCredentials: true);
            KnownSecretFiles.Invalidate();

            Assert.Contains(StripeKey, KnownSecretFiles.Values());
        }
        finally
        {
            KnownSecretFiles.UserHome = original;
            KnownSecretFiles.Invalidate();
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void Scan_RefreshesOnceTheCacheTtlHasPassed()
    {
        var original = KnownSecretFiles.CacheTtl;
        try
        {
            KnownSecretFiles.CacheTtl = TimeSpan.FromMilliseconds(1);
            KnownSecretFiles.Track(_root, DefaultSecurityPolicy.MergeFileSystemDeny(null), includeHomeCredentials: false);
            Assert.DoesNotContain(StripeKey, KnownSecretFiles.Values());

            Put(".env", $"STRIPE_SECRET_KEY={StripeKey}\n");
            Thread.Sleep(50);

            Assert.Contains(StripeKey, KnownSecretFiles.Values());
        }
        finally
        {
            KnownSecretFiles.CacheTtl = original;
            KnownSecretFiles.Invalidate();
        }
    }

    // -- through the real plugins ---------------------------------------------------------------

    [Theory]
    [InlineData("cat .e*")]                                   // no `.env` in the command text
    [InlineData("grep -r STRIPE .")]
    [InlineData("grep -rh KEY .")]
    [InlineData("python3 -c \"print(open('.'+'env').read())\"")]
    [InlineData("cat .e* | tr a-z A-Z | tr A-Z a-z")]         // round-trips through a pipeline unchanged
    public async Task ShellRun_NeverPrintsAFileSecret(string command)
    {
        PutDotEnv();
        using var shell = Shell();

        var output = await shell.RunAsync(command, _root);

        Assert.DoesNotContain(StripeKey, output);
        Assert.DoesNotContain(DbPassword, output);
    }

    [Fact]
    public async Task ShellRun_KeepsTheRestOfTheFileReadable()
    {
        PutDotEnv();
        using var shell = Shell();

        var output = await shell.RunAsync("cat .e*", _root);

        Assert.Contains("PORT=3000", output);
        Assert.Contains("NODE_ENV=production", output);
        Assert.Contains($"STRIPE_SECRET_KEY={EnvSecretMasker.Placeholder}", output);
        Assert.Contains($"postgres://app:{EnvSecretMasker.Placeholder}@db.internal", output);
    }

    [Fact]
    public async Task ShellRun_ScriptMode_MasksToo()
    {
        PutDotEnv();
        using var shell = Shell();

        var output = await shell.RunScriptAsync("cat .e*\n", workingDirectory: _root);

        Assert.DoesNotContain(StripeKey, output);
    }

    [Fact]
    public async Task BackgroundJob_OutputIsMasked()
    {
        PutDotEnv();
        using var shell = Shell();

        var started = await shell.RunBackgroundAsync("cat .e*", _root);
        var jobId = System.Text.RegularExpressions.Regex.Match(started, @"Job ID: (\w+)").Groups[1].Value;
        Assert.NotEmpty(jobId);
        await Task.Delay(500);

        var output = await shell.GetJobOutput(jobId);
        var status = await shell.GetJobStatus(jobId);

        Assert.DoesNotContain(StripeKey, output);
        Assert.DoesNotContain(StripeKey, status);
        Assert.Contains(EnvSecretMasker.Placeholder, output);
    }

    [Fact]
    public async Task Probe_NeverPrintsAFileSecret()
    {
        PutDotEnv();
        var registry = new PluginRegistry().RegisterDefaults().Configure(new SecurityConfig { FileSystemSandboxPath = _root });
        Assert.True(registry.TryGet("Probe", out var obj));
        var probe = Assert.IsType<ProbePlugin>(obj);

        var output = await probe.ProbeCodeAsync("bash", "cat .e*", _root);

        Assert.DoesNotContain(StripeKey, output);
        Assert.DoesNotContain(DbPassword, output);
    }

    [Fact]
    public async Task ADeniedFileSecretsTheUserOptedOutOfProtecting_AreStillNotMasked()
    {
        // A sandbox with NO deny rules at all: nothing is tracked, so nothing is masked. The masker
        // follows the deny rules; it does not invent protection the user turned off.
        Put("notes.txt", $"api_key={StripeKey}\n");
        using var shell = new ShellPlugin(_root);

        var output = await shell.RunAsync("cat notes.txt", _root);

        Assert.Contains(StripeKey, output);
    }
}
