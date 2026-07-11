# fuseraft CLI — Design Document

This document describes the architecture and design decisions behind fuseraft-cli. It is meant to be a living reference for contributors and for future conversations with AI assistants working in this codebase.

---

## Table of Contents

1. [What It Is](#1-what-it-is)
2. [Layer Map](#2-layer-map)
3. [Directory Layout](#3-directory-layout)
4. [Configuration](#4-configuration)
5. [Agent Construction](#5-agent-construction)
6. [Orchestrators](#6-orchestrators) (AgentOrchestrator, MagenticOrchestrator, GraphOrchestrator, AdversarialOrchestrator, MapReduceOrchestrator, ScatterGatherOrchestrator, sub-graph nodes)
7. [Selection Strategies](#7-selection-strategies)
8. [Termination Strategies](#8-termination-strategies)
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
  Interfaces/       — IOrchestrator, ISessionStore, IAgentSelector, ITerminationCondition,
                      IRoutingValidator, IHumanApprovalService, ICompensatingAgent,
                      IMemoryProvider, IContextAssemblyPipeline, IContextSnapshotter,
                      IEventSink, IOrchestrationHook, IParallelAgentSelector (not
                      necessarily exhaustive — this list drifts as interfaces are added)
  Models/           — OrchestrationConfig, AgentConfig, SessionCheckpoint, AgentMessage,
                      AgentState, SagaConfig, TokenUsage, StrategyConfig,
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
  AgentOrchestrator.cs       — General-purpose multi-agent loop (any selection strategy)
  MagenticOrchestrator.cs    — Magentic-One style two-level manager/participant loop
  GraphOrchestrator.cs       — Directed-graph orchestrator; DFS-based forward/back-edge classification, forward-edge phases, back-edge phase restarts
  WorkflowOrchestrator.cs    — Cycle-native sibling of GraphOrchestrator for Selection.Type: "workflow" (see §6.8); every edge is a plain route, no forward/back distinction
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
  Saga/                   — SagaOrchestrator: compensating rollback wrapper
  Strategies/             — Selection and termination strategy implementations
  Validation/             — Routing validator implementations
  Workflow/               — MAF WorkflowBuilder-based phase orchestrator + StateHandoff
```

---

## 3. Directory layout

All runtime artifacts are written under `.fuseraft/` in the current working directory (project-local) or `~/.fuseraft/` in the user's home directory (global). The `FuseraftPaths` static class in `src/Core/FuseraftPaths.cs` is the single authoritative source for every path — config defaults and infrastructure classes all reference it.

**Global (`~/.fuseraft/`)**

A path-refactor (mid-2026) moved nearly all runtime session/state artifacts from project-local `.fuseraft/` into the global `~/.fuseraft/` home directory, keyed by `{project_slug}` (and `{session_id}` for session-scoped files). Only a handful of artifacts remain project-local — see below.

| Path | Contents |
|------|----------|
| `~/.fuseraft/config` | Model ID, endpoint URL (no secrets) |
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
| `~/.fuseraft/logs/{project_slug}/repl_events.jsonl` | REPL session events |
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

**Folder orientation for agents** — `FuseraftPaths.BuildFolderOrientationBlock()` generates a compact manifest of the runtime directory layout (both the local `.fuseraft/` artifacts and the global `~/.fuseraft/` session/state paths above) and is appended to every agent's instructions by `OrchestratorBuilder` at session start. This means agents never need to call `list_files` on `.fuseraft/` to discover its layout — they already have it. In REPL mode the log-file entries are omitted (the session section already covers them; agents are directed to the `repl_session_*` tools). `SubAgentPlugin` prompts receive a one-line skip directive instead of the full manifest to keep their system prompts compact.

---

## 4. Configuration

Every session is driven by a single JSON or YAML file under the top-level `Orchestration` key. The file is loaded by `OrchestratorBuilder.BuildAsync` and bound to `OrchestrationConfig`.

**Top-level fields:**

| Field | Purpose |
|---|---|
| `Name` | Human-readable session name |
| `Models` | Named model aliases reusable across agents (avoids repeating endpoint/key/temp) |
| `Agents` | Ordered list of `AgentConfig` — name, instructions, model, plugins, trust score |
| `Selection` | Controls which agent speaks next (`SelectionStrategyConfig`) |
| `Termination` | Controls when the session ends (`TerminationStrategyConfig`) |
| `Security` | Filesystem sandbox path, HTTP allowlist, injection detection |
| `MaxTotalTokens` | Hard token cap for the session (input + output combined) |
| `WarnTurnTokens` | Input-token count per turn above which `TokenBudgetWarning` fires (default: 300 000) |
| `McpServers` | MCP server definitions; tools registered at startup alongside built-ins |
| `Compaction` | LLM-based history summarization settings (trigger count, keep-recent count, model) |
| `Validation` | Routing validator settings (brief path, test report path, change log path) |
| `ChangeTracking` | Writes file/shell/git activity to a JSONL change log |
| `Scratchpad` | Per-agent persistent key-value store base path |
| `Chatroom` | Shared append-only JSONL coordination log for agents |
| `Events` | Structured JSONL event log for turn_end, validation_fail, hitl_escalation |
| `Checkpoint` | Storage backend: `json` (default, `~/.fuseraft/sessions/`) or `memory` |
| `Telemetry` | OTLP endpoint for OpenTelemetry traces and metrics |
| `ApiProfiles` | Named HTTP profiles (base URL + default headers with `${ENV_VAR}` expansion) |
| `Saga` | Optional saga (compensating rollback) settings; wraps execution with `SagaOrchestrator` when `Enabled: true` |

**`AgentConfig` fields:**

| Field | Purpose |
|---|---|
| `Name` | Unique identifier within the session |
| `Instructions` | System-prompt defining role and routing keywords |
| `Model` | Model alias or inline `ModelConfig` (endpoint, API key env var, temperature, max tokens, `MaxContextTokens` soft cap) |
| `Plugins` | List of plugin names to load as tools |
| `FunctionChoice` | `auto` / `required` / `none` — maps to `tool_choice` in the API |
| `TrustScore` | 0.0–1.0 — governs execution ring assignment and privilege level |
| `ContextWindow` | Optional per-agent history filter (strips tool noise, limits tail length). Ignored when `Context` is set. |
| `Context` | Optional artifact-first context spec. When declared, replaces history replay entirely — context is assembled from disk sources (`session_context`, `changes_recent`, `brief_field`, `file`, `own_history`) rather than filtering the shared transcript. |

**Environment variable expansion** for `Security.HttpAllowedHosts`, `ApiProfiles[*].BaseUrl`, and all `ApiProfiles` header values is performed at startup via `${ENV_VAR}` tokens. Credentials never appear in agent instructions or conversation history.

**Config formats:** Both JSON (`.json`) and YAML (`.yaml` / `.yml`) are supported. YAML is parsed via `YamlConfigLoader` and converted to `IConfiguration` for the same `BindConfig` path.

---

## 5. Agent Construction

`AgentFactory.Create(AgentConfig)` produces a fully configured `AIAgent` ready for use by any orchestrator.

**Steps:**

0. **Remote agent short-circuit** — When `AgentConfig.RemoteAgent` is set, `AgentFactory` resolves the remote agent card from `{Url}/.well-known/agent.json` via `A2ACardResolver`, wraps it as an `AIAgent`, and returns immediately. Steps 1–5 below are skipped; `Model`, `Plugins`, `FunctionChoice`, and `Capabilities` are ignored. `Instructions`, `ContextWindow`, and `ChangeTracker` wrapping all continue to apply. `TrustScore` is recorded in the governance audit-emit call for observability, but does **not** govern an execution ring for remote agents — `BuildGovernanceMiddleware` (the only code path that computes a ring via `ComputeRing`) is never reached from this short-circuit, since `SandboxEnforcementFilter` has no local tool surface to enforce against for a remote agent's tool calls (those happen inside the remote A2A service, invisible to this process). `GetAIAgentAsync` is dispatched via `Task.Run` so the blocking `.GetAwaiter().GetResult()` call runs on the thread pool rather than the caller's `SynchronizationContext`, avoiding potential deadlocks in hosted environments.

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

`OrchestratorBuilder` selects among seven orchestrators based on the config's `Selection.Type`. The selection order is:

1. **`GraphOrchestrator`** — when `Selection.Type == "graph"`
2. **`WorkflowOrchestrator`** — when `Selection.Type == "workflow"` (see §6.8)
3. **`AdversarialOrchestrator`** — when `Selection.Type == "adversarial"`
4. **`MapReduceOrchestrator`** — when `Selection.Type == "mapreduce"`
5. **`ScatterGatherOrchestrator`** — when `Selection.Type == "scattergather"`
6. **`MagenticOrchestrator`** — when `Selection.Type == "magentic"`
7. **`AgentOrchestrator`** — all other cases

All seven implement `IOrchestrator`:

```csharp
Task<OrchestrationResult> RunAsync(string task, IReadOnlyList<AgentMessage>? priorHistory, CancellationToken ct)
IAsyncEnumerable<AgentMessage> StreamAsync(string task, IReadOnlyList<AgentMessage>? priorHistory, CancellationToken ct)
void SetSessionId(string sessionId)
void SetResumeExecutorId(string? executorId)   // GraphOrchestrator; consumed once
void SetResumeStateName(string? stateName)     // AgentOrchestrator + StateMachineSelectionStrategy; consumed once. GraphOrchestrator does not
                                                // override this — it falls through to IOrchestrator's no-op default and relies on
                                                // SetResumeExecutorId alone (CompactionCoordinator calls both unconditionally on every
                                                // orchestrator, so this is a harmless no-op for Graph sessions, not a bug).
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
4. Append all response messages to shared history with `AuthorName` set — routing/termination strategies always read from the full history
5. Persist entity-scoped observations to `RepositoryKnowledgeStore` (post-turn)
6. Emit `context_assembly` event with assembly metrics
7. Yield the final text response as an `AgentMessage`
8. Check `ITerminationCondition.ShouldTerminateAsync(history)` — break if true

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
  → CheckTermination        (ITerminationCondition.ShouldTerminateAsync)
  → CheckIterationCap
  → (terminated or capped ? END : SelectAgent)
```

**Shared history:** All agents read from and write to the same `List<ChatMessage>`. This is intentional — routing strategies (especially `KeywordSelectionStrategy`) read `AuthorName` from the most recent assistant message to determine who just spoke and where they want to route. `AssembledContext.Messages` is what the *model* sees; the full shared history is what routing sees.

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

### 6.3 GraphOrchestrator

A directed-graph orchestrator for `Selection.Type: graph`. Each node in the config binds an agent to a unique `Id`; edges carry routing keywords and optional validators. The topology drives execution: forward edges advance within a phase; back-edges break the phase and restart from the target node.

**Forward/back-edge classification:** At startup, `ComputeBackEdges` classifies every edge reachable from the `Entry` node via a single DFS with a 3-color node state (unvisited / on-stack / done). An edge is a *back-edge* only when its target is still on the DFS stack (a real ancestor of the source) when the edge is explored; every other edge — tree edges, edges to already-finished descendants, and cross edges to already-finished nodes in another branch — is a *forward edge*. `IsBackEdge(from, to)` looks the classification up directly from the precomputed set.

This replaced an earlier BFS-shortest-path-layer approximation (assign each node the layer of its first BFS encounter, classify an edge as back when `layer(B) ≤ layer(A)`), which had a real bug: it misclassified a legitimate forward edge as a back-edge whenever two forward paths of different lengths converged on the same node (a "diamond" — `A→B→D` and `A→C→E→D`), because the longer path's edge into `D` always landed on a layer ≤ `D`'s already-assigned (shorter-path) layer. DFS-based classification has no such failure mode since it reasons about actual ancestry, not path length.

**Route tables:** `BuildNodeRouteTables` constructs an `AgentRouteTable` for every node. Each table holds:
- `Routes` — forward-edge routes (keyword → `RouteInfo(targetNodeId, agentName, validators)`)
- `PhaseBreakKeywords` — back-edge destinations (keyword → target node ID)
- `PhaseBreakValidators` — validators keyed by back-edge keyword
- `TerminalValidators` — validators on `Terminal: true` nodes (run before keyword detection)
- `ForeignSendForwardKeywords` — keywords used to re-inject context to the MAF phase's next agent
- `IsReviewerType` — mirrors `GraphNodeConfig.ReviewerType`; selects `CorrectionEngine`'s
  reviewer-specialized correction messages (JSON judgement block tolerance, shell_run-before-decision
  requirement) instead of inferring reviewer behavior from whether `PhaseBreakKeywords` contains
  the literal string `"APPROVED"`
- `_unconditionalForwardRoutes` — forward edges with no keyword (fire automatically)
- `_unconditionalBackEdges` — back-edges with no keyword (fire automatically)
- `_unconditionalBackEdgeValidators` — validators for unconditional back-edges (stored in a parallel dictionary so they are not silently dropped)

**Phase loop:** `RunPhasesAsync` is the outer `while(true)` loop. Each iteration calls `BuildPhaseWorkflow`, which constructs a fresh MAF DAG containing only the forward edges reachable from the current start node. `InProcessExecution.RunStreamingAsync` drives the phase; `WatchStreamAsync` consumes events. A `WorkflowOutputEvent` signals a phase-break (back-edge keyword or unconditional back-edge). The outer loop reads `lastKeyword` to determine the next start node.

**Context assembly:** `RunNodeExecutorAsync` and `RunParallelNodeAsync` call `IContextAssemblyPipeline.AssembleAsync` before each agent invocation (including within the retry loop, so corrections injected between retries are included). `InvokeRecoveryAgentAsync` also goes through the pipeline. When no pipeline is wired (legacy path) the orchestrator falls back to `ContextWindowFilter.Apply` directly.

**Keyword detection:** `RunNodeExecutorAsync` runs inside each `FunctionExecutor`. It calls `agent.RunAsync`, then:
1. Checks whether the node is `Terminal: true` — if so, the termination check fires first.
2. Scans the response for keywords in the current node's route table only — keywords from other nodes are ignored.
3. For back-edge matches: validators run; on pass, `YieldOutputAsync` breaks the phase; on fail, a correction is injected and the agent is re-invoked.
4. For forward-edge matches: validators run; on pass, `SendMessageAsync` advances to the next executor in the phase.
5. For unconditional edges: `_unconditionalForwardRoutes` / `_unconditionalBackEdges` fire after the agent turn. Unconditional routing is wired per-node at config-build time by `WireBackEdges` — a node is either fully keyword-routed or fully unconditional, never both, so this is not a runtime fallback for "no keyword matched" on a node that also has keyword routes; it only applies to nodes with zero keyword-based routes at all.
6. If no keyword matches and the node has no unconditional routing wired, a correction is injected listing the available keywords.

Synthetic keywords (`__UNCOND_BACK:{nodeId}`) are used internally to track unconditional back-edges through the phase-break path. `RunPhasesAsync` translates them to human-readable `(unconditional handoff from {nodeId})` before injecting into agent history and event emission.

**`DetermineStartNodeId`:** Called at the start of each phase (and on session resume) with an optional hint string. Priority order:
1. Explicit hint matches a node `Id` exactly → use that node.
2. Explicit hint matches a node's `Agent` name → use that node's `Id`.
3. Scan history for the most recent back-edge keyword to determine where the last phase-break targeted.
4. Use the last agent name seen in history and find the corresponding node.
5. Fall back to `Entry`.

**Checkpoint and resume:** `SessionRunner` snapshots `go.StateHistory` (list of `AgentState`) into `SessionCheckpoint.StateHistory` after each yielded message. On resume, `SetResumeStateName` accepts a node ID or agent name; `DetermineStartNodeId` applies the priority chain above to resolve the correct starting node.

**Execution model:**

```
START
  → DetermineStartNodeId
  → BuildPhaseWorkflow  (MAF DAG of forward edges from start node)
  → RunStreamingAsync
  → WatchStreamAsync:
      → (WorkflowOutputEvent ? InspectKeyword : ContinueTurn)
  → InspectKeyword → DetermineNextStartNode
  → (terminal keyword ? END : BuildPhaseWorkflow)
```

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

Each of the three phases assembles its agent's context via `IContextAssemblyPipeline.AssembleAsync` (memory augmentation, ADR/knowledge retrieval, per-agent `Context:` spec) when a pipeline is wired, falling back to raw instructions+history otherwise — shared with `ScatterGatherOrchestrator` via `FanOutHelpers.AssembleContextAsync`. Per-turn observations are also persisted to `RepositoryKnowledgeStore` when configured, matching `GraphOrchestrator`/`MagenticOrchestrator`.

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

### 6.7 Sub-graph nodes in GraphOrchestrator

A `GraphNodeConfig` with `SubGraphId` set runs a nested sub-orchestrator instead of a single agent. The spec is looked up from `GraphConfig.SubGraphs`, which maps string IDs to `SubGraphSpec` — a discriminated union with exactly one of:

- `SubGraphSpec.Graph` → spawns a child `GraphOrchestrator` with a synthetic config where `Selection.Type = "graph"` and `Selection.Graph = subSpec.Graph`
- `SubGraphSpec.MapReduce` → spawns a `MapReduceOrchestrator` with `Selection.Type = "mapreduce"` and `Selection.MapReduce = subSpec.MapReduce`
- `SubGraphSpec.ScatterGather` → spawns a `ScatterGatherOrchestrator` with `Selection.Type = "scattergather"` and `Selection.ScatterGather = subSpec.ScatterGather`

All sub-orchestrators share the parent's services (agentFactory, changeTracker, eventEmitter, governanceKernel). Messages streamed by the sub-orchestrator are forwarded directly to the parent's message sink. The sub-orchestrator's terminal assistant message is injected into the parent's shared history as a synthetic `ChatMessage`, reusing the parent's route tables for keyword detection — with a text-only fallback: tool-call-based keyword extraction (`ExtractHandoffToolCallKeyword`) isn't available for sub-graph output since raw `ChatMessage`/`FunctionCallContent` isn't exposed across the sub-orchestrator boundary, so `KeywordDetector.DetectKeywords` scans the text instead.

### 6.8 WorkflowOrchestrator

A directed-graph orchestrator for `Selection.Type: "workflow"` — a cycle-native sibling of `GraphOrchestrator`. Where `GraphOrchestrator` distinguishes forward edges from back-edges (§6.3) and implements cycles via an outer phase-restart loop (rebuilding a fresh MAF DAG per phase, since MAF's `WorkflowBuilder` does not support in-graph cycles — see §17), `WorkflowOrchestrator` takes a different approach: every edge, including ones that close a cycle, becomes a plain, uniform route in the node's `AgentRouteTable`. There is no BFS/DFS layer or back-edge classification at all — a route from `tester` back to `developer` is wired identically to any forward route, with no `PhaseBreakKeywords` bucket and no phase-restart mechanism. This makes cycles config-driven and uniform, at the cost of the phase-boundary semantics `GraphOrchestrator` uses for validator gating between phases.

`WorkflowOrchestrator` deliberately duplicates `GraphOrchestrator`'s per-node retry skeleton (`MaxRetries`/`MaxTotalTurnsMultiplier`-derived turn cap, consecutive-failure counting, `TimeoutException` handling) rather than sharing it, since the two orchestrators' node-execution loops diverge enough (no forward/back distinction here) that a shared implementation would need its own abstraction layer.

`WorkflowOrchestrator` wires `governanceKernel` (circuit breaker around the agent call, plus audit/rate-limit/SLO recording on validator failure — `RecordGovernanceViolation`, independently implemented rather than shared, per the same convention as its validator-resolution logic) and `IContextAssemblyPipeline` (`HandleContextOverflowAsync`, falling back to the legacy `ContextWindowFilter` when no pipeline is configured) identically to `GraphOrchestrator`, so switching `Selection.Type` from `graph` to `workflow` no longer loses governance protection or the context pipeline's memory/knowledge injection. It still has no human-approval gate or recovery-agent invocation — both are rejected at config-validation time for `workflow` (§ workflow v1 limitations in `docs/strategies.md`), so there is nothing to wire — and no repository-knowledge-store observation extraction (`GraphOrchestrator`/`MagenticOrchestrator` persist per-turn findings from tool calls; `WorkflowOrchestrator` does not). Both orchestrators do share the same validator-name→instance resolution surface (`BuildValidatorsFromNames`, including `ArchitectureValidator`), so validator configuration itself transfers correctly between the two.

---

## 7. Selection Strategies

Built and returned by `StrategyFactory.CreateSelection`. All implement `IAgentSelector`.

| Type | Behavior |
|---|---|
| `sequential` | `SequentialAgentSelector` — one-pass sweep through agents in declaration order; returns `null` after the last agent, ending the loop |
| `roundrobin` | `RoundRobinAgentSelector` — cycles through agents in declaration order indefinitely; session ends only when a `Termination` strategy fires |
| `llm` | Calls an `IChatClient` with a configurable prompt template to pick the next agent by name |
| `keyword` | Scans the last assistant message for configured keywords; each keyword routes to a named agent. Optional validators gate the route before it fires. |
| `statemachine` | Explicit state graph: agents emit signals matched against the current state's outgoing transitions; all declared contracts must pass before a transition fires. Eliminates routing hallucinations — agents emit signals, the machine resolves transitions. |
| `structured` | Evaluates condition expressions per route rather than string keywords |
| `adversarial` | Handled entirely by `AdversarialOrchestrator`; agents are paired as generator/critic per stage. `StrategyFactory` is not involved. |
| `mapreduce` | Handled entirely by `MapReduceOrchestrator`; `StrategyFactory` is not involved. |
| `scattergather` | Handled entirely by `ScatterGatherOrchestrator`; `StrategyFactory` is not involved. |
| `magentic` | Handled entirely by `MagenticOrchestrator`; `StrategyFactory` throws if this type reaches it |
| `graph` | Handled entirely by `GraphOrchestrator`; routing is driven by per-node `AgentRouteTable` instances built at startup from the `Graph.Nodes` config. `StrategyFactory` is not involved. |

**`KeywordSelectionStrategy`** is the workhorse for structured pipelines and `GraphOrchestrator` nodes. Key behaviors:
- Keyword matching is strict per-line (not substring): the full trimmed line must equal the keyword
- Source agent filtering: a route can be restricted to fire only when a specific agent authored the last message
- Routing validators run synchronously before the route fires; failure injects a correction message and re-invokes the current agent (up to the per-type `Threshold` in `FailureHandlingConfig` before `ValidatorStuckException`)
- Governance policy violations are emitted to the event log with consecutive-failure counts
- `RequireHumanApproval: true` on a route escalates to `IHumanApprovalService.PromptApprovalAsync` before routing
- `RecoveryAgent` on a route activates an alternate agent when the validator fails repeatedly (`ActivateRecovery` action or ≥2 consecutive failures)
- `PreferStructuredOutput: true` (with a `Condition`) makes JSON the primary routing signal for that route; the keyword becomes a fallback. When the response is not JSON, a correction is injected and the source agent is re-invoked (up to `MaxStructuredParseRetries = 2`). After retries are exhausted, keyword matching resumes as a safety fallback. Example:

```json
{
  "Keyword": "HANDOFF TO REVIEWER",
  "Agent": "Reviewer",
  "PreferStructuredOutput": true,
  "Condition": { "Field": "review_result", "Is": "approved" }
}
```

**`StateMachineSelectionStrategy`** tracks an explicit current state and evaluates that state's outgoing transitions after each agent turn. Key behaviors:
- Signal detection reuses the same strict per-line matching as `KeywordSelectionStrategy`; existing agent instructions need minimal changes when migrating
- Transitions require the signal AND all declared `ContractEngine` predicates to pass (AND semantics); failure injects a typed correction and re-invokes the current state's agent
- Both `KeywordSelectionStrategy` and `StateMachineSelectionStrategy` classify failures via `FailureClassifier`/`FailureHandlingConfig` and share the core `ActivateRecovery`/`EscalateToHuman`/`Abort` semantics, but the two implementations are independent, hand-written copies that have diverged in their extras: only `KeywordSelectionStrategy` has a governance `RateLimiter` 10-minute-window escalation (failures per agent+route within the window trigger immediate escalation once the window fills); only `StateMachineSelectionStrategy` has the `MaxConsecutiveContractFailures` global backstop (below) and verifier-turn scheduling (next bullet). Do not assume a policy change to one strategy's failure handling automatically applies to the other.
- `SourceAgents` restrictions on transitions prevent ghost signals from other agents bleeding through the lookback window
- A verifier agent can be scheduled for the next turn on `ConflictingEvidence` or `NoProgress` failures when `VerifierConfig` is configured

**`StructuredSelectionStrategy`** evaluates condition strings (e.g. `"last_agent == 'Tester' && contains(last_message, 'PASS')"`) via `StructuredConditionEvaluator`. Used for configs that need multi-variable routing logic without keyword string matching. Parse failures (invalid JSON, or valid JSON matching no route) are classified via `FailureClassifier`/`FailureHandlingConfig` the same way `KeywordSelectionStrategy` and `StateMachineSelectionStrategy` are — `EscalateToHuman` throws immediately, otherwise a correction is injected and the source agent re-invoked until the classified failure type's `Threshold` is reached. There is no `RecoveryAgent` concept for this strategy (`RouteEntry` has no such field), so `ActivateRecovery` falls back to the same threshold-based escalation as `Abort`.

---

## 8. Termination Strategies

Built and returned by `StrategyFactory.CreateTermination`. All implement `ITerminationCondition`.

| Type | Behavior |
|---|---|
| `regex` | Terminates when a regex matches the last assistant message (optional agent-name filter) |
| `maxiterations` | Never terminates via condition — relies on `MaxIterations` hard cap in `AgentOrchestrator` |
| `composite` | OR (ANY) of child conditions — terminates as soon as any one child signals termination. (`CompositeTerminationStrategy`'s own docstring says this explicitly; it is not an AND of all children.) |

Termination strategies can be decorated with routing validators via the `Validators` field. A `ValidatedTerminationStrategy` runs the validators before accepting the termination signal. The `requireCurrentTurn: true` flag is specific to `RequireShellPassValidator` (it is a constructor parameter on that validator, not a termination-strategy-wide feature) and prevents a stale change-log entry from satisfying it based on an earlier turn's shell run.

---

## 9. Routing Validators

Validators implement `IRoutingValidator` and run synchronously before a route or termination fires. Most examine external artifacts (change log, test report, brief file) rather than LLM output — `RequireReviewJudgement` is the one exception, since a review judgement only exists as the reviewer's own message text (see below).

| Validator | What it checks |
|---|---|
| `RequireShellPass` | Change log contains a shell command in the current turn that matches `RequiredCommandPattern` and exited 0 |
| `RequireWriteFile` (`HandoffToTesterValidator`) | Change log contains a file write in the current turn (or a shell fallback with the configured pattern) |
| `TestReportValid` (`HandoffToReviewerValidator`) | Test report file exists, is non-empty, and all `TestAssertionPatterns` match |
| `RequireBrief` | Brief file exists and is non-empty |
| `RequireAllFilesWritten` | All files listed in the brief's deliverables section have been written per the change log |
| `RequireReviewJudgement` | Parses a structured `{"review":[{criterion, verdict, evidence}]}` JSON block from the reviewer's message (not a plain APPROVED/REJECTED keyword scan), enforces per-criterion coverage against `brief.json`, and requires a successful `shell_run` recorded in the current turn's change log to back any PASS verdict |
| `RequireAcceptanceCriteriaPassed` (`RequireAcceptanceCriteriaPassedValidator`) | Checks the brief's acceptance criteria have all been satisfied per the change log; used directly by `GraphOrchestrator`/`WorkflowOrchestrator` |
| `BlockOnConsecutiveFail` (`ConsecutiveShellFailValidator`) | Blocks the forward edge and forces a replan when the same shell command has failed repeatedly (default: last 3 turns) — pairs with `RequiredCommandPattern` to target a specific build/test command |
| `ArchitectureValidator` | Blocks a handoff when architecture layer violations are present in the project source tree, per the manifest at `.fuseraft/architecture.yaml` (or a configured path); passes unconditionally when no manifest exists |
| `RequireRelatedTestsPass` | Resolves changed files from the change log, discovers related test targets via a configurable `FindRelatedCommand` (with `{file}` substitution), runs them — falling back to `FullSuiteCommand` when discovery returns nothing — and passes only when the test command exits 0 |

This list is not necessarily exhaustive of every `ValidatorNames` constant — it covers the validators reachable by name from `KeywordSelectionStrategy`/`StateMachineSelectionStrategy`/`GraphOrchestrator`/`WorkflowOrchestrator`'s validator-name registries.

When a validator fails, the route is blocked: the source agent is re-invoked with an injected error message tailored to the failure type (`MissingEvidence`, `InvalidTransition`, `ConflictingEvidence`, `NoProgress`). The response policy is controlled by `FailureHandlingConfig` — `Reinstruct` (default) injects a correction and retries; `ActivateRecovery` routes to the route's `RecoveryAgent` on the first request; `EscalateToHuman` throws immediately; `Abort` escalates after the configured per-type `Threshold` consecutive failures. When the threshold is reached, `ValidatorStuckException` is thrown and the session escalates to HITL.

**Failure handling pipeline:** All failures follow this flow regardless of which strategy or orchestrator is active:

```
1. Classify  → FailureClassifier.Classify(error, hasToolCalls, isFirstFailure) → FailureType
2. Lookup    → FailureHandlingConfig.GetConfig(failureType) → FailureTypeConfig
3. Execute   → FailureAction (Reinstruct / ActivateRecovery / EscalateToHuman / Abort)
4. Record    → EventEmitter ("validation_fail") + GovernanceKernel audit
5. Continue or terminate (ValidatorStuckException)
```

No component may bypass this pipeline — `KeywordSelectionStrategy`, `StateMachineSelectionStrategy`, and `StructuredSelectionStrategy` all route failures through it (see §7). Correction messages injected at step 3 are always `ChatRole.User` messages appended to shared history before the source agent is re-invoked.

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
| `ResumeExecutorId` | Hint for `GraphOrchestrator` — which node was active when compacted |
| `MagenticState` | `MagenticCheckpointState` snapshot for Magentic loop resume |
| `StateHistory` | Ordered list of `AgentState` snapshots produced during the session; populated by `GraphOrchestrator`; `null` for other orchestrators |

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

**Resume path** (`RunCommand`): `--resume <sessionId>` loads the checkpoint, validates `IsComplete == false`, rehydrates `priorHistory`, and calls `SetResumeExecutorId` / `SetResumeState` on the orchestrator before the next `StreamAsync` call.

**Why we did not use the MAF framework's checkpointing layer:** The framework's `Checkpoint` type captures MAF workflow execution state — executor queue, edge state, outstanding external requests. Our `SessionCheckpoint` captures conversation semantics — agent messages, token usage, Magentic loop counters. They solve different problems at different levels of abstraction. The framework layer applies only to `GraphOrchestrator` (which uses `InProcessExecution`); `AgentOrchestrator` and `MagenticOrchestrator` are manual loops with no MAF workflow graph. Replacing our layer with the framework's would lose agent identity, role, token usage, and Magentic loop state, while gaining sub-turn recovery that provides no practical benefit given our turns are already fine-grained checkpointed.

---

## 11. Conversation Compaction

`ConversationCompactor` prevents context window exhaustion on long sessions by summarizing older turns using an LLM.

**Trigger:** `ShouldCompact(messages)` returns true when the assistant-message count in `messages` reaches `config.TriggerTurnCount`. Only assistant turns are counted — user messages and tool frames are excluded. The `SessionRunner` resets this count to the retained tail's assistant count after each compaction so the trigger fires relative to the current window, not the session lifetime.

**Process:** The oldest `Count - KeepRecentTurns` messages are compacted into a single summary `AgentMessage`. The retained tail is kept verbatim. The summary is injected with `Role = "user"` so agents treat it as context, and `IsCompactionSummary = true` so tooling can identify it.

**Change log grounding:** When `ChangeTracking` or `Validation.ChangeLogPath` is configured, the compactor reads the change log at compaction time and includes it in the summary prompt as authoritative ground truth. The prompt instructs the LLM to trust the change log over agent self-reports — if an agent claimed success but the change log records a non-zero exit code or no file write, the summary reflects reality. Sessions without a change log use a standard prompt that summarizes conversation claims only.

**Resume note:** For non-Magentic sessions, a standard `WorkflowResumptionNote` is appended to the summary prompt instructing agents to re-read the brief and change log (not available from memory alone after compaction). This note is suppressed for Magentic sessions, which have no brief or change log.

**After compaction:** `SessionRunner` captures `ResumeExecutorId` from the last assistant message in the compacted tail, updates the checkpoint, and saves before continuing.

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
| `SubAgent` | `sub_agent_explore` (multi-hop exploration, prose or file-list output, configurable iteration cap) · `sub_agent_locate` (single-target symbol/file lookup, 5-iteration hard cap, path:line output) — both run an isolated tool loop and return a distilled result without filling the caller's context. Working directory is injected automatically; the parent's cancellation token is linked. Model and plugin set are configurable via `SubAgentModel`, `SubAgentMaxToolCalls`, and `SubAgentPlugins`. Default tool set: FileSystem read, Search, Git read, and Shell — **not** read-only: the default Shell allow-list is `shell_run`, `shell_get_env`, `shell_which`, `shell_get_working_directory`, so a sub-agent can execute commands (e.g. builds, tests) by default, subject to the sandbox/ring the parent agent runs under. |

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

**Event bridge:** `GovernanceEventType.ToolCallBlocked` events (from sandbox + injection checks) are forwarded to the `EventEmitter` as `tool_blocked` JSONL events. `PolicyViolation` events are emitted directly by `KeywordSelectionStrategy` with richer per-turn context.

**Agent DIDs:** Every agent is assigned a `did:fuseraft:<name>` identifier at construction. DIDs are used as actor identifiers in the audit log and are resolved by `AgentFactory.GetDid(name)` for governance lookups.

---

## 14. Change Tracking

`ChangeTracker` wraps every agent with a `CapturingMiddleware` that intercepts tool call results and records structured entries to a JSON change log.

**Tracked functions:** `write_file`, `patch_file`, `delete_file`, `delete_directory`, `copy_file`, `move_file`, `shell_run`, `shell_run_script`, `shell_run_background`, `git_commit`.

**`ChangeLog` schema** (`changes.json`, one entry per turn):
- `ActiveSessionId` — current session ID
- `Entries[]` — `{ Agent, TurnIndex, Timestamp, SessionId, FilesWritten[], FilesDeleted[], CommandsRun[], GitCommits[] }`

**Intent log** (`~/.fuseraft/sessions/{project_slug}/{session_id}/intents.json`): Alongside the change log, `CapturingMiddleware` also writes to an `IntentLog` — one entry per tracked tool call, written *before* the call executes with `Status: Pending`, then updated to `Applied` or `Failed` once the call returns.

- `BeginTurn(agentName, turnIndex)` must be called before each `agent.RunAsync` so middleware has the correct turn index. All orchestrators (`AgentOrchestrator`, `MagenticOrchestrator`, `GraphOrchestrator`) call this immediately after `OnAgentTurnStarting()`.
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
| `session_start` | `GraphOrchestrator`, `ReplCommand` | `task` (raw task string), `start_node`, `resume` |
| `session_end` | `GraphOrchestrator`, `ReplCommand` | Turn count, succeeded |
| `session_summary` | `SessionMetrics` | `total_turns`, `total_input_tokens`, `total_output_tokens`, `max_turn_input_tokens`, `total_tool_calls`, `total_patch_failures`, `total_duplicate_reads`, `total_compactions` |
| `phase_start` | `GraphOrchestrator` | Phase name, starting executor |
| `phase_end` | `GraphOrchestrator` | Phase name, turn count |
| `compaction` | `SessionRunner` | Turn count before/after, `reason` |
| `compaction_resume_candidate` | `SessionRunner` | `last_assistant_agent`, `current_state_name`, `reason`, `total_messages` — emitted at compaction time to diagnose handoff-then-compaction resume divergence |
| `session_error` | `SessionRunner` | Exception message |

*Per-turn*

| Event | Emitter | Payload |
|---|---|---|
| `turn_start` | `GraphOrchestrator` | Agent name, turn index |
| `turn_end` | `AgentOrchestrator`, `GraphOrchestrator`, `MagenticOrchestrator` | Agent name, turn index, input/output tokens |
| `turn_timeout` | `GraphOrchestrator` | Agent name, timeout value |
| `reasoning` | `AgentOrchestrator`, `GraphOrchestrator` | Reasoning token content |
| `context_assembly` | All orchestrators | `knowledge_retrieved`, `knowledge_included`, `memory_loaded`, `memory_included`, `artifacts`, `context_chars`, `system_prompt_chars`, `assembly_ms`, `context_chars_breakdown` (per-source: `system_prompt`, `memory`, `session_context`, `knowledge`, `history`), `tool_count`, `tool_schema_est_tokens` |

*Routing and keyword handling* (`GraphOrchestrator`)

| Event | Emitter | Payload |
|---|---|---|
| `keyword_detected` | `GraphOrchestrator` | Keyword, agent routed to |
| `multi_keyword` | `GraphOrchestrator` | All keywords found in the response |
| `no_keyword` | `GraphOrchestrator` | Agent name, turn index |
| `keyword_not_found` | `KeywordSelectionStrategy` | Last message author, content excerpt |
| `agent_routed` | `GraphOrchestrator` | From agent, to agent, keyword |
| `state_advanced` | `GraphOrchestrator` | New `AgentState` version, destination executor |
| `back_edge_escalation` | `StateMachineSelectionStrategy` | `from_state`, `to_state`, `visit_count`, `max_revisits`, objections from `ReviewArtifactPath` — fired when a back-edge exceeds `MaxRevisits` |
| `context_cap_warning` | `GraphOrchestrator` | Agent name, current message count, soft threshold |
| `correction_injected` | `CorrectionEngine` | Correction message text, reason |

*Validation*

| Event | Emitter | Payload |
|---|---|---|
| `validation_fail` | `KeywordSelectionStrategy`, `GraphOrchestrator` | Validator name, consecutive failure count, error detail |
| `hitl_escalation` | `SessionRunner` | Reason (stuck validator or explicit escalation) |

*Saga / compensating rollback*

| Event | Emitter | Payload |
|---|---|---|
| `saga_compensating` | `SagaOrchestrator` | Agent name being compensated, step index |
| `saga_compensated` | `SagaOrchestrator` | Agent name, compensation result |

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

*Sub-agent*

| Event | Emitter | Payload |
|---|---|---|
| `sub_agent_start` | `SubAgentPlugin` | Agent name, query (truncated to 120 chars), mode (`explore` \| `locate`) |
| `sub_agent_tool_call` | `SubAgentPlugin` | Agent name, tool name, args summary |
| `sub_agent_end` | `SubAgentPlugin` | Agent name, outcome (`completed` \| `cancelled` \| `timeout` \| `error`), summary_chars, mode |

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

Fuseraft-cli is built on MAF (`Microsoft.Agents.AI`, `Microsoft.Agents.AI.Workflows`) with optional A2A client federation via `Microsoft.Agents.AI.A2A`.

**Non-MAF / extension dependencies** (managed separately from the MAF version):

| Package | Version | Notes |
|---|---|---|
| `Azure.AI.OpenAI` | `2.1.0` (stable) | Pinned to the last GA release. The 2.2–2.9 beta series does not have a GA date; the SDK team is steering users toward the base `OpenAI` SDK for non-Azure deployments. `AzureOpenAIClient` from this package is used only for the `provider: azure` case. |
| `OllamaSharp` | `5.4.25` | Replaces the deprecated `Microsoft.Extensions.AI.Ollama` package (frozen at `9.7.0-preview.1`, no GA planned). `OllamaApiClient` implements `IChatClient` directly — no `.AsIChatClient()` adapter required. |
| `A2A` | `1.0.0-preview2` | Google's open A2A protocol client library. Used by `AgentFactory` for remote agent card discovery. |
| `Microsoft.Agents.AI.A2A` | `1.3.0-preview.260423.1` | MAF bridge that wraps an A2A `AgentCard` as an `AIAgent`. Provides `A2ACardResolver.GetAIAgentAsync()` used in the remote agent short-circuit path. |

There is no dedicated Anthropic connector package. Claude models (`claude-*` model ID prefix) are routed through the generic OpenAI-compatible client path in `ChatClientFactory`, pointed at `https://api.anthropic.com/v1` with the `ANTHROPIC_API_KEY` env var — not a native `Microsoft.Agents.AI.Anthropic` SDK.

**What we use:**

| MAF Component | How we use it |
|---|---|
| `AIAgent` / `ChatClientAgent` | Base agent type; `RunAsync(context, null, null, ct)` drives each LLM turn |
| `AIAgentExtensions` / `ChatClientFactory` | Agent builder helpers |
| `A2ACardResolver` | Resolves remote agent cards from `{Url}/.well-known/agent.json` and wraps them as `AIAgent` instances (remote agent short-circuit in `AgentFactory`) |
| `WorkflowBuilder` | Builds phase workflows for `GraphOrchestrator` and `WorkflowOrchestrator` (§6.8) |
| `FunctionExecutor<T>` | Wraps per-agent logic in MAF's executor model |
| `InProcessExecution.RunStreamingAsync` | Drives the workflow graph; returns an async stream of events |
| `WatchStreamAsync` | Consumes `WorkflowOutputEvent` and `WorkflowErrorEvent` to drive the phase loop |
| `WorkflowOutputEvent` | Signals a phase-break (agent called `YieldOutputAsync`) |
| `WithOutputFrom` | Restricts phase-break output sources to every node reachable in the current phase graph (not to specific named agents — this entry previously read "Tester and Reviewer only," which was stale documentation from an early example config) |
| `IWorkflowContext.SendMessageAsync` | Routes `AgentContext` to the next executor (HANDOFF TO X) |
| `IWorkflowContext.YieldOutputAsync` | Signals phase-break to the outer loop |

**What we do not use:**

| MAF Feature | Reason |
|---|---|
| `AgentWorkflowBuilder.BuildConcurrent` (Concurrent orchestration) | Fan-out/fan-in via MAF; no per-branch retry loop; branches share the same `AgentContext` (race on mutable history); implemented instead at fuseraft level — see §18 |
| Conditional edge predicates / `SwitchBuilder` | Routing logic lives inside executors (requires retry loop that graph edges cannot provide) |
| `StatefulExecutor` | `AgentContext` as a shared context object serves the same purpose without scoped state isolation |
| `AggregatingExecutor` | No incremental aggregation pattern in any current orchestrator |
| `RequestPort` (external request handling) | Currently unused; a natural fit for Magentic's HITL plan review loop (see below) |
| `CheckpointManager` / `FileSystemJsonCheckpointStore` | Framework layer captures workflow execution state; our layer captures conversation semantics — different problems |
| `GroupChatWorkflowBuilder` | Requires a single shared history; Magentic's two-history model is incompatible (see §6.2) |
| `AgentWorkflowBuilder.CreateHandoffBuilderWith()` (Handoff orchestration) | Mesh routing via auto-injected handoff tool calls; no correction-injection loop; workflow blocks for human input when an agent does not call the handoff tool; shared history across all participants is incompatible with per-agent `ContextWindow` filtering |
| `Microsoft.Agents.AI.DevUI` | For hosted agent services with OpenAI-compatible API endpoints; our DevUI serves a different purpose |

**MAF `GraphOrchestrator` graph topology:** The graph is always a DAG of forward edges within a phase — `AddEdge(src, sink)` only. Cycles are implemented via the outer phase loop that builds a fresh workflow per phase. This is the correct approach: MAF's `WorkflowBuilder` validates DAG structure and does not support in-graph cycles.

**Future opportunity — `RequestPort` for Magentic HITL:** The framework's `RequestPort` is a pause-and-wait-for-external-input primitive: the workflow halts at a `RequestHaltEvent`, the caller calls `SendResponseAsync(response)` to resume. This maps cleanly onto Magentic's plan review loop (currently a polling `IHumanApprovalService` call). Migrating the plan review to `RequestPort` would require `MagenticOrchestrator` to be backed by a MAF workflow rather than a manual loop, which is a non-trivial refactor but architecturally sound.

---

## 18. Decisions Against Framework Features

A summary of explicit decisions **not** to use certain framework capabilities, with rationale.

**`GroupChatWorkflowBuilder` for `MagenticOrchestrator`**
Rejected. The framework's group chat model passes the same conversation history to both the manager and participants. `MagenticOrchestrator` requires two entirely separate histories: a private manager context (fact-gather, plan, ledger evaluations) and a shared participant context. Forcing this into `GroupChatManager.UpdateHistoryAsync` would require fabricating the manager's history on every call, which is fragile and defeats the architecture's clarity. The planning phases, stall detection, replan cycles, and HITL plan review also have no equivalent in the framework abstraction.

**MAF framework checkpointing (`CheckpointManager`, `FileSystemJsonCheckpointStore`)**
Rejected as a replacement for `ISessionStore`. The framework's `Checkpoint` type captures MAF runtime execution state (executor queues, edge state, workflow topology). Our `SessionCheckpoint` captures conversation semantics (agent messages, token usage, cost, Magentic loop state). They operate at different layers of abstraction and solve different problems. Framework checkpointing applies only to `GraphOrchestrator` and would not help `AgentOrchestrator` or `MagenticOrchestrator` at all. Sub-turn recovery (the only benefit the framework layer would add to `GraphOrchestrator`) is not a practical concern given our turns are already fine-grained checkpointed at the conversation level.

**`Microsoft.Agents.AI.DevUI`**
Rejected as a replacement for our `DevUIServer`. The framework's DevUI is designed for hosted ASP.NET Core services exposing OpenAI-compatible Responses and Conversations API endpoints. It presents a chat interface over those endpoints. Fuseraft-cli is a console executable — it has no hosted agent API to point the DevUI at. Our `DevUIServer` visualizes the real-time streaming event flow of a running orchestration session, which is a different problem the framework's DevUI does not address.

**`StatefulExecutor` in `GraphOrchestrator`**
Not adopted. Each executor sharing `AgentContext` (a single mutable object passed through MAF's message routing) achieves the same effective state — all agents read from and write to the same conversation history. `StatefulExecutor` would isolate state per executor, which would require explicit merging of histories and break the shared-history invariant that routing strategies depend on.

**Graph-level conditional routing in `GraphOrchestrator`**
Not adopted. MAF edge conditions fire once per message and have no retry semantics. When an agent fails to emit a routing keyword, the executor injects a correction and calls the LLM again. This retry loop must live inside the executor. Moving routing to graph edges would require removing retries, degrading robustness when models do not follow instructions on the first attempt.

**MAF Handoff orchestration (`AgentWorkflowBuilder.CreateHandoffBuilderWith`)**
Not adopted. MAF Handoff is a mesh topology where routing is driven by auto-injected handoff tool calls — each agent calls the tool to transfer control to the next agent. When an agent does not call the handoff tool, the workflow emits a `request_info` event and blocks, waiting for human input (or auto-continues in the experimental autonomous mode). There is no correction-injection loop: if an agent produces a response without calling the handoff tool, the framework defers to the operator rather than re-invoking the agent.

This is the core incompatibility. Fuseraft's reliability depends on `CorrectionEngine` detecting the missing keyword or tool call, injecting a corrective `ChatRole.User` message, and re-invoking the agent within the same turn. An LLM will routinely fail to emit the expected routing signal on the first attempt; correction + retry is not optional. Removing it in favour of the framework's block-and-wait model would make routing reliability entirely dependent on first-attempt model compliance.

Secondary incompatibilities:
- **Shared history.** Handoff broadcasts all agent messages to all participants for context synchronisation. Per-agent `ContextWindow` filtering (`ExcludeAgents`, `TextOnly`, `MaxTailMessages`) requires independent history slices per agent and cannot be expressed within that broadcast model.
- **Interactive-first execution model.** Handoff was designed for server-hosted scenarios where a workflow can park and resume asynchronously on external input. Fuseraft is synchronous CLI execution; the only HITL path is the synchronous `IHumanApprovalService` gate on edge approvals — not a mid-workflow pause primitive.
- **Already covered by existing components.** `HandoffPlugin` already provides tool-based routing signal detection. `GraphOrchestrator` reads it before keyword scanning. The one thing MAF Handoff adds over this is framework-level routing dispatch — but without the surrounding correction loop it would be less reliable than the current implementation, not more.

**MAF Concurrent orchestration (`AgentWorkflowBuilder.BuildConcurrent`)**
Not adopted as the parallelism primitive. MAF's `BuildConcurrent` fans out to a set of executors via `Task.WhenAll` at the workflow runtime level and collects results at a join point. The mechanism is correct, but it cannot be used directly for two reasons.

The core incompatibility is **shared mutable history**. All executors in a MAF concurrent group receive the same `AgentContext` instance. Concurrent agents writing to `AgentContext.History` (a plain `List<ChatMessage>`) would produce interleaved, non-deterministic history across branches. Per-agent `ContextWindow` filtering also assumes a coherent, branch-local view of history — a shared list destroys that invariant.

The second incompatibility is **retry semantics**. MAF concurrent branches fire once. When a parallel agent fails to emit its routing keyword, the `CorrectionEngine` must inject a correction message and re-invoke the agent. That loop must live inside the branch's executor, not at the graph-edge level. The concurrent builder has no built-in retry path.

**What we do instead.** Parallel node execution is implemented entirely within `GraphOrchestrator`:

- Nodes marked `Parallel: true` in the graph config participate in a fan-out group. When the source agent emits the group's trigger keyword, `GraphOrchestrator.RunNodeExecutorAsync` forks the `AgentContext` via `ForkContext` — creating isolated `History` snapshots that share only the `MessageSink` (already `SingleWriter = false`).
- Each parallel worker runs `RunParallelNodeAsync` — a full correction-retry loop identical to `RunNodeExecutorAsync` but without MAF routing calls. Workers run concurrently via `Task.WhenAll`.
- After all workers complete, `MergeParallelContexts` appends each worker's post-fork messages to the parent history under a labelled section header, aggregates token counts, and takes the maximum turn index.
- The source node's executor then calls `wfCtx.SendMessageAsync` to the merge-target node, which was registered in the MAF DAG via a bridging edge during `BuildPhaseWorkflow`.

This preserves the full correction-loop guarantee for each parallel branch while keeping parallel execution transparent to the MAF workflow layer.
