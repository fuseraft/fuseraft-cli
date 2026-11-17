using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using fuseraft.Cli;
using fuseraft.Cli.Team;
using fuseraft.Core.Models;

namespace fuseraft.Tests;

public class LegacyConfigConverterTests : IDisposable
{
    private readonly List<string> _files = [];

    public void Dispose()
    {
        foreach (var f in _files)
            try { File.Delete(f); } catch { /* best effort */ }
    }

    private string Write(string yaml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"legacy-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, yaml);
        _files.Add(path);
        return path;
    }

    // Converts, then loads the result as a team file — every conversion must compile.
    private (string Yaml, IReadOnlyList<string> Notes, OrchestrationConfig Compiled) Convert(string legacyYaml)
    {
        var (config, legacy) = OrchestratorConfigLoader.LoadLegacyConfig(Write(legacyYaml));
        var result = LegacyConfigConverter.Convert(config, legacy);
        return (result.Yaml, result.Notes, TeamConfigLoader.LoadAndCompile(Write(result.Yaml)));
    }

    private const string StateMachine = """
        Orchestration:
          Name: Dev
          Validation:
            BriefPath: .fuseraft/artifacts/brief.json
          Contracts:
            - Name: BriefExists
              Requires:
                - Type: FileExists
                  Path: .fuseraft/artifacts/brief.json
            - Name: Built
              Requires:
                - Type: CommandSucceeded
                  PatternField: verify_command
                  Pattern: build|test
          Agents:
            - Name: Planner
              Instructions: |
                Plan. When done call handoff(route_keyword: "HANDOFF TO DEVELOPER").
              Model: { ModelId: gpt-4o }
              Plugins: [FileSystem, Handoff]
            - Name: Developer
              Isolation: Shared
              Instructions: |
                Build. Call handoff(route_keyword: "HANDOFF TO REVIEWER") or "REPLAN REQUIRED".
              Model: { ModelId: gpt-4o }
              Plugins: [FileSystem, Shell, Handoff]
            - Name: Reviewer
              Instructions: |
                Review. Call handoff(route_keyword: "APPROVED").
              Model: { ModelId: gpt-4o }
              Plugins: [FileSystem, Handoff]
          Selection:
            Type: statemachine
            StateMachine:
              Initial: Planning
              States:
                Planning:
                  Agent: Planner
                  Transitions:
                    - { To: Implementation, Signal: "HANDOFF TO DEVELOPER", Contract: BriefExists }
                Implementation:
                  Agent: Developer
                  Transitions:
                    - { To: Review, Signal: "HANDOFF TO REVIEWER", Contract: Built }
                    - { To: Planning, Signal: "REPLAN REQUIRED" }
                Review:
                  Agent: Reviewer
                  Transitions:
                    - { To: Done, Signal: APPROVED }
                Done:
                  Agent: Reviewer
                  Terminal: true
          Termination:
            Type: composite
            MaxIterations: 30
            Strategies:
              - { Type: regex, Pattern: APPROVED, AgentNames: [Reviewer] }
        """;

    [Fact]
    public void StateMachine_BecomesStepsWithGates()
    {
        var (yaml, _, cfg) = Convert(StateMachine);

        Assert.Contains("requires: [brief]", yaml);
        Assert.Contains("commandSucceeded: brief.verify_command", yaml);
        Assert.Contains("fallback: build|test", yaml);
        Assert.Equal("Planner", cfg.Selection.StateMachine!.Initial);
        Assert.Equal(30, cfg.MaxTurns);
    }

    [Fact]
    public void TerminalStateOfTheHandingOffAgent_BecomesEnd()
    {
        var cfg = Convert(StateMachine).Compiled;
        Assert.Contains(cfg.Selection.StateMachine!.States["Reviewer"].Transitions, t => t.To == "end");
    }

    [Fact]
    public void QuotedSignals_AreRenamedInInstructions()
    {
        var (yaml, _, cfg) = Convert(StateMachine);

        Assert.Contains("route_keyword: \"Developer\"", yaml);
        Assert.Contains("route_keyword: \"end\"", yaml);
        Assert.DoesNotContain("HANDOFF TO", yaml);
        Assert.Equal(["Reviewer", "Planner"], cfg.Agents.Single(a => a.Name == "Developer").HandoffRoutes.Select(r => r.Name));
    }

    [Fact]
    public void NestedRecords_KeepOnlyTheFieldsTheUserSet()
    {
        var legacy = StateMachine.Replace("""
                  Isolation: Shared
            """, """
                  Isolation: Shared
                  ContextWindow: { TextOnly: true, MaxTurnAge: 5 }
                  Context:
                    - Source: session_context
                    - { Source: changes_recent, Label: Recent changes }
            """);
        var (yaml, _, cfg) = Convert(legacy);

        Assert.DoesNotContain("maxChars", yaml);
        Assert.DoesNotContain("label: null", yaml);
        Assert.DoesNotContain("maxTailMessages", yaml);
        Assert.Contains("label: Recent changes", yaml);
        var developer = cfg.Agents.Single(a => a.Name == "Developer");
        Assert.Equal(5, developer.ContextWindow!.MaxTurnAge);
        Assert.Equal(["session_context", "changes_recent"], developer.Context!.Select(c => c.Source));
    }

    [Fact]
    public void FunctionChoiceNone_IsDroppedFromAnAgentThatMustHandOff()
    {
        var legacy = StateMachine.Replace("""
                    Review. Call handoff(route_keyword: "APPROVED").
            """, """
                    Review. Call handoff(route_keyword: "APPROVED").
                  FunctionChoice: none
            """);
        var (yaml, notes, cfg) = Convert(legacy);

        Assert.DoesNotContain("functionChoice", yaml);
        Assert.Contains(notes, n => n.StartsWith("Reviewer: FunctionChoice 'none' dropped", StringComparison.Ordinal));
        Assert.Equal("auto", cfg.Agents.Single(a => a.Name == "Reviewer").FunctionChoice);
    }

    [Fact]
    public void CustomLogPaths_AreNotedWhenDropped()
    {
        var notes = Convert(StateMachine.Replace("  Name: Dev\n", "  Name: Dev\n  Events: { Path: .fuseraft/events.jsonl }\n")).Notes;

        Assert.Contains(notes, n => n.StartsWith("Events.Path dropped", StringComparison.Ordinal));
    }

    [Fact]
    public void DefaultLogPaths_AreNotNoted()
    {
        var notes = Convert(StateMachine.Replace("  Name: Dev\n",
            "  Name: Dev\n  ChangeTracking: { Path: \"~/.fuseraft/state/{project_slug}/changes.json\" }\n")).Notes;

        Assert.DoesNotContain(notes, n => n.Contains("dropped; team runs", StringComparison.Ordinal));
    }

    [Fact]
    public void Signals_BecomeWhenText()
    {
        Assert.Contains("when: replan required", Convert(StateMachine).Yaml);
    }

    [Fact]
    public void HandoffPlugin_IsDropped_AndIsolationMapsToSees()
    {
        var yaml = Convert(StateMachine).Yaml;

        Assert.Contains("tools: [FileSystem, Shell]", yaml);
        Assert.DoesNotContain("Handoff", yaml.Split("Flow:")[0]);
        Assert.Contains("sees: conversation", yaml);
    }

    [Fact]
    public void TerminationRules_AreNoted()
    {
        Assert.Contains(Convert(StateMachine).Notes, n => n.Contains("Termination rules dropped"));
    }

    [Fact]
    public void TerminalStateWithADifferentAgent_KeepsThatAgent()
    {
        var cfg = Convert("""
            Orchestration:
              Name: Publish
              Agents:
                - { Name: Writer, Instructions: w, Model: { ModelId: gpt-4o } }
                - { Name: Publisher, Instructions: p, Model: { ModelId: gpt-4o } }
              Selection:
                Type: statemachine
                StateMachine:
                  Initial: Writing
                  States:
                    Writing:
                      Agent: Writer
                      Transitions:
                        - { To: Publishing, Signal: DRAFT READY }
                    Publishing:
                      Agent: Publisher
                      Terminal: true
            """).Compiled;
        var sm = cfg.Selection.StateMachine!;

        Assert.Contains(sm.States["Writer"].Transitions, t => t.To == "Publisher");
        Assert.Contains(sm.States["Publisher"].Transitions, t => t.To == "end");
    }

    [Fact]
    public void SignalWrittenAsText_IsKeptAsTheRouteName()
    {
        var legacy = StateMachine.Replace(
            "Build. Call handoff(route_keyword: \"HANDOFF TO REVIEWER\") or \"REPLAN REQUIRED\".",
            "Build. When done, write on its own line: BUILD DONE")
            .Replace("Signal: \"HANDOFF TO REVIEWER\"", "Signal: \"BUILD DONE\"");
        var cfg = Convert(legacy).Compiled;

        Assert.Contains(cfg.Agents.Single(a => a.Name == "Developer").HandoffRoutes, r => r.Name == "BUILD DONE");
    }

    [Fact]
    public void RoundRobin_BecomesAChainEndingOnTheTerminationAgent()
    {
        var (_, notes, cfg) = Convert("""
            Orchestration:
              Name: Pair
              Agents:
                - { Name: A, Instructions: a, Model: { ModelId: gpt-4o } }
                - { Name: B, Instructions: b, Model: { ModelId: gpt-4o } }
              Selection: { Type: roundrobin }
              Termination:
                Type: composite
                MaxIterations: 10
                Strategies:
                  - { Type: regex, Pattern: DONE, AgentNames: [B] }
            """);
        var sm = cfg.Selection.StateMachine!;

        Assert.Equal(["B"], sm.States["A"].Transitions.Select(t => t.To));
        Assert.Contains(sm.States["B"].Transitions, t => t.To == "end");
        Assert.Contains(notes, n => n.Contains("'DONE'"));
    }

    [Fact]
    public void Keyword_RoutesBecomeEdgesWithChecks()
    {
        var (yaml, _, cfg) = Convert("""
            Orchestration:
              Name: K
              Validation:
                BriefPath: .fuseraft/artifacts/brief.json
              Agents:
                - { Name: Planner, Instructions: p, Model: { ModelId: gpt-4o } }
                - { Name: Developer, Instructions: d, Model: { ModelId: gpt-4o } }
              Selection:
                Type: keyword
                Routes:
                  - { Keyword: "HANDOFF TO DEVELOPER", Agent: Developer, Validator: RequireBrief, SourceAgents: [Planner] }
              Termination:
                Type: composite
                MaxIterations: 10
                Strategies:
                  - { Type: regex, Pattern: DONE, AgentNames: [Developer] }
            """);

        Assert.Contains("requires: [brief]", yaml);
        Assert.Contains(cfg.Selection.StateMachine!.States["Developer"].Transitions, t => t.To == "end");
    }

    [Fact]
    public void Pattern_CarriesItsFields()
    {
        var cfg = Convert("""
            Orchestration:
              Name: M
              Agents:
                - { Name: S, Instructions: s, Model: { ModelId: gpt-4o } }
                - { Name: Mp, Instructions: m, Model: { ModelId: gpt-4o } }
                - { Name: R, Instructions: r, Model: { ModelId: gpt-4o } }
              Selection:
                Type: mapreduce
                MapReduce: { Splitter: S, Mapper: Mp, Reducer: R, MaxConcurrency: 3 }
            """).Compiled;

        Assert.Equal("mapreduce", cfg.Selection.Type);
        Assert.Equal(3, cfg.Selection.MapReduce!.MaxConcurrency);
    }

    [Fact]
    public void LlmSelection_IsRefused()
    {
        var path = Write("""
            Orchestration:
              Name: L
              Agents:
                - { Name: A, Instructions: a, Model: { ModelId: gpt-4o } }
              Selection: { Type: llm, Model: { ModelId: gpt-4o } }
            """);
        var (config, legacy) = OrchestratorConfigLoader.LoadLegacyConfig(path);
        var ex = Assert.Throws<TeamConfigException>(() => LegacyConfigConverter.Convert(config, legacy));
        Assert.Contains("'llm' has no v2 equivalent", ex.Message);
    }

    [Fact]
    public void AgentKnobs_MoveUnderAdvanced()
    {
        var yaml = Convert(StateMachine.Replace(
            "Plugins: [FileSystem, Shell, Handoff]",
            "Plugins: [FileSystem, Shell, Handoff]\n      FunctionChoice: required\n      MaxInTurnToolPairs: 12")).Yaml;

        Assert.Contains("    advanced:\n      functionChoice: required\n      maxInTurnToolPairs: 12", yaml);
    }

    [Fact]
    public void RequireHumanApproval_BecomesApprove()
    {
        var (yaml, _, cfg) = Convert("""
            Orchestration:
              Name: K
              Agents:
                - { Name: Planner, Instructions: p, Model: { ModelId: gpt-4o } }
                - { Name: Developer, Instructions: d, Model: { ModelId: gpt-4o } }
              Selection:
                Type: keyword
                Routes:
                  - { Keyword: "HANDOFF TO DEVELOPER", Agent: Developer, SourceAgents: [Planner], RequireHumanApproval: true }
              Termination:
                Type: composite
                MaxIterations: 10
                Strategies:
                  - { Type: regex, Pattern: DONE, AgentNames: [Developer] }
            """);

        Assert.Contains("approve: true", yaml);
        Assert.True(cfg.Selection.StateMachine!.States["Planner"].Transitions.Single(t => t.To == "Developer").RequireHumanApproval);
    }

    [Fact]
    public void Output_IsCamelCase_WithUserNamesKept()
    {
        var yaml = Convert(StateMachine).Yaml;

        Assert.StartsWith("team: Dev", yaml.TrimStart());
        Assert.Contains("\nagents:\n  Planner:\n", yaml);
        Assert.Contains("\nflow:\n  start: Planner\n  steps:\n", yaml);
        Assert.DoesNotContain("Requires:", yaml);
    }

    [Fact]
    public void TestReportArtifact_IsNotNamedLikeTheCheck()
    {
        var yaml = Convert("""
            Orchestration:
              Name: K
              Validation:
                TestReportPath: .fuseraft/artifacts/test-report.json
              Agents:
                - { Name: Tester, Instructions: t, Model: { ModelId: gpt-4o } }
                - { Name: Reviewer, Instructions: r, Model: { ModelId: gpt-4o } }
              Selection:
                Type: keyword
                Routes:
                  - { Keyword: "HANDOFF TO REVIEWER", Agent: Reviewer, Validator: TestReportValid, SourceAgents: [Tester] }
              Termination:
                Type: composite
                MaxIterations: 10
                Strategies:
                  - { Type: regex, Pattern: DONE, AgentNames: [Reviewer] }
            """).Yaml;

        Assert.Contains("testReport: testResults", yaml);
        Assert.Contains("  testResults:\n    path: .fuseraft/artifacts/test-report.json", yaml);
    }
}
