# AGENTS.md — fuseraft CLI

Guide for AI coding assistants working in this repository. Read this before making changes.

---

## Build and test

```bash
./build.sh            # full build + test + bin output (Linux/macOS)
.\build.ps1           # full build + test + bin output (Windows)
dotnet build          # build only
dotnet test           # build + run all tests (a few seconds; no live LLM needed)
```

All tests must pass before committing. There are no integration tests that require a live LLM — everything is unit-testable with fakes.

---

## Repository layout

```
src/
  Cli/              Commands/, DevUI/, Tui/ (full-screen REPL), OrchestratorBuilder.cs, SessionRunner.cs
  Core/             Interfaces/, Models/, Exceptions/
  Infrastructure/   AgentFactory.cs, ChatClientFactory.cs, Plugins/, ...
  Orchestration/    Orchestrators, the state machine, validators, contracts, compaction
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

A turn ends only after the agent produces a final text response. This definition is load-bearing: `ChangeTracker` records evidence per turn, gates check "the current turn", and the next edge is chosen only after each turn completes.

---

## Key abstractions

| Interface | What it does | Implementations |
|-----------|-------------|-----------------|
| `IAgentSelector` | Picks the next agent each turn of a Steps flow, and ends the run at `end` | `StateMachineSelectionStrategy` |
| `IRoutingValidator` | History-based gate checks, run through `ContractEngine` as `Validator` predicates | `AnyFileWrittenValidator`, `RequireReviewJudgementValidator`, `RequireAcceptanceCriteriaPassedValidator`, `ConsecutiveShellFailValidator`, `RequireSessionContextWriteValidator`, `ArchitectureValidator` |
| `IOrchestrator` | Drives the agent loop | `AgentOrchestrator` (Steps flows), `MagenticOrchestrator`, `AdversarialOrchestrator`, `MapReduceOrchestrator`, `ScatterGatherOrchestrator` (patterns) |
| `ISessionStore` | Saves/loads checkpoints | `JsonSessionStore`, `InMemorySessionStore` |

---

## Execution order invariant

For every agent turn, control layers are evaluated in the following fixed order:

1. **Selection** — `IAgentSelector.SelectAsync` reads the handoff and finds the candidate edge
2. **Validation** — the edge's gates (its contract) run; failure injects a correction and re-invokes the source agent
3. **Failure handling** — `FailureHandlingConfig` policy applies if validation fails (correction, `OnStuck` agent, or escalation)
4. **Completion** — only an edge to `end` whose gates passed ends the run (the selector returns `null`)
5. **Turn cap** — `MaxTurns` hard stop fires unconditionally

**This ordering must not change.** Gates always run before a handoff — including the one to `end` — takes effect. Any code that ends a run or routes without running the edge's gates violates this invariant.

---

## Routing validators

Validators read disk artifacts or conversation history — they do not call the LLM. Users reach them as gate checks (`requires:` on a flow edge); see `docs/validators.md`.

All validators must be:

- **Deterministic** — same inputs always produce the same result
- **Side-effect free** — must not mutate disk, history, or any external system
- **Idempotent** — safe to call multiple times in the same turn

Validators must not call LLMs or external services. Violations collapse the determinism guarantee that makes the entire correction system work.

A validator failure injects a `ChatRole.User` correction message and re-invokes the source agent. After the configured `Threshold` consecutive failures, `ValidatorStuckException` is thrown.

---

## Change tracking invariant

`ChangeTracker` wraps every agent and records `write_file`, `patch_file`, `copy_file`, `move_file`, `delete_file`, `delete_directory`, `shell_run`, `shell_run_script`, `shell_run_background`, `git_commit` calls to a JSONL log (`.fuseraft/changes.json` by default). `copy_file` and `move_file` destinations are recorded in `FilesWritten`; `move_file` sources and `delete_directory` paths are recorded in `FilesDeleted`.

Files a tool changed without naming them (a `sed` run through `shell_run`, say) reach it through `FileLedger`: the tool-calling setup checks the files the agent has seen around every call, and `FlushTurnAsync` adds the ones that changed during a call to `FilesWritten`/`FilesDeleted`. Only files the agent has read or written are tracked.

Gate checks (`FilesWritten`, `CommandSucceeded`, `TestReport`, `AcceptanceCriteriaPassed`, …) read this log and the evidence graph; the history-based validators use it as a fallback when the history scan finds no matching tool result.

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

The canonical style is already established in `StateMachineSelectionStrategy.BuildTransitionCorrectionMessage`. Match it when adding new correction paths.

---

## Configuration schema

Users write **team files**: a top-level `team:` key, then `models`, `artifacts`, `agents` (a map keyed by name), `flow` (`start` + `steps`, or a `pattern`), `limits`, `security`, `mcpServers` and `advanced`. Keys are camelCase in everything we ship (examples, templates, docs, `migrate` output); the loader matches them case-insensitively and rejects a field written twice in different cases. Gate names are camelCase too (`filesWritten`), so an artifact can't share a gate's name. The format is documented in `docs/configuration.md`.

- `src/Core/Models/Team/TeamConfig.cs` — the user-facing records.
- `src/Cli/Team/TeamConfigLoader.cs` — strict loading: unknown fields are errors; markdown agent files (`Reviewer: agents/reviewer.md`); old `Orchestration:` files and `agentFile:` agents are rejected with a pointer to `fuseraft migrate`.
- `src/Cli/Team/TeamSchema.cs` — the JSON Schema (`fuseraft schema`). Regenerate `schema/team.schema.json` with `fuseraft schema -o schema/team.schema.json` when a team record changes; `TeamSchemaTests` fails until you do.
- `src/Cli/Team/TeamCompiler.cs` — compiles a team into the runtime `OrchestrationConfig`. `steps` always compile to the state machine; every edge's `requires:` becomes one contract; an edge to `end` targets an agentless terminal state that ends the run.
- `src/Cli/Team/TeamChecks.cs` — the gate vocabulary. Each check compiles to contract predicates; history-based checks compile to a `Validator` predicate that runs the routing validator through `ContractEngine`.
- `src/Cli/Team/TeamPrompt.cs` — the generated "Team protocol" section (artifacts, routes, gates) appended to each agent's instructions.
- `src/Cli/Team/LegacyConfigConverter.cs` — `fuseraft migrate`: old `Orchestration:` files to team files. `AgentFileMigrator.cs` does the same for `agentFile:` agents.

Team files are declarative only: don't add includes, overlays, inheritance, variables or expressions (see `docs/design.md#4-configuration`). Reuse is a markdown agent file or a global setting in `~/.fuseraft/config`.

`OrchestrationConfig` and its records are now an internal compile target. Every user-visible fact (a path, a route, an end condition) must have exactly one home in the team file; don't add a team field that duplicates one, and don't add a field that is ignored in some mode — make it a load error instead.

When adding a gate check, add it to `TeamChecks` (name, options, compiled predicates, the sentence agents are told), to the table in `docs/configuration.md#gates`, and to `docs/validators.md`.

`config/examples/` contains runnable examples and `fuseraft init` templates live in `src/Cli/Commands/InitTemplates*.cs`. `InitTemplatesTests` compiles every example and every template, and checks that no agent's instructions name a route it doesn't have. Keep them passing when the schema changes.

When adding a new `FailureAction` or `FailureType` value, update:
- `FailureHandlingConfig.cs` (enum + default config + `GetConfig` switch)
- `FailureClassifier.cs` (classification logic)
- Both strategy `HandleXxxFailure` methods
- `config/examples/` files that declare `advanced.failureHandling`
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
| How is the next agent selected? | `Steps` flows run on `src/Orchestration/Strategies/StateMachineSelectionStrategy.cs`; patterns on their own orchestrators (see `docs/strategies.md`) |
| How is a team file loaded and compiled? | `src/Cli/Team/` (`TeamConfigLoader`, `TeamCompiler`, `TeamChecks`, `TeamPrompt`) |
| How does map-reduce work? | `src/Orchestration/MapReduceOrchestrator.cs`, `src/Core/Models/Orchestration/MapReduceConfig.cs` |
| How does scatter-gather work? | `src/Orchestration/ScatterGatherOrchestrator.cs`, `src/Core/Models/Orchestration/ScatterGatherConfig.cs` |
| How does adversarial orchestration work? | `src/Orchestration/AdversarialOrchestrator.cs` |
| Which flow to use, and how each one behaves in depth | `docs/strategies.md` |
| How do validators work? | `src/Orchestration/Validation/`, full list and config keys in `docs/validators.md` |
| How are contracts evaluated? | `src/Orchestration/Contracts/ContractEngine.cs` |
| What tools do agents have? | `src/Infrastructure/Plugins/` |
| How is the config schema defined? | User-facing: `src/Core/Models/Team/TeamConfig.cs`. Runtime compile target: `src/Core/Models/Orchestration/OrchestrationConfig.cs` and friends |
| How are markdown agent files loaded? | `src/Cli/Team/TeamConfigLoader.cs` → `ReadAgentMarkdown` |
| How does compaction work? | `src/Orchestration/ConversationCompactor.cs` |
| How does change tracking work? | `src/Orchestration/ChangeTracker.cs` |
| How is agent context assembled? | `src/Orchestration/ContextAssemblyPipeline.cs` (main entry point, stages 1–6); `src/Orchestration/ContextAssembler.cs` (per-agent assembled contexts) |
| How are tool results trimmed / tombstoned? | `src/Orchestration/ToolResultWindowTrimmer.cs` |
| Full architecture decisions | `docs/design.md` |
| Hardening configs against hallucination | `docs/harness-engineering.md` |
| How does the full-screen REPL work? | `src/Cli/Tui/ReplTui.cs` — Spectre's output is redirected into a `Transcript` that reads the ANSI back; questions go through `ReplInput` so they're asked in the TUI's panel or on the plain terminal |
| Why does `fuseraft repl` behave differently from an orchestrated agent? | `fuseraft repl` doesn't run through `OrchestratorBuilder` — no validators, change tracking, or routing corrections. See the scope note at the top of `docs/harness-engineering.md`. |
