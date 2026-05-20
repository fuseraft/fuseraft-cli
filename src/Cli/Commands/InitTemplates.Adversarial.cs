using fuseraft.Core;

namespace fuseraft.Cli.Commands;

public static partial class InitTemplates
{
    /// <summary>
    /// Generates the <c>adversarial</c> template: a GAN-style pipeline where generator agents
    /// produce artifacts and critic agents review them in isolated context windows.
    /// Each stage runs up to <c>Rounds</c> generate → critique → revise cycles before the
    /// approved artifact is promoted to the next stage.
    /// </summary>
    private static string Adversarial(string model, string? endpoint) => $"""
        Orchestration:
          Name: Adversarial Pipeline
          Description: >
            GAN-style multi-agent pipeline. Generator agents produce artifacts; critic agents
            review them with fresh, isolated context windows (no shared history). Each stage
            runs up to Rounds generate → critique → revise cycles before the artifact is promoted.

          Agents:
            - Name: Planner
              Description: Produces a step-by-step implementation plan from the task description.
              Instructions: |
                You are a Planner. Given a task, produce a clear, concrete, step-by-step
                implementation plan. Be specific about what needs to be done, in what order,
                and what the expected output of each step is. Avoid vague instructions.
              Model:
                ModelId: {model}{Ep(endpoint, "        ")}
              Plugins:
                - FileSystem
                - Scratchpad

            - Name: PlanReviewer
              Description: Independently reviews a plan for logical flaws, gaps, and ambiguities.
              Instructions: |
                You are a PlanReviewer. You will receive a plan to review. Assess it critically:
                - Are the steps logically ordered with no missing dependencies?
                - Is each step concrete and actionable?
                - Are there any ambiguities, contradictions, or dead-ends?
                - Does the plan actually accomplish the stated goal?

                If the plan is sound and complete, respond with exactly:
                APPROVED

                Otherwise, list specific, actionable improvements. Be precise — point to the
                exact steps that need to change and explain why.
              Model:
                ModelId: {model}{Ep(endpoint, "        ")}

            - Name: Developer
              Description: Implements code based on an approved plan.
              Instructions: |
                You are a Developer. You will receive an approved plan and must implement it.
                Write clean, working code. Use your tools to create files and run tests.
                Report what you built and confirm it works.
              Model:
                ModelId: {model}{Ep(endpoint, "        ")}
              Plugins:
                - FileSystem
                - Shell
                - Git
                - Scratchpad

            - Name: CodeReviewer
              Description: Independently reviews implemented code for correctness and quality.
              Instructions: |
                You are a CodeReviewer. You will receive implemented code to review.
                Assess it critically with no assumptions about the author's intent:
                - Does the implementation match the plan?
                - Are there bugs, edge cases, or missing error handling?
                - Is the code readable and maintainable?
                - Do the tests cover the important paths?

                If the implementation is correct and complete, respond with exactly:
                APPROVED

                Otherwise, list specific, actionable defects. Reference exact file paths and
                line numbers where possible. Be precise — describe what is wrong and why.
              Model:
                ModelId: {model}{Ep(endpoint, "        ")}
              Plugins:
                - FileSystem

          Selection:
            Type: adversarial
            Adversarial:
              Rounds: 3          # critique rounds per stage (generator gets Rounds-1 revision opportunities)
              PassKeyword: "APPROVED"
              Stages:
                - Generator: Planner
                  Critic: PlanReviewer
                  Label: Planning

                - Generator: Developer
                  Critic: CodeReviewer
                  Label: Implementation

          Termination:
            Type: maxiterations
            MaxIterations: 50

          Compaction:
            TriggerTurnCount: 40
            KeepRecentTurns: 10

          Checkpoint:
            Mode: json
            Path: .fuseraft/checkpoints

          Events:
            Path: {FuseraftPaths.LocalEventsLog}
        """;
}
