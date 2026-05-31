using System.Text.Json;
using fuseraft.Core.Models;
using fuseraft.Orchestration.Contracts;

namespace FuseraftCli.Tests;

/// <summary>
/// Verifies that ContractEngine expands {session_id} in FileExists, FilesWritten, and
/// CommandSucceeded predicate paths correctly, and surfaces a clear error when no session
/// ID is set (including when a whitespace-only session ID is passed).
/// </summary>
public sealed class ContractEngineSessionIdTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"fuseraft_ce_{Guid.NewGuid():N}");

    public ContractEngineSessionIdTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // --- FileExists ---

    [Fact]
    public async Task FileExists_WithSessionId_Expands_And_Passes_When_File_Present()
    {
        const string sessionId = "abc123";
        var sessionDir = Path.Combine(_dir, sessionId);
        Directory.CreateDirectory(sessionDir);
        await File.WriteAllTextAsync(Path.Combine(sessionDir, "brief.json"), "{}");

        var contract = MakeFileExistsContract(Path.Combine(_dir, "{session_id}", "brief.json"));
        var engine = new ContractEngine([contract], sessionId: sessionId);

        var (ok, error) = await engine.EvaluateAsync("C");

        Assert.True(ok, error);
    }

    [Fact]
    public async Task FileExists_WithSessionId_Expands_And_Fails_When_File_Missing()
    {
        const string sessionId = "abc123";

        var contract = MakeFileExistsContract(Path.Combine(_dir, "{session_id}", "brief.json"));
        var engine = new ContractEngine([contract], sessionId: sessionId);

        var (ok, error) = await engine.EvaluateAsync("C");

        Assert.False(ok);
        // Error must mention the expanded path, not the template.
        Assert.Contains(sessionId, error);
        Assert.DoesNotContain("{session_id}", error);
    }

    [Fact]
    public async Task FileExists_WithoutSessionId_SessionIdPath_Surfaces_Clear_Error()
    {
        var contract = MakeFileExistsContract(Path.Combine(_dir, "{session_id}", "brief.json"));
        // sessionId omitted → empty string
        var engine = new ContractEngine([contract]);

        var (ok, error) = await engine.EvaluateAsync("C");

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("session_id", error);
        Assert.Contains("internal error", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FileExists_WithoutSessionId_NonTemplatedPath_Works_Normally()
    {
        var filePath = Path.Combine(_dir, "brief.json");
        await File.WriteAllTextAsync(filePath, "{}");

        var contract = MakeFileExistsContract(filePath);
        var engine = new ContractEngine([contract]);

        var (ok, _) = await engine.EvaluateAsync("C");

        Assert.True(ok);
    }

    // --- FilesWritten ---

    [Fact]
    public async Task FilesWritten_WithoutSessionId_TemplatedSource_Surfaces_Clear_Error()
    {
        var contract = new ContractConfig
        {
            Name = "C",
            Requires =
            [
                new ContractPredicate
                {
                    Type   = "FilesWritten",
                    Source = Path.Combine(_dir, "{session_id}", "brief.json"),
                    Field  = "files_to_change",
                }
            ]
        };
        var engine = new ContractEngine([contract]);

        var (ok, error) = await engine.EvaluateAsync("C");

        Assert.False(ok);
        Assert.Contains("session_id", error);
        Assert.Contains("internal error", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FilesWritten_WithSessionId_Expands_Source_Path()
    {
        const string sessionId = "sess42";
        var sessionDir = Path.Combine(_dir, sessionId);
        Directory.CreateDirectory(sessionDir);

        var briefPath = Path.Combine(sessionDir, "brief.json");
        var targetFile = Path.Combine(_dir, "src", "main.py");
        Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
        await File.WriteAllTextAsync(targetFile, "# code");

        await File.WriteAllTextAsync(briefPath,
            JsonSerializer.Serialize(new { files_to_change = new[] { targetFile } }));

        var contract = new ContractConfig
        {
            Name = "C",
            Requires =
            [
                new ContractPredicate
                {
                    Type   = "FilesWritten",
                    Source = Path.Combine(_dir, "{session_id}", "brief.json"),
                    Field  = "files_to_change",
                }
            ]
        };
        var engine = new ContractEngine([contract], sessionId: sessionId);

        // The target file exists on disk — FilesWritten falls back to File.Exists
        // when the change log is unavailable, so this should pass.
        var (ok, _) = await engine.EvaluateAsync("C");

        Assert.True(ok);
    }

    // --- Whitespace session ID ---

    [Fact]
    public async Task FileExists_WhitespaceSessionId_Surfaces_Clear_Error()
    {
        var contract = MakeFileExistsContract(Path.Combine(_dir, "{session_id}", "brief.json"));
        var engine = new ContractEngine([contract], sessionId: "   ");

        var (ok, error) = await engine.EvaluateAsync("C");

        Assert.False(ok);
        Assert.Contains("session_id", error);
        Assert.Contains("internal error", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FilesWritten_WhitespaceSessionId_Surfaces_Clear_Error()
    {
        var contract = new ContractConfig
        {
            Name = "C",
            Requires =
            [
                new ContractPredicate
                {
                    Type   = "FilesWritten",
                    Source = Path.Combine(_dir, "{session_id}", "brief.json"),
                    Field  = "files_to_change",
                }
            ]
        };
        var engine = new ContractEngine([contract], sessionId: "   ");

        var (ok, error) = await engine.EvaluateAsync("C");

        Assert.False(ok);
        Assert.Contains("session_id", error);
        Assert.Contains("internal error", error, StringComparison.OrdinalIgnoreCase);
    }

    // --- CommandSucceeded ---

    [Fact]
    public async Task CommandSucceeded_WithoutSessionId_TemplatedPatternSource_Surfaces_Clear_Error()
    {
        var contract = new ContractConfig
        {
            Name = "C",
            Requires =
            [
                new ContractPredicate
                {
                    Type         = "CommandSucceeded",
                    PatternField = "build_command",
                    PatternSource = Path.Combine(_dir, "{session_id}", "brief.json"),
                }
            ]
        };
        var engine = new ContractEngine([contract]);

        var (ok, error) = await engine.EvaluateAsync("C");

        Assert.False(ok);
        Assert.Contains("session_id", error);
        Assert.Contains("internal error", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CommandSucceeded_WhitespaceSessionId_TemplatedPatternSource_Surfaces_Clear_Error()
    {
        var contract = new ContractConfig
        {
            Name = "C",
            Requires =
            [
                new ContractPredicate
                {
                    Type         = "CommandSucceeded",
                    PatternField = "build_command",
                    PatternSource = Path.Combine(_dir, "{session_id}", "brief.json"),
                }
            ]
        };
        var engine = new ContractEngine([contract], sessionId: "   ");

        var (ok, error) = await engine.EvaluateAsync("C");

        Assert.False(ok);
        Assert.Contains("session_id", error);
        Assert.Contains("internal error", error, StringComparison.OrdinalIgnoreCase);
    }

    // Helpers

    private static ContractConfig MakeFileExistsContract(string path) =>
        new()
        {
            Name = "C",
            Requires = [new ContractPredicate { Type = "FileExists", Path = path }]
        };
}
