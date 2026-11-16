---
title: "fuseraft CLI — Design Document"
description: "This document describes the architecture and design decisions behind fuseraft-cli. It is meant to be a living reference for contributors and for future conversations with AI assistants working in…"
---

This document describes the architecture and design decisions behind fuseraft-cli. It is meant to be a living reference for contributors and for future conversations with AI assistants working in this codebase.

---

## Table of Contents

1. [What It Is](#1-what-it-is)
2. [Layer Map](#2-layer-map)
3. [Directory Layout](#3-directory-layout)
4. [Configuration](#4-configuration)
5. [Agent Construction](#5-agent-construction)
6. [Orchestrators](#6-orchestrators) (AgentOrchestrator, MagenticOrchestrator, AdversarialOrchestrator, MapReduceOrchestrator, ScatterGatherOrchestrator, sub-graph nodes)
7. [Selection](#7-selection)
8. [Ending a run](#8-ending-a-run)
9. [Routing Validators](#9-routing-validators)
10. [Session and Checkpoint Layer](#10-session-and-checkpoint-layer)
11. [Conversation Compaction](#11-conversation-compaction)
12. [Plugin System](#12-plugin-system)
13. [Governance](#13-governance)
14. [Change Tracking](#14-change-tracking)
15. [Event Emission](#15-event-emission)
16. [DevUI](#16-devui)
17. [Microsoft Agent Framework Usage](#17-microsoft-agent-framework-usage)
18. [Decisions Against Framework Features](#18-decisions-against-framework-features)
19. [The `fuseraft serve` Daemon](#19-the-fuseraft-serve-daemon)

---

## 1. What It Is

fuseraft-cli is a multi-agent coordination CLI built on the Microsoft Agent Framework (MAF). It drives teams of AI agents, whether backed by frontier LLMs or local SLMs, through configurable workflows — software development pipelines, research tasks, general automation — with runtime verification of agent contracts, built-in governance, budget control, session persistence, and human-in-the-loop support.

A session is started with a natural-language task. The CLI selects which agents speak, validates routing decisions against deterministic rules, persists the conversation to disk after every turn, and streams output to the terminal and an optional browser-based DevUI.

---

## 2. Layer Map

```
Cli/
  Commands/         — Spectre.Console CLI entry points (run, sessions, init, ...)
  DevUI/            — Lightweight SSE server for browser-based session visualization
  OrchestratorBuilder.cs  — Wires up the full object graph from a config file (no DI host)
  SessionRunner.cs  — Drives the streaming loop; handles HITL, compaction, checkpointing

Core/
  Interfaces/       — IOrchestrator, ISessionStore, IAgentSelector,
                      IRoutingValidator, IHumanApprovalService,
                      IMemoryProvider, IContextAssemblyPipeline, IContextSnapshotter,
                      IEventSink, IOrchestrationHook, IParallelAgentSelector (not
                      necessarily exhaustive — this list drifts as interfaces are added)
  Models/           — OrchestrationConfig, AgentConfig, SessionCheckpoint, AgentMessage,
                      Team/TeamConfig (the user-facing team file), TokenUsage, StrategyConfig,
                      ValidationConfig, MemoryConfig, BudgetExceededException, ...
  Exceptions/       — ValidatorStuckException, AgentBlockedException

Infrastructure/
  AgentFactory.cs         — Builds MAF AIAgent instances from AgentConfig
  ChatClientFactory.cs    — Resolves model aliases; constructs IChatClient per provider
  FalloverChatClient.cs   — IChatClient wrapper that retries on classifiable provider errors
  InMemorySessionStore.cs — In-process checkpoint store (no persistence)
  JsonSessionStore.cs     — File-backed checkpoint store (~/.fuseraft/sessions/)
  LocalMemoryProvider.cs  — IMemoryProvider backed by per-agent MemoryStore (file-based)
  McpSessionManager.cs    — Connects to MCP servers; registers their tools at startup
  MemoryManager.cs        — Aggregates IMemoryProvider instances; pre/post-turn hooks
  WebhookMemoryProvider.cs — IMemoryProvider that delegates to an HTTP endpoint
  Plugins/                — Built-in tool plugins (FileSystem, Shell, Git, Http, ...)

Orchestration/
  AgentOrchestrator.cs       — The loop for Steps flows (state machine selector, gates, verifier)
  MagenticOrchestrator.cs    — Magentic-One style two-level manager/participant loop
  AdversarialOrchestrator.cs — GAN-style adversarial loop; paired generator/critic stages; context firewall isolates the critic
  ContextAssemblyPipeline.cs — Unified context assembly: intent → memory → knowledge → history → prompt; single entry point for all agent invocations
  ConversationCompactor.cs   — LLM-based history summarization; injects tool-call trace into summary prompt
  ChangeTracker.cs           — Intercepts tool calls to record file/shell/git activity
  ContextWindowFilter.cs     — Applies per-agent context window config to conversation history
  EventEmitter.cs            — Appends structured JSONL events to a log file (turn_end, context_assembly, reasoning, ...)
  GraphExpansionRetriever.cs — One-hop graph traversal for KnowledgeWeight.High agents
  KnowledgeRetriever.cs      — Queries IKnowledgeLayer + RepositoryMemoryStore + RepositoryKnowledgeStore
  ObservationExtractor.cs    — Extracts entity-scoped findings from tool call results; builds compaction tool traces
  MemoryManager.cs           — Aggregates IMemoryProvider instances; PreTurnAsync builds the memory block for every agent turn
  Strategies/             — StateMachineSelectionStrategy and the StrategyFactory that builds it
  Validation/             — History-based validators that gate checks run through ContractEngine
  Contracts/              — ContractEngine (gate predicates) and ArtifactShape
```

---

## 3. Directory layout

All runtime artifacts are written under `.fuseraft/` in the current working directory (project-local) or `~/.fuseraft/` in the user's home directory (global). The `FuseraftPaths` static class in `src/Core/FuseraftPaths.cs` is the single authoritative source for every path — config defaults and infrastructure classes all reference it.

**Global (`~/.fuseraft/`)**

A path-refactor (mid-2026) moved nearly all runtime session/state artifacts from project-local `.fuseraft/` into the global `~/.fuseraft/` home directory, keyed by `{project_slug}` (and `{session_id}` for session-scoped files). Only a handful of artifacts remain project-local — see below.

| Path | Contents |
|------|----------|
| `~/.fuseraft/config` | Sectioned global config: provider (model ID, endpoint URL — no secrets), sampling defaults, REPL defaults, MCP servers, telemetry default, skill curation, memory/subagent model overrides. See [CLI Reference — `fuseraft settings`](cli-reference.md#fuseraft-settings). |
| `~/.fuseraft/.key` | Plain-text fallback API key (mode 0600; used only when no keychain) |
| `~/.fuseraft/sessions/` | Session checkpoint files (`<sessionId>.json`, mode 0600) — flat, not nested by `{project_slug}` |
| `~/.fuseraft/sessions/index.json` | Lightweight session index (no message history) for fast listing |
| `~/.fuseraft/sessions/{project_slug}/{session_id}/events.jsonl` | Structured JSONL session events (`EventEmitter`); matches what every `fuseraft init` template, `fuseraft log events`, and the REPL session manifest actually use. (`EventsConfig.Path`'s raw class default is a different, unreferenced path — `~/.fuseraft/logs/sessions/{project_slug}/{session_id}/events.jsonl` — that no generated config or tool falls back to in practice.) |
| `~/.fuseraft/logs/sessions/{project_slug}/{session_id}/ctx_snapshots.jsonl` | Per-turn context-window token snapshots |
| `~/.fuseraft/sessions/{project_slug}/{session_id}/intents.json` | Intent log: pre-execution records updated to APPLIED/FAILED |
| `~/.fuseraft/sessions/{project_slug}/{session_id}/brief.json` | Planner brief (validator input) |
| `~/.fuseraft/sessions/{project_slug}/{session_id}/brief.brownfield.json` | Brownfield discovery brief (`in_scope_files` seeds change envelope) |
| `~/.fuseraft/sessions/{project_slug}/{session_id}/conventions.json` | Brownfield convention profile (auto-injected into agent prompts) |
| `~/.fuseraft/sessions/{project_slug}/{session_id}/chatroom.jsonl` | Shared agent coordination log |
| `~/.fuseraft/sessions/{project_slug}/{session_id}/memory_refs.json` | GUIDs of memories scoped to this working directory |
| `~/.fuseraft/state/{project_slug}/changes.json` | Change tracker: file/shell/git activity per turn |
| `~/.fuseraft/state/{project_slug}/evidence.json` | Evidence graph: typed nodes for contract evaluation |
| `~/.fuseraft/state/{project_slug}/file_versions.json` | Per-file monotonic write counters for conflict detection |
| `~/.fuseraft/logs/{project_slug}/repl_events/{session_id}.jsonl` | REPL session events, one file per session |
| `~/.fuseraft/logs/{project_slug}/provider_errors.jsonl` | LLM provider error records |
| `~/.fuseraft/logs/{project_slug}/app.log` | Warning+ diagnostic log (always-on Serilog file sink, 5 MB rolling, 3 retained) |
| `~/.fuseraft/crashdump/` | Crash dump JSON files |
| `~/.fuseraft/scratchpad/` | Default per-agent scratchpad directory |
| `~/.fuseraft/memory/repl/` | REPL persistent memories |
| `~/.fuseraft/memory/agents/<name>/` | Per-agent persistent memories |

`{project_slug}` is the absolute project path with separators replaced by hyphens and lowercased (e.g. `/home/scs/github/myproject` → `home-scs-github-myproject`). This keeps all session observability data in one global location while remaining trivially filterable by project.

**Local (`.fuseraft/` relative to CWD)**

Only a few artifacts remain project-local; everything session- or state-scoped now lives under `~/.fuseraft/` (above).

| Path | Contents |
|------|----------|
| `.fuseraft/config/orchestration.yaml` | Orchestration config file (default path passed to `fuseraft run`) |
| `.fuseraft/artifacts/test-report.json` | Tester report (validator input) |
| `.fuseraft/context/` | Context store entries and index |
| `.fuseraft/summaries/` | File summaries written by FileSystem plugin |

All paths are configurable via their corresponding config keys. The table above shows defaults.

**Folder orientation for agents** — `FuseraftPaths.BuildFolderOrientationBlock()` generates a compact manifest of the runtime directory layout (both the local `.fuseraft/` artifacts and the global `~/.fuseraft/` session/state paths above) and is appended to every agent's instructions by `OrchestratorBuilder` at session start. This means agents never need to call `list_files` on `.fuseraft/` to discover its layout — they already have it. In REPL mode the log-file entries are omitted (the session section already covers them; agents are directed to the `repl_session_*` tools). `SubagentPlugin` prompts receive a one-line skip directive instead of the full manifest to keep their system prompts compact.

---

## 4. Configuration

Users write a **team file** (`team`, `models`, `artifacts`, `agents`, `flow`, `limits`, `security`, `mcpServers`, `advanced`; see [Configuration](configuration.md)). Keys are written in camelCase and matched case-insensitively, so a PascalCase file still loads; a field written twice in different cases is an error. It is never executed directly. Loading runs in two stages:

1. **`TeamConfigLoader`** reads YAML or JSON strictly: every unknown field is an error, a field written twice in different cases is an error, `agentFile` references are merged, and an old `Orchestration:` file is rejected with a pointer to `fuseraft migrate`.
2. **`TeamCompiler`** compiles the `TeamConfig` into the runtime `OrchestrationConfig`, which everything below consumes.

The compiler is where the format's "say each fact once" rule pays off:

| Team file | Compiles to |
|---|---|
| `flow.steps` | `Selection.Type: statemachine`, one state per step, plus an agentless terminal `end` state; the strategy returns `null` when a transition reaches it, ending the run |
| an edge's `requires:` | one `ContractConfig` named after its gates; each check becomes `ContractPredicate`s (`TeamChecks`), and history-based checks become a `Validator` predicate that `ContractEngine` runs with the conversation |
| edge route names | `TransitionConfig.Signal`, and `AgentConfig.HandoffRoutes`, which turns the `handoff` tool's `route_keyword` into an enum |
| an edge's `approve: true` | `TransitionConfig.RequireHumanApproval`, asked through `IHumanApprovalService.PromptRouteApprovalAsync` after the contract passes |
| `artifacts` + `reads`/`writes` | `file:` context sources, `Validation.BriefPath`/`TestReportPath`, and the generated "Team protocol" section of each agent's instructions (`TeamPrompt`) |
| `sees` | `AgentConfig.Isolation` (`handoff` → `Fresh`, `conversation` → `Shared`, `fork` → `Fork`); `Fresh` flow agents also get `own_history:4` |
| `flow.pattern` | `Selection.Type` of the same name with its config block |
| `limits` | `OrchestrationConfig.MaxTurns` and `MaxTotalTokens` |
| `advanced` | the matching `OrchestrationConfig` sections, bound unchanged |

`ChangeTracking`, `EvidenceStore`, `Events` and `Checkpoint` are always enabled at their default paths, so gates always have evidence to read.

**Environment variable expansion** for `security.httpAllowedHosts`, `advanced.apiProfiles[*].baseUrl`, and all `apiProfiles` header values is performed at startup via `${ENV_VAR}` tokens. Credentials never appear in agent instructions or conversation history.

**Config formats:** Both JSON (`.json`) and YAML (`.yaml` / `.yml`) are supported. YAML is converted to JSON by `YamlConfigLoader.ConvertYamlToJson`, then bound with `System.Text.Json` using `JsonUnmappedMemberHandling.Disallow`.

---

## 5. Agent Construction

`AgentFactory.Create(AgentConfig)` produces a fully configured `AIAgent` ready for use by any orchestrator.

**Steps:**

0. **Remote agent short-circuit** — When `AgentConfig.RemoteAgent` is set, `AgentFactory` resolves the remote agent card from `{Url}/.well-known/agent-card.json` via `A2ACardResolver`, wraps it as an `AIAgent`, and returns immediately. Steps 1–5 below are skipped; `Model`, `Plugins`, `FunctionChoice`, and `Capabilities` are ignored. `Instructions`, `ContextWindow`, and `ChangeTracker` wrapping all continue to apply. `TrustScore` is recorded in the governance audit-emit call for observability, but does **not** govern an execution ring for remote agents — `BuildGovernanceMiddleware` (the only code path that computes a ring via `ComputeRing`) is never reached from this short-circuit, since `SandboxEnforcementFilter` has no local tool surface to enforce against for a remote agent's tool calls (those happen inside the remote A2A service, invisible to this process). `GetAIAgentAsync` is dispatched via `Task.Run` so the blocking `.GetAwaiter().GetResult()` call runs on the thread pool rather than the caller's `SynchronizationContext`, avoiding potential deadlocks in hosted environments.

1. **Identity** — An `AgentIdentity` (DID: `did:fuseraft:<name>`) is created and registered with the `IdentityRegistry`. The governance audit log uses the DID as the actor identifier.

2. **Model resolution** — `ChatClientFactory.Resolve(config.Model)` expands model aliases from the `Models` registry, merging per-agent temperature/max-tokens overrides over the alias baseline. `ChatClientFactory.Create(resolvedModel)` constructs the `IChatClient` for the provider (OpenAI, Azure OpenAI, Anthropic, Ollama via `OllamaApiClient` from `OllamaSharp`).

3. **Tool construction** — Plugins listed in `AgentConfig.Plugins` are resolved from `PluginRegistry`. `Scratchpad` and `Chatroom` are per-agent instances built inline; all others are looked up from the registry.

4. **ChatOptions** — Temperature, max tokens, and `tool_choice` are bundled into a `ChatOptions`. A middleware wrapper merges these defaults into every `GetResponseAsync` / `GetStreamingResponseAsync` call. `ToolMode.RequireAny` is suppressed after the first tool call in a turn (once a tool-result message appears in context) to match OpenAI's behavior and avoid HTTP 400s from providers that reject `tool_choice: required` mid-tool-loop.

5. **Middleware chain** (outermost to innermost):
   - `ChangeTracker.WrapAgent` — intercepts every tool call to record it in the change log. Applied first so it observes the final result of all inner middleware, including sandbox denials.
   - `SandboxEnforcementFilter` — enforces the filesystem sandbox path and routes calls through the governance injection detector. Applied second so the sandbox decision is visible in the change log.
   - `ChatClientAgent` (base) — the MAF `ChatClientAgent` with tools and instructions.

**Tool merge invariant:** The `MergeOptions` helper always ensures `ChatOptions.Tools` is populated from the agent's own list when the inner `FunctionInvokingChatClient` does not set it, preventing `tool_choice` being sent without a tools array (which Bedrock/LiteLLM rejects).

---

## 6. Orchestrators

`OrchestratorBuilder` picks the orchestrator from the compiled config's `Selection.Type`:

1. **`AdversarialOrchestrator`** — `adversarial` pattern
2. **`MapReduceOrchestrator`** — `mapreduce` pattern
3. **`ScatterGatherOrchestrator`** — `scattergather` pattern
4. **`MagenticOrchestrator`** — `magentic` pattern
5. **`AgentOrchestrator`** — every `Steps` flow (`statemachine`)

All five implement `IOrchestrator`:

```csharp
Task<OrchestrationResult> RunAsync(string task, IReadOnlyList<AgentMessage>? priorHistory, CancellationToken ct)
IAsyncEnumerable<AgentMessage> StreamAsync(string task, IReadOnlyList<AgentMessage>? priorHistory, CancellationToken ct)
void SetSessionId(string sessionId)
void SetResumeStateName(string? stateName)     // AgentOrchestrator + StateMachineSelectionStrategy; consumed once
event Action<string>? AgentStarting
event Action<string, string, string?>? ToolCalling        // (agentName, toolName, argsSummary)
event Action<string, int, int>? TokenBudgetWarning        // (agentName, inputTokens, warnThreshold)
```

### 6.1 AgentOrchestrator

The general-purpose path. Drives any selection strategy through a single `while(true)` loop:

1. Call `IAgentSelector.SelectAsync(agents, history)` → get next agent (null = session ends)
2. Call `IContextAssemblyPipeline.AssembleAsync` — single entry point for all context construction:
   - Extracts intent signals (keywords, symbols, failure patterns) from the task
   - Loads and relevance-ranks the agent's persistent memories
   - Retrieves knowledge from the ADR registry, repository graph, repository memory, and session findings store
   - Applies the agent's `ContextWindow` filter to the shared history (or `ContextAssembler.AssembleForAgentAsync` when a `Context:` spec is declared)
   - Injects session context and the knowledge artifact (`[Pipeline Knowledge]`) into the message list
   - Returns `AssembledContext` containing the final message list and `ContextAssemblyMetrics`
3. Call `agent.RunAsync(assembled.Messages, null, null, ct)` via the governance circuit breaker
4. Append all response messages to shared history with `AuthorName` set — the selector always reads from the full history
5. Persist entity-scoped observations to `RepositoryKnowledgeStore` (post-turn)
6. Emit `context_assembly` event with assembly metrics
7. Yield the final text response as an `AgentMessage`
8. Select again; a `null` selection (an edge to `end` whose gates passed) ends the run

**Execution model:**

```
START
  → SelectAgent             (IAgentSelector.SelectAsync)
  → AssembleContext         (IContextAssemblyPipeline.AssembleAsync)
      intent → memory → knowledge → history filter → artifact injection
  → InvokeAgent             (agent.RunAsync via circuit breaker)
  → AppendHistory           (always writes to shared history for routing)
  → PersistFindings         (RepositoryKnowledgeStore, post-turn)
  → EmitContextAssembly     (EventEmitter context_assembly event)
  → CheckTurnCap            (MaxTurns)
  → SelectAgent             (null at `end` → END)
```

**Shared history:** All agents read from and write to the same `List<ChatMessage>`. This is intentional — the state machine reads `AuthorName` from the most recent assistant message to determine who just spoke and where they want to route. `AssembledContext.Messages` is what the *model* sees; the full shared history is what routing sees.

### 6.2 MagenticOrchestrator

A Magentic-One style two-level orchestrator. A dedicated manager LLM drives a planning and evaluation loop; participant agents execute tasks on the manager's instructions.

**Two-history model (the core invariant):**
- `sharedHistory` — what participant agents see: user task + all participant responses
- `managerHistory` — what the manager sees: fact-gather prompt/response, plan, and JSON ledger evaluations only. The manager **never** sees raw participant messages directly.

**How the invariant is enforced — `SummarizeParticipantActivityAsync`:** Before every ledger evaluation, replan, and final-answer synthesis, `MagenticOrchestrator` takes the relevant `sharedHistory` window and sends it to `managerClient` through a separate, isolated, one-shot call under a neutral third-person summarizer system prompt (*not* the manager's own persona/instructions from `_magConfig.Instructions`). Only that call's output — a short bullet summary of what was attempted/succeeded/failed/produced — is what actually reaches the manager: it's what gets embedded in the ledger/replan/final-answer prompt, and it's what `ReplanAsync` persists into `managerHistory`. Raw `[AuthorName]: text` participant dialogue is never added to `managerHistory` or shown to the manager's own reasoning call. This costs one extra LLM call per ledger round / replan / final answer, which is the deliberate tradeoff for the invariant actually holding rather than being aspirational.

**Phase structure:**

1. **Fact gathering** — Manager summarizes what it knows about the task and available agents
2. **Planning** — Manager produces a step-by-step plan. Optional HITL review via `IHumanApprovalService.PromptPlanReviewAsync` (feedback loop with revision until approved)
3. **Inner loop** — For each round:
   - Manager evaluates a JSON progress ledger (`MagenticProgressLedger`) against the current plan and a summary of shared-history activity (see above — never the raw history)
   - If `IsRequestSatisfied`: synthesize final answer → done
   - If stalled (`!IsProgressBeingMade || IsInLoop`): increment stall counter
   - Manager selects next participant and generates a targeted instruction
   - Selected participant context is assembled via `IContextAssemblyPipeline.AssembleAsync` (memory + knowledge + filtered `sharedHistory`); the manager's targeted instruction is appended as the final user message
   - Stall counter ≥ `MaxStallCount` → replan (resets counters); too many replans → terminate

**Why MagenticOrchestrator is not built on `GroupChatWorkflowBuilder`:** The framework's `GroupChatWorkflowBuilder` passes the same shared history to both the manager (`SelectNextAgentAsync`) and participants. Our manager must never see participant messages directly — it reasons from a private ledger. There is also no equivalent to our planning/fact-gathering phases, stall detection, or HITL plan review loop in the framework abstraction. Mapping our design onto `GroupChatManager` would require abusing `UpdateHistoryAsync` to fabricate the manager's context, which would be misleading and fragile. The two-history model is the core architectural invariant that makes this Magentic-style.

**Execution model:**

```
START
  → FactGather
  → Plan  (+ optional HITL review loop until approved)
  → InnerLoop:
      → EvaluateLedger  (MagenticProgressLedger)
      → (IsRequestSatisfied ? SynthesizeAnswer → END)
      → (Stalled ? Replan → reset counters)
      → SelectParticipant + GenerateInstruction
      → InvokeParticipant  (against sharedHistory)
      → UpdateLedger
      → InnerLoop
```

**History isolation invariant:** The manager must not see raw participant messages. The manager may only reason over its own prior outputs, the structured progress ledger, and explicit summaries derived from `sharedHistory`. No implicit leakage from `sharedHistory` to `managerHistory` is permitted. Future changes that "helpfully" pass participant context to the manager violate this invariant and break the two-history model.

**Checkpoint state** (`MagenticCheckpointState`): `CurrentPlan`, `CurrentPlanSteps`, `RoundIndex`, `StallCount`, `ResetCount`, `AwaitingPlanReview` — enough to resume the inner loop exactly where it paused. Exposed via `CurrentState` so `SessionRunner` can snapshot it after each yielded message.

### 6.4 AdversarialOrchestrator

A GAN-style adversarial orchestrator for `Selection.Type: adversarial`. Each `AdversarialStageConfig` pairs a generator agent with a critic agent. Stages run sequentially; the approved artifact from each stage is appended to a shared history that subsequent generators receive as prior context.

**Context firewall (the core invariant):** The critic always receives a fresh context containing only its own system instructions and the artifact under review — never the generator's reasoning chain or prior shared history. This produces genuine independent review rather than rubber-stamping.

**Stage loop:** Within each stage the orchestrator runs up to `Rounds` generate/critique cycles:

1. The generator produces an artifact from its instructions, the accumulated prior-stage `sharedHistory`, and the task.
2. The critic evaluates the artifact in isolation. If it emits `PassKeyword` on its own line, the stage exits early and the artifact is promoted.
3. If not approved and rounds remain, the generator receives its previous artifact plus the critique and revises.
4. If all rounds are exhausted without approval, the last artifact is promoted anyway so the pipeline continues.

After each stage the final artifact is appended to `sharedHistory` as an assistant message so subsequent stages have accumulated context.

**`AdversarialConfig` fields:**

| Field | Purpose |
|---|---|
| `Stages` | Ordered list of `AdversarialStageConfig` (`Generator`, `Critic`, optional `Label`) |
| `Rounds` | Maximum generate/critique cycles per stage (≥ 1) |
| `PassKeyword` | String the critic emits on its own line to signal approval |

**Execution model:**

```
START
  → For each Stage:
      → GenerateArtifact  (generator: instructions + sharedHistory + task)
      → For each Round (1..Rounds):
          → CritiqueArtifact  (critic: fresh context — instructions + artifact only)
          → (PassKeyword found ? PromoteArtifact → next Stage)
          → (rounds remain ? ReviseArtifact → repeat CritiqueArtifact)
      → (rounds exhausted ? PromoteArtifact anyway)
  → END
```

**Why the context firewall matters:** If the critic saw the generator's reasoning chain it would be primed by the same assumptions and more likely to ratify flawed outputs. The fresh-context invariant is what makes adversarial critique structurally independent — violating it turns the orchestrator into a consensus loop rather than a quality gate.

### 6.5 MapReduceOrchestrator

A three-phase data-parallel orchestrator for `Selection.Type: mapreduce`. No MAF DAG is involved — phases are driven directly.

**Phase 1 — Split:** The `Splitter` agent is invoked with the task. Its response must contain a JSON object with a string array at `ItemsJsonPath` (dot-notation). If the JSON is absent or the path resolves to a non-array, the splitter is retried up to `MaxSplitterRetries` times with a correction message. After exhausting retries a hard exception is thrown.

**Phase 2 — Map:** The `Mapper` agent is invoked once per item using `Task.WhenAll`. Each mapper call receives an isolated snapshot of the base history (task + splitter output) plus a user message identifying its specific item. A `SemaphoreSlim` bounds parallelism when `MaxConcurrency > 0`. Results are collected in item-index order before being yielded or merged.

**Phase 3 — Reduce:** The `Reducer` agent receives the full shared history (task + splitter output + all labeled mapper outputs) plus a synthesise prompt, then produces the terminal message.

Each of the three phases assembles its agent's context via `IContextAssemblyPipeline.AssembleAsync` (memory augmentation, ADR/knowledge retrieval, per-agent `advanced.context` spec) when a pipeline is wired, falling back to raw instructions+history otherwise — shared with `ScatterGatherOrchestrator` via `FanOutHelpers.AssembleContextAsync`. Per-turn observations are also persisted to `RepositoryKnowledgeStore` when configured, matching `AgentOrchestrator`/`MagenticOrchestrator`.

**Execution model:**

```
START
  → InvokeSplitter (retry on no JSON array at ItemsJsonPath)
  → (items.Count == 0 ? skip map, prompt reducer directly)
  → Task.WhenAll (mapper × items, bounded by MaxConcurrency)
  → Yield mapper outputs in index order → merge into shared history
  → InvokeReducer → yield terminal message
  → END
```

### 6.6 ScatterGatherOrchestrator

A two-phase broadcast orchestrator for `Selection.Type: scattergather`. Distinct from map-reduce: all participants receive the same task rather than different items split from it. Distinct from graph parallel fan-out: no coordinator node, no keyword trigger, and participants are different named agents (not N copies of the same mapper).

**Phase 1 — Scatter:** All `Participants` are invoked in parallel using `Task.WhenAll`. Each receives an isolated snapshot of the base history with no visibility into other participants' in-progress work. Concurrency is bounded by `MaxConcurrency` when non-zero.

**Phase 2 — Gather:** The `Synthesizer` agent receives the original task history plus every participant's output labeled `[Participant: AgentName]`, then produces the terminal response.

Both phases assemble their agents' context via `IContextAssemblyPipeline.AssembleAsync` when a pipeline is wired (shared with `MapReduceOrchestrator` via `FanOutHelpers.AssembleContextAsync`), and persist per-turn observations to `RepositoryKnowledgeStore` when configured.

**Execution model:**

```
START
  → Task.WhenAll (all Participants, isolated history snapshots, bounded by MaxConcurrency)
  → Yield participant outputs in declaration order → merge into gather history
  → InvokeSynthesizer (task + labeled participant outputs) → yield terminal message
  → END
```

## 7. Selection

A `Steps` flow compiles to a `StateMachineConfig`, and `StrategyFactory.CreateSelection` builds a `StateMachineSelectionStrategy` (the only `IAgentSelector`) with a `ContractEngine` for its gates. Patterns have their own orchestrators and no selector.

**`StateMachineSelectionStrategy`** tracks the current step and evaluates its outgoing transitions after each agent turn:
- The route is read from the agent's `handoff` call (`route_keyword` is an enum of the step's edges) or, for a remote agent, from a route name alone on a line.
- Only the current step's agent can fire its routes; a meta-agent such as the Verifier can't route by writing a route name in prose. User messages are exempt so a HITL user can redirect.
- A transition fires when its route is present AND its one compiled contract passes. A failure is classified by `FailureClassifier`/`FailureHandlingConfig` and injects a typed correction (or calls the `OnStuck` agent after two failures, or escalates).
- A transition to the agentless terminal state `end` makes `SelectAsync` return `null`, which ends the run.
- A verifier agent can be scheduled for the next turn on `ConflictingEvidence` or `NoProgress` failures when `VerifierConfig` is configured.
- Parallel transitions (`To: [A, B]` with `Join`) are exposed through `IParallelAgentSelector`; branches run with isolated history snapshots and are combined by `MergeEngine`.

---

## 8. Ending a run

There are no termination strategies. A run ends when:

- a `Steps` flow takes an edge to `end` whose gates passed (`SelectAsync` returns `null`; `termination_satisfied` is emitted with `reason: "flow_complete"`);
- a pattern orchestrator finishes its own loop;
- `OrchestrationConfig.MaxTurns` (from `Limits.MaxTurns`) is reached; or
- `MaxTotalTokens` (from `Limits.MaxTokens`) is exceeded, which throws `BudgetExceededException` and saves the session.

---

## 9. Routing Validators

Validators implement `IRoutingValidator` and run synchronously before a route or termination fires. Most examine external artifacts (change log, test report, brief file) rather than LLM output — `RequireReviewJudgement` is the one exception, since a review judgement only exists as the reviewer's own message text (see below).

| Validator (gate check) | What it checks |
|---|---|
| `AnyFileWrittenValidator` (`AnyFileWritten`) | A file write, patch or commit in the current turn, or a successful shell command matching the fallback pattern |
| `RequireReviewJudgementValidator` (`ReviewJudgement`) | Parses a structured `{"review":[{criterion, verdict, evidence}]}` block from the agent's reply; no FAIL, one entry per brief criterion, and a successful shell run this turn if any PASS |
| `RequireAcceptanceCriteriaPassedValidator` (`AcceptanceCriteriaPassed`) | Every brief criterion with `expected_output_contains` was met by a successful command's output this run |
| `ConsecutiveShellFailValidator` (`NoRepeatedShellFailures`) | Blocks the handoff when the same command has failed in recent turns, and tells the agent to hand off for replanning |
| `ArchitectureValidator` (`Architecture`) | No architecture layer violations per `.fuseraft/architecture.yaml` |
| `RequireSessionContextWriteValidator` (`SessionContextWritten`) | The agent called `session_context_write` this turn |

The other gate checks (`FilesWritten`, `CommandSucceeded`, `TestReport`, `FileExists`, `ChecklistComplete`, `RelatedTestsPass`, artifact shapes) are `ContractEngine` predicates that read the change log and evidence graph directly.

When a validator fails, the route is blocked: the source agent is re-invoked with an injected error message tailored to the failure type (`MissingEvidence`, `InvalidTransition`, `ConflictingEvidence`, `NoProgress`). The response policy is controlled by `FailureHandlingConfig` — `Reinstruct` (default) injects a correction and retries; `ActivateRecovery` routes to the route's `RecoveryAgent` on the first request; `EscalateToHuman` throws immediately; `Abort` escalates after the configured per-type `Threshold` consecutive failures. When the threshold is reached, `ValidatorStuckException` is thrown and the session escalates to HITL.

**Failure handling pipeline:** All failures follow this flow regardless of which strategy or orchestrator is active:

```
1. Classify  → FailureClassifier.Classify(error, hasToolCalls, isFirstFailure) → FailureType
2. Lookup    → FailureHandlingConfig.GetConfig(failureType) → FailureTypeConfig
3. Execute   → FailureAction (Reinstruct / ActivateRecovery / EscalateToHuman / Abort)
4. Record    → EventEmitter ("validation_fail") + GovernanceKernel audit
5. Continue or terminate (ValidatorStuckException)
```

No component may bypass this pipeline — `StateMachineSelectionStrategy` routes every gate failure through it (see §7). Correction messages injected at step 3 are always `ChatRole.User` messages appended to shared history before the source agent is re-invoked.

---

## 10. Session and Checkpoint Layer

Every session is backed by a `SessionCheckpoint` persisted after each agent turn.

**`SessionCheckpoint` fields:**

| Field | Purpose |
|---|---|
| `SessionId` | 8-character hex ID (`Guid.NewGuid().ToString("N")[..8]`) |
| `Task` | Original task string |
| `ConfigPath` | Config file that produced this session (used on resume) |
| `WorkingDirectory` | Absolute working directory at session start (used by the session index) |
| `Messages` | Ordered `List<AgentMessage>` — the complete conversation transcript |
| `StartedAt` | UTC timestamp of session creation (immutable) |
| `LastUpdatedAt` | UTC timestamp of last save (set by `SaveAsync`) |
| `IsComplete` | Set to `true` after the session runs to completion; prevents re-resume |
| `MagenticState` | `MagenticCheckpointState` snapshot for Magentic loop resume |

**`AgentMessage` fields:** `AgentName`, `Content`, `Role`, `TurnIndex`, `Timestamp`, `Usage` (`TokenUsage`: input/output token counts — no cost/pricing is tracked anywhere in this layer), `IsCompactionSummary`, `ToolCalls` (name, args summary, succeeded).

**`SessionIndexEntry` fields:** `SessionId`, `Task` (first non-empty line, ≤120 chars), `WorkingDirectory`, `ConfigPath`, `StartedAt`, `LastUpdatedAt`, `IsComplete`, `TurnCount`. Written to `~/.fuseraft/sessions/index.json` (keyed by session ID) on every `SaveAsync` and `DeleteAsync` so listing never requires opening checkpoint files.

**`ISessionStore` contract:**
- `SaveAsync` — create or overwrite; sets `LastUpdatedAt`; updates `index.json`
- `LoadAsync` — load by session ID, null if not found
- `DeleteAsync` — removes checkpoint file and removes entry from `index.json`
- `ListAsync` — all checkpoints sorted by `LastUpdatedAt` descending (opens every checkpoint file)
- `ListIndexAsync` — all index entries sorted by `LastUpdatedAt` descending (reads `index.json` only; bootstraps from checkpoint files on first call if index is absent)

**`JsonSessionStore`** (default): one JSON file per session at `~/.fuseraft/sessions/<sessionId>.json`. Unix file permissions set to 0600 on non-Windows. Maintains `index.json` as a side-effect of every save and delete. `fuseraft sessions` and the `--resume` prompt use `ListIndexAsync` — message history is never loaded for listing.

**`InMemorySessionStore`**: `ConcurrentDictionary` backed; sessions lost on process exit. Used when `Checkpoint.Mode = "memory"` in config or when no config-level checkpoint path is set and the user explicitly opts in.

**Save points:** after each agent message, after HITL human redirect, and before/after compaction — all in `SessionRunner`. The completion save (`IsComplete = true`) is set and persisted by `RunCommand.ExecuteAsync` after `SessionRunner.RunAsync` returns, not by `SessionRunner` itself.

**Resume path** (`RunCommand`): `--resume <sessionId>` loads the checkpoint, validates `IsComplete == false`, rehydrates `priorHistory`, and calls `SetResumeStateName` with the checkpoint's `CurrentStateName` before the next `StreamAsync` call, so a `steps` flow resumes at the step it stopped on.

**Why we did not use the MAF framework's checkpointing layer:** The framework's `Checkpoint` type captures MAF workflow execution state — executor queue, edge state, outstanding external requests. Our `SessionCheckpoint` captures conversation semantics — agent messages, token usage, Magentic loop counters. They solve different problems at different levels of abstraction. The framework layer applies only to `GraphOrchestrator` (which uses `InProcessExecution`); `AgentOrchestrator` and `MagenticOrchestrator` are manual loops with no MAF workflow graph. Replacing our layer with the framework's would lose agent identity, role, token usage, and Magentic loop state, while gaining sub-turn recovery that provides no practical benefit given our turns are already fine-grained checkpointed.

---

## 11. Conversation Compaction

`ConversationCompactor` prevents context window exhaustion on long sessions by summarizing older turns using an LLM.

**Trigger:** `ShouldCompact(messages)` returns true when the assistant-message count in `messages` reaches `config.TriggerTurnCount`. Only assistant turns are counted — user messages and tool frames are excluded. The `SessionRunner` resets this count to the retained tail's assistant count after each compaction so the trigger fires relative to the current window, not the session lifetime.

**Process:** The oldest `Count - KeepRecentTurns` messages are compacted into a single summary `AgentMessage`. The retained tail is kept verbatim. The summary is injected with `Role = "user"` so agents treat it as context, and `IsCompactionSummary = true` so tooling can identify it.

**Change log grounding:** When `ChangeTracking` or `Validation.ChangeLogPath` is configured, the compactor reads the change log at compaction time and includes it in the summary prompt as authoritative ground truth. The prompt instructs the LLM to trust the change log over agent self-reports — if an agent claimed success but the change log records a non-zero exit code or no file write, the summary reflects reality. Sessions without a change log use a standard prompt that summarizes conversation claims only.

**Resume note:** For non-Magentic sessions, a standard `WorkflowResumptionNote` is appended to the summary prompt instructing agents to re-read the brief and change log (not available from memory alone after compaction). This note is suppressed for Magentic sessions, which have no brief or change log.

**After compaction:** `CompactionCoordinator` records the state machine's current step in `CurrentStateName`, updates the checkpoint, and saves before continuing.

**Compaction invariants:** Compaction must preserve:
- the last assistant message (always retained verbatim in the tail)
- routing signals that could still be active
- all validator-relevant artifacts, or replace them with equivalent summaries grounded in the change log
- turn-boundary markers (`[fuseraft: A → B]`) in the retained tail

Compaction must never cause a previously valid route to become invalid, or a validator to pass or fail differently than it would against the original history. The routing-signal invariant is enforced by `TryPinLastRoutingSignal` (`CompactionCoordinator`), gated behind `CompactionConfig.PinLastRoutingSignal` (default `true`) — when enabled, the single most recent `HandoffPlugin` signal is re-injected at the head of the retained window if trimming would otherwise have dropped it. This covers the common case (one pending signal) but not a parallel/fan-out transition with multiple branches' signals still pending — only the last one is pinned.

---

## 12. Plugin System

Plugins are `AIFunction`-providing objects registered in `PluginRegistry` and referenced by name in `AgentConfig.Plugins`.

**Built-in plugins:**

| Plugin | Tools |
|---|---|
| `FileSystem` | `read_file`, `grep_file`, `get_file_summary`, `get_file_info`, `save_file_summary`, `list_files`, `list_directory`, `write_file`, `patch_file`, `create_directory`, `copy_file`, `move_file`, `set_permissions`, `delete_file`, `delete_directory` |
| `Shell` | `shell_run`, `shell_run_script`, `shell_run_background`, `shell_set_env`, `shell_get_env`, `shell_get_job_status`, `shell_get_job_output`, `shell_kill_job`, `shell_which`, `shell_get_working_directory`, `shell_get_session_temp_dir` |
| `Git` | `git_status`, `git_diff`, `git_log`, `git_show`, `git_branch_list`, `git_stash_list`, `git_is_inside_work_tree`, `git_add`, `git_commit`, `git_checkout`, `git_create_branch`, `git_init`, `git_push`, `git_pull`, `git_stash`, `git_stash_pop`, `git_reset`, `git_rebase` |
| `Http` | `http_get`, `http_head`, `http_post`, `http_put`, `http_patch`, `http_delete` — uses named `ApiProfiles` |
| `Json` | `json_format`, `json_minify`, `json_get`, `json_keys`, `json_search`, `json_to_text`, `json_validate`, `json_merge` |
| `Document` | `document_extract_text`, `document_get_info`, `document_list_sheets`, `document_get_sheet` |
| `Search` | `search_content`, `search_symbol`, `search_callers` — finding files by name is `list_files` (FileSystem) |
| `Decision` | `decision_search`, `decision_read`, `decision_create`, `decision_supersede` — ADR registry |
| `Graph` | `graph_search`, `graph_refs`, `graph_dependents` — repository semantic graph, all read-only |
| `CodeExecution` | `code_execution_check_docker`, `code_execution_sandbox_run`, `code_execution_repl_start`, `code_execution_repl_exec`, `code_execution_repl_reset`, `code_execution_repl_stop` — Docker-sandboxed execution |
| `Changes` | `changes_read`, `changes_read_latest` — read the JSONL change log for observability by downstream agents |
| `Probe` | `probe_code`, `probe_assert_output`, `probe_compare_outputs`, `probe_run_hypothesis` — code execution and output verification |
| `Scratchpad` | `scratchpad_read`, `scratchpad_read_all`, `scratchpad_search`, `scratchpad_write`, `scratchpad_delete` — per-agent key-value store |
| `Chatroom` | `chatroom_send`, `chatroom_read` — shared coordination log |
| `Handoff` | `handoff` — emits a routing keyword to trigger a state machine or keyword route transition |
| `Subagent` | `subagent_explore` (multi-hop exploration, prose or file-list output, configurable round cap) · `subagent_locate` (single-target symbol/file lookup, 5-round hard cap, path:line output) — both run an isolated tool loop and return a distilled result without filling the caller's context. Working directory is injected automatically; the parent's cancellation token is linked. Model and plugin set are configurable via `SubagentModel`, `SubagentMaxToolCalls`, and `SubagentPlugins`. Default tool set: FileSystem read, Search, Git read, and Shell — **not** read-only: the default Shell allow-list is `shell_run`, `shell_get_env`, `shell_which`, `shell_get_working_directory`, so a subagent can execute commands (e.g. builds, tests) by default, subject to the sandbox/ring the parent agent runs under. |

**MCP servers** (`McpSessionManager`): connected at startup via `ModelContextProtocol`. Each server's tools are registered under the server's configured name and are available to any agent that lists that name in `Plugins`. MCP connections are disposed when the session ends.

**`SandboxEnforcementFilter`** (middleware, not a plugin): wraps any agent with a filesystem sandbox. Tool calls that would access paths outside the sandbox root are denied with `[DENIED: sandbox]`. Prompt injection attempts are detected by the governance kernel's `InjectionDetector` and also denied.

**Per-plugin capability filtering** (`AgentConfig.Capabilities`): agents can declare which operations they are permitted to perform within each plugin, independently of the sandbox. The `PluginCapabilityMap` maps tool function names to capability tags; `AgentFactory.BuildTools` filters the tool list at construction time so disallowed tools are never registered on the agent. Tools not in the capability map (e.g. MCP-registered tools) pass through unfiltered. The available capability tags per plugin are:

| Plugin | Capabilities |
|---|---|
| `FileSystem` | `read` (read_file, grep_file, get_file_summary, get_file_info, list_files) · `write` (write_file, patch_file, save_file_summary, create_directory, copy_file, move_file, set_permissions) · `delete` (delete_file, delete_directory). `list_directory` is not in the capability map and always passes through unfiltered regardless of declared capabilities. |
| `Shell` | `read` (shell_get_env, shell_get_job_status, shell_get_job_output, shell_which, shell_get_working_directory, shell_get_session_temp_dir) · `run` (shell_run, shell_run_script, shell_run_background, shell_set_env, shell_kill_job) |
| `Git` | `read` (git_status, git_diff, git_log, git_show, git_branch_list, git_stash_list, git_is_inside_work_tree) · `write` (git_add, git_commit, git_checkout, git_create_branch, git_init, git_push, git_pull, git_stash, git_stash_pop, git_reset, git_rebase) |
| `Http` | `get` (http_get, http_head) · `post` · `put` · `patch` · `delete` — `http_head` maps to the `get` capability, not a separate `head` capability |
| `Json` | `read` · `write` (merge) |
| `Document` | `read` (document_extract_text, document_get_info, document_list_sheets, document_get_sheet) |
| `Search` | `read` |
| `Changes` | `read` |
| `Scratchpad` | `read` · `write` |
| `Chatroom` | `read` · `write` |
| `Probe` | `run` (probe_code, probe_assert_output, probe_compare_outputs, probe_run_hypothesis) |
| `CodeExecution` | `read` (check_docker) · `execute` (sandbox_run, repl_*) |
| `Decision` | `read` (decision_search, decision_read) · `write` (decision_create, decision_supersede) |
| `Graph` | `read` (graph_search, graph_refs, graph_dependents — all read-only) |

Example — a Reviewer that inspects files and git history but cannot write, delete, or run commands:

```json
"Capabilities": {
  "FileSystem": ["read"],
  "Git":        ["read"]
}
```

---

## 13. Governance

`GovernanceKernel` (from `Microsoft.AgentGovernance`) is constructed by `OrchestratorBuilder` and threaded through the entire stack.

**Capabilities enabled at startup:**

| Feature | Purpose |
|---|---|
| Audit | Hash-chain audit log of every governance event (allow/deny decisions) |
| Metrics | Counters for validator passes/failures |
| Prompt injection detection | Detects and blocks injection attempts in tool inputs |
| Rings | Maps `AgentConfig.TrustScore` to execution privilege rings (Ring 1 ≥ 0.80, Ring 2 ≥ 0.60, Ring 3 < 0.60) |
| Circuit breaker | Wraps `agent.RunAsync` calls; trips after 5 failures, resets after 30s, half-open with 1 probe call |
| SLO engine | Tracks routing validator compliance rate over a 1-hour rolling window; 95% target; burn-rate alerts at 2× (warning, 3600s window) and 5× (critical, 600s window) |

**Policy files:** If `policies/default.yaml` exists in the same directory as the config file (e.g. `.fuseraft/config/policies/default.yaml`), it is loaded as a governance policy and applied to all agents in the session.

**Event bridge:** `GovernanceEventType.ToolCallBlocked` events (from sandbox + injection checks) are forwarded to the `EventEmitter` as `tool_blocked` JSONL events. Gate failures are emitted directly by `StateMachineSelectionStrategy` (`validation_fail`) with richer per-turn context.

**Agent DIDs:** Every agent is assigned a `did:fuseraft:<name>` identifier at construction. DIDs are used as actor identifiers in the audit log and are resolved by `AgentFactory.GetDid(name)` for governance lookups.

---

## 14. Change Tracking

`ChangeTracker` wraps every agent with a `CapturingMiddleware` that intercepts tool call results and records structured entries to a JSON change log.

**Tracked functions:** `write_file`, `patch_file`, `delete_file`, `delete_directory`, `copy_file`, `move_file`, `shell_run`, `shell_run_script`, `shell_run_background`, `git_commit`.

**`ChangeLog` schema** (`changes.json`, one entry per turn):
- `ActiveSessionId` — current session ID
- `Entries[]` — `{ Agent, TurnIndex, Timestamp, SessionId, FilesWritten[], FilesDeleted[], CommandsRun[], GitCommits[] }`

**Intent log** (`~/.fuseraft/sessions/{project_slug}/{session_id}/intents.json`): Alongside the change log, `CapturingMiddleware` also writes to an `IntentLog` — one entry per tracked tool call, written *before* the call executes with `Status: Pending`, then updated to `Applied` or `Failed` once the call returns.

- `BeginTurn(agentName, turnIndex)` must be called before each `agent.RunAsync` so middleware has the correct turn index. All orchestrators (`AgentOrchestrator`, `MagenticOrchestrator`, and the pattern orchestrators) call this immediately after `OnAgentTurnStarting()`.
- On session resume, any `Pending` entries indicate operations that were in-flight at interruption time.
- The `"intent"` compaction mode reads from this log to produce a deterministic `✓`/`✗` summary — no LLM call required.
- If the intent log file is corrupt or unreadable on load, the failure is emitted via `ILogger<IntentLog>` at Warning level and the store resets to empty for the session.

**`ChangeLog` load failures** (`~/.fuseraft/state/{project_slug}/changes.json`): Both the session-init path (setting `ActiveSessionId`) and the per-entry flush path read the existing change log before appending. If either read fails, the failure is emitted via `ILogger<ChangeTracker>` at Warning level and the log resets to empty for that operation. `EvidenceStore` and `FileVersionStore` follow the same pattern. All warnings route to `~/.fuseraft/logs/{project_slug}/app.log` via the always-on Serilog file sink so they survive past the terminal session.

**`IntentStore` schema** (`~/.fuseraft/sessions/{project_slug}/{session_id}/intents.json`):
- `ActiveSessionId`
- `Entries[]` — `{ IntentId, Timestamp, Agent, TurnIndex, SessionId, Operation: { FunctionName, TargetPath, ArgsSummary }, Status, ErrorMessage, CompletedAt }`

**`FileVersionStore`** (`~/.fuseraft/state/{project_slug}/file_versions.json`): A lightweight per-file version counter, also initialized by `OrchestratorBuilder`. Every successful `write_file` call increments the counter. Agents call `get_file_info` to probe the current version and pass `baseVersion` to `write_file` to detect concurrent-write conflicts. If the store file is corrupt or unreadable, the failure is emitted via `ILogger<FileVersionStore>` at Warning level and the counter resets to zero for the session — agents will see all files at version 0 and conflict detection will not fire until files are written again.

**Downstream use:** The `Changes` plugin exposes `changes_read` and `changes_read_latest` so agents (typically Tester or Reviewer) can read what previous agents actually did rather than inferring it from chat history. `RequireShellPass` and `RequireWriteFile` validators also read this log to verify deterministic pre-conditions before routes fire.

**`SetSessionIdAsync`** is called by `SessionRunner` once the session ID is established, stamping the `ActiveSessionId` field so multiple sessions in the same working directory can be distinguished.

---

## 15. Event Emission

`EventEmitter` is the primary mechanism for extending orchestration behavior without modifying core logic. It appends structured JSONL events to a configured file path; external systems can tail this file and react to events in real time. All writes are serialized through a `SemaphoreSlim`. Errors are swallowed — event emission is best-effort and never disrupts the session.

Event consumers may inject messages, trigger external systems, or enforce additional constraints by reading the log and calling back into the session via HITL or external tooling. This makes the event system a programmable control-plane extension point, not merely an audit log.

**Event schema:** `{ ts, session, agent, turn, event_type, payload }`

**Event types emitted:**

*Session lifecycle*

| Event | Emitter | Payload |
|---|---|---|
| `session_start` | `ReplCommand` | `task` (raw task string), `start_node`, `resume` |
| `session_end` | `ReplCommand` | Turn count, succeeded |
| `session_summary` | `SessionMetrics` | `total_turns`, `total_input_tokens`, `total_output_tokens`, `max_turn_input_tokens`, `total_tool_calls`, `total_patch_failures`, `total_duplicate_reads`, `total_compactions` |
| `phase_start` | `MapReduceOrchestrator`, `ScatterGatherOrchestrator` | Phase number, agent |
| `phase_end` | `MapReduceOrchestrator`, `ScatterGatherOrchestrator` | Phase number, item count |
| `compaction` | `SessionRunner` | Turn count before/after, `reason` |
| `compaction_resume_candidate` | `SessionRunner` | `last_assistant_agent`, `current_state_name`, `reason`, `total_messages` — emitted at compaction time to diagnose handoff-then-compaction resume divergence |
| `session_error` | `SessionRunner` | Exception message |

*Per-turn*

| Event | Emitter | Payload |
|---|---|---|
| `turn_start` | REPL (`ReplTurn`) | Agent name, turn index |
| `turn_end` | `AgentOrchestrator`, `MagenticOrchestrator` | Agent name, turn index, input/output tokens |
| `reasoning` | `AgentOrchestrator` | Reasoning token content |
| `context_assembly` | All orchestrators | `knowledge_retrieved`, `knowledge_included`, `memory_loaded`, `memory_included`, `artifacts`, `context_chars`, `system_prompt_chars`, `assembly_ms`, `context_chars_breakdown` (per-source: `system_prompt`, `memory`, `session_context`, `knowledge`, `history`), `tool_count`, `tool_schema_est_tokens` |

*Routing*

| Event | Emitter | Payload |
|---|---|---|
| `keyword_not_found` | `StateMachineSelectionStrategy` | Last message author, content excerpt |
| `back_edge_escalation` | `StateMachineSelectionStrategy` | `from_state`, `to_state`, `visit_count`, `max_revisits`, objections from the step agent's first `Writes` artifact — fired when a back-edge exceeds `MaxRevisits` |
| `correction_injected` | `CorrectionEngine` | Correction message text, reason |

*Validation*

| Event | Emitter | Payload |
|---|---|---|
| `validation_fail` | `StateMachineSelectionStrategy` | Gate name, consecutive failure count, error detail |
| `hitl_escalation` | `SessionRunner` | Reason (stuck gate or explicit escalation) |

*Magentic*

| Event | Emitter | Payload |
|---|---|---|
| `magentic_plan` | `MagenticOrchestrator` | Plan text |
| `magentic_replan` | `MagenticOrchestrator` | Round index, stall count, replan reason |
| `magentic_complete` | `MagenticOrchestrator` | Round count, final answer excerpt |

*Adversarial*

| Event | Emitter | Payload |
|---|---|---|
| `adversarial_stage_start` | `AdversarialOrchestrator` | Stage index, label, generator agent, critic agent |
| `adversarial_stage_pass` | `AdversarialOrchestrator` | Stage index, label, round at which the critic approved |
| `adversarial_stage_timeout` | `AdversarialOrchestrator` | Stage index, label, rounds exhausted (artifact promoted without approval) |
| `adversarial_complete` | `AdversarialOrchestrator` | Total stage count |

*Tools and infrastructure*

| Event | Emitter | Payload |
|---|---|---|
| `tool_blocked` | `OrchestratorBuilder` (governance bridge) | Agent DID, policy name, denial data |
| `tool_call` | `ChangeTracker`, `ReplCommand` | Tool name |
| `circuit_breaker_open` | `SessionRunner` | Agent name |
| `http_reasoning` | `ChatClientFactory` | Reasoning content |

*Subagent*

| Event | Emitter | Payload |
|---|---|---|
| `subagent_start` | `SubagentPlugin` | Agent name, query (truncated to 120 chars), mode (`explore` \| `locate`) |
| `subagent_tool_call` | `SubagentPlugin` | Agent name, tool name, args summary |
| `subagent_end` | `SubagentPlugin` | Agent name, outcome (`completed` \| `iteration_limit` \| `cancelled` \| `timeout` \| `error`), summary_chars, mode |

*REPL-specific*

| Event | Emitter | Payload |
|---|---|---|
| `user_input` | `ReplCommand` | Turn index, input text |
| `assistant_response` | `ReplCommand` | Turn index, response text |
| `command` | `ReplCommand` | Slash command name and args |

**Hook system:** `IOrchestrationHook` is an interface with a single method `OnEventAsync(OrchestrationEvent, CancellationToken)`. Hooks are registered via `EventEmitter.RegisterHook(hook)` and called in registration order after each JSONL write. Hooks receive the typed `OrchestrationEvent` record (event type, timestamp, session ID, agent, turn, payload) and filter on `EventType` to react only to relevant events. Use hooks for:

- Injecting diagnostic context into agent history on `validation_fail` (adaptive feedback)
- Posting real-time alerts to Slack, PagerDuty, or a webhook
- Pushing metrics to Prometheus, DataDog, or a custom dashboard
- Triggering secondary monitoring or auditing agents

**Built-in hooks:**

| Hook | Behavior |
|---|---|
| `ValidationDiagnosticHook` | Watches `validation_fail` events; on consecutive ≥ 2, reads the most recent change log entry and injects a diagnostic summary into the shared history. Gives the re-invoked agent ground-truth data (what was actually written/run on disk) rather than only the abstract validator error. |
| `ReasoningAuditHook` | SHA-256-digests reasoning-token content into the governance audit chain, registered by `OrchestratorBuilder`. |
| `TelemetryEventHook` | Forwards `compaction`, `retry_attempt`/`retry_scheduled`/`retry_exhausted`, `circuit_breaker_open`, `hitl_escalation`/`hitl_rejected`, `context_budget_warn`/`context_budget_cutover`, and `max_turns_exceeded` events into OTel counters via `FuseraftTelemetry`. Registered by `RunCommand` whenever `Telemetry` is configured — see [Telemetry](configuration.md#telemetry). |

`AgentOrchestrator` registers `ValidationDiagnosticHook` automatically when both `Events` and `ChangeTracking` are configured. The hook is registered once per orchestrator instance and uses a mutable `_activeHistory` reference so it always targets the current session's history across multiple `StreamAsync` calls.

`EmitAsync` accepts an optional `CancellationToken`, threaded through to every registered hook's `OnEventAsync` call.

---

## 16. DevUI

`DevUIServer` is a lightweight ASP.NET Core server (started inline via `WebApplication.CreateSlimBuilder`) that provides real-time session visualization in a browser.

**Endpoints:**
- `GET /` — self-contained HTML page (inline in `DevUIHtml.cs`)
- `GET /api/stream` — Server-Sent Events stream of session events

**Event types:** `session_start`, `agent_starting`, `message` (with agent name, content, token usage, elapsed ms — no cost/pricing, which isn't tracked anywhere in this layer), `session_end`.

**Full-history replay:** New SSE clients receive the complete event history on connect so page refresh always shows the entire session from the beginning.

**Port:** dynamically assigned via Kestrel's `UseUrls("http://localhost:0")` at startup (not `TcpListener`), read back from `_app.Urls.First()`, and printed to the terminal.

**Why we did not use the framework's `Microsoft.Agents.AI.DevUI`:** The framework's DevUI is an API playground for hosted agent services — it requires `AddOpenAIResponses()`, `AddOpenAIConversations()`, and ASP.NET Core hosting, and presents a chat interface over those HTTP endpoints. Fuseraft-cli is a console executable with no hosted agent API. Our DevUI visualizes the streaming event flow of a running orchestration session (agent turns, token usage, phase transitions) — a fundamentally different use case that the framework's DevUI does not address.

---

## 17. Microsoft Agent Framework Usage

Fuseraft-cli is built on MAF (`Microsoft.Agents.AI`) with optional A2A federation: the client via `Microsoft.Agents.AI.A2A`, and hosting (`fuseraft serve --a2a`) via `Microsoft.Agents.AI.Hosting.A2A.AspNetCore`.

**Non-MAF / extension dependencies** (managed separately from the MAF version):

| Package | Version | Notes |
|---|---|---|
| `Azure.AI.OpenAI` | `2.1.0` (stable) | Pinned to the last GA release. The 2.2–2.9 beta series does not have a GA date; the SDK team is steering users toward the base `OpenAI` SDK for non-Azure deployments. `AzureOpenAIClient` from this package is used only for the `provider: azure` case. |
| `OllamaSharp` | `5.4.25` | Replaces the deprecated `Microsoft.Extensions.AI.Ollama` package (frozen at `9.7.0-preview.1`, no GA planned). `OllamaApiClient` implements `IChatClient` directly — no `.AsIChatClient()` adapter required. |
| `A2A` | `1.0.0-preview2` | Google's open A2A protocol client library. Used by `AgentFactory` for remote agent card discovery. |
| `Microsoft.Agents.AI.A2A` | `1.22.0-preview.260918.1` | MAF bridge that wraps an A2A `AgentCard` as an `AIAgent`. Provides `A2ACardResolver.GetAIAgentAsync()` used in the remote agent short-circuit path. |
| `Microsoft.Agents.AI.Hosting.A2A.AspNetCore` | `1.22.0-preview.260918.1` | The server side: `AddA2AServer`/`MapA2AJsonRpc` expose an `AIAgent` as an A2A JSON-RPC endpoint. Used by `ServeA2AHost` (§19). |
| `Anthropic.SDK` | `5.10.0` | Native Claude Messages API client (not a MAF/`Microsoft.Extensions.AI` package). Claude models (`claude-*` model ID prefix, `provider: anthropic`) are routed through this instead of the generic OpenAI-compatible client path — talking to `https://api.anthropic.com` directly is what makes prompt caching available (see `AnthropicPromptCachingChatClient`); Anthropic's own OpenAI-compatible endpoint does not support it. |

**What we use:**

| MAF Component | How we use it |
|---|---|
| `AIAgent` / `ChatClientAgent` | Base agent type; `RunAsync(context, null, null, ct)` drives each LLM turn |
| `AIAgentExtensions` / `ChatClientFactory` | Agent builder helpers |
| `A2ACardResolver` | Resolves remote agent cards from `{Url}/.well-known/agent-card.json` and wraps them as `AIAgent` instances (remote agent short-circuit in `AgentFactory`) |
| `Microsoft.Agents.AI.Compaction.ToolResultCompactionStrategy` / `CompactionProvider` | Deterministic sliding-window collapse of tool-call/result groups in `AgentContextCompactionFilters.KeepLastToolPairs` (§11-adjacent in-turn filtering, distinct from session-level `ConversationCompactor`). Still gated behind `MAAI001` in the framework version we pin; the suppression is scoped with `#pragma warning disable/restore` around this one call site rather than project-wide |
| `AgentSkillsProvider` / `AgentSkillsProviderBuilder` | MAF's own Agent Skills feature (`Microsoft.Agents.AI.Skills`), wired in `OrchestratorBuilder.BuildSkillsProvider` and layered as an `AIContextProvider` outside `UseFunctionInvocation` in `AgentMiddlewareBuilder.BuildEventEmitMiddleware` — same ordering the framework's own `HarnessAgent` uses internally. Not a fuseraft-built system despite the similarity to the REPL/orchestration skill loaders described in §12 |

**What we do not use:**

The `Microsoft.Agents.AI.Workflows` package was dropped with the team-file format: `Steps` flows run on fuseraft's own state machine, because its gates, `OnStuck` recovery, back-edge escalation and failure classification have no Workflows equivalent. The rows below record why each Workflows feature was not adopted.

| MAF Feature | Reason |
|---|---|
| `AgentWorkflowBuilder.BuildConcurrent` (Concurrent orchestration) | Fan-out/fan-in via MAF; no per-branch retry loop (the actual incompatibility — see §18); implemented instead at fuseraft level |
| Conditional edge predicates / `SwitchBuilder` | Routing logic lives inside executors (requires retry loop that graph edges cannot provide) |
| `StatefulExecutor` | `AgentContext` (a fuseraft type, not a MAF one) as a shared context object serves the same purpose without scoped state isolation |
| `AggregatingExecutor` | No incremental aggregation pattern in any current orchestrator |
| `RequestPort` (external request handling) | Currently unused; a natural fit for Magentic's HITL plan review loop (see below) |
| `CheckpointManager` / `FileSystemJsonCheckpointStore` | Framework layer captures workflow execution state; our layer captures conversation semantics — different problems |
| `Microsoft.Agents.AI.Compaction.TruncationCompactionStrategy` / `SummarizationCompactionStrategy` (session-level compaction) | Operate on `ChatMessage`/`CompactionMessageGroup`, not `AgentMessage`/`AgentContext.History` (a different, fuseraft-owned model carrying `TurnIndex`, `Usage`, `IsCompactionSummary`, checkpoint state); neither strategy knows about `HandoffPlugin` routing signals, so `TryPinLastRoutingSignal` (§11) would still need reimplementing on top — see §18 |
| `GroupChatWorkflowBuilder` | The manager can hold private state via a `GroupChatManager` subclass (the framework's own checkpoint hooks are documented for exactly this — see §18), but `GroupChatHost.TakeTurnAsync` always passes the full canonical history into `SelectNextAgentAsync`/`ShouldTerminateAsync`; it cannot be made to see only a summary. Magentic's two-history model requires exactly that — see §6.2 |
| `MagenticWorkflowBuilder` | Present in the pinned MAF version (1.16.0) as a purpose-built Magentic-One builder, but its internal `MagenticOrchestrator`/`MagenticManager` feed every manager reasoning call from the same shared `taskContext.ChatHistory` participants see, with no private-context/summarization layer — same defect as `GroupChatWorkflowBuilder`, just Magentic-specific — see §18 |
| `AgentWorkflowBuilder.CreateHandoffBuilderWith()` (Handoff orchestration) | Mesh routing via auto-injected handoff tool calls; shared history across all participants is incompatible with per-agent `ContextWindow` filtering; autonomous mode (graduated out of experimental as of a version already an ancestor of what we pin) does inject a continuation message and re-invoke the agent when the handoff tool isn't called — see §18 for what that changes and doesn't |
| `Microsoft.Agents.AI.DevUI` | For hosted agent services with OpenAI-compatible API endpoints; our DevUI serves a different purpose |

**Future opportunity — `RequestPort` for Magentic HITL:** The framework's `RequestPort` is a pause-and-wait-for-external-input primitive: the workflow halts at a `RequestHaltEvent`, the caller calls `SendResponseAsync(response)` to resume. This maps cleanly onto Magentic's plan review loop (currently a polling `IHumanApprovalService` call). Migrating the plan review to `RequestPort` would require `MagenticOrchestrator` to be backed by a MAF workflow rather than a manual loop, which is a non-trivial refactor but architecturally sound.

---

## 18. Decisions Against Framework Features

> Several entries below discuss `GraphOrchestrator`, which ran on `Microsoft.Agents.AI.Workflows` until the team-file format replaced it with the state machine. They are kept as the record of why the framework's orchestration builders didn't fit.

A summary of explicit decisions **not** to use certain framework capabilities, with rationale.

**`GroupChatWorkflowBuilder` for `MagenticOrchestrator`**
Rejected. The framework's group chat model passes the same conversation history to both the manager and participants. `MagenticOrchestrator` requires two entirely separate histories: a private manager context (fact-gather, plan, ledger evaluations) and a shared participant context.

**Nuance (checked against source):** a `GroupChatManager` subclass *can* hold private state — the framework's own `OnCheckpointingAsync`/`OnCheckpointRestoredAsync` hooks are documented for persisting "additional state they maintain (e.g., a round-robin cursor or an LLM session)." So it's not literally true that the manager has no way to keep anything private. What's actually forced is narrower but still fatal for our design: `GroupChatHost.TakeTurnAsync` always passes the *full* canonical history into `SelectNextAgentAsync`/`ShouldTerminateAsync` — the manager cannot be given a filtered or summarized view for its own decision-making, only supplement it with private side-state. Our invariant is that the manager's reasoning calls never see raw participant messages at all, only LLM-generated summaries; `GroupChatWorkflowBuilder` cannot express that no matter what the manager subclass holds privately. Forcing it in via `UpdateHistoryAsync` would still require fabricating the manager's history on every call (that hook shapes the *participant* broadcast, not the manager's decision input), which is fragile and defeats the architecture's clarity. The planning phases, stall detection, replan cycles, and HITL plan review also have no equivalent in the framework abstraction.

**`MagenticWorkflowBuilder` for `MagenticOrchestrator`**
Rejected. This is available in the pinned MAF version — `Microsoft.Agents.AI.Workflows` 1.16.0 (`src/fuseraft.csproj`), which corresponds to upstream tag `dotnet-1.16.0`; `MagenticWorkflowBuilder` was introduced well before that release (upstream commit `ce70ca1a9`). It is a fluent builder purpose-built for Magentic-One orchestration: participants, round/reset/stall limits, `RequirePlanSignoff` human-in-the-loop review via `RequestPort`, prompt overrides, response-language control, and its own checkpoint hooks. Checked against source (`Specialized/Magentic/MagenticOrchestrator.cs`, `MagenticManager.cs`): it has the same defect as `GroupChatWorkflowBuilder` above, just built directly into the Magentic-specific implementation instead of something a manager subclass could theoretically work around. A participant's raw reply is appended straight into the shared `taskContext.ChatHistory` (`ChatHistory.AddRange(messages)`, `MagenticOrchestrator.cs:205`), and that same shared history is passed unfiltered into every manager reasoning call — facts/plan update, progress-ledger evaluation, and final-answer synthesis all invoke the manager agent with `[.. taskContext.ChatHistory, ...]` (`MagenticManager.cs:45`, `:75`, `:111`). There is no private-manager-context or summarization layer standing between raw participant dialogue and the manager's reasoning. Adopting it would mean giving up the two-history invariant (§6.2) that makes our design Magentic-style in the first place, plus losing the governance middleware, `ISessionStore` checkpointing, and `RepositoryKnowledgeStore` observation hooks that wrap our manual loop.

**MAF framework checkpointing (`CheckpointManager`, `FileSystemJsonCheckpointStore`)**
Rejected as a replacement for `ISessionStore`. The framework's `Checkpoint` type captures MAF runtime execution state (executor queues, edge state, workflow topology). Our `SessionCheckpoint` captures conversation semantics (agent messages, token usage, cost, Magentic loop state). They operate at different layers of abstraction and solve different problems. Framework checkpointing applies only to `GraphOrchestrator` and would not help `AgentOrchestrator` or `MagenticOrchestrator` at all. Sub-turn recovery (the only benefit the framework layer would add to `GraphOrchestrator`) is not a practical concern given our turns are already fine-grained checkpointed at the conversation level.

**`Microsoft.Agents.AI.DevUI`**
Rejected as a replacement for our `DevUIServer`. The framework's DevUI is designed for hosted ASP.NET Core services exposing OpenAI-compatible Responses and Conversations API endpoints. It presents a chat interface over those endpoints. Fuseraft-cli is a console executable — it has no hosted agent API to point the DevUI at. Our `DevUIServer` visualizes the real-time streaming event flow of a running orchestration session, which is a different problem the framework's DevUI does not address.

**`Microsoft.Agents.AI.Compaction.TruncationCompactionStrategy` / `SummarizationCompactionStrategy` for session-level compaction**
Not adopted for `ConversationCompactor` (§11). Both strategies operate on `Microsoft.Extensions.AI.ChatMessage`, indexed into atomic `CompactionMessageGroup`s via `CompactionMessageIndex` — the raw per-call chat-client message list. That's the layer `ToolResultCompactionStrategy` already lives at (`AgentContextCompactionFilters.KeepLastToolPairs`), which is why that strategy was adopted and these were not. `ConversationCompactor` operates one layer up, on fuseraft's own `AgentMessage`/`AgentContext.History` — a persisted, cross-turn model carrying `TurnIndex`, `AgentName`, `Usage`, `ToolCalls`, `IsCompactionSummary`, and checkpoint state with no equivalent in MAF's `ChatMessage`/`CompactionMessageGroup` world. Adopting either strategy here would require converting `AgentMessage` to `ChatMessage` and back, reattaching all of that metadata.

Even after that conversion, neither strategy expresses fuseraft's compaction invariants (§11). `TruncationCompactionStrategy` excludes the oldest atomic groups behind a `MinimumPreservedGroups` floor, but has no notion of `HandoffPlugin` routing signals — `TryPinLastRoutingSignal` would still need to be reimplemented on top of it to keep a pending route from being silently dropped. Fuseraft's own truncation-only path, `ConversationCompactor.TrimToWindow` (`window` mode), already exists for exactly this case: it skips `IsCompactionSummary`-pinned messages, drops content in User+Assistant pairs, and deliberately reuses the same chars/4 estimator as the trigger check (`ShouldCompact`) to avoid a trigger/trim divergence the code has already hit once (see the comment on the quadratic growth of `Usage.TotalTokens` vs. the char-based estimate).

`SummarizationCompactionStrategy` is the closer conceptual match to `ConversationCompactor`'s default `llm` mode — both replace old turns with one LLM-generated summary — but it only takes a chat client and a prompt. It has no hook for change-log grounding (trusting exit codes and file writes over agent self-reports), tool-trace injection, `ExecutionState`-aware content filtering, or the anti-thrash guard (`AntiThrashWindow`/`AntiThrashMinSavingsRatio`) that skips compaction when repeated runs aren't saving space. `ConversationCompactor` also supports `lossless`/`hybrid`/`intent` modes that reconstruct context deterministically from durable evidence instead of an LLM call at all — capabilities with no MAF Compaction equivalent.

**`StatefulExecutor` in `GraphOrchestrator`**
Not adopted. Each executor sharing `AgentContext` (a single mutable object passed through MAF's message routing) achieves the same effective state — all agents read from and write to the same conversation history. `StatefulExecutor` would isolate state per executor, which would require explicit merging of histories and break the shared-history invariant that routing strategies depend on.

**Graph-level conditional routing in `GraphOrchestrator`**
Not adopted. MAF edge conditions fire once per message and have no retry semantics. When an agent fails to emit a routing keyword, the executor injects a correction and calls the LLM again. This retry loop must live inside the executor. Moving routing to graph edges would require removing retries, degrading robustness when models do not follow instructions on the first attempt.

**MAF Handoff orchestration (`AgentWorkflowBuilder.CreateHandoffBuilderWith`)**
Not adopted. MAF Handoff is a mesh topology where routing is driven by auto-injected handoff tool calls — each agent calls the tool to transfer control to the next agent.

**Correction (checked against the framework version we pin, `Microsoft.Agents.AI.Workflows` 1.16.0):** this section previously claimed the framework "blocks for human input" with "no correction-injection loop" when an agent doesn't call the handoff tool. That's no longer accurate, and possibly never was for the autonomous-mode path. `HandoffWorkflowBuilderCore.WithAutonomousMode(...)` and `HandoffEndExecutor.HandleAsync` implement exactly a correction/continuation-injection loop: when the agent doesn't call the handoff tool, a synthetic `ChatRole.User` message ("User did not respond. Continue assisting autonomously.") is injected and the same agent is re-invoked, up to a per-agent turn limit (default 50). Autonomous mode also graduated out of experimental in a version already an ancestor of 1.16.0 — it is no longer the "experimental" opt-in this section originally described it as. The default (autonomous-mode-off) path still doesn't use `RequestPort`/`RequestInfoEvent` for this — it's a plain `YieldOutputAsync` turn-end, not a distinct blocking-on-human primitive.

What this changes: the "no retry loop" framing is no longer the core incompatibility — MAF's own retry loop is structurally similar to `CorrectionEngine`'s. What it doesn't change: the loop is generic ("continue assisting autonomously"), not `CorrectionEngine`'s typed, validator-driven correction messages (`MissingEvidence`/`InvalidTransition`/`ConflictingEvidence`/`NoProgress`) tied into the failure-classification pipeline in §9. Adopting MAF Handoff would still mean giving up that typed correction surface for a generic one.

Remaining incompatibilities:
- **Shared history.** Handoff broadcasts all agent messages to all participants for context synchronisation. Per-agent `ContextWindow` filtering (`ExcludeAgents`, `TextOnly`, `MaxTailMessages`) requires independent history slices per agent and cannot be expressed within that broadcast model. This is still the primary reason, now that the retry-loop gap is closed.
- **Already covered by existing components.** `HandoffPlugin` already provides tool-based routing signal detection. `GraphOrchestrator` reads it before keyword scanning. The one thing MAF Handoff adds over this is framework-level routing dispatch and its own (now non-experimental) retry loop — but the generic-vs-typed correction gap above means switching would trade specificity for framework ownership, not gain reliability.

**MAF Concurrent orchestration (`AgentWorkflowBuilder.BuildConcurrent`)**
Not adopted as the parallelism primitive. MAF's `BuildConcurrent` fans out to a set of executors via `Task.WhenAll` at the workflow runtime level and collects results at a join point.

**Correction:** this section previously claimed the core incompatibility was "shared mutable history" — that "all executors in a MAF concurrent group receive the same `AgentContext` instance," causing races on a shared list. Checked against source, that's not how `ConcurrentWorkflowBuilder` works: each agent is bound to its own `AIAgentHostExecutor`, and each of those holds a **private** `AgentSession` field — branches don't share one mutable history object. (`AgentContext` is a fuseraft type to begin with, not a MAF concept, so the claim was also a category error.) There is a real, framework-acknowledged race, but it's at the fan-in join point in `ConcurrentEndExecutor`'s result aggregation, guarded by a lock with an upstream `// TODO` noting the lock shouldn't be necessary (tracked as a known issue) — not a shared-history mutation problem, and not something that would block adoption on its own.

The actual incompatibility is **retry semantics**. MAF concurrent branches fire once. When a parallel agent fails to emit its routing keyword, the `CorrectionEngine` must inject a correction message and re-invoke the agent. That loop must live inside the branch's executor, not at the graph-edge level. The concurrent builder has no built-in retry path — this is the reason we didn't adopt it, on its own, without needing the shared-history argument.

**What we do instead.** Parallel node execution is implemented entirely within `GraphOrchestrator`:

- Nodes marked `Parallel: true` in the graph config participate in a fan-out group. When the source agent emits the group's trigger keyword, `GraphOrchestrator.RunNodeExecutorAsync` forks the `AgentContext` via `ForkContext` — creating isolated `History` snapshots that share only the `MessageSink` (already `SingleWriter = false`).
- Each parallel worker runs `RunParallelNodeAsync` — a full correction-retry loop identical to `RunNodeExecutorAsync` but without MAF routing calls. Workers run concurrently via `Task.WhenAll`.
- After all workers complete, `MergeParallelContexts` appends each worker's post-fork messages to the parent history under a labelled section header, aggregates token counts, and takes the maximum turn index.
- The source node's executor then calls `wfCtx.SendMessageAsync` to the merge-target node, which was registered in the MAF DAG via a bridging edge during `BuildPhaseWorkflow`.

This preserves the full correction-loop guarantee for each parallel branch while keeping parallel execution transparent to the MAF workflow layer.

---

## 19. The `fuseraft serve` Daemon

Every other entry point in this document — `run`, `repl`, `schedule run` — builds an orchestrator, drives it to completion (or one interactive session), and exits. `serve` (`src/Cli/Serve/ServeHost.cs`) is the one long-lived process: it builds the orchestrator once and stays resident, waiting for a human or another agent to dispatch work into it, going idle again between tasks.

**One daemon = one project.** `ServeHost.RunAsync` calls `OrchestratorBuilder.BuildAsync` exactly once at startup and holds the resulting `OrchestratorBuildResult` — orchestrator, compactor, governance kernel, MCP client manager, chat client factory — for the daemon's entire lifetime, disposing it only on shutdown. Rebuilding per task was rejected: `BuildAsync` spawns a child process for every configured MCP server and constructs a fresh `GovernanceKernel` (circuit breaker, SLO tracker, audit chain) meant to accumulate state across a session's lifetime, not reset every task. `Directory.SetCurrentDirectory` is process-global (see `RunCommand`), which is the same reason a daemon commits to one working directory/config for its whole run rather than serving multiple projects from one process.

**Serialized task queue is the idle mode.** `ServeTaskQueue` wraps a `Channel<ServeTaskRequest>`; `dispatch_task` enqueues and returns immediately with a session ID, and a single worker loop (`ServeHost.WorkerLoopAsync`) drains it one task at a time. This isn't a throughput choice — every orchestrator in this codebase assumes one shared, mutable `List<ChatMessage>` history (§6.1); running two tasks concurrently against the same orchestrator instance would violate that invariant. Blocking on an empty channel *is* the idle state. Each dequeued task gets a fresh `SessionCheckpoint` and a new `SessionRunner` (cheap — matches how `RunCommand` already treats `SessionRunner` as per-invocation), with the shared orchestrator/compactor re-stamped via `SetSessionId`/`SetStructuredTask` exactly as `RunCommand` does for a fresh session. The session-*scoped* collaborators built once inside `BuildAsync` (`SessionReadCache`, `ToolResultArtifactStore`, `SessionContextPlugin`) aren't rebuilt per task — that would mean rebuilding the whole orchestrator — but `OrchestratorBuilder` hands back a `RebindSessionScopedState` closure over their shared instances that `ProcessTaskAsync` calls with the new session ID before every task, so each one still ends up scoped to the task actually running instead of staying permanently bound to whatever session ID (or none) was current at daemon startup.

**Two front doors, one queue.** Both funnel into the same `ServeTaskQueue`:
- **MCP over streamable HTTP** (`ServeMcpTools`, hosted via `ModelContextProtocol.AspNetCore`'s `WithHttpTransport()`/`MapMcp()` inside a Kestrel `WebApplication`, the same in-process hosting shape `DevUIServer` already uses) — `dispatch_task`/`get_status`/`get_result`, for other agents. HTTP rather than stdio deliberately: an MCP stdio server is spawned per-caller as a child process and dies with that caller's connection, which is the opposite of "stays resident between callers." The port defaults to `FuseraftPaths.DefaultDaemonHttpPort` — deterministically derived from the project slug the same way `DaemonSocketPath` is — rather than one hardcoded number for every project, so two daemons for two different projects started with no flags don't fail with an address-in-use error against each other; `--http-port` still pins a specific one.
- **A Unix domain socket** (`ServeSocketListener`) for `fuseraft attach` — a human front door reusing the REPL's VS Code JSON-bridge protocol (`ReplStdinPump` verbatim; a per-connection sibling of `ReplJsonBridge`, since that class's output writer is a process-wide static). Any number of clients may attach concurrently. The socket lives at `FuseraftPaths.DaemonSocketPath` — a short hash of the project slug in a flat `~/.fuseraft/run/` directory, deliberately *not* the `{project_slug}`-templated path every other per-project artifact uses, because AF_UNIX paths are capped at 108 bytes on Linux and a full absolute-path-derived slug can easily exceed that for a nested project directory.

**Approval routing — broadcast vs. origin-targeted, attended vs. unattended.** `ServeHumanApprovalService` is constructed before `BuildAsync` (it's one of that call's own parameters — see §5) and passed with `hitlMode: true` unconditionally, which is what wires the mutating-action approval closures into the sandboxed plugins at all (`OrchestratorBuilder.ResolveSecurityConfig` only wires `shellApprover`/`toolActionApprover`/`fileWriteApprover` when `hitlMode && humanApprovalService is not null`). `SessionRunner.RunAsync`'s own per-call `hitlMode` — the "pause after every turn" loop — is deliberately always `false` for dispatched tasks; a daemon task runs straight through rather than stopping after every message for a prompt nobody may be present to answer. The service holds two independent things: a broadcast list of every attached connection, and a single "current task origin" pointer set by `ServeHost.ProcessTaskAsync` right before (and cleared right after) invoking the orchestrator for one task, mirroring the existing per-task `SetSessionId` re-stamping. Live progress (`AgentStarting`/`ToolCalling`/`TokenBudgetWarning` — the same events `DevUIServer` consumes) is forwarded to *every* attached connection via `BroadcastEvent`, subscribed and unsubscribed per task in `ServeHost` (not once at startup — the same orchestrator instance is reused across every task for the daemon's lifetime, so a startup-only subscription would multiply on every dispatch). The three mutating-action gates, plus `PromptRouteApprovalAsync` (gating `RequireHumanApproval` route/edge decisions the same way, rather than always auto-approving), go only to the *current task's origin* — a bystander connection that's merely attached is never asked to approve a task it didn't dispatch. Each gate's emit-then-read round-trip against a sink is serialized behind a semaphore in `ServeHumanApprovalService`: `IServeAttachSink`'s wire protocol has no per-request correlation ID, which only stayed safe once orchestrators that can run agent branches concurrently within one task (`ScatterGatherOrchestrator`, `MapReduceOrchestrator`, both via `Task.WhenAll`) started calling these gates from more than one branch at a time — without the lock, two concurrent round-trips could deliver one branch's answer to a different branch's pending action. A task with no origin (dispatched over MCP, or whose dispatching connection has since disconnected) falls through to the unattended policy: **deny by default** rather than auto-approving like `NonInteractiveHumanApprovalService`'s CI-oriented default, matching the project's existing default-deny precedents (`.env` denied by default, `--yolo` required to open the sandbox) rather than the "an operator launched this and chose not to babysit it" CI case. `--unattended-policy allow` opts back into auto-approve. Critically, this unattended fallback still applies even when other connections are attached and idle — attaching does not retroactively make a bystander an approver for work they didn't ask for (live-verified: an MCP-dispatched shell command was denied with two humans attached and watching, neither of whom saw an approval prompt for it).

**Queue position.** `ServeTaskQueue.GetQueuePosition` counts tasks ahead of a given one (running, plus earlier-queued still-`Queued` records by `CreatedAt`), returning `null` once it's no longer queued. Surfaced in `dispatch_task`/`get_status` (`ServeMcpTools`) and the attach socket's `queued` acknowledgement.

**Single-instance guard.** A pidfile at `FuseraftPaths.LocalDaemonPidFile` (project-scoped, no length constraint unlike the socket) is checked-and-written at startup and removed on clean shutdown, mirroring `ScheduleRunCommand`'s lockfile pattern. `Console.CancelKeyPress` is registered exactly once for the daemon's whole lifetime (not per task, unlike `RunCommand`'s per-invocation registration, which is safe there only because it runs once per process).

**Objective-driven auto-dispatch.** `--auto-objective <id>` (repeatable) is the one deliberate exception to "the daemon only ever runs what it's told." `ServeHost.WorkerLoopAsync`'s `await foreach (var request in queue.Reader.ReadAllAsync(...))` is a manual loop instead: a non-blocking `queue.Reader.TryRead` first; if empty, `TryPickAutoDispatchTaskAsync` checks the configured objectives (in order) for a runnable `RemainingTasks` entry and, if found, self-enqueues it via the same `queue.Enqueue` every other front door uses and loops back around — from that point it's an ordinary queued request, so broadcast, queue position, `get_status`/`get_result`, and checkpointing all apply with zero special-casing in `ProcessTaskAsync`. Only when there's truly nothing — no queued request, no auto-dispatch candidate — does it fall through to the real blocking `queue.Reader.ReadAsync`, so an idle daemon with auto-dispatch enabled but an empty backlog doesn't busy-loop (live-verified: stable ~4.5% CPU, not climbing, once a 3-task objective drained). A self-picked task gets no special trust: it has no attach-connection origin, so `ServeHumanApprovalService` falls through to `--unattended-policy` exactly like an MCP-dispatched task — self-initiated work is not a separate, more-trusted tier. A `Dictionary<(ObjectiveId, Task), int>` failure counter (daemon-lifetime only, deliberately not added to the persisted `Objective`/YAML schema) stops a consistently-failing task from being re-picked after `AutoDispatchFailureThreshold` (3) consecutive failures — `ObjectiveManager.LinkTaskAsync(completed: false, ...)` already leaves a failed task sitting right back in `RemainingTasks`, so without this it would be immediately re-picked forever. Live-verified against a config with `MaxTotalTokens: 1` (forcing `BudgetExceededException` on every attempt via `AgentOrchestrator`'s pre-turn budget guard): exactly 3 attempts, 3 distinct sessions recorded, then the daemon went idle with the task still visible in `RemainingTasks` rather than retrying a 4th time.

**A2A hosting, one endpoint per agent.** `--a2a` starts a second Kestrel app (`ServeA2AHost`) beside the MCP one, because the MCP endpoint must stay loopback-only while A2A may bind other interfaces. Each hosted agent is built once by the build result's `AgentFactory` — the same governed agent (sandbox, ring, `ChangeTracker`, approval closures) the orchestrator would build — and mapped at `/{AgentName}` with its card at `/{AgentName}/.well-known/agent-card.json`. It hosts agents, not the team: the caller already has an orchestrator, and a `RemoteAgent` is one slot in it, so routing and validators belong on the caller's side. Three decisions:
- **One gate for tasks and calls.** Hosted agents share the working directory and the session-scoped state `RebindSessionScopedState` swaps, so `ServeHost._runGate` (a `SemaphoreSlim(1)`) is held around each dispatched task and each A2A call. A call binds a fresh `a2a-xxxxxxxx` session ID while it holds the gate. The hosting layer drives agents through `RunCoreStreamingAsync`, so `GatedAgent` gates that path as well as `RunCoreAsync`.
- **Cards built per request.** A bind of `0.0.0.0` says nothing about which of the machine's names a caller used, so the card's interface URL comes from the request's scheme and `Host`, with `X-Forwarded-Proto`/`X-Forwarded-Host` honoured only from loopback proxies (ASP.NET's default `KnownNetworks`).
- **A token beyond loopback.** `ServeA2AHost.Validate` refuses a non-loopback bind without `FUSERAFT_A2A_TOKEN`; the bearer check is constant-time and covers cards too. No `AgentIsolationKeyProvider` is registered: with one shared token there is only one principal to isolate.

**Not yet built:** multi-tenant (multiple working directories/configs behind one process — the shared-history invariant above is the reason this isn't a small extension), hosting the whole team as one A2A agent, and A2A streaming.
