using fuseraft.Core;

namespace fuseraft.Cli.Commands;

internal sealed record GeneratedConfig(
    string MainConfig,
    IReadOnlyList<(string RelativePath, string Content)> AgentFiles)
{
    internal static GeneratedConfig Inline(string yaml) => new(yaml, []);
}

internal static class InitTemplates
{
    internal static GeneratedConfig Build(string template, string model, string? endpoint) =>
        template switch
        {
            "research"   => Research(model, endpoint),
            "devops"     => DevOps(model, endpoint),
            "content"    => Content(model, endpoint),
            "minimal"    => GeneratedConfig.Inline(Minimal(model, endpoint)),
            "magentic"   => GeneratedConfig.Inline(Magentic(model, endpoint)),
            "designer"   => GeneratedConfig.Inline(Designer(model, endpoint)),
            "brownfield" => Brownfield(model, endpoint),
            "graph"            => Graph(model, endpoint),
            "brownfield-graph" => BrownfieldGraph(model, endpoint),
            _                  => DevTeam(model, endpoint),
        };

    // Returns "\n{pad}Endpoint: {endpoint}" when endpoint is set, otherwise empty.
    private static string Ep(string? endpoint, string pad) =>
        string.IsNullOrWhiteSpace(endpoint) ? string.Empty : $"\n{pad}Endpoint: {endpoint}";

    // Endpoint line for agent files (Model: is top-level, ModelId at 2-space indent).
    private static string EpAgent(string? endpoint) =>
        string.IsNullOrWhiteSpace(endpoint) ? string.Empty : $"\n  Endpoint: {endpoint}";

    private const string AgentFileOptions = """

        # -- Optional overrides -------------------------------------------------------
        # ContextWindow:
        #   TextOnly: true          # strip tool frames from cross-turn history
        # FunctionChoice: required  # force at least one tool call per turn (auto|required|none)
        # TrustScore: 0.8           # 0.0–1.0; governs sandbox ring (≥0.8 → ring 1)
        # MaxToolCallsPerTurn: 20
        # MaxTokens: 4096
        # Capabilities:             # per-plugin tool allowlist
        #   Shell: [shell_run]
        #   FileSystem: [read_file, list_files]
        """;

    private static string OptionalSections(string model, string? endpoint) => $"""

          # ---------------------------------------------------------------------------
          # OPTIONAL SECTIONS — uncomment and fill in as needed
          # ---------------------------------------------------------------------------

          # Named model aliases. Agents reference these by alias so you only need to
          # change the model ID in one place. Supports any OpenAI-compatible endpoint.
          # Models:
          #   fast:
          #     ModelId: {model}
          #     Endpoint: {(string.IsNullOrWhiteSpace(endpoint) ? "https://api.openai.com/v1" : endpoint)}
          #     ApiKeyEnvVar: OPENAI_API_KEY
          #     MaxContextTokens: 128000
          #   reasoning:
          #     ModelId: {model}

          # Sandbox agents to a directory and restrict outbound HTTP hosts.
          # Security:
          #   FileSystemSandboxPath: ~/my-project
          #   HttpAllowedHosts:
          #     - api.github.com
          #     - registry.npmjs.org

          # EvidenceStore: structured evidence graph — required for contracts and lossless compaction.
          # EvidenceStore:
          #   Path: {FuseraftPaths.LocalEvidence}

          # Contracts: evidence-gated guards on routes or state machine transitions.
          # Contracts:
          #   - Name: BriefExists
          #     Requires:
          #       - Type: FileExists
          #         Path: {FuseraftPaths.LocalBrief}
          #   - Name: ImplementationComplete
          #     Requires:
          #       - Type: CommandSucceeded
          #         Pattern: "build|compile"

          # FailureHandling: targeted correction policy per failure type.
          # FailureHandling:
          #   MissingEvidence:
          #     Action: Reinstruct
          #     Threshold: 3
          #   ConflictingEvidence:
          #     Action: Reinstruct
          #     Threshold: 2

          # Verifier: meta-agent that audits the evidence graph for inconsistencies.
          # Verifier:
          #   AgentName: Verifier          # must match an agent name in Agents
          #   EveryNTurns: 5
          #   TriggerOnSuspiciousTransition: true
          #   FindingsKeyword: INCONSISTENCY

          # Compaction: summarise old turns to prevent context-window overflow.
          # Compaction:
          #   TriggerTurnCount: 30
          #   KeepRecentTurns: 8
          #   Mode: lossless    # or "hybrid" (reconstruction + LLM narrative), "llm" (default)

          # Checkpoint: save and resume sessions across restarts.
          # Checkpoint:
          #   Mode: json
          #   Path: .fuseraft/checkpoints

          # ChangeTracking: record every file write/delete made by agents.
          # ChangeTracking:
          #   Path: {FuseraftPaths.LocalChanges}

          # Validation: paths used by built-in routing validators.
          # Validation:
          #   BriefPath: {FuseraftPaths.LocalBrief}
          #   TestReportPath: {FuseraftPaths.LocalTestReport}
          #   ChangeLogPath: {FuseraftPaths.LocalChanges}

          Events:
            Path: {FuseraftPaths.LocalEventsLog}

          # MaxTotalTokens: token budget (input + output combined) — session halts when exceeded.
          # MaxTotalTokens: 200000

          # McpServers: connect to Model Context Protocol tool servers.
          # McpServers:
          #   - Name: my-mcp-server
          #     Command: npx
          #     Args: [-y, "@modelcontextprotocol/server-filesystem", "."]
        """;

    // ─── DevTeam ────────────────────────────────────────────────────────────────

    private static GeneratedConfig DevTeam(string model, string? endpoint)
    {
        var planner = $"""
            Name: Planner
            Description: Analyses the task and writes a structured brief.
            Instructions: |
              You are a software architect and planner. Your job is to:
              1. Read and understand the task thoroughly.
              2. Use sub_agent_explore for broad codebase questions without filling
                 your context with raw file contents.
              3. Write a brief to {FuseraftPaths.LocalBrief} with fields:
                   goal — one-sentence description of what to build
                   files_to_change — array of file paths to create or modify
                   acceptance_criteria — array of testable criteria the code must satisfy
              4. Break work into concrete steps for the Developer.
              When done, call handoff(route_keyword: "HANDOFF TO DEVELOPER").
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - Search
              - SubAgent
              - Handoff
            FunctionChoice: required
            {AgentFileOptions}
            """;

        var developer = $"""
            Name: Developer
            Description: Implements the changes described in the brief.
            Instructions: |
              You are a senior software engineer. Your job is to:
              1. Read {FuseraftPaths.LocalBrief} and implement every listed file using write_file.
              2. Run a build command with shell_run to confirm it compiles.
              3. Commit your work with git_add and git_commit.
              When done, call handoff(route_keyword: "HANDOFF TO TESTER").
              If the plan is unclear, call handoff(route_keyword: "REPLAN REQUIRED").
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - Shell
              - Git
              - Changes
              - Handoff
            FunctionChoice: required
            {AgentFileOptions}
            """;

        var tester = $"""
            Name: Tester
            Description: Writes and runs tests, produces a structured report.
            Instructions: |
              You are a QA engineer. Your job is to:
              1. Read {FuseraftPaths.LocalBrief} to understand acceptance criteria.
              2. Write tests and run them with shell_run.
              3. Write results to {FuseraftPaths.LocalTestReport} with fields:
                   passed — true or false
                   results — array of objects, each with name, status (PASS or FAIL), exit_code, and command
                   command — the exact shell command you ran to verify this result (required for every PASS)
              A PASS result with an empty or missing command field is treated as fabricated and will block handoff.
              If all pass, call handoff(route_keyword: "HANDOFF TO REVIEWER").
              If any fail, call handoff(route_keyword: "BUGS FOUND").
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - Shell
              - Changes
              - Handoff
            FunctionChoice: required
            {AgentFileOptions}
            """;

        var reviewer = $"""
            Name: Reviewer
            Description: Reviews implementation and test results; gives final approval.
            Instructions: |
              You are a principal engineer. Your job is to:
              1. Read the implementation and {FuseraftPaths.LocalTestReport}.
              2. Run at least one acceptance criterion as a spot-check with shell_run.
              If the code meets all acceptance criteria, call handoff(route_keyword: "APPROVED").
              If changes are needed, call handoff(route_keyword: "REVISION REQUIRED") and explain what to fix.
              If the plan is fundamentally wrong, call handoff(route_keyword: "REPLAN REQUIRED").
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - Shell
              - Changes
              - Handoff
            FunctionChoice: auto
            ContextWindow:
              TextOnly: true
            {AgentFileOptions}
            """;

        var verifier = $"""
            Name: Verifier
            Description: Audits the evidence graph for inconsistencies between claims and recorded actions.
            Instructions: |
              You are an evidence auditor. Detect inconsistencies between what agents
              claim and what is recorded in the change log.

              1. Call changes_read_latest to see what was actually done this session.
              2. Compare recorded file writes, shell commands, and exit codes against
                 any claims made in recent conversation messages.
              3. If consistent: "Evidence verified — no inconsistencies found."
              4. If inconsistent: "INCONSISTENCY DETECTED: <what was claimed vs what the evidence shows>"
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - Changes
            FunctionChoice: required
            {AgentFileOptions}
            """;

        var mainConfig = $"""
            Orchestration:
              Name: Software Development Team
              Description: >-
                Planner → Developer → Tester → Reviewer with state machine routing,
                evidence contracts, failure handling, and self-verification.

              EvidenceStore:
                Path: {FuseraftPaths.LocalEvidence}

              ChangeTracking:
                Path: {FuseraftPaths.LocalChanges}

              Validation:
                BriefPath: {FuseraftPaths.LocalBrief}
                TestReportPath: {FuseraftPaths.LocalTestReport}
                ChangeLogPath: {FuseraftPaths.LocalChanges}

              Contracts:
                - Name: BriefExists
                  Requires:
                    - Type: FileExists
                      Path: {FuseraftPaths.LocalBrief}

                - Name: ImplementationComplete
                  Requires:
                    - Type: FilesWritten
                      Source: {FuseraftPaths.LocalBrief}
                      Field: files_to_change
                    - Type: CommandSucceeded
                      Pattern: "build|compile"

                - Name: TestsValid
                  Requires:
                    - Type: FileExists
                      Path: {FuseraftPaths.LocalTestReport}
                    - Type: TestReport
                      NoFailures: true
                      HasAssertions: true

              FailureHandling:
                MissingEvidence:
                  Action: Reinstruct
                  Threshold: 3
                ConflictingEvidence:
                  Action: Reinstruct
                  Threshold: 2
                NoProgress:
                  Action: Abort
                  Threshold: 3

              Verifier:
                AgentName: Verifier
                EveryNTurns: 5
                TriggerOnSuspiciousTransition: true
                FindingsKeyword: INCONSISTENCY

              Compaction:
                TriggerTurnCount: 30
                KeepRecentTurns: 8
                Mode: lossless

              Events:
                Path: {FuseraftPaths.LocalEventsLog}

              # Each agent lives in its own YAML file in agents/ — edit, version, or reuse
              # them independently across configs. Inline fields override the file at load time.
              Agents:
                - AgentFile: agents/planner.yaml
                - AgentFile: agents/developer.yaml
                - AgentFile: agents/tester.yaml
                - AgentFile: agents/reviewer.yaml
                - AgentFile: agents/verifier.yaml

              Selection:
                Type: statemachine
                StateMachine:
                  Initial: Planning

                  States:
                    Planning:
                      Agent: Planner
                      Transitions:
                        - To: Implementation
                          Signal: "HANDOFF TO DEVELOPER"
                          Contract: BriefExists

                    Implementation:
                      Agent: Developer
                      Transitions:
                        - To: Testing
                          Signal: "HANDOFF TO TESTER"
                          Contract: ImplementationComplete
                        - To: Planning
                          Signal: "REPLAN REQUIRED"

                    Testing:
                      Agent: Tester
                      Transitions:
                        - To: Review
                          Signal: "HANDOFF TO REVIEWER"
                          Contract: TestsValid
                        - To: Implementation
                          Signal: "BUGS FOUND"

                    Review:
                      Agent: Reviewer
                      Transitions:
                        - To: Done
                          Signal: APPROVED
                        - To: Implementation
                          Signal: "REVISION REQUIRED"

                    Done:
                      Agent: Reviewer
                      Terminal: true

              Termination:
                Type: composite
                Strategies:
                  - Type: regex
                    Pattern: "\\bAPPROVED\\b"
                    AgentNames: [Reviewer]
                  - Type: maxiterations
                    MaxIterations: 60

              # ---------------------------------------------------------------------------
              # OPTIONAL EXTRAS — uncomment and fill in as needed
              # ---------------------------------------------------------------------------

              # Security:
              #   FileSystemSandboxPath: ~/my-project
              #   HttpAllowedHosts:
              #     - api.github.com

              # MaxTotalTokens: 500000

              # McpServers:
              #   - Name: my-mcp-server
              #     Command: npx
              #     Args: [-y, "@modelcontextprotocol/server-filesystem", "."]

              # Checkpoint:
              #   Mode: json
              #   Path: .fuseraft/checkpoints

              # Models:
              #   fast:
              #     ModelId: {model}
              #   reasoning:
              #     ModelId: {model}
            """;

        return new GeneratedConfig(mainConfig, [
            ("agents/planner.yaml",   planner),
            ("agents/developer.yaml", developer),
            ("agents/tester.yaml",    tester),
            ("agents/reviewer.yaml",  reviewer),
            ("agents/verifier.yaml",  verifier),
        ]);
    }

    // ─── Research ───────────────────────────────────────────────────────────────

    private static GeneratedConfig Research(string model, string? endpoint)
    {
        var researcher = $"""
            Name: Researcher
            Description: Gathers information and writes structured findings to disk.
            Instructions: |
              You are a diligent researcher. Your job is to:
              1. Break the topic into focused questions.
              2. Search for answers using available tools.
              3. Write your structured findings to .fuseraft/research-findings.md.
              When your research is thorough and complete, call handoff(route_keyword: "HANDOFF TO WRITER").
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - Http
              - Search
              - FileSystem
              - Handoff
            FunctionChoice: required
            {AgentFileOptions}
            """;

        var writer = $"""
            Name: Writer
            Description: Turns research findings into a polished final document.
            Instructions: |
              You are a skilled technical writer. Your job is to:
              1. Read the research findings from .fuseraft/research-findings.md.
              2. Synthesize a clear, well-structured document that answers the original question.
              3. Write the final document to .fuseraft/report.md.
              When done, call handoff(route_keyword: "DOCUMENT COMPLETE").
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - Handoff
            FunctionChoice: required
            {AgentFileOptions}
            """;

        var mainConfig = $"""
            Orchestration:
              Name: Research Team
              Description: >-
                Researcher gathers information with a verified handoff; Writer synthesises the final document.

              EvidenceStore:
                Path: {FuseraftPaths.LocalEvidence}

              Contracts:
                - Name: ResearchComplete
                  Requires:
                    - Type: FileExists
                      Path: .fuseraft/research-findings.md

              # Each agent lives in its own YAML file in agents/ — edit, version, or reuse
              # them independently across configs. Inline fields override the file at load time.
              Agents:
                - AgentFile: agents/researcher.yaml
                - AgentFile: agents/writer.yaml

              Selection:
                Type: statemachine
                StateMachine:
                  Initial: Research

                  States:
                    Research:
                      Agent: Researcher
                      Transitions:
                        - To: Writing
                          Signal: "HANDOFF TO WRITER"
                          Contract: ResearchComplete

                    Writing:
                      Agent: Writer
                      Transitions:
                        - To: Done
                          Signal: "DOCUMENT COMPLETE"

                    Done:
                      Agent: Writer
                      Terminal: true

              Termination:
                Type: composite
                Strategies:
                  - Type: regex
                    Pattern: DOCUMENT COMPLETE
                    AgentNames: [Writer]
                  - Type: maxiterations
                    MaxIterations: 20
            {OptionalSections(model, endpoint)}
            """;

        return new GeneratedConfig(mainConfig, [
            ("agents/researcher.yaml", researcher),
            ("agents/writer.yaml",     writer),
        ]);
    }

    // ─── DevOps ─────────────────────────────────────────────────────────────────

    private static GeneratedConfig DevOps(string model, string? endpoint)
    {
        var planner = $"""
            Name: Planner
            Description: Designs the deployment or infrastructure plan.
            Instructions: |
              You are a DevOps architect. Your job is to:
              1. Understand the infrastructure or deployment task.
              2. Use sub_agent_explore to survey relevant config files and scripts.
              3. Write a step-by-step execution plan to {FuseraftPaths.LocalBrief} with fields:
                   goal — what the deployment achieves
                   steps — ordered list of execution steps
                   rollback — steps to undo if something goes wrong
              When the plan is ready, call handoff(route_keyword: "PLANNING_COMPLETE").
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - SubAgent
              - Handoff
            FunctionChoice: required
            {AgentFileOptions}
            """;

        var developer = $"""
            Name: Developer
            Description: Implements scripts, manifests, and config files.
            Instructions: |
              You are a DevOps engineer. Your job is to:
              1. Read the plan from {FuseraftPaths.LocalBrief} and implement all required
                 scripts, manifests, or config files using write_file.
              2. Run static analysis or validation with shell_run (e.g. lint, validate, check).
              3. Commit with git_add and git_commit when ready.
              When done, call handoff(route_keyword: "DEVELOPMENT_COMPLETE").
              If the plan is unclear, call handoff(route_keyword: "REPLAN_REQUIRED").
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - Shell
              - Git
              - Changes
              - Handoff
            FunctionChoice: required
            {AgentFileOptions}
            """;

        var operator_ = $"""
            Name: Operator
            Description: Executes the deployment and verifies success.
            Instructions: |
              You are a site reliability engineer. Your job is to:
              1. Execute the deployment steps from {FuseraftPaths.LocalBrief} using shell_run.
              2. Run smoke tests to verify the deployment succeeded.
              3. Report the outcome clearly with exact command output.
              If successful, call handoff(route_keyword: "DEPLOYMENT_COMPLETE").
              If failed, call handoff(route_keyword: "DEPLOYMENT_FAILED") and describe what went wrong.
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - Shell
              - Git
              - Changes
              - Handoff
            FunctionChoice: required
            {AgentFileOptions}
            """;

        var mainConfig = $"""
            Orchestration:
              Name: DevOps Team
              Description: >-
                Planner → Developer → Operator pipeline for infrastructure and deployment tasks.

              EvidenceStore:
                Path: {FuseraftPaths.LocalEvidence}

              Contracts:
                - Name: PlanExists
                  Requires:
                    - Type: FileExists
                      Path: {FuseraftPaths.LocalBrief}

                - Name: ArtifactsReady
                  Requires:
                    - Type: CommandSucceeded
                      Pattern: "lint|validate|check|test"

              FailureHandling:
                MissingEvidence:
                  Action: Reinstruct
                  Threshold: 3
                NoProgress:
                  Action: Abort
                  Threshold: 3

              # Each agent lives in its own YAML file in agents/ — edit, version, or reuse
              # them independently across configs. Inline fields override the file at load time.
              Agents:
                - AgentFile: agents/planner.yaml
                - AgentFile: agents/developer.yaml
                - AgentFile: agents/operator.yaml

              Selection:
                Type: statemachine
                StateMachine:
                  Initial: Planning

                  States:
                    Planning:
                      Agent: Planner
                      Transitions:
                        - To: Development
                          Signal: "PLANNING_COMPLETE"
                          Contract: PlanExists

                    Development:
                      Agent: Developer
                      Transitions:
                        - To: Operations
                          Signal: "DEVELOPMENT_COMPLETE"
                          Contract: ArtifactsReady
                        - To: Planning
                          Signal: "REPLAN_REQUIRED"

                    Operations:
                      Agent: Operator
                      Transitions:
                        - To: Done
                          Signal: "DEPLOYMENT_COMPLETE"
                        - To: Development
                          Signal: "DEPLOYMENT_FAILED"

                    Done:
                      Agent: Operator
                      Terminal: true

              Termination:
                Type: composite
                Strategies:
                  - Type: regex
                    Pattern: DEPLOYMENT_COMPLETE
                    AgentNames: [Operator]
                  - Type: maxiterations
                    MaxIterations: 20
            {OptionalSections(model, endpoint)}
            """;

        return new GeneratedConfig(mainConfig, [
            ("agents/planner.yaml",   planner),
            ("agents/developer.yaml", developer),
            ("agents/operator.yaml",  operator_),
        ]);
    }

    // ─── Content ────────────────────────────────────────────────────────────────

    private static GeneratedConfig Content(string model, string? endpoint)
    {
        var writer = $"""
            Name: Writer
            Description: Produces a complete first draft and saves it to disk.
            Instructions: |
              You are a creative and precise writer. Your job is to:
              1. Understand the content brief from the task.
              2. Write a complete draft and save it to output/draft.md using write_file.
              When the draft is ready for review, call handoff(route_keyword: "DRAFT_COMPLETE").
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - Search
              - Handoff
            FunctionChoice: required
            {AgentFileOptions}
            """;

        var editor = $"""
            Name: Editor
            Description: Edits for clarity, accuracy, and style; writes the final version.
            Instructions: |
              You are a senior editor. Your job is to:
              1. Read the draft from output/draft.md.
              2. Edit for clarity, accuracy, tone, and structure.
              3. Save the final version to output/final.md using write_file.
              When editing is complete, call handoff(route_keyword: "CONTENT_APPROVED").
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - Handoff
            FunctionChoice: required
            {AgentFileOptions}
            """;

        var mainConfig = $"""
            Orchestration:
              Name: Content Pipeline
              Description: >-
                Writer drafts content with a verified handoff; Editor refines and approves.

              EvidenceStore:
                Path: {FuseraftPaths.LocalEvidence}

              Contracts:
                - Name: DraftExists
                  Requires:
                    - Type: FileExists
                      Path: output/draft.md

              # Each agent lives in its own YAML file in agents/ — edit, version, or reuse
              # them independently across configs. Inline fields override the file at load time.
              Agents:
                - AgentFile: agents/writer.yaml
                - AgentFile: agents/editor.yaml

              Selection:
                Type: statemachine
                StateMachine:
                  Initial: Writing

                  States:
                    Writing:
                      Agent: Writer
                      Transitions:
                        - To: Editing
                          Signal: "DRAFT_COMPLETE"
                          Contract: DraftExists

                    Editing:
                      Agent: Editor
                      Transitions:
                        - To: Done
                          Signal: "CONTENT_APPROVED"

                    Done:
                      Agent: Editor
                      Terminal: true

              Termination:
                Type: composite
                Strategies:
                  - Type: regex
                    Pattern: CONTENT_APPROVED
                    AgentNames: [Editor]
                  - Type: maxiterations
                    MaxIterations: 10
            {OptionalSections(model, endpoint)}
            """;

        return new GeneratedConfig(mainConfig, [
            ("agents/writer.yaml", writer),
            ("agents/editor.yaml", editor),
        ]);
    }

    // ─── Brownfield ─────────────────────────────────────────────────────────────

    private static GeneratedConfig Brownfield(string model, string? endpoint)
    {
        var archaeologist = $"""
            Name: Archaeologist
            Description: Recons the codebase and writes the discovery brief and convention profile.
            Instructions: |
              You are a codebase archaeologist. Your job is to understand an existing project
              before any changes are made. Follow this procedure:

              1. Read the entry point files listed in the task to orient yourself.
              2. Use list_files and sub_agent_explore to map the directory structure — do NOT
                 read every file; focus on understanding the shape of the codebase.
              3. Identify: primary language and framework, naming conventions (snake_case vs camelCase),
                 import style, test framework, build system, and key architectural patterns.
              4. Write the convention profile to {FuseraftPaths.LocalConventions} with fields:
                   language, framework, naming_convention, import_style, test_framework,
                   build_command, lint_command, notes (array of key architectural observations).
              5. Identify the files most likely to need modification for the given task.
              6. Write the discovery brief to {FuseraftPaths.LocalBrownfieldBrief} with fields:
                   summary — one paragraph describing the codebase structure
                   in_scope_files — array of file paths likely relevant to the task
                   dependencies — key external dependencies to be aware of
                   risks — array of fragility signals (e.g. no tests, circular deps, god objects)

              When both files are written, call handoff(route_keyword: "RECON COMPLETE").
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - Search
              - SubAgent
              - Handoff
            FunctionChoice: required
            {AgentFileOptions}
            """;

        var planner = $"""
            Name: Planner
            Description: Designs the targeted change based on the discovery brief.
            Instructions: |
              You are a software architect working on an existing codebase. Your job is to:
              1. Read {FuseraftPaths.LocalBrownfieldBrief} to understand the codebase shape and risks.
              2. Read {FuseraftPaths.LocalConventions} to understand the project's conventions — follow them exactly.
              3. Use sub_agent_explore for any additional targeted questions about specific files.
              4. Write a scoped brief to {FuseraftPaths.LocalBrief} with fields:
                   goal — one-sentence description of the change
                   findings — summary of relevant existing code to modify
                   files_to_change — only the files that genuinely need to change
                   acceptance_criteria — observable code properties the change must satisfy
                   convention_notes — specific conventions to follow from the profile
              When done, call handoff(route_keyword: "HANDOFF TO DEVELOPER").
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - Search
              - SubAgent
              - Handoff
            FunctionChoice: required
            {AgentFileOptions}
            """;

        var developer = $"""
            Name: Developer
            Description: Implements the change staying strictly within the scoped file list.
            Instructions: |
              You are a developer working carefully inside an existing codebase. Your job is to:
              1. Read {FuseraftPaths.LocalBrief} — implement ONLY the files listed in files_to_change.
              2. Read {FuseraftPaths.LocalConventions} — follow the project's naming, import, and style conventions exactly.
              3. Use read_file to read existing files before modifying them — never overwrite blindly.
              4. Use patch_file for surgical edits to existing files; use write_file only for new files.
              5. Run the build command from the convention profile to confirm nothing is broken.
              6. Commit with git_add and git_commit.
              When done, call handoff(route_keyword: "HANDOFF TO REVIEWER").
              If the brief is unclear, call handoff(route_keyword: "REPLAN REQUIRED").
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - Shell
              - Git
              - Changes
              - Handoff
            FunctionChoice: required
            {AgentFileOptions}
            """;

        var reviewer = $"""
            Name: Reviewer
            Description: Code-review-only inspection against the brief and conventions.
            Instructions: |
              You are a principal engineer reviewing a change to an existing codebase. Your job is to:
              1. Read each file listed in {FuseraftPaths.LocalBrief} under files_to_change.
              2. Verify every acceptance criterion is satisfied by code inspection.
              3. Check that the change follows conventions from {FuseraftPaths.LocalConventions}.
              4. Confirm no files outside files_to_change were modified (use changes_read_latest).
              Do NOT run shell commands — this is a code-inspection-only review.
              If the change is correct, call handoff(route_keyword: "APPROVED").
              If revision is needed, call handoff(route_keyword: "REVISION REQUIRED") and explain what to fix.
              If the plan needs rethinking, call handoff(route_keyword: "REPLAN REQUIRED").
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - Changes
              - Handoff
            FunctionChoice: auto
            ContextWindow:
              TextOnly: true
            {AgentFileOptions}
            """;

        var mainConfig = $"""
            Orchestration:
              Name: Brownfield Codebase Pipeline
              Description: >-
                Archaeologist recons the existing codebase and writes a discovery brief;
                Planner designs the targeted change; Developer implements with a scoped change
                envelope; Reviewer inspects by code review. Conventions detected during recon
                are automatically injected into every agent's system prompt.

              Security:
                FileSystemSandboxPath: .   # set to your project root (e.g. ~/projects/myapp)
                # ChangeEnvelope is seeded automatically from the discovery brief when
                # Brownfield.SeedEnvelopeFromBrief is true — no need to list files manually.

              Brownfield:
                EntryPoints:
                  - src/   # replace with your actual entry points (e.g. cmd/server/main.go)
                SeedEnvelopeFromBrief: true
                DiscoveryBriefPath: {FuseraftPaths.LocalBrownfieldBrief}
                ConventionProfilePath: {FuseraftPaths.LocalConventions}

              EvidenceStore:
                Path: {FuseraftPaths.LocalEvidence}

              ChangeTracking:
                Path: {FuseraftPaths.LocalChanges}

              Validation:
                BriefPath: {FuseraftPaths.LocalBrief}
                ChangeLogPath: {FuseraftPaths.LocalChanges}

              Contracts:
                - Name: ReconComplete
                  Requires:
                    - Type: FileExists
                      Path: {FuseraftPaths.LocalBrownfieldBrief}
                    - Type: FileExists
                      Path: {FuseraftPaths.LocalConventions}

                - Name: BriefExists
                  Requires:
                    - Type: FileExists
                      Path: {FuseraftPaths.LocalBrief}

                - Name: ImplementationComplete
                  Requires:
                    - Type: FilesWritten
                      Source: {FuseraftPaths.LocalBrief}
                      Field: files_to_change

              FailureHandling:
                MissingEvidence:
                  Action: Reinstruct
                  Threshold: 3
                NoProgress:
                  Action: Abort
                  Threshold: 3

              Events:
                Path: {FuseraftPaths.LocalEventsLog}

              # Each agent lives in its own YAML file in agents/ — edit, version, or reuse
              # them independently across configs. Inline fields override the file at load time.
              Agents:
                - AgentFile: agents/archaeologist.yaml
                - AgentFile: agents/planner.yaml
                - AgentFile: agents/developer.yaml
                - AgentFile: agents/reviewer.yaml

              Selection:
                Type: statemachine
                StateMachine:
                  Initial: Recon

                  States:
                    Recon:
                      Agent: Archaeologist
                      Transitions:
                        - To: Planning
                          Signal: "RECON COMPLETE"
                          Contract: ReconComplete

                    Planning:
                      Agent: Planner
                      Transitions:
                        - To: Implementation
                          Signal: "HANDOFF TO DEVELOPER"
                          Contract: BriefExists

                    Implementation:
                      Agent: Developer
                      Transitions:
                        - To: Review
                          Signal: "HANDOFF TO REVIEWER"
                          Contract: ImplementationComplete
                        - To: Planning
                          Signal: "REPLAN REQUIRED"

                    Review:
                      Agent: Reviewer
                      Transitions:
                        - To: Done
                          Signal: APPROVED
                        - To: Implementation
                          Signal: "REVISION REQUIRED"
                        - To: Planning
                          Signal: "REPLAN REQUIRED"

                    Done:
                      Agent: Reviewer
                      Terminal: true

              Termination:
                Type: composite
                Strategies:
                  - Type: regex
                    Pattern: "\\bAPPROVED\\b"
                    AgentNames: [Reviewer]
                  - Type: maxiterations
                    MaxIterations: 60
            """;

        return new GeneratedConfig(mainConfig, [
            ("agents/archaeologist.yaml", archaeologist),
            ("agents/planner.yaml",       planner),
            ("agents/developer.yaml",     developer),
            ("agents/reviewer.yaml",      reviewer),
        ]);
    }

    // ─── Graph ──────────────────────────────────────────────────────────────────

    private static GeneratedConfig Graph(string model, string? endpoint)
    {
        var planner = $"""
            Name: Planner
            Description: Analyses the task and writes a structured brief.
            Instructions: |
              You are a software architect. Your job is to:
              1. Read and understand the task thoroughly.
              2. Use sub_agent_explore for broad codebase questions without filling your context
                 with raw file contents.
              3. Write a brief to {FuseraftPaths.LocalBrief} with fields:
                   goal — one-sentence description of what to build
                   files_to_change — array of file paths to create or modify
                   acceptance_criteria — array of testable criteria the code must satisfy
              4. Break work into concrete steps for the Developer.
              When done, call handoff(route_keyword: "HANDOFF TO DEVELOPER").
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - Search
              - SubAgent
              - Handoff
            FunctionChoice: required
            {AgentFileOptions}
            """;

        var developer = $"""
            Name: Developer
            Description: Implements the changes described in the brief.
            Instructions: |
              You are a senior software engineer. Your job is to:
              1. Read {FuseraftPaths.LocalBrief} and implement every listed file using write_file.
              2. Run a build command with shell_run to confirm it compiles.
              3. Commit your work with git_add and git_commit.
              When done, call handoff(route_keyword: "HANDOFF TO TESTER").
              If the brief is unclear or needs rethinking, call handoff(route_keyword: "REPLAN REQUIRED").
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - Shell
              - Git
              - Changes
              - Handoff
            FunctionChoice: required
            {AgentFileOptions}
            """;

        var tester = $"""
            Name: Tester
            Description: Writes and runs tests, produces a structured test report.
            Instructions: |
              You are a QA engineer. Your job is to:
              1. Read {FuseraftPaths.LocalBrief} to understand the acceptance criteria.
              2. Write tests and run them with shell_run.
              3. Write results to {FuseraftPaths.LocalTestReport} with fields:
                   passed — true or false
                   results — array of objects, each with name, status (PASS or FAIL), exit_code, and command
                   command — the exact shell command you ran to verify this result (required for every PASS)
              A PASS result with an empty or missing command field is treated as fabricated and will block handoff.
              If all tests pass, call handoff(route_keyword: "HANDOFF TO REVIEWER").
              If any tests fail, call handoff(route_keyword: "BUGS FOUND").
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - Shell
              - Changes
              - Handoff
            FunctionChoice: required
            {AgentFileOptions}
            """;

        var reviewer = $"""
            Name: Reviewer
            Description: Reviews implementation and test results; gives final approval or requests changes.
            Instructions: |
              You are a principal engineer. Your job is to:
              1. Read the implementation and {FuseraftPaths.LocalTestReport}.
              2. Run at least one acceptance criterion as a spot-check with shell_run.
              3. Emit a JSON review block listing each acceptance criterion with verdict (PASS/FAIL)
                 and evidence before your routing keyword.
              If all criteria pass, call handoff(route_keyword: "APPROVED").
              If targeted fixes are needed, call handoff(route_keyword: "REVISION REQUIRED") and explain what to fix.
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - Shell
              - Changes
              - Handoff
            FunctionChoice: auto
            ContextWindow:
              TextOnly: true
            {AgentFileOptions}
            """;

        var mainConfig = $"""
            Orchestration:
              Name: Graph Pipeline
              Description: >-
                Planner → Developer → Tester → Reviewer expressed as a declarative directed graph.
                Back-edges (BUGS FOUND, REVISION REQUIRED, REPLAN REQUIRED) return to earlier nodes
                without restarting the full pipeline. APPROVED routes to a terminal confirmation node.

              ChangeTracking:
                Path: {FuseraftPaths.LocalChanges}

              Validation:
                BriefPath: {FuseraftPaths.LocalBrief}
                TestReportPath: {FuseraftPaths.LocalTestReport}
                ChangeLogPath: {FuseraftPaths.LocalChanges}

              Events:
                Path: {FuseraftPaths.LocalEventsLog}

              # Each agent lives in its own YAML file in agents/ — edit, version, or reuse
              # them independently across configs. Inline fields override the file at load time.
              Agents:
                - AgentFile: agents/planner.yaml
                - AgentFile: agents/developer.yaml
                - AgentFile: agents/tester.yaml
                - AgentFile: agents/reviewer.yaml

              Selection:
                Type: graph
                Graph:
                  EntryNode: planner
                  MaxRetries: 4   # max consecutive validator failures per node before HITL escalation

                  # Nodes bind agents to named positions in the graph. IDs are stable,
                  # lowercase, and appear in event logs. Multiple nodes may share an agent.
                  Nodes:
                    - Id: planner
                      Agent: Planner
                    - Id: developer
                      Agent: Developer
                    - Id: tester
                      Agent: Tester
                    - Id: reviewer
                      Agent: Reviewer             # routes on keyword — NOT terminal
                    - Id: approved                # terminal node — session ends after this run
                      Agent: Reviewer             # same agent, terminal confirmation
                      Terminal: true

                  # Edges define control flow — first matching edge fires each turn.
                  # Forward edges (target has higher BFS layer) use SendMessage within the
                  # current MAF phase. Back-edges (lower layer) yield and restart the phase
                  # loop from the target node, enabling cycles without a DAG violation.
                  Edges:
                    # ── Forward edges ──────────────────────────────────────────────
                    - From: planner
                      To: developer
                      Keyword: "HANDOFF TO DEVELOPER"
                      Validators: [RequireBrief]      # blocks until brief.json is valid

                    - From: developer
                      To: tester
                      Keyword: "HANDOFF TO TESTER"
                      Validators: [RequireWriteFile]  # blocks until at least one file is written

                    - From: tester
                      To: reviewer
                      Keyword: "HANDOFF TO REVIEWER"
                      Validators: [TestReportValid]   # blocks until test-report.json passes

                    - From: reviewer
                      To: approved
                      Keyword: "APPROVED"
                      Validators: [RequireReviewJudgement]  # gate on edge; terminal node runs clean

                    # ── Back-edges (cycles) ─────────────────────────────────────────
                    - From: tester
                      To: developer
                      Keyword: "BUGS FOUND"           # test failures → back to developer

                    - From: reviewer
                      To: developer
                      Keyword: "REVISION REQUIRED"    # review feedback → back to developer

                    - From: developer
                      To: planner
                      Keyword: "REPLAN REQUIRED"      # unclear brief → back to planner

              Termination:
                Type: composite
                Strategies:
                  - Type: regex
                    Pattern: "\\bAPPROVED\\b"
                    AgentNames: [Reviewer]
                  - Type: maxiterations
                    MaxIterations: 60

              # ---------------------------------------------------------------------------
              # OPTIONAL EXTRAS — uncomment as needed
              # ---------------------------------------------------------------------------

              # EvidenceStore:              # required for evidence contracts
              #   Path: {FuseraftPaths.LocalEvidence}

              # Contracts:
              #   - Name: BriefExists
              #     Requires:
              #       - Type: FileExists
              #         Path: {FuseraftPaths.LocalBrief}

              # Security:
              #   FileSystemSandboxPath: ~/my-project

              # Compaction:
              #   TriggerTurnCount: 30
              #   KeepRecentTurns: 8
              #   Mode: lossless

              # Checkpoint:
              #   Mode: json
              #   Path: .fuseraft/checkpoints

              # Models:
              #   fast:
              #     ModelId: {model}
              #   reasoning:
              #     ModelId: {model}
            """;

        return new GeneratedConfig(mainConfig, [
            ("agents/planner.yaml",   planner),
            ("agents/developer.yaml", developer),
            ("agents/tester.yaml",    tester),
            ("agents/reviewer.yaml",  reviewer),
        ]);
    }

    // ─── BrownfieldGraph ────────────────────────────────────────────────────────

    /// <summary>
    /// Brownfield variant using Selection.Type: graph. The key showcase relative to the
    /// statemachine brownfield template is the Reviewer's two distinct back-edge targets:
    /// "REVISION REQUIRED" returns to Developer (targeted fix) while "REPLAN REQUIRED"
    /// returns to Planner (approach rethink). Expressing this in a state machine requires
    /// an extra state and duplicated transitions; the graph expresses it as two labelled
    /// edges from a single node.
    /// </summary>
    private static GeneratedConfig BrownfieldGraph(string model, string? endpoint)
    {
        var archaeologist = $"""
            Name: Archaeologist
            Description: Recons the codebase and writes the discovery brief and convention profile.
            Instructions: |
              You are a codebase archaeologist. Your job is to understand an existing project
              before any changes are made. Follow this procedure:

              1. Read the entry point files listed in the task to orient yourself.
              2. Use list_files and sub_agent_explore to map the directory structure — do NOT
                 read every file; focus on understanding the shape of the codebase.
              3. Identify: primary language and framework, naming conventions (snake_case vs camelCase),
                 import style, test framework, build system, and key architectural patterns.
              4. Write the convention profile to {FuseraftPaths.LocalConventions} with fields:
                   language, framework, naming_convention, import_style, test_framework,
                   build_command, lint_command, notes (array of key architectural observations).
              5. Identify the files most likely to need modification for the given task.
              6. Write the discovery brief to {FuseraftPaths.LocalBrownfieldBrief} with fields:
                   summary — one paragraph describing the codebase structure
                   in_scope_files — array of file paths likely relevant to the task
                   dependencies — key external dependencies to be aware of
                   risks — array of fragility signals (e.g. no tests, circular deps, god objects)

              When both files are written, call handoff(route_keyword: "RECON COMPLETE").
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - Search
              - SubAgent
              - Handoff
            FunctionChoice: required
            {AgentFileOptions}
            """;

        var planner = $"""
            Name: Planner
            Description: Designs the targeted change based on the discovery brief.
            Instructions: |
              You are a software architect working on an existing codebase. Your job is to:
              1. Read {FuseraftPaths.LocalBrownfieldBrief} to understand the codebase shape and risks.
              2. Read {FuseraftPaths.LocalConventions} to understand the project's conventions — follow them exactly.
              3. Use sub_agent_explore for any additional targeted questions about specific files.
              4. Write a scoped brief to {FuseraftPaths.LocalBrief} with fields:
                   goal — one-sentence description of the change
                   findings — summary of relevant existing code to modify
                   files_to_change — only the files that genuinely need to change
                   acceptance_criteria — observable code properties the change must satisfy
                   convention_notes — specific conventions to follow from the profile
              When done, call handoff(route_keyword: "HANDOFF TO DEVELOPER").
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - Search
              - SubAgent
              - Handoff
            FunctionChoice: required
            {AgentFileOptions}
            """;

        var developer = $"""
            Name: Developer
            Description: Implements the change staying strictly within the scoped file list.
            Instructions: |
              You are a developer working carefully inside an existing codebase. Your job is to:
              1. Read {FuseraftPaths.LocalBrief} — implement ONLY the files listed in files_to_change.
              2. Read {FuseraftPaths.LocalConventions} — follow the project's naming, import, and style conventions exactly.
              3. Use read_file to read existing files before modifying them — never overwrite blindly.
              4. Use patch_file for surgical edits to existing files; use write_file only for new files.
              5. Run the build command from the convention profile to confirm nothing is broken.
              6. Commit with git_add and git_commit.
              When done, call handoff(route_keyword: "HANDOFF TO REVIEWER").
              If the brief is fundamentally unclear or the approach is wrong, call handoff(route_keyword: "REPLAN REQUIRED").
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - Shell
              - Git
              - Changes
              - Handoff
            FunctionChoice: required
            {AgentFileOptions}
            """;

        var reviewer = $"""
            Name: Reviewer
            Description: Code-review-only inspection; routes to Developer, Planner, or final approval.
            Instructions: |
              You are a principal engineer reviewing a change to an existing codebase. Your job is to:
              1. Read each file listed in {FuseraftPaths.LocalBrief} under files_to_change.
              2. Verify every acceptance criterion is satisfied by code inspection.
              3. Check that the change follows conventions from {FuseraftPaths.LocalConventions}.
              4. Confirm no files outside files_to_change were modified (use changes_read_latest).
              Do NOT run shell commands — this is a code-inspection-only review.
              Emit a JSON review block listing each acceptance criterion with verdict (PASS/FAIL)
              and evidence before your routing keyword.
              If all criteria pass, call handoff(route_keyword: "APPROVED").
              If targeted fixes are needed, call handoff(route_keyword: "REVISION REQUIRED") and describe what to fix.
              If the approach itself is wrong and the brief needs rethinking, call handoff(route_keyword: "REPLAN REQUIRED").
            Model:
              ModelId: {model}{EpAgent(endpoint)}
            Plugins:
              - FileSystem
              - Changes
              - Handoff
            FunctionChoice: auto
            ContextWindow:
              TextOnly: true
            {AgentFileOptions}
            """;

        var mainConfig = $"""
            Orchestration:
              Name: Brownfield Graph Pipeline
              Description: >-
                Archaeologist → Planner → Developer → Reviewer expressed as a directed graph.
                The Reviewer has two distinct back-edge targets: "REVISION REQUIRED" returns to
                Developer for targeted fixes; "REPLAN REQUIRED" returns to Planner when the
                approach needs rethinking. Multi-target back-edges from a single node are the
                key advantage of graph routing over state machine for complex review cycles.

              Security:
                FileSystemSandboxPath: .   # set to your project root (e.g. ~/projects/myapp)

              Brownfield:
                EntryPoints:
                  - src/   # replace with your actual entry points (e.g. cmd/server/main.go)
                SeedEnvelopeFromBrief: true
                DiscoveryBriefPath: {FuseraftPaths.LocalBrownfieldBrief}
                ConventionProfilePath: {FuseraftPaths.LocalConventions}

              ChangeTracking:
                Path: {FuseraftPaths.LocalChanges}

              Validation:
                BriefPath: {FuseraftPaths.LocalBrief}
                ChangeLogPath: {FuseraftPaths.LocalChanges}

              Events:
                Path: {FuseraftPaths.LocalEventsLog}

              # Each agent lives in its own YAML file in agents/ — edit, version, or reuse
              # them independently across configs. Inline fields override the file at load time.
              Agents:
                - AgentFile: agents/archaeologist.yaml
                - AgentFile: agents/planner.yaml
                - AgentFile: agents/developer.yaml
                - AgentFile: agents/reviewer.yaml

              Selection:
                Type: graph
                Graph:
                  EntryNode: recon
                  MaxRetries: 4

                  Nodes:
                    - Id: recon
                      Agent: Archaeologist
                    - Id: planner
                      Agent: Planner
                    - Id: developer
                      Agent: Developer
                    - Id: reviewer
                      Agent: Reviewer             # routes on keyword — NOT terminal
                    - Id: approved                # terminal node — session ends after this run
                      Agent: Reviewer             # same agent, terminal confirmation
                      Terminal: true

                  # ── Key pattern: Reviewer routes to TWO different back-edge targets ──────
                  # "REVISION REQUIRED" → developer  (fix is targeted; recon/planning stay valid)
                  # "REPLAN REQUIRED"   → planner    (approach is wrong; needs a new brief)
                  # This cannot be expressed in a state machine without duplicating states or
                  # adding a routing guard — in graph it is simply two labelled edges.
                  Edges:
                    # ── Forward edges ──────────────────────────────────────────────────
                    - From: recon
                      To: planner
                      Keyword: "RECON COMPLETE"
                      Validators: [RequireWriteFile]       # blocks until discovery files are written

                    - From: planner
                      To: developer
                      Keyword: "HANDOFF TO DEVELOPER"
                      Validators: [RequireBrief]           # blocks until brief.json is valid

                    - From: developer
                      To: reviewer
                      Keyword: "HANDOFF TO REVIEWER"
                      Validators: [RequireWriteFile]       # blocks until at least one file is written

                    - From: reviewer
                      To: approved
                      Keyword: "APPROVED"
                      Validators: [RequireReviewJudgement] # blocks until a review JSON block exists

                    # ── Back-edges ──────────────────────────────────────────────────────
                    - From: reviewer
                      To: developer
                      Keyword: "REVISION REQUIRED"         # targeted fix → restart from developer

                    - From: reviewer
                      To: planner
                      Keyword: "REPLAN REQUIRED"           # rethink approach → restart from planner

                    - From: developer
                      To: planner
                      Keyword: "REPLAN REQUIRED"           # developer can also escalate to planner

              Termination:
                Type: composite
                Strategies:
                  - Type: regex
                    Pattern: "\\bAPPROVED\\b"
                    AgentNames: [Reviewer]
                  - Type: maxiterations
                    MaxIterations: 60

              # ---------------------------------------------------------------------------
              # OPTIONAL EXTRAS — uncomment as needed
              # ---------------------------------------------------------------------------

              # EvidenceStore:
              #   Path: {FuseraftPaths.LocalEvidence}

              # Contracts:
              #   - Name: ReconComplete
              #     Requires:
              #       - Type: FileExists
              #         Path: {FuseraftPaths.LocalBrownfieldBrief}
              #       - Type: FileExists
              #         Path: {FuseraftPaths.LocalConventions}
              #   - Name: BriefExists
              #     Requires:
              #       - Type: FileExists
              #         Path: {FuseraftPaths.LocalBrief}

              # FailureHandling:
              #   MissingEvidence:
              #     Action: Reinstruct
              #     Threshold: 3
              #   NoProgress:
              #     Action: Abort
              #     Threshold: 3

              # Compaction:
              #   TriggerTurnCount: 30
              #   KeepRecentTurns: 8
              #   Mode: lossless

              # Checkpoint:
              #   Mode: json
              #   Path: .fuseraft/checkpoints

              # Models:
              #   fast:
              #     ModelId: {model}
              #   reasoning:
              #     ModelId: {model}
            """;

        return new GeneratedConfig(mainConfig, [
            ("agents/archaeologist.yaml", archaeologist),
            ("agents/planner.yaml",       planner),
            ("agents/developer.yaml",     developer),
            ("agents/reviewer.yaml",      reviewer),
        ]);
    }

    // ─── Minimal ────────────────────────────────────────────────────────────────

    private static string Minimal(string model, string? endpoint) => $"""
        Orchestration:
          Name: Minimal Agent
          Description: A single general-purpose agent for simple tasks.

          Agents:
            - Name: Agent
              Description: Completes the given task using available tools.
              Instructions: |
                You are a capable, methodical assistant. Complete the task step by step,
                using the available tools. When the task is fully done, end with: TASK_COMPLETE
              Model:
                ModelId: {model}{Ep(endpoint, "        ")}
              Plugins:
                - FileSystem
                - Shell

              # ContextWindow:
              #   TextOnly: true          # strip tool-call frames from cross-turn history
              # FunctionChoice: required  # force at least one tool call per turn (auto|required|none)
              # TrustScore: 0.8           # 0.0–1.0; lower scores increase sandbox ring restrictions
              # MaxTokens: 4096           # override model's default max output tokens
              # Capabilities:             # per-plugin tool allowlist
              #   Shell: [shell_run]
              #   FileSystem: [read_file, list_files]

          Selection:
            Type: sequential

          Termination:
            Type: regex
            Pattern: TASK_COMPLETE
            MaxIterations: 20
        {OptionalSections(model, endpoint)}
        """;

    // ─── Magentic ───────────────────────────────────────────────────────────────

    private static string Magentic(string model, string? endpoint) => $"""
        Orchestration:
          Name: Magentic Team
          Description: >
            AI-managed team orchestrated by Magentic. A manager LLM plans the work,
            dynamically selects participants each round, and replans if progress stalls.

          # Named model aliases — agents reference these by alias name so you only need to
          # change the model ID in one place.  The manager benefits from a reasoning-capable
          # model (e.g. o3, claude-opus-4-6, gemini-2.5-pro); both default to '{model}' here.
          Models:
            manager:
              ModelId: {model}{Ep(endpoint, "      ")}
            worker:
              ModelId: {model}{Ep(endpoint, "      ")}

          Agents:
            - Name: Researcher
              Description: Gathers information, searches, and produces sourced summaries.
              Instructions: |
                You are a Researcher. Find information, analyse it, and produce well-sourced
                summaries. Use your tools to search and read content. Be thorough but concise.
              Model:
                ModelId: worker
              Plugins:
                - FileSystem
                - Search
                - Scratchpad

            - Name: Developer
              Description: Writes code, implements features, runs tests, and fixes bugs.
              Instructions: |
                You are a Developer. Write clean, working code that solves the problem.
                Implement what is asked, verify with shell_run, and report results accurately.
                Prefer working code over theoretical explanations.
              Model:
                ModelId: worker
              Plugins:
                - FileSystem
                - Shell
                - Git
                - Scratchpad

          Selection:
            Type: magentic
            Magentic:
              # The manager drives the planning and progress-evaluation loop.
              # A reasoning-capable model is strongly recommended for this role.
              Model:
                ModelId: manager
              MaxRoundCount: 20      # hard cap on coordination rounds
              MaxStallCount: 3       # consecutive stalled rounds before replanning
              MaxResetCount: 2       # max replan cycles before terminating
              EnablePlanReview: false  # set to true to approve the plan before execution begins

          # NOTE: The Termination section is IGNORED for Selection.Type 'magentic'.
          # Session end is controlled entirely by MaxRoundCount, MaxStallCount, and
          # MaxResetCount in the Magentic block above.  This section is present only
          # to satisfy the config schema and may be removed.
          Termination:
            Type: maxiterations
            MaxIterations: 50

          Compaction:
            TriggerTurnCount: 50
            KeepRecentTurns: 10

          Checkpoint:
            Mode: json
            Path: .fuseraft/checkpoints

          Events:
            Path: {FuseraftPaths.LocalEventsLog}
        """;

    // ─── Designer ───────────────────────────────────────────────────────────────

    private static string Designer(string model, string? endpoint) => $"""
        Orchestration:
          Name: Fuseraft Orchestration Designer
          Description: >
            A single-agent assistant that designs, writes, and validates fuseraft
            orchestration configurations. Describe your use case and the Designer
            will generate a ready-to-run YAML config, write it to disk, and validate it.

          Agents:
            - Name: Designer
              Description: Designs and validates fuseraft orchestration configurations.
              Instructions: |
                You are a fuseraft orchestration designer. Your job is to help the user
                create a valid fuseraft-cli YAML orchestration configuration.

                PROCESS:
                1. Ask one focused clarifying question if the use case is ambiguous.
                2. Identify: agent roles, which orchestrator, which plugins, routing, termination.
                3. Generate a complete, valid YAML config.
                4. Write it to the path the user specifies (suggest config/orchestration.yaml when unspecified).
                5. Run `fuseraft validate <path>` to confirm it is valid.
                6. Present the result and offer to iterate.

                ORCHESTRATOR SELECTION:
                - Deterministic pipelines → Selection.Type: statemachine (recommended default)
                - Directed-graph pipelines with named nodes and explicit cycles → Selection.Type: graph
                - Open-ended coordination where an LLM should decide who speaks → Selection.Type: magentic
                - Single agent / simple interactive tasks → Selection.Type: sequential or roundrobin

                PLUGINS (available to agents):
                FileSystem — read/write/delete files; Search — grep/find across filesystem;
                Shell — run commands and scripts; Git — git status/diff/add/commit/checkout;
                Http — HTTP GET/POST/PUT/PATCH/DELETE; Scratchpad — persistent per-agent notes;
                Chatroom — shared cross-agent message board; Plan — structured plan read/write;
                SubAgent — spawn a focused sub-agent for wide exploration (avoids context flooding);
                Handoff — explicit routing via handoff(route_keyword: "KEYWORD");
                Changes — read the session change log; Json — JSON read/merge;
                Probe — run arbitrary diagnostic probes; CodeExecution — sandboxed code execution.

                AGENT FIELDS:
                Name (required), Instructions (required), Description (one sentence, used by LLM selectors),
                Model.ModelId, Plugins (list), FunctionChoice (auto|required|none — use required for action
                agents to prevent fabricated tool output), TrustScore (0.0–1.0, default 0.7),
                Capabilities (per-plugin tool filter, e.g. FileSystem: [read_file]),
                ContextWindow.TextOnly (strip tool frames from history — useful for review agents),
                MaxToolCallsPerTurn, MaxInTurnContextTokens, EnableMemory, SubAgentModel, SubAgentPlugins,
                AgentFile (path to a standalone agent YAML — inline fields override the file at load time),
                RemoteAgent.Url (delegate to remote A2A endpoint — ignores Model/Plugins/FunctionChoice/Capabilities).

                ROUTING:
                - statemachine: States with Agent, Transitions (Signal, To, optional Contract for evidence gates).
                  Agents signal transitions with handoff(route_keyword: "SIGNAL") or plain keyword on its own line.
                - graph: Graph.Nodes bind agents to named IDs; Graph.Edges carry Keyword + optional Validators.
                  Forward edges (higher BFS layer) use SendMessage within a phase; back-edges (lower layer)
                  restart the phase loop from the target — enabling cycles. Terminal: true ends the session.
                  Use this when you need explicit named positions (multiple nodes per agent) or cycles that
                  don't fit cleanly into a state machine.
                - magentic: manager LLM selects participants dynamically each round. No routing keywords needed.
                - roundrobin / sequential: agents take turns in order.
                - keyword: routes on text patterns in responses.
                - llm: LLM selects the next agent each turn.

                TERMINATION:
                - regex: session ends when a response matches a regex (e.g. Pattern: "\\bAPPROVED\\b").
                - maxiterations: hard cap on total turns.
                - composite: combine multiple strategies (first match wins).
                - llm: LLM decides when to stop.

                EVIDENCE CONTRACTS (optional, for production pipelines):
                EvidenceStore.Path: {FuseraftPaths.LocalEvidence}
                Contracts[]: Name + Requires[] (FileExists, FilesWritten, CommandSucceeded, TestReport).
                Transitions reference a Contract name to gate state advancement.

                BROWNFIELD (for existing codebases):
                Add a Brownfield block with EntryPoints and SeedEnvelopeFromBrief: true.
                Add an Archaeologist agent that writes {FuseraftPaths.LocalBrownfieldBrief} and
                {FuseraftPaths.LocalConventions} before the Planner runs.
                See the 'brownfield' template for a complete example.

                STANDARD PATHS:
                Brief: {FuseraftPaths.LocalBrief}, TestReport: {FuseraftPaths.LocalTestReport},
                Changes: {FuseraftPaths.LocalChanges}, Evidence: {FuseraftPaths.LocalEvidence},
                Events: {FuseraftPaths.LocalEventsLog}

                COMMON AGENT PATTERNS:
                - Planner: FunctionChoice required, Plugins: FileSystem + Search + SubAgent + Handoff
                - Developer: FunctionChoice required, Plugins: FileSystem + Shell + Git + Changes + Handoff
                - Tester: FunctionChoice required, Plugins: FileSystem + Shell + Changes + Handoff
                - Reviewer: FunctionChoice auto, ContextWindow.TextOnly true, Plugins: FileSystem + Changes + Handoff
                - Researcher: Plugins: FileSystem + Search + Http + Scratchpad + Handoff
                - Writer: Plugins: FileSystem + Search + Handoff
                - Archaeologist: FunctionChoice required, Plugins: FileSystem + Search + SubAgent + Handoff

                RULES:
                - Never invent plugin names or field names. Use only those listed above.
                - Always run `fuseraft validate <path>` after writing a config.
                - Ask before overwriting an existing file.
                - When in doubt, read config/examples/ for style reference.
                - Prefer statemachine routing — it is the most predictable and debuggable.
                - Keep agent Instructions focused: what the agent does, what tools to call, and what keyword signals completion.

              Model:
                ModelId: {model}{Ep(endpoint, "      ")}
              FunctionChoice: auto
              Plugins:
                - FileSystem
                - Shell
                - Search
                - SubAgent
              Capabilities:
                Shell: [shell_run]

          Selection:
            Type: roundrobin

          Termination:
            Type: maxiterations
            MaxIterations: 50
        """;
}
