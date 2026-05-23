///////////////////////////////////////////////////////////////////////////////
// fuseraft-cli Cake Build Script
//
// Usage (after running `dotnet tool restore`):
//   dotnet cake build.cake                              # Default (Publish)
//   dotnet cake build.cake --target=Build
//   dotnet cake build.cake --target=Pack --runtime=linux-x64
//   dotnet cake build.cake --configuration=Debug
//   dotnet cake build.cake --target=Lint
//
// Or via the bootstrappers:
//   ./build.sh [--target=X] [--configuration=Y] [--runtime=Z]
//   .\build.ps1 [-Target X] [-Configuration Y] [-Runtime Z]
///////////////////////////////////////////////////////////////////////////////

// Arguments
var target        = Argument("target",        "Default");
var configuration = Argument("configuration", "Release");
var runtime       = Argument("runtime",       "");          // e.g. "linux-x64"
var skipTests     = Argument("skipTests",     false);

// Paths
var projectFile    = "src/FuseraftCli.csproj";
var solutionFile   = "src/FuseraftCli.sln";
var artifactsDir   = Directory("artifacts");
var publishDir     = Directory("bin");
var packDir        = artifactsDir + Directory("packages");
var testResultsDir = artifactsDir + Directory("test-results");

// Version — computed once at script start from minver-cli so every task uses the same value.
var version = GetVersion();

// Helpers

// Ask minver-cli for the exact version it will stamp into the assembly.
// minver-cli is registered as a local dotnet tool in .config/dotnet-tools.json
// and restored by build.sh before Cake runs.
string GetVersion()
{
    try
    {
        IEnumerable<string> lines;
        var exit = StartProcess("dotnet", new ProcessSettings
        {
            Arguments = "minver --tag-prefix v",
            RedirectStandardOutput = true,
            RedirectStandardError  = true   // suppress "no tags" warnings
        }, out lines);

        if (exit == 0)
        {
            var v = string.Concat(lines).Trim();
            if (!string.IsNullOrEmpty(v)) return v;
        }
    }
    catch { /* minver unavailable — fall through */ }

    return "0.0.0";
}

string GetGitHash()
{
    try
    {
        IEnumerable<string> lines;
        if (StartProcess("git", new ProcessSettings
        {
            Arguments = "rev-parse --short HEAD",
            RedirectStandardOutput = true
        }, out lines) == 0)
            return string.Concat(lines).Trim();
    }
    catch { /* git unavailable */ }
    return "unknown";
}

// Lifecycle hooks
Setup(ctx =>
{
    Information("╔══════════════════════════════════════════════════════╗");
    Information("║      fuseraft CLI · Multi-Agent Orchestration        ║");
    Information("╠══════════════════════════════════════════════════════╣");
    Information($"║  Version        {version,-37}║");
    Information($"║  Configuration  {configuration,-37}║");
    Information($"║  Runtime        {(string.IsNullOrEmpty(runtime) ? "(framework-dependent)" : runtime),-37}║");
    Information($"║  Target         {target,-37}║");
    Information($"║  Git commit     {GetGitHash(),-37}║");
    Information("╚══════════════════════════════════════════════════════╝");
});

Teardown(ctx =>
{
    if (ctx.Successful)
        Information($"\n✓  '{target}' succeeded.");
    else
        Error($"\n✗  '{target}' failed: {ctx.ThrownException?.Message}");
});

///////////////////////////////////////////////////////////////////////////////
// TASKS
///////////////////////////////////////////////////////////////////////////////

//
// Clean: removes artifacts/ and dotnet bin/obj trees
//
Task("Clean")
    .Description("Remove build artifacts and clean dotnet output directories.")
    .Does(() =>
    {
        if (DirectoryExists(artifactsDir))
            CleanDirectory(artifactsDir);

        if (DirectoryExists(publishDir))
            CleanDirectory(publishDir);

        DotNetClean(projectFile, new DotNetCleanSettings
        {
            Configuration = configuration,
            Verbosity     = DotNetVerbosity.Minimal
        });

        Information("Clean complete.");
    });

//
// Restore: fetch NuGet packages
//
Task("Restore")
    .Description("Restore NuGet packages.")
    .IsDependentOn("Clean")
    .Does(() =>
    {
        DotNetRestore(projectFile, new DotNetRestoreSettings
        {
            Verbosity = DotNetVerbosity.Minimal
        });

        foreach (var testProject in GetFiles("tests/**/*.csproj"))
            DotNetRestore(testProject.ToString(), new DotNetRestoreSettings
            {
                Verbosity = DotNetVerbosity.Minimal
            });

        Information("Restore complete.");
    });

//
// Build: compile project in the requested configuration
//
Task("Build")
    .Description("Compile the project.")
    .IsDependentOn("Restore")
    .Does(() =>
    {
        DotNetBuild(projectFile, new DotNetBuildSettings
        {
            Configuration = configuration,
            NoRestore     = true,
            Verbosity     = DotNetVerbosity.Minimal,
            MSBuildSettings = new DotNetMSBuildSettings()
                .WithProperty("Version",            version)
                .WithProperty("InformationalVersion", version)
                .WithProperty("SourceRevisionId",   GetGitHash())
                .WithProperty("MinVerSkip",         "true")   // minver-cli already computed the version above
        });

        Information("Build complete.");
    });

//
// Test: discover and run all test projects under tests/
//
Task("Test")
    .Description("Run all test projects found under tests/.")
    .IsDependentOn("Build")
    .Does(() =>
    {
        if (skipTests)
        {
            Warning("--skipTests flag is set. Skipping.");
            return;
        }

        var testProjects = GetFiles("tests/**/*.csproj");

        if (!testProjects.Any())
        {
            Warning("No test projects found under tests/. Skipping.");
            Information("Tip: add an xUnit project under tests/ to enable this step.");
            return;
        }

        EnsureDirectoryExists(testResultsDir);

        foreach (var testProject in testProjects)
        {
            Information($"Testing: {testProject.GetFilename()}");

            DotNetTest(testProject.ToString(), new DotNetTestSettings
            {
                Configuration    = configuration,
                NoRestore        = true,
                ResultsDirectory = testResultsDir,
                Loggers          = new[] { "trx" },
                Verbosity        = DotNetVerbosity.Minimal,
                MSBuildSettings  = new DotNetMSBuildSettings()
                    .WithProperty("Version",              version)
                    .WithProperty("InformationalVersion", version)
                    .WithProperty("SourceRevisionId",     GetGitHash())
                    .WithProperty("MinVerSkip",           "true")
            });
        }

        Information("Tests complete.");
    });

//
// Publish: produce a deployable output in artifacts/publish/
//
Task("Publish")
    .Description("Publish to artifacts/publish/. Pass --runtime=<rid> for a self-contained binary.")
    .IsDependentOn("Test")
    .Does(() =>
    {
        EnsureDirectoryExists(publishDir);

        var settings = new DotNetPublishSettings
        {
            Configuration = configuration,
            OutputDirectory = publishDir,
            NoRestore     = true,
            NoBuild       = true,
            Verbosity     = DotNetVerbosity.Minimal,
            MSBuildSettings = new DotNetMSBuildSettings()
                .WithProperty("Version",            version)
                .WithProperty("InformationalVersion", version)
                .WithProperty("MinVerSkip",         "true")
        };

        if (!string.IsNullOrEmpty(runtime))
        {
            settings.Runtime       = runtime;
            settings.SelfContained = true;
            settings.MSBuildSettings
                .WithProperty("PublishSingleFile",              "true")
                .WithProperty("EnableCompressionInSingleFile",  "true");

            Information($"Self-contained single-file publish for: {runtime}");
        }
        else
        {
            Information("Framework-dependent publish (no --runtime specified).");
        }

        DotNetPublish(projectFile, settings);

        // On Windows builds, also publish the updater helper alongside the main binary.
        if (!string.IsNullOrEmpty(runtime) && runtime.StartsWith("win"))
        {
            var updaterProject = "src/FuseraftUpdate/FuseraftUpdate.csproj";
            var updaterSettings = new DotNetPublishSettings
            {
                Configuration   = configuration,
                OutputDirectory = publishDir,
                Runtime         = runtime,
                SelfContained   = true,
                Verbosity       = DotNetVerbosity.Minimal,
                MSBuildSettings = new DotNetMSBuildSettings()
                    .WithProperty("PublishSingleFile",             "true")
                    .WithProperty("EnableCompressionInSingleFile", "true")
                    .WithProperty("MinVerSkip",                    "true")
            };
            DotNetPublish(updaterProject, updaterSettings);
            Information("fuseraft-update published alongside fuseraft.exe.");
        }

        Information($"Publish complete → {publishDir}");
    });

//
// Pack: zip artifacts/publish/ into a versioned archive
//
Task("Pack")
    .Description("Zip the published output into a versioned archive under artifacts/packages/.")
    .IsDependentOn("Publish")
    .Does(() =>
    {
        EnsureDirectoryExists(packDir);

        var version   = GetVersion();
        var rtSuffix  = string.IsNullOrEmpty(runtime) ? "portable" : runtime;
        var zipName   = $"fuseraft-{version}-{rtSuffix}.zip";
        var zipPath   = packDir + File(zipName);

        Zip(publishDir, zipPath);

        var kb = new System.IO.FileInfo(zipPath.ToString()).Length / 1024;
        Information($"Package ready: {zipName}  ({kb:N0} KB)");
    });

//
// Lint: verify code formatting without modifying files
//
Task("Lint")
    .Description("Check code style with 'dotnet format --verify-no-changes'.")
    .Does(() =>
    {
        var exitCode = StartProcess("dotnet", new ProcessSettings
        {
            Arguments = $"format \"{projectFile}\" --verify-no-changes --severity warn"
        });

        if (exitCode != 0)
            throw new CakeException(
                "Code formatting issues detected. Run 'dotnet format' to fix them.");

        Information("Lint passed.");
    });

//
// Default: full pipeline without the Pack step
//
Task("Default")
    .Description("Full pipeline: Clean → Restore → Build → Test → Publish.")
    .IsDependentOn("Publish");

RunTarget(target);
