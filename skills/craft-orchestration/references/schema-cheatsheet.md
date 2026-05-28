# Orchestration Schema Cheat Sheet

Quick reference for crafting fuseraft orchestration configs. All fields are YAML; JSON is also accepted with identical keys.

---

## Top-level structure

```yaml
Orchestration:
  Name: <string>
  Description: <string>           # optional, shown at startup

  Models:                         # named aliases — reference by alias in agent Model.ModelId
    fast:
      ModelId: grok-4-1-fast-non-reasoning
      Endpoint: https://api.x.ai/v1
      ApiKeyEnvVar: XAI_API_KEY
    reasoning:
      ModelId: grok-4-1-fast-reasoning
      Endpoint: https://api.x.ai/v1
      ApiKeyEnvVar: XAI_API_KEY

  Agents: [...]                   # at least one required
  Selection: { ... }              # routing strategy
  Termination: { ... }            # stop conditions

  ChangeTracking:                 # enables Changes plugin + validator cross-reference
    Path: .fuseraft/changes.json

  EvidenceStore:                  # enables evidence contracts + lossless compaction
    Path: .fuseraft/evidence.json

  Validation:                     # required for TestReportValid, RequireBrief, RequireAllFilesWritten
    BriefPath: .fuseraft/brief.json
    TestReportPath: .fuseraft/test-report.json
    ChangeLogPath: .fuseraft/changes.json
    TestAssertionPatterns:
      - "tester::assert"
      - "if .+ throw"
      - "\\bassert\\b"
      - "\\bexpect\\b"

  Contracts:                      # named evidence contracts reusable across routes/states
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
          Pattern: "build|compile|test|check"
    - Name: TestsValid
      Requires:
        - Type: FileExists
          Path: .fuseraft/test-report.json
        - Type: TestReport
          NoFailures: true
          HasAssertions: true

  FailureHandling:                # auto-reinstruct or abort on repeated failures
    MissingEvidence:
      Action: Reinstruct
      Threshold: 3
    ConflictingEvidence:
      Action: Reinstruct
      Threshold: 2
    NoProgress:
      Action: Abort
      Threshold: 3

  Verifier:                       # optional meta-agent that audits evidence graph
    AgentName: Verifier
    EveryNTurns: 5
    TriggerOnSuspiciousTransition: true
    FindingsKeyword: INCONSISTENCY

  Compaction:
    TriggerTurnCount: 25
    KeepRecentTurns: 10
    Mode: lossless                # or: summarize

  Checkpoint:
    Mode: json
    Path: .fuseraft/checkpoints

  Events:
    Path: .fuseraft/events.jsonl
```

---

## Agent fields

```yaml
- Name: Developer
  Description: Senior software engineer who implements features.
  Instructions: |
    You are an expert software developer.
    ...
    Call handoff(route_keyword: "HANDOFF TO TESTER").
  Model:
    ModelId: fast                 # alias from Models, or a literal model ID string
    MaxTokens: 16384
  FunctionChoice: required        # forces at least one tool call per turn
  Plugins:
    - FileSystem
    - Shell
    - Handoff
  ContextWindow:
    TextOnly: true                # strip tool-call results from context window
  SubAgentModel: claude-haiku-4-5-20251001   # cheaper model for sub-agent exploration
  SubAgentMaxToolCalls: 20        # cap on sub-agent iterations
  SubAgentPlugins:                # custom plugin list for sub-agent (defaults to read-only set)
    - FileSystem
    - Search
```

---

## All plugin names

| Plugin | What it provides |
|--------|-----------------|
| `FileSystem` | read_file, write_file, patch_file, list_files, search_files, delete_file, … |
| `Shell` | shell_run, shell_run_script, shell_run_background, shell_get_job_* |
| `Git` | git_status, git_diff, git_log, git_add, git_commit, git_push, git_pull, … |
| `Search` | search_files, search_content, search_symbol, search_callers |
| `Http` | http_get, http_post, http_put, http_patch, http_delete |
| `Json` | json_format, json_get, json_keys, json_merge, json_validate |
| `Scratchpad` | scratchpad_write, scratchpad_read, scratchpad_read_all, scratchpad_search |
| `Chatroom` | chatroom_send, chatroom_read |
| `Changes` | changes_read, changes_read_latest — requires `ChangeTracking` in config |
| `SubAgent` | sub_agent_explore, sub_agent_locate |
| `Handoff` | handoff(route_keyword) — terminates tool loop immediately |
| `Probe` | probe_code, probe_assert_output, probe_compare_outputs, probe_run_hypothesis |
| `CodeExecution` | code_execution_sandbox_run, code_execution_repl_start/exec/stop |
| `Compaction` | compact_conversation |
| `Document` | document_extract_text, document_get_info, document_list_sheets |
| `Session` | repl_session_current, repl_session_list, repl_session_read_log |

---

## Routing: keyword

```yaml
Selection:
  Type: keyword
  Routes:
    - Keyword: "HANDOFF TO DEVELOPER"
      Agent: Developer
      SourceAgents: [Planner]
      Validator: RequireBrief           # single validator
      # OR multiple (AND semantics):
      Validators: [RequireWriteFile, RequireShellPass]
      Contracts: [BriefExists]
      RequiredCommandPattern: "go build|go test"   # optional, for RequireShellPass
      ShellFallbackPattern: "npm install|pip install"  # optional, for RequireWriteFile

    - Keyword: "HANDOFF TO TESTER"
      Agent: Tester
      SourceAgents: [Developer]
      Validators: [RequireWriteFile, RequireShellPass]

    - Keyword: "HANDOFF TO REVIEWER"
      Agent: Reviewer
      SourceAgents: [Tester]
      Validator: TestReportValid

    - Keyword: APPROVED
      Agent: Reviewer
      SourceAgents: [Reviewer]
      Validators: [RequireShellPass, RequireReviewJudgement]
```

---

## Routing: state machine

```yaml
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
            RecoveryAgent: Developer    # invoked after N consecutive failures on this transition
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
```

---

## Termination

```yaml
Termination:
  Type: composite
  MaxIterations: 40
  Strategies:
    - Type: regex
      Pattern: '(?m)^\s*APPROVED\s*$'
      AgentNames: [Reviewer]
    - Type: maxiterations
      MaxIterations: 40             # hard cap — always fires regardless of validators
```

---

## Built-in validators

| Name | Attach to | What it enforces |
|------|-----------|-----------------|
| `RequireBrief` | Planner → Developer | `brief.json` exists with non-empty goal, files_to_change, acceptance_criteria |
| `RequireWriteFile` | Developer → Tester | At least one `write_file` or `patch_file` call this turn |
| `RequireAllFilesWritten` | Developer → Tester | Every file in `brief.json`'s `files_to_change` written this session |
| `RequireShellPass` | Any | At least one successful `shell_run` this turn |
| `TestReportValid` | Tester → Reviewer | `test-report.json` exists, no FAILs, non-empty commands, no fake tests |
| `RequireReviewJudgement` | Reviewer → Done | Reviewer emitted `{"review":[...]}` with all PASS verdicts + shell run |
| `RequireRelatedTestsPass` | Developer → Tester | Targeted tests for changed files pass (needs `TestSelector`) |
| `RequireAcceptanceCriteriaPassedValidator` | Developer → Reviewer | Machine-testable criteria verified by real shell output |

---

## Evidence contract predicates

| Type | Key fields | What it checks |
|------|-----------|----------------|
| `FileExists` | `Path` | File exists on disk |
| `FilesWritten` | `Source`, `Field` | Files from a JSON array field were all written |
| `CommandSucceeded` | `Pattern` or `PatternField` | A shell command matching pattern exited 0 |
| `TestReport` | `NoFailures`, `HasAssertions` | `test-report.json` has no FAILs and real assertions |
| `RelatedTestsPass` | — | Tests for changed files pass (needs `TestSelector`) |

---

## Common providers

| Provider | ModelId example | Endpoint | ApiKeyEnvVar |
|----------|----------------|----------|-------------|
| xAI | `grok-4-1-fast-non-reasoning` | `https://api.x.ai/v1` | `XAI_API_KEY` |
| Anthropic | `claude-sonnet-4-6` | `https://api.anthropic.com/v1` | `ANTHROPIC_API_KEY` |
| OpenAI | `gpt-4o` | `https://api.openai.com/v1` | `OPENAI_API_KEY` |
| Ollama (local) | `llama3.1` | `http://localhost:11434/v1` | *(none needed)* |

---

## Brief schema (written by Planner to `.fuseraft/brief.json`)

```json
{
  "goal": "One sentence describing the task.",
  "files_to_change": ["src/api/users.py", "tests/test_users.py"],
  "acceptance_criteria": [
    "GET /users?page=2&limit=10 returns the correct slice",
    "Invalid page values return 400 with a descriptive error",
    "Test files contain real assertions that can fail"
  ],
  "constraints": ["Do not change existing endpoint response shape"]
}
```

## Test report schema (written by Tester to `.fuseraft/test-report.json`)

```json
{
  "results": [
    {
      "criterion": "<exact criterion text from brief.json>",
      "status": "PASS",
      "command": "<exact shell_run command used>",
      "exit_code": 0
    }
  ],
  "fake_test_files": []
}
```

`command` must be non-empty for every PASS entry — an empty command is rejected as fabricated.

---

## Reviewer judgement block (emitted before APPROVED)

```json
{
  "review": [
    { "criterion": "<exact criterion text>", "verdict": "PASS", "evidence": "ran go test ./... exit 0" },
    { "criterion": "<another criterion>",    "verdict": "FAIL", "evidence": "output missing has_next field" }
  ]
}
```

Every acceptance criterion from `brief.json` must have an entry. At least one successful `shell_run` must have been called in the same turn as any PASS verdict.
