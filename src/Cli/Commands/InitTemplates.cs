namespace fuseraft.Cli.Commands;

internal static class InitTemplates
{
    internal static string Build(string template, string model, string? endpoint) =>
        template switch
        {
            "research"      => Research(model, endpoint),
            "devops"        => DevOps(model, endpoint),
            "content"       => Content(model, endpoint),
            "minimal"       => Minimal(model, endpoint),
            "code-research" => CodeResearch(model, endpoint),
            "magentic"      => Magentic(model, endpoint),
            "designer"      => Designer(model, endpoint),
            _               => DevTeam(model, endpoint),   // "dev-team" + fallback
        };

    // Returns "\n{pad}Endpoint: {endpoint}" when endpoint is set, otherwise empty.
    private static string Ep(string? endpoint, string pad) =>
        string.IsNullOrWhiteSpace(endpoint) ? string.Empty : $"\n{pad}Endpoint: {endpoint}";

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
      #   Path: .fuseraft/evidence.json

      # Contracts: evidence-gated guards on routes or state machine transitions.
      # Contracts:
      #   - Name: BriefExists
      #     Requires:
      #       - Type: FileExists
      #         Path: .fuseraft/brief.json
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
      #   Path: .fuseraft/changes.json

      # Validation: paths used by built-in routing validators.
      # Validation:
      #   BriefPath: .fuseraft/brief.json
      #   TestReportPath: .fuseraft/test-report.json
      #   ChangeLogPath: .fuseraft/changes.json

      Events:
        Path: .fuseraft/events.jsonl

      # MaxTotalTokens: token budget (input + output combined) — session halts when exceeded.
      # MaxTotalTokens: 200000

      # McpServers: connect to Model Context Protocol tool servers.
      # McpServers:
      #   - Name: my-mcp-server
      #     Command: npx
      #     Args: [-y, "@modelcontextprotocol/server-filesystem", "."]
    """;

    private const string AgentOptions = """

          # ContextWindow:
          #   TextOnly: true          # strip tool-call frames from cross-turn history
          # FunctionChoice: required  # force at least one tool call per turn (auto|required|none)
          # TrustScore: 0.8           # 0.0–1.0; lower scores increase sandbox ring restrictions
          # MaxTokens: 4096           # override model's default max output tokens
          # Capabilities:             # per-plugin tool allowlist
          #   Shell: [shell_run]
          #   FileSystem: [read_file, list_files]
      """;

    private static string DevTeam(string model, string? endpoint) => $"""
        Orchestration:
          Name: Software Development Team
          Description: >-
            Planner → Developer → Tester → Reviewer with state machine routing,
            evidence contracts, failure handling, and self-verification.

          EvidenceStore:
            Path: .fuseraft/evidence.json

          ChangeTracking:
            Path: .fuseraft/changes.json

          Validation:
            BriefPath: .fuseraft/brief.json
            TestReportPath: .fuseraft/test-report.json
            ChangeLogPath: .fuseraft/changes.json

          Contracts:
            - Name: BriefExists
              Requires:
                - Type: FileExists
                  Path: .fuseraft/brief.json

            - Name: ImplementationComplete
              Requires:
                - Type: FilesWritten
                  Source: .fuseraft/brief.json
                  Field: files_to_change
                - Type: CommandSucceeded
                  Pattern: "build|compile"

            - Name: TestsValid
              Requires:
                - Type: FileExists
                  Path: .fuseraft/test-report.json
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
            Path: .fuseraft/events.jsonl

          Agents:
            - Name: Planner
              Description: Analyses the task and writes a structured brief.
              Instructions: |
                You are a software architect and planner. Your job is to:
                1. Read and understand the task thoroughly.
                2. Use sub_agent_explore for broad codebase questions without filling
                   your context with raw file contents.
                3. Write a brief to .fuseraft/brief.json with fields:
                     goal — one-sentence description of what to build
                     files_to_change — array of file paths to create or modify
                     acceptance_criteria — array of testable criteria the code must satisfy
                4. Break work into concrete steps for the Developer.
                When done, call handoff(route_keyword: "HANDOFF TO DEVELOPER").
              Model:
                ModelId: {model}{Ep(endpoint, "        ")}
              Plugins:
                - FileSystem
                - Search
                - SubAgent
                - Handoff
              FunctionChoice: required
        {AgentOptions}
            - Name: Developer
              Description: Implements the changes described in the brief.
              Instructions: |
                You are a senior software engineer. Your job is to:
                1. Read .fuseraft/brief.json and implement every listed file using write_file.
                2. Run a build command with shell_run to confirm it compiles.
                3. Commit your work with git_add and git_commit.
                When done, call handoff(route_keyword: "HANDOFF TO TESTER").
                If the plan is unclear, call handoff(route_keyword: "REPLAN REQUIRED").
              Model:
                ModelId: {model}{Ep(endpoint, "        ")}
              Plugins:
                - FileSystem
                - Shell
                - Git
                - Changes
                - Handoff
              FunctionChoice: required
        {AgentOptions}
            - Name: Tester
              Description: Writes and runs tests, produces a structured report.
              Instructions: |
                You are a QA engineer. Your job is to:
                1. Read .fuseraft/brief.json to understand acceptance criteria.
                2. Write tests and run them with shell_run.
                3. Write results to .fuseraft/test-report.json with fields:
                     passed — true or false
                     results — array of objects, each with name, status (PASS or FAIL), and exit_code
                If all pass, call handoff(route_keyword: "HANDOFF TO REVIEWER").
                If any fail, call handoff(route_keyword: "BUGS FOUND").
              Model:
                ModelId: {model}{Ep(endpoint, "        ")}
              Plugins:
                - FileSystem
                - Shell
                - Changes
                - Handoff
              FunctionChoice: required
        {AgentOptions}
            - Name: Reviewer
              Description: Reviews implementation and test results; gives final approval.
              Instructions: |
                You are a principal engineer. Your job is to:
                1. Read the implementation and .fuseraft/test-report.json.
                2. Run at least one acceptance criterion as a spot-check with shell_run.
                If the code meets all acceptance criteria, call handoff(route_keyword: "APPROVED").
                If changes are needed, call handoff(route_keyword: "REVISION REQUIRED") and explain what to fix.
                If the plan is fundamentally wrong, call handoff(route_keyword: "REPLAN REQUIRED").
              Model:
                ModelId: {model}{Ep(endpoint, "        ")}
              Plugins:
                - FileSystem
                - Shell
                - Changes
                - Handoff
              FunctionChoice: auto
              ContextWindow:
                TextOnly: true

            - Name: Verifier
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
                ModelId: {model}{Ep(endpoint, "        ")}
              Plugins:
                - Changes
              FunctionChoice: required

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

    private static string Research(string model, string? endpoint) => $"""
        Orchestration:
          Name: Research Team
          Description: >-
            Researcher gathers information with a verified handoff; Writer synthesises the final document.

          EvidenceStore:
            Path: .fuseraft/evidence.json

          Contracts:
            - Name: ResearchComplete
              Requires:
                - Type: FileExists
                  Path: .fuseraft/research-findings.md

          Agents:
            - Name: Researcher
              Description: Gathers information and writes structured findings to disk.
              Instructions: |
                You are a diligent researcher. Your job is to:
                1. Break the topic into focused questions.
                2. Search for answers using available tools.
                3. Write your structured findings to .fuseraft/research-findings.md.
                When your research is thorough and complete, call handoff(route_keyword: "HANDOFF TO WRITER").
              Model:
                ModelId: {model}{Ep(endpoint, "        ")}
              Plugins:
                - Http
                - Search
                - FileSystem
                - Handoff
              FunctionChoice: required
        {AgentOptions}
            - Name: Writer
              Description: Turns research findings into a polished final document.
              Instructions: |
                You are a skilled technical writer. Your job is to:
                1. Read the research findings from .fuseraft/research-findings.md.
                2. Synthesize a clear, well-structured document that answers the original question.
                3. Write the final document to .fuseraft/report.md.
                When done, call handoff(route_keyword: "DOCUMENT COMPLETE").
              Model:
                ModelId: {model}{Ep(endpoint, "        ")}
              Plugins:
                - FileSystem
                - Handoff
              FunctionChoice: required
        {AgentOptions}
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

    private static string DevOps(string model, string? endpoint) => $"""
        Orchestration:
          Name: DevOps Team
          Description: >-
            Planner → Developer → Operator pipeline for infrastructure and deployment tasks.

          EvidenceStore:
            Path: .fuseraft/evidence.json

          Contracts:
            - Name: PlanExists
              Requires:
                - Type: FileExists
                  Path: .fuseraft/brief.json

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

          Agents:
            - Name: Planner
              Description: Designs the deployment or infrastructure plan.
              Instructions: |
                You are a DevOps architect. Your job is to:
                1. Understand the infrastructure or deployment task.
                2. Use sub_agent_explore to survey relevant config files and scripts.
                3. Write a step-by-step execution plan to .fuseraft/brief.json with fields:
                     goal — what the deployment achieves
                     steps — ordered list of execution steps
                     rollback — steps to undo if something goes wrong
                When the plan is ready, call handoff(route_keyword: "PLANNING_COMPLETE").
              Model:
                ModelId: {model}{Ep(endpoint, "        ")}
              Plugins:
                - FileSystem
                - SubAgent
                - Handoff
              FunctionChoice: required
        {AgentOptions}
            - Name: Developer
              Description: Implements scripts, manifests, and config files.
              Instructions: |
                You are a DevOps engineer. Your job is to:
                1. Read the plan from .fuseraft/brief.json and implement all required
                   scripts, manifests, or config files using write_file.
                2. Run static analysis or validation with shell_run (e.g. lint, validate, check).
                3. Commit with git_add and git_commit when ready.
                When done, call handoff(route_keyword: "DEVELOPMENT_COMPLETE").
                If the plan is unclear, call handoff(route_keyword: "REPLAN_REQUIRED").
              Model:
                ModelId: {model}{Ep(endpoint, "        ")}
              Plugins:
                - FileSystem
                - Shell
                - Git
                - Changes
                - Handoff
              FunctionChoice: required
        {AgentOptions}
            - Name: Operator
              Description: Executes the deployment and verifies success.
              Instructions: |
                You are a site reliability engineer. Your job is to:
                1. Execute the deployment steps from .fuseraft/brief.json using shell_run.
                2. Run smoke tests to verify the deployment succeeded.
                3. Report the outcome clearly with exact command output.
                If successful, call handoff(route_keyword: "DEPLOYMENT_COMPLETE").
                If failed, call handoff(route_keyword: "DEPLOYMENT_FAILED") and describe what went wrong.
              Model:
                ModelId: {model}{Ep(endpoint, "        ")}
              Plugins:
                - Shell
                - Git
                - Changes
                - Handoff
              FunctionChoice: required
        {AgentOptions}
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

    private static string Content(string model, string? endpoint) => $"""
        Orchestration:
          Name: Content Pipeline
          Description: >-
            Writer drafts content with a verified handoff; Editor refines and approves.

          EvidenceStore:
            Path: .fuseraft/evidence.json

          Contracts:
            - Name: DraftExists
              Requires:
                - Type: FileExists
                  Path: output/draft.md

          Agents:
            - Name: Writer
              Description: Produces a complete first draft and saves it to disk.
              Instructions: |
                You are a creative and precise writer. Your job is to:
                1. Understand the content brief from the task.
                2. Write a complete draft and save it to output/draft.md using write_file.
                When the draft is ready for review, call handoff(route_keyword: "DRAFT_COMPLETE").
              Model:
                ModelId: {model}{Ep(endpoint, "        ")}
              Plugins:
                - FileSystem
                - Search
                - Handoff
              FunctionChoice: required
        {AgentOptions}
            - Name: Editor
              Description: Edits for clarity, accuracy, and style; writes the final version.
              Instructions: |
                You are a senior editor. Your job is to:
                1. Read the draft from output/draft.md.
                2. Edit for clarity, accuracy, tone, and structure.
                3. Save the final version to output/final.md using write_file.
                When editing is complete, call handoff(route_keyword: "CONTENT_APPROVED").
              Model:
                ModelId: {model}{Ep(endpoint, "        ")}
              Plugins:
                - FileSystem
                - Handoff
              FunctionChoice: required
        {AgentOptions}
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
        {AgentOptions}
          Selection:
            Type: sequential

          Termination:
            Type: regex
            Pattern: TASK_COMPLETE
            MaxIterations: 20
        {OptionalSections(model, endpoint)}
        """;

    private static string CodeResearch(string model, string? endpoint) => $"""
        Orchestration:
          Name: Code Research Team
          Description: >-
            Planner explores the codebase and writes a findings brief; Developer makes targeted
            changes; Reviewer inspects by code review alone — no test execution required.

          EvidenceStore:
            Path: .fuseraft/evidence.json

          ChangeTracking:
            Path: .fuseraft/changes.json

          Contracts:
            - Name: BriefExists
              Requires:
                - Type: FileExists
                  Path: .fuseraft/brief.json

          FailureHandling:
            MissingEvidence:
              Action: Reinstruct
              Threshold: 3
            NoProgress:
              Action: Abort
              Threshold: 3

          Agents:
            - Name: Planner
              Description: Explores the codebase and writes a structured findings brief.
              Instructions: |
                You are a senior engineer doing code research. Your job is to:
                1. Use sub_agent_explore for broad surveys — avoid loading large files into your context.
                2. Read only the specific sections you need to understand the problem.
                3. Write a brief to .fuseraft/brief.json with fields:
                     goal — one-sentence description of the change to make
                     findings — summary of relevant code locations and logic
                     files_to_change — array of file paths to create or modify
                     acceptance_criteria — array of observable code properties the change must satisfy
                When done, call handoff(route_keyword: "HANDOFF TO DEVELOPER").
              Model:
                ModelId: {model}{Ep(endpoint, "        ")}
              Plugins:
                - FileSystem
                - Search
                - SubAgent
                - Handoff
              FunctionChoice: required
        {AgentOptions}
            - Name: Developer
              Description: Makes targeted code changes described in the brief.
              Instructions: |
                You are a senior developer making a targeted code change. Your job is to:
                1. Read .fuseraft/brief.json and implement the described change.
                2. Modify only the files listed in files_to_change — do not touch other files.
                3. Commit with git_add and git_commit.
                When done, call handoff(route_keyword: "HANDOFF TO REVIEWER").
                If the brief is unclear or findings are incomplete, call handoff(route_keyword: "REPLAN REQUIRED").
              Model:
                ModelId: {model}{Ep(endpoint, "        ")}
              Plugins:
                - FileSystem
                - Shell
                - Git
                - Changes
                - Handoff
              FunctionChoice: required
        {AgentOptions}
            - Name: Reviewer
              Description: Inspects the code change for correctness by code review alone.
              Instructions: |
                You are a principal engineer doing a code review. Your job is to:
                1. Read each file listed in .fuseraft/brief.json under files_to_change.
                2. Verify every acceptance criterion from the brief is satisfied by code inspection.
                3. Confirm no unrelated files were modified.
                Do NOT run shell commands or tests — this is a code-inspection-only review.
                If the change is correct, call handoff(route_keyword: "APPROVED").
                If the change needs revision, call handoff(route_keyword: "REVISION REQUIRED") and explain what to fix.
                If the plan needs rethinking, call handoff(route_keyword: "REPLAN REQUIRED").
              Model:
                ModelId: {model}{Ep(endpoint, "        ")}
              Plugins:
                - FileSystem
                - Changes
                - Handoff
              FunctionChoice: auto
              ContextWindow:
                TextOnly: true

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
                    - To: Review
                      Signal: "HANDOFF TO REVIEWER"
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
                MaxIterations: 20
        {OptionalSections(model, endpoint)}
        """;

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
            Path: .fuseraft/events.jsonl
        """;

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
                Capabilities (per-plugin tool filter, e.g. FileSystem: [read]),
                ContextWindow.TextOnly (strip tool frames from history — useful for review agents),
                MaxToolCallsPerTurn, MaxInTurnContextTokens, EnableMemory, SubAgentModel, SubAgentPlugins,
                RemoteAgent.Url (delegate to remote A2A endpoint — ignores Model/Plugins/FunctionChoice/Capabilities).

                ROUTING:
                - statemachine: States with Agent, Transitions (Signal, To, optional Contract for evidence gates).
                  Agents signal transitions with handoff(route_keyword: "SIGNAL") or plain keyword on its own line.
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
                EvidenceStore.Path: path to evidence graph JSON.
                Contracts[]: Name + Requires[] (FileExists, FilesWritten, CommandSucceeded, TestReport).
                Transitions reference a Contract name to gate state advancement.

                COMMON AGENT PATTERNS:
                - Planner: FunctionChoice required, Plugins: FileSystem + Search + SubAgent + Handoff
                - Developer: FunctionChoice required, Plugins: FileSystem + Shell + Git + Changes + Handoff
                - Tester: FunctionChoice required, Plugins: FileSystem + Shell + Changes + Handoff
                - Reviewer: FunctionChoice auto, ContextWindow.TextOnly true, Plugins: FileSystem + Changes + Handoff
                - Researcher: Plugins: FileSystem + Search + Http + Scratchpad + Handoff
                - Writer: Plugins: FileSystem + Search + Handoff

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
                Shell: [run]

          Selection:
            Type: roundrobin

          Termination:
            Type: maxiterations
            MaxIterations: 50
        """;
}
