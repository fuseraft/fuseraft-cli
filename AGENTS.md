# AGENTS.md — fuseraft CLI

Guide for AI coding assistants working in this repository. Read this before making changes.

---

## Build and test

```bash
./build.sh            # full build + test + bin output (Linux/macOS)
.\build.ps1           # full build + test + bin output (Windows)
dotnet build          # build only
dotnet test           # build + run all tests (681 tests, ~1s)
```

All tests must pass before committing. There are no integration tests that require a live LLM — everything is unit-testable with fakes.

---

## Repository layout

```
src/
  Cli/              Commands/, DevUI/, OrchestratorBuilder.cs, SessionRunner.cs
  Core/             Interfaces/, Models/, Exceptions/
  Infrastructure/   AgentFactory.cs, ChatClientFactory.cs, Plugins/, ...
  Orchestration/    Orchestrators, strategies, validators, contracts, compaction, Saga/
  Resources/        FUSERAFT.md (base agent system prompt injected at runtime)

tests/
  FuseraftCli.Tests/   xUnit tests — one file per class under test

config/
  examples/         Runnable YAML/JSON config examples (kept in sync with the schema)

docs/               User-facing documentation
```

---

## Turn definition

A **turn** is one complete invocation of a single agent, including:
- all LLM responses
- all tool calls and their results
- the final assistant text message

A turn ends only after the agent produces a final text response. This definition is load-bearing: `ChangeTracker` records evidence per turn, validators check "the current turn", and termination is evaluated after each turn completes.

---

## Key abstractions

| Interface | What it does | Implementations |
|-----------|-------------|-----------------|
| `IAgentSelector` | Picks the next agent each turn | `KeywordSelectionStrategy`, `StateMachineSelectionStrategy`, `LlmSelectionStrategy`, `SequentialAgentSelector`, `RoundRobinAgentSelector`, `StructuredSelectionStrategy` |
| `ITerminationCondition` | Decides when the session ends | `RegexTerminationCondition`, `MaxIterationsTerminationCondition`, `CompositeTerminationCondition` |
| `IRoutingValidator` | Blocks a handoff unless evidence is present | `RequireBriefValidator`, `HandoffToTesterValidator`, `HandoffToReviewerValidator`, `RequireShellPassValidator`, `RequireAllFilesWrittenValidator`, `RequireReviewJudgementValidator` |
| `IOrchestrator` | Drives the agent loop | `AgentOrchestrator`, `GraphOrchestrator`, `MagenticOrchestrator`, `AdversarialOrchestrator`, `MapReduceOrchestrator`, `ScatterGatherOrchestrator`, `SagaOrchestrator` (compensating rollback wrapper) |
| `ICompensatingAgent` | Rolls back an agent's work when the saga aborts | Provided by callers; none built-in |
| `ISessionStore` | Saves/loads checkpoints | `JsonSessionStore`, `InMemorySessionStore` |

---

## Orchestrator selection

`OrchestratorBuilder` picks the orchestrator at startup:

1. `GraphOrchestrator` — when `Selection.Type == "graph"`; drives a declarative directed graph with named nodes, keyword-gated edges, optional parallel fan-out/fan-in via `Parallel: true` nodes, and hierarchical sub-graphs via `SubGraphId` nodes
2. `MagenticOrchestrator` — when `Selection.Type == "magentic"`
3. `AdversarialOrchestrator` — when `Selection.Type == "adversarial"`; runs fixed generate→critique→revise stages with a context firewall between generator and critic
4. `MapReduceOrchestrator` — when `Selection.Type == "mapreduce"`; runs a three-phase split→parallel-map→reduce pipeline
5. `ScatterGatherOrchestrator` — when `Selection.Type == "scattergather"`; broadcasts the same task to all participants in parallel then synthesises their independent outputs
6. `AgentOrchestrator` — everything else (`keyword`, `statemachine`, `llm`, `sequential`, `roundrobin`, `structured`); driven by an `IAgentSelector` + `ITerminationCondition`

`SagaOrchestrator` wraps whichever orchestrator is selected when `Saga.Enabled == true`.

`StateMachineSelectionStrategy` runs inside `AgentOrchestrator` for the `statemachine` type.

---

## Selection strategies

**`SequentialAgentSelector`** (`sequential` type):
- Iterates through agents in declaration order, one pass. Returns `null` after the last agent, ending the loop.
- Distinct from round-robin: sequential is one-pass; round-robin cycles indefinitely.

**`RoundRobinAgentSelector`** (`roundrobin` type):
- Cycles through agents in declaration order, wrapping after the last. Runs until a `Termination` strategy fires.

**`KeywordSelectionStrategy`** (`keyword` type):
- Keyword must appear **alone on its own line** — not embedded in a sentence
- Routes can be restricted to specific source agents via `SourceAgents`
- Validators run before the route fires; failure injects a correction and re-invokes the source agent
- `RecoveryAgent` on a route activates an alternate agent when the validator fails repeatedly

**`StateMachineSelectionStrategy`** (`statemachine` type):
- Tracks an explicit current state; evaluates that state's outgoing transitions after each turn
- Transitions require signal presence AND all `ContractEngine` predicates to pass
- `RecoveryAgent` on a `TransitionConfig` works identically to the keyword strategy
- Uses the same per-line signal matching as the keyword strategy

**`GraphOrchestrator`** (`graph` type — not a selection strategy):
- Agents are bound to named nodes (`GraphNodeConfig`); directed edges (`GraphEdgeConfig`) carry optional keyword conditions and routing validators
- Forward edges are wired into a MAF `WorkflowBuilder` phase; back-edges restart the outer phase loop from the target node, enabling cycles
- Nodes with `Parallel: true` participate in fan-out groups: a source node fans out to all parallel nodes that share the triggering keyword, runs them concurrently with isolated history snapshots, then merges outputs before advancing
- Nodes with `SubGraphId` run a nested `GraphOrchestrator` (declared in `GraphConfig.SubGraphs`) as a black-box step; the sub-graph's terminal output is injected into the parent's shared history for keyword detection and edge routing
- Terminal nodes end the session after the agent (or sub-graph) executes once; the node may declare its own `Validators` list

**Failure classification** (keyword and statemachine strategies):
- `FailureType` enum: `MissingEvidence`, `InvalidTransition`, `ConflictingEvidence`, `NoProgress`
- `FailureAction` enum: `Reinstruct`, `ActivateRecovery`, `EscalateToHuman`, `Abort`
- Policy is configured per `FailureType` in `FailureHandlingConfig`

---

## Execution order invariant

For every agent turn, control layers are evaluated in the following fixed order:

1. **Selection** — `IAgentSelector.SelectAsync` determines the candidate route
2. **Validation** — validators gate the route; failure injects a correction and re-invokes the source agent
3. **Failure handling** — `FailureHandlingConfig` policy applies if validation fails (correction, recovery agent, or escalation)
4. **Termination** — `ITerminationCondition.ShouldTerminateAsync` is evaluated only after a successful turn completes
5. **Iteration cap** — `MaxIterations` hard stop fires unconditionally

**This ordering must not change.** Validators always gate routing before termination is considered. Any code that evaluates termination before validators, or that routes without running validators, violates this invariant.

---

## Routing validators

Validators read disk artifacts or conversation history — they do not call the LLM.

| Validator | Config key | Blocks until |
|-----------|------------|--------------|
| `RequireBriefValidator` | `RequireBrief` | `brief.json` exists with non-empty `goal`, `files_to_change`, `acceptance_criteria`, `implementation` |
| `HandoffToTesterValidator` | `RequireWriteFile` | A `write_file` call appears in the current turn (or a `ShellFallbackPattern` match) |
| `RequireAllFilesWrittenValidator` | `RequireAllFilesWritten` | Every file in `brief.json`'s `files_to_change` has been written (this turn or recorded in `changes.json`) |
| `RequireShellPassValidator` | `RequireShellPass` | A shell command exited 0 this turn (optionally matching `RequiredCommandPattern`) |
| `HandoffToReviewerValidator` | `TestReportValid` | `test-report.json` exists, all results pass, assertion patterns match, commands cross-check with `changes.json` |
| `RequireReviewJudgementValidator` | `RequireReviewJudgement` | Last reviewer message contains a `{"review": [...]}` JSON block with per-criterion verdicts |

A validator failure injects a `ChatRole.User` correction message and re-invokes the source agent. After the configured `Threshold` consecutive failures, `ValidatorStuckException` is thrown.

### Validator invariants

All validators must be:

- **Deterministic** — same inputs always produce the same result
- **Side-effect free** — must not mutate disk, history, or any external system
- **Idempotent** — safe to call multiple times in the same turn

Validators must not call LLMs or external services. Violations collapse the determinism guarantee that makes the entire correction system work.

---

## Change tracking invariant

`ChangeTracker` wraps every agent and records `write_file`, `patch_file`, `copy_file`, `move_file`, `delete_file`, `delete_directory`, `shell_run`, `shell_run_script`, `shell_run_background`, `git_commit` calls to a JSONL log (`.fuseraft/changes.json` by default). `copy_file` and `move_file` destinations are recorded in `FilesWritten`; `move_file` sources and `delete_directory` paths are recorded in `FilesDeleted`.

`RequireAllFilesWrittenValidator` and `HandoffToReviewerValidator` cross-reference this log. `RequireShellPassValidator` uses it as a fallback when the history scan finds no matching tool result.

**All tools that modify external state (files, shell, git) must be wrapped by `ChangeTracker`.** A tool that bypasses `ChangeTracker` will silently break validators — they will see no evidence of the tool's actions and block routes that should pass. New tools that write files, run commands, or commit to git are invalid unless `ChangeTracker` records them.

---

## Context shaping

Two mechanisms reduce lost-in-the-middle effects for long agent contexts:

**Task Reminder** (`ContextAssembler`): When the assembled context exceeds 2 000 characters and the task string is longer than 50 characters, `ContextAssembler.AssembleAsync` appends a `[Task Reminder]` `ChatRole.User` message (up to 200 chars of the task) at the recency end of the context list. This exploits the primacy+recency sandwich — the task appears both at the top (system prompt) and at the bottom (reminder).

**Context Manifest** (`ToolResultWindowTrimmer` + `AgentOrchestrator`): When `MaxToolResultTokens` is exceeded, `ToolResultWindowTrimmer.ApplyWithManifest` tombstones old results and returns a manifest string listing active vs. superseded tool results. `AgentOrchestrator` appends this manifest as a final `ChatRole.User` message so the agent knows which reads are still in context and which must be re-issued with targeted ranges.

Tombstones now include the evicted tool's name, a key argument label, and up to 300 characters of the original content as a preview:
```
[tool result — evicted: read_file(src/Foo.cs). Preview: "using System;…". Re-read with targeted ranges if needed.]
```

`ToolResultWindowTrimmer.Apply` is still the zero-manifest entry point used by callers that don't need the manifest. Both delegate to the private `ApplyCore`.

---

## Shared history invariant

The system maintains two views of history:

- **Logical history** — the full sequence of events in the order they occurred; never reordered
- **Physical history** — the in-memory `List<ChatMessage>` after compaction; a subset of logical history

Compaction may replace old messages with a summary, but must preserve:
- relative ordering of retained messages
- turn-boundary markers (`[fuseraft: A → B]`)
- last-speaker identity (`AuthorName`)
- all routing signals that could still be active

**Never strip or re-order messages in physical history outside of compaction.** Doing so breaks routing, stale-signal detection, and turn-boundary markers.

Turn-boundary markers are `ChatRole.User` messages of the form `[fuseraft: AgentA → AgentB]`. They are injected on every agent transition and used by selection strategies to detect stale keywords across turns.

---

## Agent-facing strings (correction messages)

Strings injected into conversation history as `ChatRole.User` messages are the primary corrective mechanism. They must be:
- **Specific** — name the exact artifact missing or action required
- **Actionable** — include numbered steps the agent can follow immediately
- **Compact** — no verbose boilerplate; agents have limited context windows

The canonical style is already established in `KeywordSelectionStrategy.BuildCorrectionMessage` and `StateMachineSelectionStrategy.BuildTransitionCorrectionMessage`. Match it when adding new correction paths.

---

## Configuration schema

Config is bound via `OrchestratorBuilder.BindConfig` from `OrchestrationConfig`. The top-level key is `Orchestration`. Both JSON and YAML are supported.

**AgentFile**: Agents may be declared inline or loaded from a standalone YAML file via `AgentFile: path/to/agent.yaml`. Inline fields override the file at load time. `OrchestratorBuilder.ResolveAgentFiles` merges them before agents are built.

**ContractPredicate format**: Predicates in `Contracts[].Requires[]` must use the flat `Type:` field (`Type: FileExists`, `Type: FilesWritten`, etc.) — not the object-key style (`FileExists: {Path: ...}`), which does not bind to `ContractPredicate.Type`.

`config/examples/` contains runnable examples. **Keep these in sync** when adding new config fields — they are the primary reference for users and are checked by tests.

When adding a new `FailureAction` or `FailureType` value, update:
- `FailureHandlingConfig.cs` (enum + default config + `GetConfig` switch)
- `FailureClassifier.cs` (classification logic)
- Both strategy `HandleXxxFailure` methods
- `config/examples/` files that declare `FailureHandling`
- `docs/configuration.md` (actions table)

---

## Testing conventions

- One test file per class: `FooTests.cs` tests `Foo.cs`
- Use `Assert.Contains(substring, actual)` with the **shortest unique substring** of an error message — not the full string. This survives minor wording changes.
- History is built manually as `List<ChatMessage>` with `FunctionCallContent` / `FunctionResultContent` pairs for tool-call scenarios
- No mocks of `ILogger` — pass `NullLogger<T>.Instance`
- No live LLM calls in tests — fake agents and fake validators only

---

## What not to do

- **Do not edit files in the Kiwi repo** — only fix fuseraft-cli orchestration configs
- **Do not add `CorrectRoute` or `TriggerAudit`** — these enum values were removed as dead code; use `Reinstruct` instead
- **Do not call `dotnet test --no-build`** on a fresh change — always let the build run
- **Do not add verbose multi-paragraph docstrings** — one-line summaries only; the `<summary>` tag is enough
- **Do not add comments explaining what code does** — only add comments when the *why* is non-obvious

---

## Where to look

| Question | Where to look |
|----------|--------------|
| How is the next agent selected? | `src/Orchestration/Strategies/KeywordSelectionStrategy.cs`, `StateMachineSelectionStrategy.cs`, `SequentialAgentSelector.cs`, `RoundRobinAgentSelector.cs` |
| How does graph orchestration work? | `src/Orchestration/GraphOrchestrator.cs`, `src/Core/Models/Orchestration/GraphConfig.cs` |
| How do sub-graph nodes work? | `src/Orchestration/GraphOrchestrator.cs` → `BuildExecutorBindings`, `RunSubGraphNodeAsync`; `src/Core/Models/Orchestration/GraphConfig.cs` → `SubGraphs`, `SubGraphId` |
| How does map-reduce work? | `src/Orchestration/MapReduceOrchestrator.cs`, `src/Core/Models/Orchestration/MapReduceConfig.cs` |
| How does scatter-gather work? | `src/Orchestration/ScatterGatherOrchestrator.cs`, `src/Core/Models/Orchestration/ScatterGatherConfig.cs` |
| How does adversarial orchestration work? | `src/Orchestration/AdversarialOrchestrator.cs` |
| How do validators work? | `src/Orchestration/Validation/` |
| How are contracts evaluated? | `src/Orchestration/Contracts/ContractEngine.cs` |
| What tools do agents have? | `src/Infrastructure/Plugins/` |
| How is the config schema defined? | `src/Core/Models/OrchestrationConfig.cs`, `StrategyConfig.cs`, `StateMachineConfig.cs`, `GraphConfig.cs`, `MapReduceConfig.cs`, `ScatterGatherConfig.cs` |
| How does AgentFile loading work? | `src/Cli/OrchestratorBuilder.cs` → `ResolveAgentFiles` |
| How does compaction work? | `src/Orchestration/ConversationCompactor.cs` |
| How does change tracking work? | `src/Orchestration/ChangeTracker.cs` |
| How is agent context assembled? | `src/Orchestration/ContextAssemblyPipeline.cs` (main entry point, stages 1–6); `src/Orchestration/ContextAssembler.cs` (per-agent assembled contexts) |
| How are tool results trimmed / tombstoned? | `src/Orchestration/ToolResultWindowTrimmer.cs` |
| Full architecture decisions | `docs/design.md` |
| Hardening configs against hallucination | `docs/harness-engineering.md` |
