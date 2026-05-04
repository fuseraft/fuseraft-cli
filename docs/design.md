# Fuseraft CLI — Design Document

This document describes the architecture and design decisions behind fuseraft-cli. It is meant to be a living reference for contributors and for future conversations with AI assistants working in this codebase.

---

## Table of Contents

1. [What It Is](#1-what-it-is)
2. [Layer Map](#2-layer-map)
3. [Directory Layout](#3-directory-layout)
4. [Configuration](#4-configuration)
5. [Agent Construction](#5-agent-construction)
6. [Orchestrators](#6-orchestrators)
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

Fuseraft-cli is a production-grade multi-agent orchestration CLI built on the Microsoft Agent Framework (MAF). It drives teams of LLM agents through configurable workflows — software development pipelines, research tasks, general automation — with built-in governance, budget control, session persistence, and human-in-the-loop support.

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
                      IRoutingValidator, IHumanApprovalService, ICompensatingAgent
  Models/           — OrchestrationConfig, AgentConfig, SessionCheckpoint, AgentMessage,
                      AgentState, SagaConfig, TokenUsage, StrategyConfig,
                      ValidationConfig, ...
  Exceptions/       — BudgetExceededException, ValidatorStuckException

Infrastructure/
  AgentFactory.cs         — Builds MAF AIAgent instances from AgentConfig
  ChatClientFactory.cs    — Resolves model aliases; constructs IChatClient per provider
  InMemorySessionStore.cs — In-process checkpoint store (no persistence)
  JsonSessionStore.cs     — File-backed checkpoint store (~/.fuseraft/sessions/)
  McpSessionManager.cs    — Connects to MCP servers; registers their tools at startup
  Plugins/                — Built-in tool plugins (FileSystem, Shell, Git, Http, ...)

Orchestration/
  AgentOrchestrator.cs    — General-purpose multi-agent loop (any selection strategy)
  MagenticOrchestrator.cs — Magentic-One style two-level manager/participant loop
  GraphOrchestrator.cs    — Directed-graph orchestrator; BFS-layer topology, forward-edge phases, back-edge phase restarts
  ConversationCompactor.cs — LLM-based history summarization for long sessions
  ChangeTracker.cs        — Intercepts tool calls to record file/shell/git activity
  ContextWindowFilter.cs  — Applies per-agent context window config to conversation history
  EventEmitter.cs         — Appends structured JSONL events to a log file
  Saga/                   — SagaOrchestrator: compensating rollback wrapper
  Strategies/             — Selection and termination strategy implementations
  Validation/             — Routing validator implementations
  Workflow/               — MAF WorkflowBuilder-based phase orchestrator + StateHandoff
```

---

## 3. Directory layout

All runtime artifacts are written under `.fuseraft/` in the current working directory (project-local) or `~/.fuseraft/` in the user's home directory (global). The `FuseraftPaths` static class in `src/Core/FuseraftPaths.cs` is the single authoritative source for every path — config defaults and infrastructure classes all reference it.

**Global (`~/.fuseraft/`)**

| Path | Contents |
|------|----------|
| `~/.fuseraft/config` | Model ID, endpoint URL (no secrets) |
| `~/.fuseraft/.key` | Plain-text fallback API key (mode 0600; used only when no keychain) |
| `~/.fuseraft/sessions/` | Session checkpoint files (`<sessionId>.json`, mode 0600) |
| `~/.fuseraft/crashdump/` | Crash dump JSON files |
| `~/.fuseraft/scratchpad/` | Default per-agent scratchpad directory |
| `~/.fuseraft/memory/repl/` | REPL persistent memories |
| `~/.fuseraft/memory/agents/<name>/` | Per-agent persistent memories |

**Local (`.fuseraft/` relative to CWD)**

| Path | Contents |
|------|----------|
| `.fuseraft/logs/events.jsonl` | Structured JSONL session events (`EventEmitter`) |
| `.fuseraft/logs/repl_events.jsonl` | REPL session events |
| `.fuseraft/logs/provider_errors.jsonl` | LLM provider error records |
| `.fuseraft/logs/app.log` | Warning+ diagnostic log (always-on Serilog file sink, 5 MB rolling, 3 retained) |
| `.fuseraft/state/changes.json` | Change tracker: file/shell/git activity per turn |
| `.fuseraft/state/intents.json` | Intent log: pre-execution records updated to APPLIED/FAILED |
| `.fuseraft/state/evidence.json` | Evidence graph: typed nodes for contract evaluation |
| `.fuseraft/state/file_versions.json` | Per-file monotonic write counters for conflict detection |
| `.fuseraft/brief.json` | Planner brief (validator input) |
| `.fuseraft/test-report.json` | Tester report (validator input) |
| `.fuseraft/chatroom.jsonl` | Shared agent coordination log |
| `.fuseraft/conventions.json` | Brownfield convention profile (auto-injected into agent prompts) |
| `.fuseraft/brief.brownfield.json` | Brownfield discovery brief (`in_scope_files` seeds change envelope) |
| `.fuseraft/memory_refs.json` | GUIDs of memories scoped to this working directory |
| `.fuseraft/context/` | Context store entries and index |
| `.fuseraft/summaries/` | File summaries written by FileSystem plugin |

All paths are configurable via their corresponding config keys. The table above shows defaults.

---

## 5. Configuration

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
| `ContextWindow` | Optional per-agent history filter (strips tool noise, limits tail length) |

**Environment variable expansion** for `Security.HttpAllowedHosts` and all `ApiProfiles` header values is performed at startup via `${ENV_VAR}` tokens. Credentials never appear in agent instructions or conversation history.

**Config formats:** Both JSON (`.json`) and YAML (`.yaml` / `.yml`) are supported. YAML is parsed via `YamlConfigLoader` and converted to `IConfiguration` for the same `BindConfig` path.

---

## 6. Agent Construction

`AgentFactory.Create(AgentConfig)` produces a fully configured `AIAgent` ready for use by any orchestrator.

**Steps:**

0. **Remote agent short-circuit** — When `AgentConfig.RemoteAgent` is set, `AgentFactory` resolves the remote agent card from `{Url}/.well-known/agent.json` via `A2ACardResolver`, wraps it as an `AIAgent`, and returns immediately. Steps 1–5 below are skipped; `Model`, `Plugins`, `FunctionChoice`, and `Capabilities` are ignored. `Instructions`, `TrustScore`, `ContextWindow`, and `ChangeTracker` wrapping all continue to apply. `GetAIAgentAsync` is dispatched via `Task.Run` so the blocking `.GetAwaiter().GetResult()` call runs on the thread pool rather than the caller's `SynchronizationContext`, avoiding potential deadlocks in hosted environments.

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

`OrchestratorBuilder` selects among four orchestrators based on the config's `Selection.Type` and agent names. The selection order is:

1. **`MagenticOrchestrator`** — when `Selection.Type == "magentic"`
2. **`GraphOrchestrator`** — when `Selection.Type == "graph"`
3. **`WorkflowOrchestrator`** — when `Selection.Type == "keyword"` AND agents are exactly `{planner, developer, tester, reviewer}`
4. **`AgentOrchestrator`** — all other cases

All four implement `IOrchestrator`:

```csharp
Task<OrchestrationResult> RunAsync(string task, IReadOnlyList<AgentMessage>? priorHistory, CancellationToken ct)
IAsyncEnumerable<AgentMessage> StreamAsync(string task, IReadOnlyList<AgentMessage>? priorHistory, CancellationToken ct)
void SetSessionId(string sessionId)
void SetResumeExecutorId(string? executorId)   // WorkflowOrchestrator; consumed once
void SetResumeStateName(string? stateName)     // AgentOrchestrator + StateMachineSelectionStrategy + GraphOrchestrator; consumed once
event Action<string>? AgentStarting
event Action<string, string, string?>? ToolCalling        // (agentName, toolName, argsSummary)
event Action<string, int, int>? TokenBudgetWarning        // (agentName, inputTokens, warnThreshold)
```

### 5.1 AgentOrchestrator

The general-purpose path. Drives any selection strategy through a single `while(true)` loop:

1. Call `IAgentSelector.SelectAsync(agents, history)` → get next agent (null = session ends)
2. Apply the agent's `ContextWindow` filter to trim the history slice passed to the LLM
3. Prepend the agent's system instruction (MAF's `ChatClientAgent.RunAsync` does not inject instructions automatically when `session = null`)
4. Call `agent.RunAsync(context, null, null, ct)` via the governance circuit breaker
5. Append all response messages (including tool calls/results) to shared history with `AuthorName` set
6. Yield the final text response as an `AgentMessage`
7. Check `ITerminationCondition.ShouldTerminateAsync(history)` — break if true
8. Check `MaxIterations` hard cap

**Execution model:**

```
START
  → SelectAgent       (IAgentSelector.SelectAsync)
  → FilterHistory     (ContextWindowFilter)
  → InvokeAgent       (agent.RunAsync via circuit breaker)
  → AppendHistory
  → CheckTermination  (ITerminationCondition.ShouldTerminateAsync)
  → CheckIterationCap
  → (terminated or capped ? END : SelectAgent)
```

**Why instructions are injected manually:** When calling `RunAsync` without a session, MAF does not prepend the agent's `Instructions` as a system message. Agents must see their role definition and routing keywords on every turn, so we prepend it explicitly.

**Shared history:** All agents read from and write to the same `List<ChatMessage>`. This is intentional — routing strategies (especially `KeywordSelectionStrategy`) read `AuthorName` from the most recent assistant message to determine who just spoke and where they want to route.

### 5.2 WorkflowOrchestrator

A phase-based orchestrator built on MAF's `WorkflowBuilder`. Used only for the classic Planner → Developer → Tester → Reviewer pipeline.

**Phase loop:**

Each phase is a fresh linear MAF workflow built from a slice of the four-agent chain starting at `currentStart`. `InProcessExecution.Default.RunStreamingAsync` drives the workflow; `WatchStreamAsync` consumes events. A `WorkflowOutputEvent` signals phase-break (an agent called `YieldOutputAsync`). The outer `while(phaseCount < maxPhases)` loop inspects `AgentContext.LastKeyword` to determine the next phase's starting executor.

**`AgentContext`** is a shared mutable object threaded through all executors via MAF's message passing:
- `History` — shared `List<ChatMessage>` for the session
- `MessageSink` — unbounded `ChannelWriter<AgentMessage>` that decouples executor threads from the caller's async enumerable
- `LastKeyword` — the phase-break keyword set by the last executor before `YieldOutputAsync`
- `CumulativeTokens` — session-lifetime token accumulator (input + output combined)
- `TurnIndex` — monotonic turn counter
- `CurrentState` — the latest `AgentState` snapshot, advanced by `StateHandoff.Advance` on each successful routing step

**MAF graph topology:** Strictly linear — `AddEdge(src, sink)` only. `WithOutputFrom(tester, reviewer)` restricts phase-break output to the two terminal agents. No fan-in, fan-out, conditional edges, or switches are used (see [§16](#16-microsoft-agent-framework-usage)).

**Routing inside executors:** Each `FunctionExecutor` runs a `while(true)` loop that calls `agent.RunAsync`, scans the response for routing keywords via strict per-line matching, and either:
- Calls `SendMessageAsync(ctx, routeTarget)` for in-phase send-forward (HANDOFF TO X)
- Calls `YieldOutputAsync(ctx)` for phase-break (BUGS FOUND, APPROVED, etc.)
- Injects a correction message and retries when no keyword is detected

**Why routing lives inside executors, not on graph edges:** MAF edge conditions (`AddEdge(src, dst, condition: msg => ...)`) fire once per message — they have no retry loop. When an agent fails to emit a routing keyword, we must inject a correction and call the LLM again in the same turn. This retry semantic requires the routing logic to live inside the executor.

**ResumeExecutorId:** After compaction or a mid-phase process restart, `SetResumeExecutorId` provides a one-time hint telling the orchestrator which executor was active when the session paused. It is consumed on the first `StreamAsync` call and cleared.

**Execution model:**

```
START
  → BuildPhaseWorkflow  (linear MAF graph from currentStart)
  → RunStreamingAsync
  → WatchStreamAsync:
      → (WorkflowOutputEvent ? InspectKeyword : ContinueTurn)
  → InspectKeyword → DetermineNextStart
  → (terminal keyword ? END : BuildPhaseWorkflow)
```

### 5.3 MagenticOrchestrator

A Magentic-One style two-level orchestrator. A dedicated manager LLM drives a planning and evaluation loop; participant agents execute tasks on the manager's instructions.

**Two-history model (the core invariant):**
- `sharedHistory` — what participant agents see: user task + all participant responses
- `managerHistory` — what the manager sees: fact-gather prompt/response, plan, and JSON ledger evaluations only. The manager **never** sees participant messages directly.

**Phase structure:**

1. **Fact gathering** — Manager summarizes what it knows about the task and available agents
2. **Planning** — Manager produces a step-by-step plan. Optional HITL review via `IHumanApprovalService.PromptPlanReviewAsync` (feedback loop with revision until approved)
3. **Inner loop** — For each round:
   - Manager evaluates a JSON progress ledger (`MagenticProgressLedger`) against the current plan and shared history
   - If `IsRequestSatisfied`: synthesize final answer → done
   - If stalled (`!IsProgressBeingMade || IsInLoop`): increment stall counter
   - Manager selects next participant and generates a targeted instruction
   - Selected participant executes against shared history + instruction
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

**Checkpoint state** (`MagenticCheckpointState`): `CurrentPlan`, `RoundIndex`, `StallCount`, `ResetCount`, `AwaitingPlanReview` — enough to resume the inner loop exactly where it paused. Exposed via `CurrentState` so `SessionRunner` can snapshot it after each yielded message.

### 5.4 GraphOrchestrator

A directed-graph orchestrator for `Selection.Type: graph`. Each node in the config binds an agent to a unique `Id`; edges carry routing keywords and optional validators. The topology drives execution: forward edges advance within a phase; back-edges break the phase and restart from the target node.

**BFS layer assignment:** At startup, `ComputeBfsLayers` assigns an integer layer to every node via BFS from the `Entry` node, following only non-back edges (detected by topological order). An edge from node `A` to node `B` is a *forward edge* when `layer(B) > layer(A)` and a *back-edge* when `layer(B) ≤ layer(A)`. Layer assignment uses the node list position as a proxy when the exact DAG has not yet been resolved — accurate for topologically ordered node lists, documented in code for future improvement.

**Route tables:** `BuildNodeRouteTables` constructs an `AgentRouteTable` for every node. Each table holds:
- `Routes` — forward-edge routes (keyword → `RouteInfo(targetNodeId, agentName, validators)`)
- `PhaseBreakKeywords` — back-edge destinations (keyword → target node ID)
- `PhaseBreakValidators` — validators keyed by back-edge keyword
- `TerminalValidators` — validators on `Terminal: true` nodes (run before keyword detection)
- `ForeignSendForwardKeywords` — keywords used to re-inject context to the MAF phase's next agent
- `_unconditionalForwardRoutes` — forward edges with no keyword (fire automatically)
- `_unconditionalBackEdges` — back-edges with no keyword (fire automatically)
- `_unconditionalBackEdgeValidators` — validators for unconditional back-edges (stored in a parallel dictionary so they are not silently dropped)

**Phase loop:** `RunPhasesAsync` is the outer `while(true)` loop. Each iteration calls `BuildPhaseWorkflow`, which constructs a fresh MAF DAG containing only the forward edges reachable from the current start node. `InProcessExecution.RunStreamingAsync` drives the phase; `WatchStreamAsync` consumes events. A `WorkflowOutputEvent` signals a phase-break (back-edge keyword or unconditional back-edge). The outer loop reads `lastKeyword` to determine the next start node.

**Keyword detection:** `RunNodeExecutorAsync` runs inside each `FunctionExecutor`. It calls `agent.RunAsync`, then:
1. Checks whether the node is `Terminal: true` — if so, the termination check fires first.
2. Scans the response for keywords in the current node's route table only — keywords from other nodes are ignored.
3. For back-edge matches: validators run; on pass, `YieldOutputAsync` breaks the phase; on fail, a correction is injected and the agent is re-invoked.
4. For forward-edge matches: validators run; on pass, `SendMessageAsync` advances to the next executor in the phase.
5. For unconditional edges: `_unconditionalForwardRoutes` / `_unconditionalBackEdges` fire after the agent turn if no keyword matched, optionally running `_unconditionalBackEdgeValidators` before the phase-break.
6. If no keyword matches and no unconditional edge applies, a correction is injected listing the available keywords.

Synthetic keywords (`__UNCOND_BACK:{nodeId}`) are used internally to track unconditional back-edges through the phase-break path. `RunPhasesAsync` translates them to human-readable `(unconditional handoff from {nodeId})` before injecting into agent history and event emission.

**`DetermineStartNodeId`:** Called at the start of each phase (and on session resume) with an optional hint string. Priority order:
1. Explicit hint matches a node `Id` exactly → use that node.
2. Explicit hint matches a node's `Agent` name → use that node's `Id`.
3. Scan history for the most recent back-edge keyword to determine where the last phase-break targeted.
4. Use the last agent name seen in history and find the corresponding node.
5. Fall back to `Entry`.

**Checkpoint and resume:** `SessionRunner` snapshots `go.StateHistory` (list of `AgentState`) into `SessionCheckpoint.StateHistory` after each yielded message, the same as `WorkflowOrchestrator`. On resume, `SetResumeStateName` accepts a node ID or agent name; `DetermineStartNodeId` applies the priority chain above to resolve the correct starting node.

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

**Why GraphOrchestrator is not WorkflowOrchestrator with a dynamic chain:** `WorkflowOrchestrator` builds a linear `planner → developer → tester → reviewer` chain and encodes loop-back as outer-phase restarts keyed on a fixed keyword set. `GraphOrchestrator` supports arbitrary node topologies, per-node route tables, and multiple back-edges from a single node to different targets. The phase-workflow construction differs (BFS-layered DAG vs. fixed linear chain), and `DetermineStartNodeId`'s hint-fallback chain handles mid-graph resume — none of which fit cleanly into `WorkflowOrchestrator`'s fixed executor model.

---

## 7. Selection Strategies

Built and returned by `StrategyFactory.CreateSelection`. All implement `IAgentSelector`.

| Type | Behavior |
|---|---|
| `sequential` / `roundrobin` | Cycles through agents in order |
| `llm` | Calls an `IChatClient` with a configurable prompt template to pick the next agent by name |
| `keyword` | Scans the last assistant message for configured keywords; each keyword routes to a named agent. Optional validators gate the route before it fires. |
| `statemachine` | Explicit state graph: agents emit signals matched against the current state's outgoing transitions; all declared contracts must pass before a transition fires. Eliminates routing hallucinations — agents emit signals, the machine resolves transitions. |
| `structured` | Evaluates CEL-like condition expressions per route rather than string keywords |
| `magentic` | Handled entirely by `MagenticOrchestrator`; `StrategyFactory` throws if this type reaches it |
| `graph` | Handled entirely by `GraphOrchestrator`; routing is driven by per-node `AgentRouteTable` instances built at startup from the `Graph.Nodes` config. `StrategyFactory` is not involved. |

**`KeywordSelectionStrategy`** is the workhorse for `WorkflowOrchestrator` and structured pipelines. Key behaviors:
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
- Failure classification and `FailureHandlingConfig` policy apply identically to the keyword strategy — `ActivateRecovery` routes to a `RecoveryAgent` declared on the transition, `EscalateToHuman` throws immediately, `Abort` escalates after the configured threshold
- `SourceAgents` restrictions on transitions prevent ghost signals from other agents bleeding through the lookback window
- A verifier agent can be scheduled for the next turn on `ConflictingEvidence` or `NoProgress` failures when `VerifierConfig` is configured

**`StructuredSelectionStrategy`** evaluates condition strings (e.g. `"last_agent == 'Tester' && contains(last_message, 'PASS')"`) via `StructuredConditionEvaluator`. Used for configs that need multi-variable routing logic without keyword string matching.

---

## 8. Termination Strategies

Built and returned by `StrategyFactory.CreateTermination`. All implement `ITerminationCondition`.

| Type | Behavior |
|---|---|
| `regex` | Terminates when a regex matches the last assistant message (optional agent-name filter) |
| `maxiterations` | Never terminates via condition — relies on `MaxIterations` hard cap in `AgentOrchestrator` |
| `composite` | AND of child conditions — all must return true simultaneously |

Termination strategies can be decorated with routing validators via the `Validators` field. A `ValidatedTerminationStrategy` runs the validators before accepting the termination signal. The `requireCurrentTurn: true` flag prevents a stale change-log entry from satisfying a validator that was satisfied in an earlier turn.

---

## 9. Routing Validators

Validators implement `IRoutingValidator` and run synchronously before a route or termination fires. They examine external artifacts (change log, test report, brief file) rather than LLM output.

| Validator | What it checks |
|---|---|
| `RequireShellPass` | Change log contains a shell command in the current turn that matches `RequiredCommandPattern` and exited 0 |
| `RequireWriteFile` (`HandoffToTesterValidator`) | Change log contains a file write in the current turn (or a shell fallback with the configured pattern) |
| `TestReportValid` (`HandoffToReviewerValidator`) | Test report file exists, is non-empty, and all `TestAssertionPatterns` match |
| `RequireBrief` | Brief file exists and is non-empty |
| `RequireAllFilesWritten` | All files listed in the brief's deliverables section have been written per the change log |
| `RequireReviewJudgement` | Last reviewer message contains an explicit APPROVED or REJECTED keyword |

When a validator fails, the route is blocked: the source agent is re-invoked with an injected error message tailored to the failure type (`MissingEvidence`, `InvalidTransition`, `ConflictingEvidence`, `NoProgress`). The response policy is controlled by `FailureHandlingConfig` — `Reinstruct` (default) injects a correction and retries; `ActivateRecovery` routes to the route's `RecoveryAgent` on the first request; `EscalateToHuman` throws immediately; `Abort` escalates after the configured per-type `Threshold` consecutive failures. When the threshold is reached, `ValidatorStuckException` is thrown and the session escalates to HITL.

**Failure handling pipeline:** All failures follow this flow regardless of which strategy or orchestrator is active:

```
1. Classify  → FailureClassifier.Classify(error, hasToolCalls, isFirstFailure) → FailureType
2. Lookup    → FailureHandlingConfig.GetConfig(failureType) → FailureTypeConfig
3. Execute   → FailureAction (Reinstruct / ActivateRecovery / EscalateToHuman / Abort)
4. Record    → EventEmitter ("validation_fail") + GovernanceKernel audit
5. Continue or terminate (ValidatorStuckException)
```

No component may bypass this pipeline. Correction messages injected at step 3 are always `ChatRole.User` messages appended to shared history before the source agent is re-invoked.

---

## 10. Session and Checkpoint Layer

Every session is backed by a `SessionCheckpoint` persisted after each agent turn.

**`SessionCheckpoint` fields:**

| Field | Purpose |
|---|---|
| `SessionId` | 8-character hex ID (`Guid.NewGuid().ToString("N")[..8]`) |
| `Task` | Original task string |
| `ConfigPath` | Config file that produced this session (used on resume) |
| `Messages` | Ordered `List<AgentMessage>` — the complete conversation transcript |
| `StartedAt` | UTC timestamp of session creation (immutable) |
| `LastUpdatedAt` | UTC timestamp of last save (set by `SaveAsync`) |
| `IsComplete` | Set to `true` after the session runs to completion; prevents re-resume |
| `ResumeExecutorId` | Hint for `WorkflowOrchestrator` — which executor was active when compacted |
| `MagenticState` | `MagenticCheckpointState` snapshot for Magentic loop resume |
| `StateHistory` | Ordered list of `AgentState` snapshots produced during the session; populated by `WorkflowOrchestrator` and `GraphOrchestrator`; `null` for other orchestrators |

**`AgentMessage` fields:** `AgentName`, `Content`, `Role`, `TurnIndex`, `Timestamp`, `Usage` (tokens + cost), `IsCompactionSummary`, `ToolCalls` (name, args summary, succeeded).

**`ISessionStore` contract:**
- `SaveAsync` — create or overwrite; sets `LastUpdatedAt`
- `LoadAsync` — load by session ID, null if not found
- `DeleteAsync`
- `ListAsync` — all checkpoints sorted by `LastUpdatedAt` descending

**`JsonSessionStore`** (default): one JSON file per session at `~/.fuseraft/sessions/<sessionId>.json`. Unix file permissions set to 0600 on non-Windows. `ListAsync` deserializes all `.json` files in the directory with error logging for unreadable files.

**`InMemorySessionStore`**: `ConcurrentDictionary` backed; sessions lost on process exit. Used when `Checkpoint.Mode = "memory"` in config or when no config-level checkpoint path is set and the user explicitly opts in.

**Save points** (in `SessionRunner`): after each agent message, after HITL human redirect, before and after compaction, and at session completion (`IsComplete = true`).

**Resume path** (`RunCommand`): `--resume <sessionId>` loads the checkpoint, validates `IsComplete == false`, rehydrates `priorHistory`, and calls `SetResumeExecutorId` / `SetResumeState` on the orchestrator before the next `StreamAsync` call.

**Why we did not use the MAF framework's checkpointing layer:** The framework's `Checkpoint` type captures MAF workflow execution state — executor queue, edge state, outstanding external requests. Our `SessionCheckpoint` captures conversation semantics — agent messages, token usage, cost, Magentic loop counters. They solve different problems at different levels of abstraction. The framework layer applies only to `WorkflowOrchestrator` (which uses `InProcessExecution`); `AgentOrchestrator` and `MagenticOrchestrator` are manual loops with no MAF workflow graph. Replacing our layer with the framework's would lose agent identity, role, token usage, cost tracking, and Magentic loop state, while gaining sub-turn recovery that provides no practical benefit given our turns are already fine-grained checkpointed.

---

## 11. Conversation Compaction

`ConversationCompactor` prevents context window exhaustion on long sessions by summarizing older turns using an LLM.

**Trigger:** `ShouldCompact(messages)` returns true when `messages.Count >= config.TriggerTurnCount`.

**Process:** The oldest `Count - KeepRecentTurns` messages are compacted into a single summary `AgentMessage`. The retained tail is kept verbatim. The summary is injected with `Role = "user"` so agents treat it as context, and `IsCompactionSummary = true` so tooling can identify it.

**Change log grounding:** When `ChangeTracking` or `Validation.ChangeLogPath` is configured, the compactor reads the change log at compaction time and includes it in the summary prompt as authoritative ground truth. The prompt instructs the LLM to trust the change log over agent self-reports — if an agent claimed success but the change log records a non-zero exit code or no file write, the summary reflects reality. Sessions without a change log use a standard prompt that summarizes conversation claims only.

**Resume note:** For `WorkflowOrchestrator` sessions, a standard `WorkflowResumptionNote` is appended to the summary prompt instructing agents to re-read the brief and change log (not available from memory alone after compaction). This note is suppressed for Magentic sessions, which have no brief or change log.

**After compaction:** `SessionRunner` captures `ResumeExecutorId` from the last assistant message in the compacted tail, updates the checkpoint, and saves before continuing.

**Compaction invariants:** Compaction must preserve:
- the last assistant message (always retained verbatim in the tail)
- all routing signals that could still be active
- all validator-relevant artifacts, or replace them with equivalent summaries grounded in the change log
- turn-boundary markers (`[fuseraft: A → B]`) in the retained tail

Compaction must never cause a previously valid route to become invalid, or a validator to pass or fail differently than it would against the original history.

---

## 12. Plugin System

Plugins are `AIFunction`-providing objects registered in `PluginRegistry` and referenced by name in `AgentConfig.Plugins`.

**Built-in plugins:**

| Plugin | Tools |
|---|---|
| `FileSystem` | `read_file`, `grep_file`, `get_file_summary`, `get_file_info`, `save_file_summary`, `list_files`, `write_file`, `patch_file`, `create_directory`, `copy_file`, `move_file`, `set_permissions`, `delete_file`, `delete_directory` |
| `Shell` | `shell_run`, `shell_run_script`, `shell_run_background`, `shell_set_env`, `shell_get_env`, `shell_get_job_status`, `shell_get_job_output`, `shell_kill_job`, `shell_which`, `shell_get_working_directory` |
| `Git` | `git_status`, `git_diff`, `git_log`, `git_show`, `git_branch_list`, `git_stash_list`, `git_add`, `git_commit`, `git_checkout`, `git_create_branch`, `git_init`, `git_push`, `git_pull`, `git_stash`, `git_stash_pop`, `git_reset` |
| `Http` | `http_get`, `http_head`, `http_post`, `http_put`, `http_patch`, `http_delete` — uses named `ApiProfiles` |
| `Json` | `json_format`, `json_minify`, `json_get`, `json_keys`, `json_search`, `json_to_text`, `json_validate`, `json_merge` |
| `Search` | `search_files`, `search_content`, `search_symbol` |
| `CodeExecution` | `code_execution_check_docker`, `code_execution_sandbox_run`, `code_execution_repl_start`, `code_execution_repl_exec`, `code_execution_repl_reset`, `code_execution_repl_stop` — Docker-sandboxed execution |
| `Changes` | `changes_read`, `changes_read_latest` — read the JSONL change log for observability by downstream agents |
| `Probe` | `probe_code`, `probe_assert_output`, `probe_compare_outputs`, `probe_run_hypothesis` — code execution and output verification |
| `Scratchpad` | `scratchpad_read`, `scratchpad_read_all`, `scratchpad_search`, `scratchpad_write`, `scratchpad_delete` — per-agent key-value store |
| `Chatroom` | `chatroom_send`, `chatroom_read` — shared coordination log |
| `Handoff` | `handoff` — emits a routing keyword to trigger a state machine or keyword route transition |
| `SubAgent` | `sub_agent_explore` — spawns a short-lived sub-agent for broad codebase surveys without filling the caller's context. The sub-agent model and plugin set are configurable via `SubAgentModel` and `SubAgentPlugins` on the parent `AgentConfig`. Default tool set: FileSystem read, Search, Shell read, Git read. |

**MCP servers** (`McpSessionManager`): connected at startup via `ModelContextProtocol`. Each server's tools are registered under the server's configured name and are available to any agent that lists that name in `Plugins`. MCP connections are disposed when the session ends.

**`SandboxEnforcementFilter`** (middleware, not a plugin): wraps any agent with a filesystem sandbox. Tool calls that would access paths outside the sandbox root are denied with `[DENIED: sandbox]`. Prompt injection attempts are detected by the governance kernel's `InjectionDetector` and also denied.

**Per-plugin capability filtering** (`AgentConfig.Capabilities`): agents can declare which operations they are permitted to perform within each plugin, independently of the sandbox. The `PluginCapabilityMap` maps tool function names to capability tags; `AgentFactory.BuildTools` filters the tool list at construction time so disallowed tools are never registered on the agent. Tools not in the capability map (e.g. MCP-registered tools) pass through unfiltered. The available capability tags per plugin are:

| Plugin | Capabilities |
|---|---|
| `FileSystem` | `read` (read_file, grep_file, get_file_summary, get_file_info, list_files) · `write` (write_file, patch_file, save_file_summary, create_directory, copy_file, move_file, set_permissions) · `delete` (delete_file, delete_directory) |
| `Shell` | `read` (shell_get_env, shell_get_job_status, shell_get_job_output, shell_which, shell_get_working_directory) · `run` (shell_run, shell_run_script, shell_run_background, shell_set_env, shell_kill_job) |
| `Git` | `read` (git_status, git_diff, git_log, git_show, git_branch_list, git_stash_list) · `write` (git_add, git_commit, git_checkout, git_create_branch, git_init, git_push, git_pull, git_stash, git_stash_pop, git_reset) |
| `Http` | `get` · `head` · `post` · `put` · `patch` · `delete` — one per HTTP verb |
| `Json` | `read` · `write` (merge) |
| `Search` | `read` |
| `Changes` | `read` |
| `Scratchpad` | `read` · `write` |
| `Chatroom` | `read` · `write` |
| `Probe` | `run` (probe_code, probe_assert_output, probe_compare_outputs, probe_run_hypothesis) |
| `CodeExecution` | `read` (check_docker) · `execute` (sandbox_run, repl_*) |

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
| SLO engine | Tracks routing validator compliance rate over a 1-hour rolling window; 95% target; burn-rate alerts at 2× (warning) and 5× (critical) over 600s |

**Policy files:** If `config/policies/default.yaml` exists alongside the config file, it is loaded as a governance policy and applied to all agents in the session.

**Event bridge:** `GovernanceEventType.ToolCallBlocked` events (from sandbox + injection checks) are forwarded to the `EventEmitter` as `tool_blocked` JSONL events. `PolicyViolation` events are emitted directly by `KeywordSelectionStrategy` with richer per-turn context.

**Agent DIDs:** Every agent is assigned a `did:fuseraft:<name>` identifier at construction. DIDs are used as actor identifiers in the audit log and are resolved by `AgentFactory.GetDid(name)` for governance lookups.

---

## 14. Change Tracking

`ChangeTracker` wraps every agent with a `CapturingMiddleware` that intercepts tool call results and records structured entries to a JSON change log.

**Tracked functions:** `write_file`, `patch_file`, `delete_file`, `copy_file`, `move_file`, `shell_run`, `shell_run_script`, `shell_run_background`, `git_commit`.

**`ChangeLog` schema** (`changes.json`, one entry per turn):
- `ActiveSessionId` — current session ID
- `Entries[]` — `{ Agent, TurnIndex, Timestamp, SessionId, FilesWritten[], FilesDeleted[], CommandsRun[], GitCommits[] }`

**Intent log** (`.fuseraft/state/intents.json`): Alongside the change log, `CapturingMiddleware` also writes to an `IntentLog` — one entry per tracked tool call, written *before* the call executes with `Status: Pending`, then updated to `Applied` or `Failed` once the call returns.

- `BeginTurn(agentName, turnIndex)` must be called before each `agent.RunAsync` so middleware has the correct turn index. All three orchestrators (`AgentOrchestrator`, `MagenticOrchestrator`, `WorkflowOrchestrator`) call this immediately after `OnAgentTurnStarting()`.
- On session resume, any `Pending` entries indicate operations that were in-flight at interruption time.
- The `"intent"` compaction mode reads from this log to produce a deterministic `✓`/`✗` summary — no LLM call required.
- If the intent log file is corrupt or unreadable on load, the failure is emitted via `ILogger<IntentLog>` at Warning level and the store resets to empty for the session.

**`ChangeLog` load failures** (`.fuseraft/state/changes.json`): Both the session-init path (setting `ActiveSessionId`) and the per-entry flush path read the existing change log before appending. If either read fails, the failure is emitted via `ILogger<ChangeTracker>` at Warning level and the log resets to empty for that operation. `EvidenceStore` and `FileVersionStore` follow the same pattern. All warnings route to `.fuseraft/logs/app.log` via the always-on Serilog file sink so they survive past the terminal session.

**`IntentStore` schema** (`.fuseraft/state/intents.json`):
- `ActiveSessionId`
- `Entries[]` — `{ IntentId, Timestamp, Agent, TurnIndex, SessionId, Operation: { FunctionName, TargetPath, ArgsSummary }, Status, ErrorMessage, CompletedAt }`

**`FileVersionStore`** (`.fuseraft/state/file_versions.json`): A lightweight per-file version counter, also initialized by `OrchestratorBuilder`. Every successful `write_file` call increments the counter. Agents call `stat_file` to probe the current version and pass `baseVersion` to `write_file` to detect concurrent-write conflicts. If the store file is corrupt or unreadable, the failure is emitted via `ILogger<FileVersionStore>` at Warning level and the counter resets to zero for the session — agents will see all files at version 0 and conflict detection will not fire until files are written again.

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
| `session_start` | `WorkflowOrchestrator`, `ReplCommand` | Task, agent count |
| `session_end` | `WorkflowOrchestrator`, `ReplCommand` | Turn count, succeeded |
| `phase_start` | `WorkflowOrchestrator` | Phase name, starting executor |
| `phase_end` | `WorkflowOrchestrator` | Phase name, turn count |
| `compaction` | `SessionRunner` | Turn count before/after |
| `session_error` | `SessionRunner` | Exception message |

*Per-turn*

| Event | Emitter | Payload |
|---|---|---|
| `turn_start` | `WorkflowOrchestrator` | Agent name, turn index |
| `turn_end` | `AgentOrchestrator`, `WorkflowOrchestrator`, `MagenticOrchestrator` | Agent name, turn index, input/output tokens |
| `turn_timeout` | `WorkflowOrchestrator` | Agent name, timeout value |
| `reasoning` | `WorkflowOrchestrator` | Reasoning token content |

*Routing and keyword handling* (`WorkflowOrchestrator`)

| Event | Emitter | Payload |
|---|---|---|
| `keyword_detected` | `WorkflowOrchestrator` | Keyword, agent routed to |
| `multi_keyword` | `WorkflowOrchestrator` | All keywords found in the response |
| `no_keyword` | `WorkflowOrchestrator` | Agent name, turn index |
| `keyword_not_found` | `KeywordSelectionStrategy` | Last message author, content excerpt |
| `agent_routed` | `WorkflowOrchestrator` | From agent, to agent, keyword |
| `state_advanced` | `WorkflowOrchestrator` | New `AgentState` version, destination executor |
| `context_cap_warning` | `WorkflowOrchestrator` | Agent name, current message count, soft threshold |
| `correction_injected` | `WorkflowOrchestrator` | Correction message text, reason |

*Validation*

| Event | Emitter | Payload |
|---|---|---|
| `validation_fail` | `KeywordSelectionStrategy`, `WorkflowOrchestrator` | Validator name, consecutive failure count, error detail |
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
| `sub_agent_start` | `SubAgentPlugin` | Agent name, task |
| `sub_agent_end` | `SubAgentPlugin` | Agent name, succeeded, token count |

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

`AgentOrchestrator` registers `ValidationDiagnosticHook` automatically when both `Events` and `ChangeTracking` are configured. The hook is registered once per orchestrator instance and uses a mutable `_activeHistory` reference so it always targets the current session's history across multiple `StreamAsync` calls.

---

## 16. DevUI

`DevUIServer` is a lightweight ASP.NET Core server (started inline via `WebApplication.CreateSlimBuilder`) that provides real-time session visualization in a browser.

**Endpoints:**
- `GET /` — self-contained HTML page (inline in `DevUIHtml.cs`)
- `GET /api/stream` — Server-Sent Events stream of session events

**Event types:** `session_start`, `agent_starting`, `message` (with agent name, content, token usage, cost, elapsed ms), `session_end`.

**Full-history replay:** New SSE clients receive the complete event history on connect so page refresh always shows the entire session from the beginning.

**Port:** dynamically assigned via `TcpListener(IPAddress.Loopback, 0)` at startup; printed to the terminal.

**Why we did not use the framework's `Microsoft.Agents.AI.DevUI`:** The framework's DevUI is an API playground for hosted agent services — it requires `AddOpenAIResponses()`, `AddOpenAIConversations()`, and ASP.NET Core hosting, and presents a chat interface over those HTTP endpoints. Fuseraft-cli is a console executable with no hosted agent API. Our DevUI visualizes the streaming event flow of a running orchestration session (agent turns, cost, token usage, phase transitions) — a fundamentally different use case that the framework's DevUI does not address.

---

## 17. Microsoft Agent Framework Usage

Fuseraft-cli is built on MAF (`Microsoft.Agents.AI`, `Microsoft.Agents.AI.Workflows`) with optional A2A client federation via `Microsoft.Agents.AI.A2A`.

**Non-MAF / extension dependencies** (managed separately from the MAF version):

| Package | Version | Notes |
|---|---|---|
| `Azure.AI.OpenAI` | `2.1.0` (stable) | Pinned to the last GA release. The 2.2–2.9 beta series does not have a GA date; the SDK team is steering users toward the base `OpenAI` SDK for non-Azure deployments. `AzureOpenAIClient` from this package is used only for the `provider: azure` case. |
| `OllamaSharp` | `5.4.25` | Replaces the deprecated `Microsoft.Extensions.AI.Ollama` package (frozen at `9.7.0-preview.1`, no GA planned). `OllamaApiClient` implements `IChatClient` directly — no `.AsIChatClient()` adapter required. |
| `Microsoft.Agents.AI.Anthropic` | `1.3.0-preview.260423.1` | The Anthropic connector ships in a rolling preview cadence independently of the MAF core (which went GA at 1.0). No stable NuGet release has been announced; the connector is expected to remain preview-versioned. |
| `A2A` | `1.0.0-preview2` | Google's open A2A protocol client library. Used by `AgentFactory` for remote agent card discovery. |
| `Microsoft.Agents.AI.A2A` | `1.3.0-preview.260423.1` | MAF bridge that wraps an A2A `AgentCard` as an `AIAgent`. Provides `A2ACardResolver.GetAIAgentAsync()` used in the remote agent short-circuit path. |

**What we use:**

| MAF Component | How we use it |
|---|---|
| `AIAgent` / `ChatClientAgent` | Base agent type; `RunAsync(context, null, null, ct)` drives each LLM turn |
| `AIAgentExtensions` / `ChatClientFactory` | Agent builder helpers |
| `AnthropicClientExtensions` | Constructs Anthropic-backed `AIAgent` instances |
| `A2ACardResolver` | Resolves remote agent cards from `{Url}/.well-known/agent.json` and wraps them as `AIAgent` instances (remote agent short-circuit in `AgentFactory`) |
| `WorkflowBuilder` | Builds linear phase workflows for `WorkflowOrchestrator` |
| `FunctionExecutor<T>` | Wraps per-agent logic in MAF's executor model |
| `InProcessExecution.RunStreamingAsync` | Drives the workflow graph; returns an async stream of events |
| `WatchStreamAsync` | Consumes `WorkflowOutputEvent` and `WorkflowErrorEvent` to drive the phase loop |
| `WorkflowOutputEvent` | Signals a phase-break (agent called `YieldOutputAsync`) |
| `WithOutputFrom` | Restricts phase-break output to Tester and Reviewer only |
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
| `GroupChatWorkflowBuilder` | Requires a single shared history; Magentic's two-history model is incompatible (see §5.3) |
| `AgentWorkflowBuilder.CreateHandoffBuilderWith()` (Handoff orchestration) | Mesh routing via auto-injected handoff tool calls; no correction-injection loop; workflow blocks for human input when an agent does not call the handoff tool; shared history across all participants is incompatible with per-agent `ContextWindow` filtering |
| `Microsoft.Agents.AI.DevUI` | For hosted agent services with OpenAI-compatible API endpoints; our DevUI serves a different purpose |

**MAF `WorkflowOrchestrator` graph topology:** The graph is always a linear chain — `AddEdge(src, sink)` only. Cycles are implemented via the outer `while(phaseCount < maxPhases)` loop that builds a fresh workflow per phase. This is the correct approach: MAF's `WorkflowBuilder` validates DAG structure and does not support in-graph cycles.

**Future opportunity — `RequestPort` for Magentic HITL:** The framework's `RequestPort` is a pause-and-wait-for-external-input primitive: the workflow halts at a `RequestHaltEvent`, the caller calls `SendResponseAsync(response)` to resume. This maps cleanly onto Magentic's plan review loop (currently a polling `IHumanApprovalService` call). Migrating the plan review to `RequestPort` would require `MagenticOrchestrator` to be backed by a MAF workflow rather than a manual loop, which is a non-trivial refactor but architecturally sound.

---

## 18. Decisions Against Framework Features

A summary of explicit decisions **not** to use certain framework capabilities, with rationale.

**`GroupChatWorkflowBuilder` for `MagenticOrchestrator`**
Rejected. The framework's group chat model passes the same conversation history to both the manager and participants. `MagenticOrchestrator` requires two entirely separate histories: a private manager context (fact-gather, plan, ledger evaluations) and a shared participant context. Forcing this into `GroupChatManager.UpdateHistoryAsync` would require fabricating the manager's history on every call, which is fragile and defeats the architecture's clarity. The planning phases, stall detection, replan cycles, and HITL plan review also have no equivalent in the framework abstraction.

**MAF framework checkpointing (`CheckpointManager`, `FileSystemJsonCheckpointStore`)**
Rejected as a replacement for `ISessionStore`. The framework's `Checkpoint` type captures MAF runtime execution state (executor queues, edge state, workflow topology). Our `SessionCheckpoint` captures conversation semantics (agent messages, token usage, cost, Magentic loop state). They operate at different layers of abstraction and solve different problems. Framework checkpointing applies only to `WorkflowOrchestrator` and would not help `AgentOrchestrator` or `MagenticOrchestrator` at all. Sub-turn recovery (the only benefit the framework layer would add to `WorkflowOrchestrator`) is not a practical concern given our turns are already fine-grained checkpointed at the conversation level.

**`Microsoft.Agents.AI.DevUI`**
Rejected as a replacement for our `DevUIServer`. The framework's DevUI is designed for hosted ASP.NET Core services exposing OpenAI-compatible Responses and Conversations API endpoints. It presents a chat interface over those endpoints. Fuseraft-cli is a console executable — it has no hosted agent API to point the DevUI at. Our `DevUIServer` visualizes the real-time streaming event flow of a running orchestration session, which is a different problem the framework's DevUI does not address.

**`StatefulExecutor` in `WorkflowOrchestrator`**
Not adopted. Each executor sharing `AgentContext` (a single mutable object passed through MAF's message routing) achieves the same effective state — all agents read from and write to the same conversation history. `StatefulExecutor` would isolate state per executor, which would require explicit merging of histories and break the shared-history invariant that routing strategies depend on.

**Graph-level conditional routing in `WorkflowOrchestrator`**
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
