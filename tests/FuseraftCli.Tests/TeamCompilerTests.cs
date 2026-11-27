using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using fuseraft.Cli.Team;
using fuseraft.Core.Models;
using fuseraft.Core.Models.Agents;

namespace fuseraft.Tests;

public class TeamCompilerTests : IDisposable
{
    private readonly List<string> _files = [];

    public void Dispose()
    {
        foreach (var f in _files)
            try { File.Delete(f); } catch { /* best effort */ }
    }

    private const string Minimal = """
        Team: T
        Agents:
          Writer:
            Instructions: Write it.
        Flow:
          Start: Writer
          Steps:
            Writer:
              - To: end
        """;

    private const string DevTeam = """
        Team: Dev
        Artifacts:
          brief:
            Path: .fuseraft/artifacts/brief.json
            Shape:
              goal: string
              files_to_change: [string]
              verify_command: string
          testResults:
            Path: .fuseraft/artifacts/test-report.json
        Agents:
          Planner:
            Instructions: Plan.
            Writes: [brief]
          Developer:
            Instructions: Build.
            Tools: [FileSystem, Shell]
            Reads: [brief]
          Tester:
            Instructions: Test.
            Writes: [testResults]
        Flow:
          Start: Planner
          Steps:
            Planner:
              - To: Developer
                Requires: [brief]
            Developer:
              - To: Tester
                Requires:
                  - FilesWritten: brief.files_to_change
                  - CommandSucceeded: brief.verify_command
                    Fallback: build|test
              - To: Planner
                When: the plan is wrong
            Tester:
              - To: end
                Requires:
                  - TestReport: testResults
                OnStuck: Developer
        """;

    private OrchestrationConfig Compile(string yaml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"team-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, yaml);
        _files.Add(path);
        return TeamConfigLoader.LoadAndCompile(path);
    }

    private static ContractConfig ContractFor(OrchestrationConfig cfg, string step, int edge)
    {
        var name = cfg.Selection.StateMachine!.States[step].Transitions[edge].Contract;
        return cfg.Contracts!.Single(c => c.Name == name);
    }

    [Fact]
    public void Contracts_AreNamedAfterTheirGates()
    {
        var cfg = Compile(DevTeam);

        Assert.Equal("brief", ContractFor(cfg, "Planner", 0).Name);
        Assert.Equal("filesWritten brief.files_to_change + commandSucceeded brief.verify_command", ContractFor(cfg, "Developer", 0).Name);
    }

    private TeamConfigException Fails(string yaml) => Assert.Throws<TeamConfigException>(() => Compile(yaml));

    [Fact]
    public void Minimal_CompilesToStateMachineWithEndState()
    {
        var cfg = Compile(Minimal);

        Assert.Equal("T", cfg.Name);
        Assert.Equal("statemachine", cfg.Selection.Type);
        var sm = cfg.Selection.StateMachine!;
        Assert.Equal("Writer", sm.Initial);
        Assert.True(sm.States["end"].Terminal);
        Assert.Equal("", sm.States["end"].Agent);
        Assert.Equal("end", sm.States["Writer"].Transitions.Single().Signal);
    }

    [Fact]
    public void Minimal_AddsHandoffToolAndRoutes()
    {
        var agent = Compile(Minimal).Agents.Single();

        Assert.Contains("Handoff", agent.Plugins);
        Assert.Equal("end", agent.HandoffRoutes.Single().Name);
        Assert.Contains("finish the run", agent.HandoffRoutes.Single().Description);
    }

    [Fact]
    public void Defaults_FreshIsolationWithOwnHistory()
    {
        var agent = Compile(Minimal).Agents.Single();

        Assert.Equal(AgentIsolation.Fresh, agent.Isolation);
        Assert.Contains(agent.Context!, c => c.Source == "own_history:4");
    }

    [Fact]
    public void Sees_Conversation_MapsToShared()
    {
        var cfg = Compile(Minimal.Replace("Instructions: Write it.", "Instructions: Write it.\n    Sees: conversation"));
        Assert.Equal(AgentIsolation.Shared, cfg.Agents.Single().Isolation);
    }

    [Fact]
    public void AlwaysEnablesStateTracking()
    {
        var cfg = Compile(Minimal);

        Assert.NotNull(cfg.ChangeTracking);
        Assert.NotNull(cfg.EvidenceStore);
        Assert.NotNull(cfg.Events);
        Assert.NotNull(cfg.Checkpoint);
        Assert.Equal(40, cfg.MaxTurns);
    }

    [Fact]
    public void ArtifactRequirement_CompilesToFileExistsAndShape()
    {
        var cfg = Compile(DevTeam);
        var contract = ContractFor(cfg, "Planner", 0);

        Assert.Equal(["FileExists", "Shape"], contract.Requires.Select(p => p.Type));
        Assert.Contains("files_to_change", contract.Requires[1].Shape);
    }

    [Fact]
    public void FieldReferences_ResolveToArtifactPath()
    {
        var cfg = Compile(DevTeam);
        var reqs = ContractFor(cfg, "Developer", 0).Requires;

        var files = reqs.Single(p => p.Type == "FilesWritten");
        Assert.Equal(".fuseraft/artifacts/brief.json", files.Source);
        Assert.Equal("files_to_change", files.Field);

        var cmd = reqs.Single(p => p.Type == "CommandSucceeded");
        Assert.Equal("verify_command", cmd.PatternField);
        Assert.Equal("build|test", cmd.Pattern);
    }

    [Fact]
    public void CommandSucceeded_WithoutArtifactPrefix_IsAPattern()
    {
        var cfg = Compile(DevTeam.Replace("- CommandSucceeded: brief.verify_command\n            Fallback: build|test", "- CommandSucceeded: go test"));
        var cmd = cfg.Contracts!.SelectMany(c => c.Requires).Single(p => p.Type == "CommandSucceeded");

        Assert.Equal("go test", cmd.Pattern);
        Assert.Null(cmd.PatternField);
    }

    [Fact]
    public void TestReport_DefaultsToStrictAndSetsValidationPath()
    {
        var cfg = Compile(DevTeam);
        var report = cfg.Contracts!.SelectMany(c => c.Requires).Single(p => p.Type == "TestReport");

        Assert.True(report.NoFailures);
        Assert.True(report.HasAssertions);
        Assert.Equal(".fuseraft/artifacts/test-report.json", cfg.Validation!.TestReportPath);
        Assert.Equal(".fuseraft/artifacts/brief.json", cfg.Validation.BriefPath);
    }

    [Fact]
    public void OnStuck_BecomesRecoveryAgent()
    {
        var cfg = Compile(DevTeam);
        var t = cfg.Selection.StateMachine!.States["Tester"].Transitions.Single();
        Assert.Equal("Developer", t.RecoveryAgent);
    }

    [Fact]
    public void Reads_BecomeFileContextSources()
    {
        var dev = Compile(DevTeam).Agents.Single(a => a.Name == "Developer");
        Assert.Contains(dev.Context!, c => c.Source == "file:.fuseraft/artifacts/brief.json");
    }

    [Fact]
    public void Prompt_ListsRoutesGatesAndShape()
    {
        var dev = Compile(DevTeam).Agents.Single(a => a.Name == "Developer");
        var planner = Compile(DevTeam).Agents.Single(a => a.Name == "Planner");

        Assert.StartsWith("Build.", dev.Instructions);
        Assert.Contains("`Tester`: hand off to Tester.", dev.Instructions);
        Assert.Contains("every path listed in brief.files_to_change has been written", dev.Instructions);
        Assert.Contains("`Planner`: hand off to Planner when the plan is wrong.", dev.Instructions);
        Assert.Contains("\"files_to_change\"", planner.Instructions);
    }

    [Fact]
    public void Validator_Checks_CompileToValidatorPredicates()
    {
        var cfg = Compile(Minimal.Replace("- To: end", "- To: end\n        Requires: [ReviewJudgement, AnyFileWritten]"));
        var reqs = cfg.Contracts!.Single().Requires;

        Assert.All(reqs, p => Assert.Equal("Validator", p.Type));
        Assert.Equal(["RequireReviewJudgement", "RequireWriteFile"], reqs.Select(p => p.Validator));
    }

    [Fact]
    public void ParallelEdge_CompilesToFanOutWithJoin()
    {
        var cfg = Compile("""
            Team: P
            Agents:
              Lead: { Instructions: Lead }
              A: { Instructions: A }
              B: { Instructions: B }
              Merge: { Instructions: Merge }
            Flow:
              Start: Lead
              Steps:
                Lead:
                  - To: [A, B]
                    Join: Merge
                Merge:
                  - To: end
            """);
        var t = cfg.Selection.StateMachine!.States["Lead"].Transitions.Single();

        Assert.True(t.Parallel);
        Assert.Equal(["A", "B"], t.Targets);
        Assert.Equal("Merge", t.To);
        Assert.Empty(cfg.Selection.StateMachine.States["A"].Transitions);
    }

    [Fact]
    public void Pattern_MapReduce_BindsFields()
    {
        var cfg = Compile("""
            Team: M
            Agents:
              S: { Instructions: s }
              M: { Instructions: m }
              R: { Instructions: r }
            Flow:
              Pattern: mapreduce
              Splitter: S
              Mapper: M
              Reducer: R
              MaxConcurrency: 4
            """);

        Assert.Equal("mapreduce", cfg.Selection.Type);
        Assert.Equal(4, cfg.Selection.MapReduce!.MaxConcurrency);
    }

    [Fact]
    public void Pattern_Magentic_DefaultsAgentsToConversation()
    {
        var cfg = Compile("""
            Team: G
            Models:
              manager: { ModelId: gpt-5 }
            Agents:
              A: { Instructions: a }
            Flow:
              Pattern: magentic
              Model: manager
            """);

        Assert.Equal("manager", cfg.Selection.Magentic!.Model!.ModelId);
        Assert.Equal(AgentIsolation.Shared, cfg.Agents.Single().Isolation);
    }

    [Fact]
    public void AdvancedFields_BindToRuntimeConfig()
    {
        var cfg = Compile(Minimal + """

            Advanced:
              Compaction:
                TriggerTurnCount: 7
            """);
        Assert.Equal(7, cfg.Compaction!.TriggerTurnCount);
    }

    [Fact]
    public void AgentAdvanced_BindsAgentKnobs()
    {
        var cfg = Compile(Minimal.Replace("Instructions: Write it.", "Instructions: Write it.\n    Advanced:\n      MaxInTurnToolPairs: 9"));
        Assert.Equal(9, cfg.Agents.Single().MaxInTurnToolPairs);
    }

    [Fact]
    public void OldFormat_IsRejectedWithMigrateHint() =>
        Assert.Contains("fuseraft migrate", Fails("Orchestration:\n  Name: x\n").Message);

    [Fact]
    public void MissingTeamKey_IsRejected() =>
        Assert.Contains("missing the top-level 'team:'", Fails("Agents: {}\n").Message);

    [Fact]
    public void UnknownTopLevelField_IsRejected() =>
        Assert.Contains("unknown field 'Selection'", Fails(Minimal + "\nSelection: {}\n").Message);

    [Fact]
    public void UnknownAgentField_IsRejected() =>
        Assert.Contains("unknown field 'Plugins'", Fails(Minimal.Replace("Instructions: Write it.", "Instructions: Write it.\n    Plugins: [Shell]")).Message);

    [Fact]
    public void UnknownCheck_IsRejected() =>
        Assert.Contains("'RequireBreif' is neither", Fails(Minimal.Replace("- To: end", "- To: end\n        Requires: [RequireBreif]")).Message);

    [Fact]
    public void UnknownCheckOption_IsRejected() =>
        Assert.Contains("'Strict' isn't an option of testReport",
            Fails(DevTeam.Replace("- TestReport: testResults", "- TestReport: testResults\n            Strict: true")).Message);

    [Fact]
    public void FieldReference_ToUndeclaredArtifact_IsRejected() =>
        Assert.Contains("doesn't name a declared artifact",
            Fails(DevTeam.Replace("FilesWritten: brief.files_to_change", "FilesWritten: plan.files")).Message);

    [Fact]
    public void UnknownEdgeTarget_IsRejected() =>
        Assert.Contains("to 'Ghost' isn't declared", Fails(Minimal.Replace("- To: end", "- To: Ghost")).Message);

    [Fact]
    public void TargetWithoutStep_IsRejected() =>
        Assert.Contains("has no entry under flow.steps", Fails("""
            Team: T
            Agents:
              A: { Instructions: a }
              B: { Instructions: b }
            Flow:
              Start: A
              Steps:
                A:
                  - To: B
                  - To: end
            """).Message);

    [Fact]
    public void FlowWithoutEnd_IsRejected() =>
        Assert.Contains("no edge goes to 'end'", Fails(Minimal.Replace("- To: end", "- To: Writer")).Message);

    [Fact]
    public void DuplicateEdgeName_IsRejected() =>
        Assert.Contains("already has an edge named 'end'", Fails(Minimal.Replace("- To: end", "- To: end\n      - To: end")).Message);

    [Fact]
    public void ParallelEdgeWithoutJoin_IsRejected() =>
        Assert.Contains("needs 'join:'", Fails("""
            Team: T
            Agents:
              A: { Instructions: a }
              B: { Instructions: b }
            Flow:
              Start: A
              Steps:
                A:
                  - To: [A, B]
                  - To: end
            """).Message);

    [Fact]
    public void ReadsUndeclaredArtifact_IsRejected() =>
        Assert.Contains("'plan' in reads/writes isn't declared", Fails(Minimal.Replace("Instructions: Write it.", "Instructions: Write it.\n    Reads: [plan]")).Message);

    [Fact]
    public void Model_PlainIdAlongsideAliases_IsAccepted()
    {
        var cfg = Compile(Minimal.Replace("Team: T", "Team: T\nModels:\n  fast: { ModelId: gpt-5 }").Replace("Instructions: Write it.", "Instructions: Write it.\n    Model: claude-sonnet-4-6"));
        Assert.Equal("claude-sonnet-4-6", cfg.Agents.Single().Model.ModelId);
    }

    [Fact]
    public void AdvancedOwnedField_IsRejected() =>
        Assert.Contains("isn't an advanced field", Fails(Minimal + "\nAdvanced:\n  Contracts: []\n").Message);

    [Fact]
    public void AgentAdvancedOwnedField_PointsToAgentField() =>
        Assert.Contains("set it on the agent itself", Fails(Minimal.Replace("Instructions: Write it.", "Instructions: Write it.\n    Advanced:\n      Plugins: [Shell]")).Message);

    [Fact]
    public void ArtifactNamedLikeCheck_IsRejected() =>
        Assert.Contains("is also a check name", Fails(Minimal.Replace("Team: T", "Team: T\nArtifacts:\n  TestReport: { Path: r.json }")).Message);

    [Fact]
    public void RelatedTestsPass_WithoutTestSelector_IsRejected() =>
        Assert.Contains("needs advanced.testSelector", Fails(Minimal.Replace("- To: end", "- To: end\n        Requires: [RelatedTestsPass]")).Message);

    [Fact]
    public void MalformedShape_IsRejected() =>
        Assert.Contains("unknown type 'strng'", Fails(Minimal.Replace("Team: T", "Team: T\nArtifacts:\n  a: { Path: a.json, Shape: { x: strng } }")).Message);

    private string AgentMarkdown(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-{Guid.NewGuid():N}.md");
        File.WriteAllText(path, content);
        _files.Add(path);
        return path;
    }

    private string WithMarkdownAgent(string content) =>
        Minimal.Replace("  Writer:\n    Instructions: Write it.", $"  Writer: {AgentMarkdown(content)}");

    [Fact]
    public void MarkdownAgent_FrontmatterIsFields_BodyIsInstructions()
    {
        var agent = Compile(WithMarkdownAgent("---\nrole: From file\ntools: [Shell]\n---\nWrite it from the file.\n")).Agents.Single();

        Assert.Equal("From file", agent.Description);
        Assert.StartsWith("Write it from the file.", agent.Instructions);
        Assert.Contains("Shell", agent.Plugins);
    }

    [Fact]
    public void MarkdownAgent_RelativePath_ResolvesFromTeamFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"team-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "agents"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "agents", "writer.md"), "---\nrole: Writes\n---\nWrite it.\n");
            var team = Path.Combine(dir, "team.yaml");
            File.WriteAllText(team, Minimal.Replace("  Writer:\n    Instructions: Write it.", "  Writer: agents/writer.md"));

            Assert.Equal("Writes", TeamConfigLoader.LoadAndCompile(team).Agents.Single().Description);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void MarkdownAgent_UnknownField_IsRejectedNamingTheFile()
    {
        var ex = Fails(WithMarkdownAgent("---\nrole: R\ncolour: blue\n---\nWrite it.\n"));
        Assert.Contains("unknown field 'colour'", ex.Message);
        Assert.Contains(".md)", ex.Message);
    }

    [Fact]
    public void MarkdownAgent_InstructionsInFrontmatter_IsRejected() =>
        Assert.Contains("the markdown body is the instructions", Fails(WithMarkdownAgent("---\ninstructions: x\n---\nWrite it.\n")).Message);

    [Fact]
    public void MarkdownAgent_WithoutFrontmatter_IsRejected() =>
        Assert.Contains("must start with a '---'", Fails(WithMarkdownAgent("Write it.\n")).Message);

    [Fact]
    public void MarkdownAgent_EmptyBody_NeedsInstructions() =>
        Assert.Contains("instructions: required", Fails(WithMarkdownAgent("---\nrole: R\n---\n")).Message);

    [Fact]
    public void MarkdownAgent_Missing_IsRejected() =>
        Assert.Contains("not found", Fails(Minimal.Replace("  Writer:\n    Instructions: Write it.", "  Writer: nowhere/writer.md")).Message);

    [Fact]
    public void AgentStringThatIsntMarkdown_IsRejected() =>
        Assert.Contains("isn't a markdown file", Fails(Minimal.Replace("  Writer:\n    Instructions: Write it.", "  Writer: agents/writer.yaml")).Message);

    [Fact]
    public void AgentFile_PointsToMigrate() =>
        Assert.Contains("fuseraft migrate", Fails(Minimal.Replace("    Instructions: Write it.", "    agentFile: agents/w.yaml\n    Instructions: Write it.")).Message);

    [Fact]
    public void RemoteAgent_IsToldToWriteTheRouteName()
    {
        var cfg = Compile(Minimal.Replace("    Instructions: Write it.", "    Instructions: Write it.\n    RemoteAgent: { Url: \"https://remote.example\" }"));
        var agent = cfg.Agents.Single();

        Assert.DoesNotContain("Handoff", agent.Plugins);
        Assert.Contains("route names on its own line", agent.Instructions);
        Assert.DoesNotContain("`handoff`", agent.Instructions);
    }

    [Fact]
    public void RemoteAgent_WithLocalOnlyFields_IsRejected() =>
        Assert.Contains("a remoteAgent runs on its own server",
            Fails(Minimal.Replace("    Instructions: Write it.", "    Instructions: Write it.\n    Tools: [Shell]\n    RemoteAgent: { Url: \"https://remote.example\" }")).Message);

    [Fact]
    public void ModelFields_AcceptAnAlias()
    {
        var cfg = Compile(Minimal + "\nAdvanced:\n  Compaction:\n    Model: fast\n");
        Assert.Equal("fast", cfg.Compaction!.Model!.ModelId);
    }

    [Fact]
    public void Pattern_Magentic_RejectsHandoffAgents() =>
        Assert.Contains("needs every agent to see the conversation", Fails("""
            Team: G
            Agents:
              A: { Instructions: a, Sees: handoff }
            Flow:
              Pattern: magentic
              Model: gpt-5
            """).Message);

    [Fact]
    public void ParallelMerge_RankedWithoutAgent_IsRejected() =>
        Assert.Contains("strategy Ranked needs agent", Fails("""
            Team: P
            Agents:
              Lead: { Instructions: Lead }
              A: { Instructions: A }
              B: { Instructions: B }
              Join: { Instructions: Join }
            Flow:
              Start: Lead
              Steps:
                Lead:
                  - To: [A, B]
                    Join: Join
                    Merge: { Strategy: Ranked }
                Join:
                  - To: end
            """).Message);

    [Fact]
    public void ParallelMerge_UnknownStrategy_IsRejected() =>
        Assert.Contains("merge", Fails("""
            Team: P
            Agents:
              Lead: { Instructions: Lead }
              A: { Instructions: A }
              B: { Instructions: B }
              Join: { Instructions: Join }
            Flow:
              Start: Lead
              Steps:
                Lead:
                  - To: [A, B]
                    Join: Join
                    Merge: { Strategy: Benchmark }
                Join:
                  - To: end
            """).Message);

    [Fact]
    public void Approve_CompilesOntoTheTransition_AndIsInThePrompt()
    {
        var cfg = Compile(Minimal.Replace("- To: end", "- To: end\n        Approve: true"));

        Assert.True(cfg.Selection.StateMachine!.States["Writer"].Transitions[0].RequireHumanApproval);
        Assert.Contains("A person approves this handoff", cfg.Agents.Single().Instructions);
    }

    [Fact]
    public void Approve_DefaultsToFalse() =>
        Assert.False(Compile(Minimal).Selection.StateMachine!.States["Writer"].Transitions[0].RequireHumanApproval);

    [Fact]
    public void Approve_MustBeABool() =>
        Assert.Contains("approve: expected true or false", Fails(Minimal.Replace("- To: end", "- To: end\n        Approve: maybe")).Message);

    [Fact]
    public void FunctionChoiceNone_OnAStep_IsAnError() =>
        Assert.Contains("agents.Writer.advanced.functionChoice: 'none' stops Writer from calling handoff",
            Fails(MinimalCamel.Replace("functionChoice: required", "functionChoice: none")).Message);

    private const string MinimalCamel = """
        team: T
        agents:
          Writer:
            instructions: Write it.
            advanced: { functionChoice: required }
        flow:
          start: Writer
          steps:
            Writer:
              - to: end
                requires: [anyFileWritten]
        limits:
          maxTurns: 7
        """;

    [Fact]
    public void CamelCaseKeys_CompileLikePascalCase()
    {
        var camel  = Compile(MinimalCamel);
        var pascal = Compile(MinimalCamel
            .Replace("team:", "Team:").Replace("agents:", "Agents:").Replace("instructions:", "Instructions:")
            .Replace("advanced: { functionChoice:", "Advanced: { FunctionChoice:").Replace("flow:", "Flow:")
            .Replace("start:", "Start:").Replace("steps:", "Steps:").Replace("- to:", "- To:")
            .Replace("requires: [anyFileWritten]", "Requires: [AnyFileWritten]").Replace("limits:", "Limits:").Replace("maxTurns:", "MaxTurns:"));

        Assert.Equal(7, camel.MaxTurns);
        Assert.Equal("required", camel.Agents.Single().FunctionChoice);
        Assert.Equal(pascal.Contracts!.Single().Name, camel.Contracts!.Single().Name);
        Assert.Equal("anyFileWritten", camel.Contracts!.Single().Name);
    }

    [Fact]
    public void SameTopLevelKeyInTwoCases_IsRejected() =>
        Assert.Contains("'limits' and 'Limits' are the same field", Fails(MinimalCamel + "\nLimits:\n  MaxTurns: 3\n").Message);

    [Fact]
    public void SameNestedKeyInTwoCases_IsRejected() =>
        Assert.Contains("limits: 'maxTurns' and 'MaxTurns'", Fails(MinimalCamel.Replace("  maxTurns: 7", "  maxTurns: 7\n  MaxTurns: 3")).Message);

    [Fact]
    public void SameEdgeKeyInTwoCases_IsRejected() =>
        Assert.Contains("flow.steps.Writer[0]: 'to' and 'To'", Fails(MinimalCamel.Replace("      - to: end", "      - to: end\n        To: end")).Message);

    [Fact]
    public void MarkdownAgent_FieldWrittenTwiceInDifferentCases_IsRejected() =>
        Assert.Contains("'role' and 'Role'", Fails(WithMarkdownAgent("---\nrole: a\nRole: b\n---\nWrite it.\n")).Message);

    [Fact]
    public void ArtifactNamedLikeACheckInAnyCase_IsRejected() =>
        Assert.Contains("is also a check name", Fails(MinimalCamel.Replace("team: T", "team: T\nartifacts:\n  testReport: { path: r.json }")).Message);
}
