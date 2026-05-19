# Examples

Ready-to-use orchestration configs for common team structures.

---

## Software development team

A four-agent pipeline (Planner → Developer → Tester → Reviewer) using state machine routing with evidence contracts, failure handling, self-verification, and lossless compaction. Copy this as a starting point for any software development task.

**Key features:**
- `statemachine` routing — agents can only advance to the next state; signals from the wrong state are ignored
- Evidence contracts on every forward transition — contract failures inject targeted corrections instead of a generic retry
- `EvidenceStore` records every `write_file`, shell run, and git commit as a typed node
- `Verifier` meta-agent runs every 5 turns and on suspicious transitions to catch hallucinated progress
- Lossless compaction reconstructs context from the evidence graph — no LLM call at compaction time, no hallucination risk
- `ContextWindow.TextOnly` on the Reviewer strips tool frames from its context window (90%+ token reduction)

```yaml
Orchestration:
  Name: SoftwareTeam
  Description: >
    Production-grade software development team. State machine routing ensures
    agents can only advance when evidence contracts are satisfied.

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
        - FileExists:
            Path: .fuseraft/brief.json

    - Name: ImplementationComplete
      Requires:
        - FilesWritten:
            Source: .fuseraft/brief.json
            Field: files_to_change
        - CommandSucceeded:
            Pattern: "build|compile"

    - Name: TestsValid
      Requires:
        - FileExists:
            Path: .fuseraft/test-report.json
        - TestReport:
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

  Agents:
    - Name: Planner
      Description: Analyses the task and writes a structured brief.
      Instructions: |
        You are a software planner. Analyse the task and write .fuseraft/brief.json:
          { "goal": "...", "files_to_change": [{"path": "src/a.go", "reason": "..."}], "acceptance_criteria": [...], "implementation": [{"action": "write", "path": "src/a.go", "description": "..."}] }
        When done, call handoff(route_keyword: "HANDOFF TO DEVELOPER").
      Model:
        ModelId: gpt-4o
      Plugins:
        - FileSystem
        - Handoff
      FunctionChoice: required

    - Name: Developer
      Description: Implements the changes described in the brief.
      Instructions: |
        You are a software developer. Read .fuseraft/brief.json and implement every
        listed file using write_file. Run the build with shell_run to confirm it
        compiles. When done, call handoff(route_keyword: "HANDOFF TO TESTER").
        If you need a clearer plan, call handoff(route_keyword: "REPLAN REQUIRED").
      Model:
        ModelId: gpt-4o
      Plugins:
        - FileSystem
        - Shell
        - Git
        - Changes
        - Handoff
      FunctionChoice: required

    - Name: Tester
      Description: Writes and runs tests, produces a structured report.
      Instructions: |
        You are a software tester. Write tests covering the acceptance criteria in
        .fuseraft/brief.json. Run them with shell_run. Write results to
        .fuseraft/test-report.json:
          { "passed": true, "results": [{ "name": "TestFoo", "status": "PASS" }] }
        If tests fail, call handoff(route_keyword: "BUGS FOUND").
        When all pass, call handoff(route_keyword: "HANDOFF TO REVIEWER").
      Model:
        ModelId: gpt-4o
      Plugins:
        - FileSystem
        - Shell
        - Changes
        - Handoff
      FunctionChoice: required

    - Name: Reviewer
      Description: Reviews implementation and test results.
      Instructions: |
        You are a code reviewer. Read the implementation and .fuseraft/test-report.json.
        If the code meets all acceptance criteria, call handoff(route_keyword: "APPROVED").
        If changes are needed, call handoff(route_keyword: "REVISION REQUIRED") and
        explain exactly what to fix.
      Model:
        ModelId: gpt-4o
      Plugins:
        - FileSystem
        - Shell
        - Changes
        - Handoff
      FunctionChoice: auto
      ContextWindow:
        TextOnly: true

    - Name: Verifier
      Description: Audits the evidence graph for inconsistencies.
      Instructions: |
        You are an evidence auditor. Detect inconsistencies between agent claims and
        recorded evidence.

        1. Call changes_read_latest to see what was actually done.
        2. Compare recorded writes, commands, and exit codes against conversation claims.
        3. If everything is consistent: "Evidence verified — no inconsistencies found."
        4. If you find a discrepancy: "INCONSISTENCY DETECTED: <claimed vs evidence>"
      Model:
        ModelId: gpt-4o-mini
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
        Pattern: \bAPPROVED\b
        AgentNames:
          - Reviewer
      - Type: maxiterations
        MaxIterations: 60

  MaxTotalTokens: 500000
```

A complete runnable version of this config is in `config/examples/orchestration.yaml`.

---

## Brownfield pipeline

A five-agent pipeline designed for large existing codebases where agents must understand existing conventions, limit their write surface to the task scope, and run targeted tests rather than the full suite.

**Key features:**
- `Archaeologist` agent runs first — surveys the codebase from declared entry points, writes a discovery brief (`brief.brownfield.json`) and a convention profile (`conventions.json`)
- `ReconComplete` contract blocks the handoff to Planning until both files exist on disk
- Change envelope seeded automatically from the discovery brief's `in_scope_files` list — the Developer cannot write files outside that list (`[DENIED]` tool result with governance event)
- Convention profile injected into every agent's system prompt at session startup — no re-derivation needed on subsequent runs
- Fragility signals in the discovery brief inform the Planner and Developer to handle high-churn files conservatively
- `TestSelector.FindRelatedCommand` guides the Tester to run only the tests covering changed files
- State machine routing ensures agents can only advance when evidence contracts are satisfied

```yaml
Orchestration:
  Name: BrownfieldPipeline

  Brownfield:
    EntryPoints:
      - src/main.go
    DiscoveryBriefPath: .fuseraft/brief.brownfield.json
    ConventionProfilePath: .fuseraft/conventions.json
    SeedEnvelopeFromBrief: true

  TestSelector:
    FindRelatedCommand: "go test -list . $(go list -f '{{.Dir}}' {file}) 2>/dev/null | head -40"
    FullSuiteCommand: "go test ./..."

  Security:
    FileSystemSandboxPath: .

  EvidenceStore:
    Path: .fuseraft/evidence.json

  ChangeTracking:
    Path: .fuseraft/changes.json

  Contracts:
    - Name: ReconComplete
      Requires:
        - FileExists:
            Path: .fuseraft/brief.brownfield.json
        - FileExists:
            Path: .fuseraft/conventions.json

    - Name: BriefExists
      Requires:
        - FileExists:
            Path: .fuseraft/brief.json

    - Name: ImplementationComplete
      Requires:
        - FilesWritten:
            Source: .fuseraft/brief.json
            Field: files_to_change
        - CommandSucceeded:
            Pattern: "build|compile|test"

    - Name: TestsValid
      Requires:
        - FileExists:
            Path: .fuseraft/test-report.json
        - TestReport:
            NoFailures: true
            HasAssertions: true

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
              RecoveryAgent: Developer
            - To: Implementation
              Signal: "BUGS FOUND"
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
    MaxIterations: 50
    Strategies:
      - Type: regex
        Pattern: "(?m)^\\s*APPROVED\\s*$"
        AgentNames:
          - Reviewer
```

A complete runnable version with full agent instructions (including the Archaeologist's step-by-step recon procedure) is in `config/examples/brownfield.yaml`.

**How the envelope is enforced**

On the first run, the Archaeologist writes `brief.brownfield.json` containing `in_scope_files`. On every subsequent run (and after the first recon completes), the orchestrator reads that file at startup and merges `in_scope_files` into `Security.ChangeEnvelope`. The `SandboxEnforcementFilter` then blocks any `write_file`, `patch_file`, or `delete_file` call that does not match the envelope globs:

```
[DENIED] Path 'src/auth/token.go' is outside the configured change envelope.
Only files matching the declared envelope globs may be written in this session.
Ask the Planner to expand the scope if this file needs to change.
```

The Developer's instructions direct it to call `REPLAN REQUIRED` when this happens, routing back to the Planner to formally expand the scope.

**How the convention profile is injected**

Once `conventions.json` exists (written by the Archaeologist), the orchestrator formats it into a `PROJECT CONVENTIONS` block and appends it to every agent's system prompt:

```
PROJECT CONVENTIONS (detected by Archaeologist — follow these in all code you write):
  Language/ecosystem: go
  Build command: go build ./...
  Test command:  go test ./...
  Naming:       test files match *_test.go
  Error handling: wrap errors with fmt.Errorf("%w", err)
  Forbidden:    no panic() outside main
  Tests:        table-driven tests with testify/require
```

---

## Single agent (minimal)

A single agent with filesystem access — good for quick tasks or as a starting point.

```yaml
Orchestration:
  Name: Assistant
  Agents:
    - Name: Assistant
      Instructions: >-
        You are a helpful software assistant. Use the tools available to read and write
        files, run shell commands, and complete the user's task. When you are done,
        say TASK COMPLETE.
      Model:
        ModelId: gpt-4o
      Plugins:
        - FileSystem
        - Shell
        - Search
      FunctionChoice: auto
  Selection:
    Type: sequential
  Termination:
    Type: composite
    Strategies:
      - Type: regex
        Pattern: TASK COMPLETE
      - Type: maxiterations
        MaxIterations: 20
  MaxTotalTokens: 100000
```

---

## Research team

Two agents: a Researcher who gathers information via web requests and a Writer who synthesises it into a document. The `ResearchComplete` contract ensures the Researcher writes structured findings to disk before handing off — the Writer cannot start from an empty context.

```yaml
Orchestration:
  Name: ResearchTeam

  EvidenceStore:
    Path: .fuseraft/evidence.json

  Contracts:
    - Name: ResearchComplete
      Requires:
        - FileExists:
            Path: .fuseraft/research-findings.md

  Agents:
    - Name: Researcher
      Description: Gathers information from the web and structures findings.
      Instructions: |
        You are a research assistant. Use http_get to fetch content from provided URLs.
        Summarise your findings in structured form and write them to
        .fuseraft/research-findings.md. When done, call handoff(route_keyword: "HANDOFF TO WRITER").
      Model:
        ModelId: gpt-4o
      Plugins:
        - Http
        - Json
        - FileSystem
        - Handoff
      FunctionChoice: required

    - Name: Writer
      Description: Synthesises research into a polished document.
      Instructions: |
        You are a technical writer. Read .fuseraft/research-findings.md and write a
        clear, well-structured document. Save the final document using write_file.
        When done, call handoff(route_keyword: "DOCUMENT COMPLETE").
      Model:
        ModelId: gpt-4o
      Plugins:
        - FileSystem
        - Handoff
      FunctionChoice: required

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
        AgentNames:
          - Writer
      - Type: maxiterations
        MaxIterations: 15

  MaxTotalTokens: 150000
```

---

## Local model team (Ollama)

Two agents running entirely on local models via Ollama. No API keys required.

```yaml
Orchestration:
  Name: LocalTeam
  Models:
    local-fast:
      ModelId: llama3.2
      MaxTokens: 4096
    local-coder:
      ModelId: qwen2.5-coder:7b
      MaxTokens: 8192
  Agents:
    - Name: Planner
      Instructions: >-
        You are a planner. Analyse the task and write a clear plan with numbered steps.
        When done, call handoff(route_keyword: "HANDOFF TO DEVELOPER").
      Model:
        ModelId: local-fast
      Plugins:
        - Handoff
      FunctionChoice: auto
    - Name: Developer
      Instructions: >-
        You are a developer. Follow the plan provided and implement the changes using
        the tools available. When done, call handoff(route_keyword: "TASK COMPLETE").
      Model:
        ModelId: local-coder
      Plugins:
        - FileSystem
        - Shell
        - Handoff
      FunctionChoice: auto
  Selection:
    Type: keyword
    DefaultAgent: Planner
    Routes:
      - Keyword: "HANDOFF TO DEVELOPER"
        Agent: Developer
  Termination:
    Type: composite
    Strategies:
      - Type: regex
        Pattern: TASK COMPLETE
      - Type: maxiterations
        MaxIterations: 20
```

---

## Mixed provider team

Different agents use different providers: GPT-4o for planning, Claude for implementation, a local Llama for review. State machine routing with contracts gates each handoff — the Developer cannot hand off until a build or test command succeeds, and the plan must exist on disk before work begins.

```yaml
Orchestration:
  Name: MixedProviderTeam

  Models:
    planner-model:
      ModelId: gpt-4o
      MaxTokens: 4096
    coder-model:
      ModelId: claude-sonnet-4-6
      MaxTokens: 16384
    reviewer-model:
      ModelId: llama3.2
      MaxTokens: 4096

  EvidenceStore:
    Path: .fuseraft/evidence.json

  Contracts:
    - Name: PlanExists
      Requires:
        - FileExists:
            Path: .fuseraft/plan.md

    - Name: ImplementationComplete
      Requires:
        - CommandSucceeded:
            Pattern: "build|compile|test|make"

  FailureHandling:
    MissingEvidence:
      Action: Reinstruct
      Threshold: 3
    ConflictingEvidence:
      Action: Reinstruct
      Threshold: 2

  Agents:
    - Name: Planner
      Instructions: |
        Analyse the task and write a plan to .fuseraft/plan.md with numbered steps
        and a list of files to change. When done, call handoff(route_keyword: "HANDOFF TO DEVELOPER").
      Model:
        ModelId: planner-model
      Plugins:
        - FileSystem
        - Search
        - Handoff
      FunctionChoice: required

    - Name: Developer
      Instructions: |
        Read .fuseraft/plan.md and implement the changes. Run a build or test command
        to verify your work. When done, call handoff(route_keyword: "HANDOFF TO REVIEWER").
        If the plan is unclear, call handoff(route_keyword: "REPLAN REQUIRED").
      Model:
        ModelId: coder-model
      Plugins:
        - FileSystem
        - Shell
        - Git
        - Handoff
      FunctionChoice: required

    - Name: Reviewer
      Instructions: |
        Review the implementation. If it meets requirements, call handoff(route_keyword: "APPROVED").
        If not, call handoff(route_keyword: "REVISION REQUIRED") and describe what to fix.
      Model:
        ModelId: reviewer-model
      Plugins:
        - FileSystem
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
              Contract: PlanExists

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

        Done:
          Agent: Reviewer
          Terminal: true

  Termination:
    Type: composite
    Strategies:
      - Type: regex
        Pattern: \bAPPROVED\b
        AgentNames:
          - Reviewer
      - Type: maxiterations
        MaxIterations: 30

  MaxTotalTokens: 200000
```

---

## Magentic team

A manager LLM plans the work and coordinates a Researcher and Developer dynamically each round. No routing keywords or conditions needed — the manager reasons about what has been done and picks the right agent next.

A complete version of this config is in `config/examples/magentic-team.yaml`.

**Key features:**
- Manager LLM drives a two-level loop: orientation (once) then per-round ledger evaluation + speaker selection
- Participant agents need no special routing keywords — they just do the work
- Built-in stall detection: if `MaxStallCount` consecutive rounds make no progress, the manager replans
- `Termination` section is ignored; session end is controlled by `MaxRoundCount`, `MaxStallCount`, and `MaxResetCount`
- Named model aliases let you assign a stronger reasoning model to the manager and a faster model to workers

```yaml
Orchestration:
  Name: Magentic Team
  Description: >
    AI-managed team orchestrated by Magentic. A manager LLM plans the work,
    dynamically selects participants each round, and replans if progress stalls.

  Models:
    manager:
      ModelId: gpt-4o          # upgrade to o3 or claude-opus-4-6 for complex tasks
    worker:
      ModelId: gpt-4o-mini

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
      Model:
        ModelId: manager
      MaxRoundCount: 20
      MaxStallCount: 3
      MaxResetCount: 2
      EnablePlanReview: false

  # NOTE: Termination is ignored for Selection.Type 'magentic'.
  Termination:
    Type: maxiterations
    MaxIterations: 50

  Compaction:
    TriggerTurnCount: 50
    KeepRecentTurns: 10

  Checkpoint:
    Mode: json
    Path: .fuseraft/checkpoints
```

Generate this config with `fuseraft init --template magentic`.

---

## With MCP tools

Adding an MCP server that provides browser automation tools alongside built-in plugins:

```yaml
Orchestration:
  Name: WebAgent
  McpServers:
    - Name: Browser
      Transport: stdio
      Command: npx
      Args:
        - "-y"
        - "@modelcontextprotocol/server-puppeteer"
  Agents:
    - Name: WebScraper
      Instructions: >-
        You are a web scraping agent. Use the browser tools to navigate pages, extract
        data, and save it to files. When done, say TASK COMPLETE.
      Model:
        ModelId: gpt-4o
      Plugins:
        - Browser
        - FileSystem
        - Json
      FunctionChoice: auto
  Selection:
    Type: sequential
  Termination:
    Type: composite
    Strategies:
      - Type: regex
        Pattern: TASK COMPLETE
      - Type: maxiterations
        MaxIterations: 15
```

---

## With sandbox and cost cap

A config safe to run on untrusted tasks — file access restricted to a specific project, HTTP limited to known hosts, and a hard cost cap:

```yaml
Orchestration:
  Name: SafeTeam
  Agents: [ ... ]
  Selection: { ... }
  Termination: { ... }
  Security:
    FileSystemSandboxPath: /home/user/projects/sandboxed-project
    HttpAllowedHosts:
      - api.github.com
      - pypi.org
  MaxTotalTokens: 50000
```

---

## With compaction for long tasks

For tasks that may run for many turns (e.g. large refactors, multi-file migrations). `hybrid` mode combines a deterministic evidence reconstruction with an LLM narrative summary — agents get authoritative ground-truth plus the narrative context of what happened.

```yaml
Orchestration:
  Name: LongRunningTeam

  EvidenceStore:
    Path: .fuseraft/evidence.json

  ChangeTracking:
    Path: .fuseraft/changes.json

  Compaction:
    TriggerTurnCount: 40
    KeepRecentTurns: 10
    Mode: hybrid          # lossless reconstruction + LLM narrative

  Agents: [ ... ]
  Selection: { ... }
  Termination:
    Type: composite
    Strategies:
      - Type: regex
        Pattern: \bAPPROVED\b
      - Type: maxiterations
        MaxIterations: 100
```

**Choosing a compaction mode:**

| Mode | When to use |
|------|-------------|
| `lossless` | `statemachine` + `EvidenceStore` configured. No LLM call at compaction time — state position and contract status are reconstructed verbatim from disk. |
| `hybrid` | Same requirements as `lossless`, but also adds an LLM narrative. Agents get both authoritative facts and the story of what happened. |
| `llm` | Any routing strategy. An LLM summarises the compacted turns. Falls back from `lossless`/`hybrid` automatically when no state machine is active. |

When 40 messages have accumulated, the orchestrator compacts the oldest 30 turns into a single context message and continues with the 10 most recent turns verbatim. This can run indefinitely without hitting context limits.

---

## Structured routing — content review pipeline

Three agents use JSON output instead of routing keywords. The Reviewer returns `{"review_result": "Yes"}` or `{"review_result": "No"}` and the orchestrator routes based on that field value, with no keyword scanning involved.

```yaml
Orchestration:
  Name: ContentPipeline
  Agents:
    - Name: Drafter
      Instructions: >-
        Write a draft based on the user's request. Return your response as a JSON
        object: {"draft_content": "<your draft here>"}. Your entire response must
        be valid JSON.
      Model:
        ModelId: gpt-4o
      FunctionChoice: none
    - Name: Reviewer
      Instructions: >-
        Review the draft in the conversation. Check that it is accurate and
        well-written. Return your decision as a JSON object:
        {"review_result": "Yes", "reason": "<explanation>"} if acceptable, or
        {"review_result": "No", "reason": "<what to fix>"} if it needs revision.
        Your entire response must be valid JSON.
      Model:
        ModelId: gpt-4o
      FunctionChoice: none
    - Name: Publisher
      Instructions: >-
        The draft has been approved. Format it as a final polished document and
        print it. End your response with the word PUBLISHED on its own line.
      Model:
        ModelId: gpt-4o
      FunctionChoice: none
  Selection:
    Type: structured
    DefaultAgent: Drafter
    StructuredRoutes:
      - Agent: Reviewer
        Condition:
          Field: draft_content
          Exists: true
        SourceAgents:
          - Drafter
      - Agent: Publisher
        Condition:
          Field: review_result
          Is: "Yes"
        SourceAgents:
          - Reviewer
      - Agent: Drafter
        Condition:
          Field: review_result
          Is: "No"
        SourceAgents:
          - Reviewer
  Termination:
    Type: composite
    Strategies:
      - Type: regex
        Pattern: PUBLISHED
        AgentNames:
          - Publisher
      - Type: maxiterations
        MaxIterations: 15
  MaxTotalTokens: 100000
```

**Key differences from keyword routing:**
- Agents return JSON objects — routing happens on field values, not on specific text appearing in the response
- No `APPROVED`/`REVISION REQUIRED` style keywords needed
- Session end is handled by the `Termination` regex strategy rather than a self-routing route
- The `--diagram` flag renders conditions as edge labels: `review_result = Yes`, `draft_content exists`

---

## Remote agent (A2A federation)

Two local agents collaborate with a third agent hosted at a remote A2A endpoint. The remote agent participates identically to local agents — it receives conversation history, is selected by the routing strategy, and its replies are added to the shared history.

**Key features:**
- `RemoteAgent.Url` points to the base URL of the remote A2A service; the agent card is fetched from `{Url}/.well-known/agent.json` at session startup
- `Model`, `Plugins`, `FunctionChoice`, and `Capabilities` are ignored for remote agents — those are properties of the remote service
- `Instructions`, `TrustScore`, and `ContextWindow` still apply: instructions are prepended to each call, trust score governs the governance ring assignment, and the context window filter controls what history the remote agent sees
- `TimeoutSeconds` defaults to 120; increase it for remote agents with long turn times

```yaml
Orchestration:
  Name: Federated Review Team
  Description: >
    Local planner and developer hand off to a remote specialist reviewer
    hosted as an independent A2A service.

  Agents:
    - Name: Planner
      Instructions: |
        You are a Planner. Break the task into clear steps and delegate
        implementation to the Developer. When development is complete, route
        to the Reviewer with: REVIEWER please review.
      Model:
        ModelId: gpt-4o-mini
      Plugins:
        - Scratchpad

    - Name: Developer
      Instructions: |
        You are a Developer. Implement what the Planner specifies. When done,
        reply: REVIEWER please review.
      Model:
        ModelId: gpt-4o-mini
      FunctionChoice: required
      Plugins:
        - FileSystem
        - Shell
        - Git

    - Name: Reviewer
      Instructions: |
        You are a code reviewer. Assess correctness, security, and style.
        If the work is acceptable reply: DONE. Otherwise reply: REVISION REQUIRED
        and list specific changes.
      TrustScore: 0.65
      RemoteAgent:
        Url: https://reviewer.internal
        TimeoutSeconds: 90

  Selection:
    Type: keyword
    Routes:
      - From: ["*"]
        Keyword: "REVIEWER"
        To: Reviewer
      - From: ["*"]
        Keyword: "REVISION REQUIRED"
        To: Developer

  Termination:
    Type: keyword
    Keyword: "^DONE$"
```

---

## Orchestration designer

A single-agent orchestration that helps you design, write, and validate fuseraft configs interactively. Describe your use case in plain language and the Designer generates a ready-to-run YAML config, writes it to disk, and runs `fuseraft validate` to confirm it is correct.

```
fuseraft init --template designer
fuseraft run "I need a two-agent pipeline: a Researcher that searches the web and a Writer that turns findings into a report."
```

**What the Designer knows:**
- All orchestrator types (`statemachine`, `magentic`, `roundrobin`, `keyword`, `llm`)
- All plugins and their capabilities
- All agent fields including `RemoteAgent` for A2A federation
- Evidence contracts and failure handling patterns
- Common agent role patterns (Planner, Developer, Tester, Reviewer, Researcher, Writer)

**Key features:**
- Asks one focused clarifying question if the use case is ambiguous
- Writes the config to the path you specify, defaults to `.fuseraft/config/orchestration.yaml`
- Runs `fuseraft validate <path>` after every write — you never get a broken config
- Reads `config/examples/` as style reference when generating unfamiliar patterns
- Can iterate — ask it to add an agent, change the routing strategy, or tighten termination

The static config lives at `config/examples/fuseraft-designer.yaml`. Generate it with:

```
fuseraft init --template designer .fuseraft/config/my-designer.yaml
```

Use a strong reasoning model (`claude-sonnet-4-6`, `gpt-4o`, `gemini-2.5-pro`) for best results — the Designer reasons about schema trade-offs, not just field names.
