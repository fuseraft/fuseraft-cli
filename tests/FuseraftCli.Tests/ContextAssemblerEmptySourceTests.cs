using fuseraft.Core.Models.Orchestration;
using Microsoft.Extensions.AI;
using fuseraft.Orchestration.Context;

namespace FuseraftCli.Tests;

/// <summary>
/// Unit tests for <see cref="ContextAssembler.AssembleForAgentAsync"/>'s empty-source
/// reporting — the signal that lets the <c>context_assembly</c> event distinguish "the
/// agent's Context: spec omitted a needed source" from "the declared source referenced
/// an artifact that was never produced" (docs/context-management.md, Layer 3a).
/// </summary>
public sealed class ContextAssemblerEmptySourceTests
{
    private static ContextSource Src(string source) => new() { Source = source };

    [Fact]
    public async Task Brief_field_present_in_brief_json_is_not_reported_empty()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var briefPath = Path.Combine(dir.FullName, "brief.json");
            await File.WriteAllTextAsync(briefPath, """{ "acceptance_criteria": "all tests pass" }""");

            var assembler = new ContextAssembler(briefPath: briefPath);
            var result = await assembler.AssembleForAgentAsync(
                "Reviewer", "review the change",
                [Src("brief_field:acceptance_criteria")],
                new List<ChatMessage>());

            Assert.Empty(result.EmptySources);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public async Task Brief_field_missing_from_brief_json_is_reported_empty()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var briefPath = Path.Combine(dir.FullName, "brief.json");
            await File.WriteAllTextAsync(briefPath, """{ "acceptance_criteria": "all tests pass" }""");

            var assembler = new ContextAssembler(briefPath: briefPath);
            var result = await assembler.AssembleForAgentAsync(
                "Reviewer", "review the change",
                [Src("brief_field:test_targets")],
                new List<ChatMessage>());

            Assert.Equal(["brief_field:test_targets"], result.EmptySources);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public async Task Missing_brief_file_reports_all_brief_field_sources_empty()
    {
        var assembler = new ContextAssembler(briefPath: "/nonexistent/brief.json");
        var result = await assembler.AssembleForAgentAsync(
            "Reviewer", "review the change",
            [Src("brief_field:acceptance_criteria"), Src("brief_field:test_targets")],
            new List<ChatMessage>());

        Assert.Equal(
            new[] { "brief_field:acceptance_criteria", "brief_field:test_targets" },
            result.EmptySources);
    }

    [Fact]
    public async Task Own_history_source_is_never_reported_as_an_empty_artifact()
    {
        var assembler = new ContextAssembler(briefPath: "/nonexistent/brief.json");
        var history = new List<ChatMessage> { new(ChatRole.Assistant, "prior turn") { AuthorName = "Reviewer" } };

        var result = await assembler.AssembleForAgentAsync(
            "Reviewer", "review the change",
            [Src("own_history:3")],
            history);

        Assert.Empty(result.EmptySources);
    }

    [Fact]
    public async Task Resolved_source_content_still_appears_in_assembled_messages()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var briefPath = Path.Combine(dir.FullName, "brief.json");
            await File.WriteAllTextAsync(briefPath, """{ "acceptance_criteria": "all tests pass" }""");

            var assembler = new ContextAssembler(briefPath: briefPath);
            var result = await assembler.AssembleForAgentAsync(
                "Reviewer", "review the change",
                [Src("brief_field:acceptance_criteria")],
                new List<ChatMessage>());

            Assert.Contains(result.Messages, m => m.Text?.Contains("all tests pass") == true);
        }
        finally { dir.Delete(recursive: true); }
    }
}
