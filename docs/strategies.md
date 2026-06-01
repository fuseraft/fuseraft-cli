# Strategies

Strategies control two things: which agent speaks next (selection) and when the run ends (termination).

---

## Selection strategies

Configured under `Selection.Type`.

### sequential

Agents take turns in the order they are declared in `Agents`. When the last agent finishes its turn, the cycle repeats from the first.

```yaml
Selection:
  Type: sequential
```

Use this for simple pipelines where the flow is always the same, or for single-agent configs.

### keyword

An agent's message is scanned for routing keywords to determine who speaks next. If no keyword matches, the `DefaultAgent` is selected (falls back to the first agent if `DefaultAgent` is not set).

```yaml
Selection:
  Type: keyword
  DefaultAgent: Planner
  Routes:
    - Keyword: "HANDOFF TO DEVELOPER"
      Agent: Developer
      Validator: RequireBrief
      SourceAgents:
        - Planner
    - Keyword: "HANDOFF TO TESTER"
      Agent: Tester
      Validators:
        - RequireWriteFile
        - RequireShellPass
      RequiredCommandPattern: "go build|go test"
      SourceAgents:
        - Developer
    - Keyword: "HANDOFF TO REVIEWER"
      Agent: Reviewer
      Validator: TestReportValid
      SourceAgents:
        - Tester
    - Keyword: BUGS FOUND
      Agent: Developer
      SourceAgents:
        - Tester
    - Keyword: REVISION REQUIRED
      Agent: Developer
      SourceAgents:
        - Reviewer
    - Keyword: REPLAN REQUIRED
      Agent: Planner
      SourceAgents:
        - Reviewer
    - Keyword: APPROVED
      Agent: Reviewer
      Validators:
        - RequireShellPass
        - RequireReviewJudgement
      SourceAgents:
        - Reviewer
```

**How routing works**

1. **Tool-call routing (preferred):** If the agent calls `handoff(route_keyword: "...")` via the `Handoff` plugin, the argument is used directly as the routing keyword — no text scanning occurs. This is the most reliable signal because it is a typed function argument, not free text. fuseraft also terminates the agent's tool loop immediately when `handoff` is called, so the agent cannot accidentally call other tools after signalling completion. Add `- Handoff` to an agent's `Plugins` list and instruct it to `call handoff(route_keyword: "KEYWORD")` instead of emitting the keyword as text.
2. **Text scanning (fallback):** If no `handoff` tool call is present, the response text is scanned for every keyword configured in `Routes`.
3. **Line matching** — a text keyword matches when it appears **alone on its own line** (exact match) or at the **start of a line followed by whitespace or punctuation** (e.g. `BUGS FOUND: all issues fixed`), after stripping markdown formatting characters `*` and `_`. A keyword embedded mid-sentence does not match. This prevents accidental routing when agents reference another role's keyword in their output.
4. If **multiple** text keywords appear on their own lines in the same response, the response is rejected as ambiguous and a correction is injected asking the agent to use exactly one keyword. This prevents silent first-match bias from config ordering.
5. The **single** matched keyword is checked against `SourceAgents` — the route only fires if the message author is in that list (or if `SourceAgents` is omitted).
6. If a route has validators (`Validator` or `Validators`), they run before the route fires. If validation fails, the source agent is re-invoked with an error message injected.
7. If the route has `RequireHumanApproval: true`, the operator is prompted to approve before the route fires. If rejected, the source agent is re-invoked with a "route blocked" message.
8. If no keyword matches, `DefaultAgent` handles the next turn.

**Phase-break keywords**

Some keywords end the current pipeline phase and restart from a different agent (`BUGS FOUND`, `REVISION REQUIRED`, `REPLAN REQUIRED`). Others terminate the session (`APPROVED`). These are called *phase-break keywords* and are identified by the self-routing convention: when a route's `SourceAgents` list includes the same agent as `Agent`, it is treated as a terminal route (session ends when the keyword is emitted).

```yaml
- Keyword: APPROVED
  Agent: Reviewer
  SourceAgents:
    - Reviewer
  Validators:
    - RequireShellPass
    - RequireReviewJudgement
```

Here `Agent: Reviewer` and `SourceAgents: [Reviewer]` — the agent routes to itself — which signals termination. All other phase-break routes (BUGS FOUND → Developer, REVISION REQUIRED → Developer, REPLAN REQUIRED → Planner) use different source and target agents and trigger a phase restart rather than session end.

Phase-break keywords must be declared in `Selection.Routes` even if they are also referenced in `Termination.Strategies`. The routing engine reads agent ownership from `Selection.Routes` exclusively — a keyword absent from there will not be recognized when an agent emits it.

**Route fields**

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `Keyword` | string | — | Case-insensitive. Must appear alone on its own line in the response to match — not as part of a sentence or section header. |
| `Agent` | string | — | Agent to activate when the keyword fires. When `Agent` matches one of the `SourceAgents`, the route is terminal (session ends). |
| `Validator` | string | — | Optional. Single validator name. Blocks the route until validation passes. |
| `Validators` | array | — | Optional. Multiple validators (AND semantics — all must pass). Use instead of `Validator` when multiple checks are needed. |
| `SourceAgents` | array | any | Optional. If set, the route only fires when the message author is in this list. Use to prevent agents from triggering routes intended for other roles. |
| `RequiredCommandPattern` | string | — | Optional. When `Validator` is `RequireShellPass`, the passing command must contain at least one of these pipe-separated substrings. |
| `RequireHumanApproval` | bool | `false` | Optional. When `true`, the operator must explicitly approve (`y`) before the route fires. If rejected, the source agent is re-invoked. Works independently of `--hitl` — approval gates fire in normal mode too. |

**Built-in validators**

| Validator | What it checks |
|-----------|---------------|
| `RequireBrief` | Blocks unless `brief.json` exists on disk with a non-empty `goal`, `files_to_change`, and `acceptance_criteria`. Ensures the Planner did its job before the Developer starts. |
| `RequireWriteFile` | Blocks unless the current agent called `write_file` this turn. Prevents fabricated "I wrote the file" claims. |
| `RequireAllFilesWritten` | Blocks unless every file in `brief.json`'s `files_to_change` has been written — in the current turn or in a prior turn recorded in `changes.json`. Prevents partial implementations from passing handoff. |
| `RequireShellPass` | Blocks unless a shell command exited 0 this turn (optionally matching `RequiredCommandPattern`). |
| `TestReportValid` | Blocks unless a valid `test-report.json` exists and passes all structural checks. |

See [Validators](validators.md) for details.

### structured

Routes the next agent based on JSON field conditions evaluated against the last agent's response. Use this when agents produce structured output and routing depends on the *content* of that output rather than a fixed keyword.

```yaml
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
```

**How routing works**

1. After each agent turn, the strategy locates the most recent assistant text message.
2. It tries to extract a JSON object from the text (raw JSON, a ` ```json ` code fence, or the first `{`…last `}` substring — whichever parses first).
3. Each route is evaluated in order. The first route whose `Condition` evaluates to `true` **and** whose `SourceAgents` restriction is satisfied fires. The matched agent handles the next turn.
4. If the response is not valid JSON, or no condition matches, the strategy re-invokes the last agent with a correction message naming the required field(s). After 3 consecutive failures a `ValidatorStuckException` is thrown and the session stops.
5. If no route has fired yet (the very first turn), `DefaultAgent` starts.

**Condition operators**

Exactly one operator should be set per condition. Evaluated in the order listed:

| Operator | YAML | Evaluates to true when… |
|----------|------|--------------------------|
| `Is` | `Is: "value"` | The field's string value equals `value` (case-insensitive). |
| `IsNot` | `IsNot: "value"` | The field's string value does NOT equal `value` (case-insensitive). |
| `Contains` | `Contains: "text"` | The field's string value contains `text` as a substring (case-insensitive). |
| `Exists` | `Exists: true` | The field is present and non-null. |
| `Exists` | `Exists: false` | The field is absent or null. |

**Field paths**

`Field` supports dot-notation for nested objects. For example, `Field: data.status` navigates `{"data": {"status": "ok"}}`.

**StructuredRoute fields**

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `Agent` | string | — | Agent to route to when the condition is true. |
| `Condition` | object | — | Condition to evaluate against the parsed JSON. |
| `SourceAgents` | array | any | Optional. Route only fires when the message author is in this list. |

**Termination with structured routing**

Structured routing has no built-in terminal convention (unlike keyword routing's self-routing `APPROVED`). Session end is always handled by `Termination` strategies. A typical pattern is a `regex` strategy on the last agent in the pipeline:

```yaml
Termination:
  Type: composite
  Strategies:
    - Type: regex
      Pattern: PUBLISHED
      AgentNames:
        - Publisher
    - Type: maxiterations
      MaxIterations: 15
```

**Agent instructions for structured routing**

Agents in a structured workflow should be instructed to return JSON. The routing is invisible to them — they just need to know what fields to include:

```
You are a content reviewer. Evaluate the draft and return your decision as a JSON object:
{"review_result": "Yes", "reason": "..."} if the draft meets requirements, or
{"review_result": "No", "reason": "..."} if it needs revision.
Your entire response must be valid JSON.
```

---

### statemachine

An explicit state graph where agent sequencing is driven by declared transitions rather than keyword scanning. Agents emit signals, but the state machine — not the agent — resolves transitions. This eliminates an entire class of routing hallucinations because agents cannot route themselves to arbitrary states by emitting unexpected text.

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

      Testing:
        Agent: Tester
        Transitions:
          - To: Review
            Signal: "HANDOFF TO REVIEWER"
            Contract: TestsValid
          - To: Implementation
            Signal: BUGS FOUND

      Review:
        Agent: Reviewer
        Transitions:
          - To: Done
            Signal: APPROVED
          - To: Implementation
            Signal: REVISION REQUIRED

      Done:
        Agent: Reviewer
        Terminal: true
```

**How it works**

1. The machine starts in `Initial`. The agent assigned to that state runs first.
2. After each agent turn, the strategy scans the last few messages for signals matching the current state's outgoing transitions.
3. A transition fires when its `Signal` is detected **and** all declared `Contract`/`Contracts` pass.
4. On a successful transition the machine advances to the new state and returns its agent.
5. If a signal is detected but a contract fails, a targeted correction is injected (using the `FailureHandling` policy) and the current state's agent is re-invoked.
6. If no signal is detected, the current state's agent is re-invoked with a nudge listing the available signals.
7. A `Terminal: true` state re-invokes its agent every turn until the `Termination` strategy fires — it has no outgoing transitions.

**Signal detection rules** are the same as keyword routing: the signal must appear alone on its own line or at the start of a line followed by whitespace or punctuation (after stripping `*`/`_` markdown). Agents may also use the `Handoff` plugin (`handoff(route_keyword: "SIGNAL")`) for typed, unambiguous signalling.

**`StateMachineConfig` fields**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `Initial` | string | yes | Name of the starting state. Must exist in `States`. |
| `States` | object | yes | Map of state name → `StateConfig`. At least one state required. |

**`StateConfig` fields**

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `Agent` | string | — | Agent to invoke while in this state. Must match an agent name in `Agents`. |
| `Transitions` | array | `[]` | Outgoing transitions. Empty means terminal (agent runs until termination fires). |
| `Terminal` | bool | `false` | Marks the state as explicitly terminal. Equivalent to having no transitions but makes intent clear. |

**`TransitionConfig` fields**

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `To` | string | — | Target state name. For sequential transitions: the state to enter. For parallel transitions: the **join state** entered after all branches finish and outputs are merged. Must exist in `States`. |
| `Signal` | string | — | Signal the current agent must emit to trigger this transition. When omitted, the transition fires automatically (no signal required) — useful for unconditional handoffs. |
| `Contract` | string | — | Single named contract that must pass. Referenced by name from `Orchestration.Contracts`. |
| `Contracts` | array | — | Multiple named contracts (AND semantics — all must pass). Use instead of or together with `Contract`. |
| `SourceAgents` | array | any | Optional. Restrict this transition to messages authored by agents in this list. |
| `Parallel` | bool | `false` | When `true`, fans out to all states listed in `Targets` concurrently instead of routing to a single state. Each branch runs one agent turn with an isolated history snapshot. Outputs are merged via `Merge` before control advances to the join state in `To`. |
| `Targets` | array | — | Branch state names for parallel fan-out. Required when `Parallel: true`. Each must exist in `States`. |
| `Merge` | object | — | Merge strategy for parallel fan-out. See `MergeConfig` below. Ignored when `Parallel` is `false`. |

**Contracts on transitions**

Transition contracts are evaluated in the same order as `Contracts` (then `Contract`). All must pass for the transition to fire. Contract definitions live in `Orchestration.Contracts`. See [Evidence contracts](configuration.md#evidence-contracts).

```yaml
Orchestration:
  Contracts:
    - Name: ImplementationComplete
      Requires:
        - FilesWritten:
            Source: .fuseraft/artifacts/brief.json
            Field: files_to_change
        - CommandSucceeded:
            Pattern: "build|compile"

  Selection:
    Type: statemachine
    StateMachine:
      Initial: Implementation
      States:
        Implementation:
          Agent: Developer
          Transitions:
            - To: Testing
              Signal: "HANDOFF TO TESTER"
              Contract: ImplementationComplete
```

**Stuck detection and escalation**

When a contract fails consecutively, the `FailureHandling` policy for the classified failure type determines the response (correction injection, audit request, HITL escalation). See [Failure handling](configuration.md#failure-handling).

The `Verifier` agent integrates directly with the state machine: on `ConflictingEvidence` or `NoProgress` failures, the state machine selects the verifier for one audit turn before re-invoking the primary agent. See [Verifier](configuration.md#verifier).

**Parallel fan-out / fan-in**

A transition can fan out to multiple agents running concurrently by setting `Parallel: true` and listing branch states in `Targets`. Each branch agent gets one turn with an isolated copy of the shared history. After all branches complete, their outputs are merged and control advances to the join state (`To`).

```yaml
Selection:
  Type: statemachine
  StateMachine:
    Initial: Planning

    States:
      Planning:
        Agent: Planner
        Transitions:
          - To: Integration          # join state — entered after all branches finish
            Targets:                 # branch states — run concurrently
              - BackendWork
              - FrontendWork
              - MigrationWork
            Parallel: true
            Signal: "IMPLEMENT"
            Merge:
              Strategy: union        # concatenate outputs in declaration order

      BackendWork:
        Agent: BackendDev
        # No transitions — branch agents run one turn; signals are not evaluated.

      FrontendWork:
        Agent: FrontendDev

      MigrationWork:
        Agent: MigrationDev

      Integration:
        Agent: Integrator
        Transitions:
          - To: Done
            Signal: APPROVED

      Done:
        Agent: Integrator
        Terminal: true
```

**How parallel fan-out works**

1. When the triggering signal is detected in the current state, the strategy resolves all `Targets` states and their agents.
2. All branch agents run concurrently (`Task.WhenAll`), each with an isolated snapshot of the shared history at the moment of fan-out. Branches cannot see each other's in-progress work.
3. All branch outputs are merged according to `Merge.Strategy` and the result is injected into the shared history as a single block.
4. The machine transitions to the join state (`To`). The join state's agent then runs as normal with the merged output visible in history.
5. Branch agents' own `Transitions` are **not** evaluated — they run for exactly one turn. Do not instruct branch agents to emit a handoff signal.

**`MergeConfig` fields**

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `Strategy` | string | `union` | How to combine branch outputs. See merge strategies below. |
| `Agent` | string | — | Agent name used for `ranked` and `semantic_diff` strategies. Must be declared in `Agents`. |
| `ConflictResolution` | array | — | Fallback strategy names tried in order when the primary cannot reach a decision. |

**Merge strategies**

| Strategy | Behaviour | `Merge.Agent` required? |
|---|---|---|
| `union` | Concatenate all branch outputs in declaration order. | No |
| `consensus` | Pass through if all branches agree on their final statement; fall back to union on disagreement. | No |
| `vote` | Pick the output agreed by the most branches (majority); fall back to union on a tie. | No |
| `ranked` | Scoring agent receives all branch outputs and selects or synthesises the best result. | Yes |
| `semantic_diff` | Resolver agent identifies agreements, resolves conflicts, and produces a single reconciled output. | Yes |

For `ranked` and `semantic_diff`, the merge agent receives the branch outputs as context and returns its result as plain text. It does not need any special plugins.

**Parallel fan-out rules and constraints**

- `Targets` must be non-empty when `Parallel: true`.
- Every entry in `Targets` and the join state `To` must be declared states.
- `To` (join state) must be distinct from all `Targets` entries.
- Branch agents do not need the `Handoff` plugin and should not be instructed to emit signals.
- Evidence contracts (`Contract`/`Contracts`) are not evaluated on parallel transitions — add contracts to the transition that leaves the join state if post-merge evidence is needed.
- `RecoveryAgent` on a parallel transition is ignored.

**HandoffContext — targeted artifact injection on transition**

`HandoffContext` on a `TransitionConfig` injects a compact artifact block into shared history at the moment the transition fires. The receiving agent sees the block as the most recent history entry before its first turn.

```yaml
Implementation:
  Agent: Developer
  Transitions:
    - To: Testing
      Signal: "HANDOFF TO TESTER"
      Contract: ImplementationComplete
      HandoffContext:                   # inject targeted artifacts when transition fires
        - Source: session_context
        - Source: changes_recent
        - Source: brief_field:test_targets
```

**Supported source types:**

| Source | Description |
|--------|-------------|
| `session_context` | Handoff summary from `session_context_write` |
| `changes_recent[:N]` | Last N entries from `changes.json` |
| `brief_field:FIELD` | A named field from `brief.json` |
| `file:PATH` | Raw contents of an artifact file |

`own_history` is not supported in `HandoffContext` — it is only available in `AgentConfig.Context`.

**HandoffContext vs. Context spec:**

- `HandoffContext` injects content *into shared history*. Any agent in subsequent turns — including those without a `Context` spec — sees the injected block.
- `AgentConfig.Context` assembles context from disk artifacts at invocation time and does not touch shared history. The receiving agent sees only the declared artifact sources and its own prior turns.

**Recommended usage:** use `HandoffContext` on transitions when the receiving agent uses standard `ContextWindow` filtering; use `AgentConfig.Context` on the receiving agent when it should receive no cross-agent history at all. Both can be used together — `HandoffContext` on the transition provides a snapshot for routing/termination agents that read shared history, while the `Context` spec controls exactly what the model receives.

---

### graph

A declarative directed graph where each agent is bound to a named node and edges carry routing keywords. Forward edges (to nodes in later BFS layers) activate the target agent in the current phase. Back-edges (to nodes in equal or earlier BFS layers) break the current phase and restart execution from the target node. Loop-back paths — revision cycles, bug-fix loops, replanning triggers — are explicit in the topology rather than encoded as implicit keyword conventions.

```yaml
Selection:
  Type: graph
  Graph:
    EntryNode: planner
    Nodes:
      - Id: planner
        Agent: Planner
      - Id: developer
        Agent: Developer
      - Id: tester
        Agent: Tester
      - Id: reviewer
        Agent: Reviewer
      - Id: approved
        Agent: Reviewer
        Terminal: true
    Edges:
      - From: planner
        To: developer
        Keyword: "HANDOFF TO DEVELOPER"
        Validators:
          - RequireBrief
      - From: developer
        To: tester
        Keyword: "HANDOFF TO TESTER"
        Validators:
          - RequireWriteFile
          - RequireShellPass
      - From: developer
        To: planner
        Keyword: REPLAN REQUIRED
      - From: tester
        To: reviewer
        Keyword: "HANDOFF TO REVIEWER"
        Validators:
          - TestReportValid
      - From: tester
        To: developer
        Keyword: BUGS FOUND
      - From: reviewer
        To: approved
        Keyword: APPROVED
        Validators:
          - RequireReviewJudgement
      - From: reviewer
        To: developer
        Keyword: REVISION REQUIRED
```

**How it works**

1. **BFS layer assignment:** at startup, fuseraft computes a BFS layer for every node from the entry node following only forward edges. Edges are classified as *forward* (target layer > source layer) or *back-edges* (target layer ≤ source layer).
2. **Forward edges** activate the target agent in the current multi-agent phase via normal framework messaging.
3. **Back-edges** break the current phase. When a back-edge keyword is detected and all validators pass, the orchestrator terminates the active phase and restarts execution from the target node.
4. **Keyword detection** uses strict line matching: the keyword must appear **alone on its own line** with no trailing text (after stripping `*`/`_` markdown), or be emitted via the `Handoff` plugin (`handoff(route_keyword: "KEYWORD")`). This is stricter than keyword routing — a keyword at the start of a line followed by punctuation (e.g. `APPROVED: see notes`) does not match in graph mode. Only the current node's outgoing edges are checked — keywords that belong to other nodes are ignored.
5. **Terminal nodes** (`Terminal: true`) invoke the termination check before keyword detection. Back-edges on a terminal node are unreachable — if you need a terminal outcome with evidence gating, use a routing node whose forward edge points to a separate terminal node with validators on that edge.
6. **Unconditional edges** (no `Keyword`) fire automatically after the agent's turn without keyword scanning. Unconditional forward edges hand off immediately; unconditional back-edges break the phase immediately.

**Multi-target back-edges**

A single node may declare back-edges to different target nodes — the key differentiator from keyword routing's loop-back conventions. In the example below the `reviewer` node routes back to two different targets depending on which keyword fires:

```yaml
Nodes:
  - Id: reviewer
    Agent: Reviewer
  - Id: approved
    Agent: Reviewer
    Terminal: true
Edges:
  - From: reviewer
    To: approved
    Keyword: APPROVED
    Validators:
      - RequireReviewJudgement
  - From: reviewer
    To: developer
    Keyword: REVISION REQUIRED     # back-edge → developer
  - From: reviewer
    To: planner
    Keyword: REPLAN REQUIRED       # back-edge → planner (different target)
```

In keyword routing this pattern requires two separate loop-back routes and depends on keyword scanning order. In graph routing the topology is explicit: each edge has a distinct target.

**`GraphConfig` fields**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `EntryNode` | string | no | Node ID of the first node to execute. Defaults to the first node when omitted. |
| `Nodes` | array | yes | Node definitions. Each binds an agent role to a named position in the graph. |
| `Edges` | array | yes | Directed edges. Evaluated in declaration order — the first matching edge fires. |
| `MaxRetries` | int | `4` | Maximum consecutive correction attempts per node before a `ValidatorStuckException` is thrown. |

**`GraphNodeConfig` fields**

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `Id` | string | — | Unique node identifier. Referenced by `EntryNode` and by edges' `From`/`To` fields. |
| `Agent` | string | — | Agent name from the `Agents` list to invoke at this node. Multiple nodes may share the same agent. |
| `Terminal` | bool | `false` | When `true`, the session terminates after the agent executes once. Outgoing edges are not evaluated. |
| `Parallel` | bool | `false` | When `true`, the node participates in a parallel fan-out group — runs concurrently with other `Parallel` nodes sharing the same triggering keyword. |
| `Validators` | array | — | Validators that must all pass before a `Terminal` node ends the session. Ignored on non-terminal nodes. |

**`GraphEdgeConfig` fields**

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `From` | string | — | Source node ID. Must match a `GraphNodeConfig.Id`. |
| `To` | string | — | Target node ID. Must match a `GraphNodeConfig.Id`. Forward vs. back-edge classification is computed automatically from BFS layer topology. |
| `Keyword` | string | — | Routing keyword. Must appear alone on its own line. When omitted, the edge is *unconditional* — it fires after the agent's turn without keyword scanning. |
| `Validator` | string | — | Optional single validator. Blocks the edge until validation passes. |
| `Validators` | array | — | Optional multiple validators (AND semantics). Takes precedence over `Validator` when both are set. |
| `SourceAgents` | array | any | Optional. Edge only fires when the message author is in this list. |
| `RequiredCommandPattern` | string | — | Used with `RequireShellPass`. The passing command must contain at least one pipe-separated substring. |
| `ShellFallbackPattern` | string | — | Used with `RequireWriteFile`. A shell command matching this pattern is accepted in lieu of `write_file`. |
| `RequireHumanApproval` | bool | `false` | When `true`, the operator must explicitly approve (`y`) before this edge fires. If rejected, the source agent is re-invoked with a "route blocked" message. Applies to both forward edges and back-edges. |
| `RecoveryAgent` | string | — | Optional. Agent to invoke for one intervention turn when a validator has failed two or more consecutive times on this edge. The recovery agent receives a diagnostic message and may fix the blocking issue. Activates at most once per edge per session. |

---

### llm

An LLM call picks the next agent each turn based on the conversation history. Useful when routing logic is too complex to express as keywords, or when the handoff decision should be context-sensitive.

```yaml
Selection:
  Type: llm
  Model:
    ModelId: gpt-4o-mini
  Prompt: |
    You are an orchestrator. Given the agents: {{$agents}}
    And the conversation:
    {{$history}}
    Which agent should respond next? Reply with only the agent name.
```

| Field | Required | Description |
|-------|----------|-------------|
| `Model` | yes | The model used for the selection call. Can be a lightweight/fast model since the task is just picking a name. |
| `Prompt` | no | Custom prompt. Available placeholders: `{{$agents}}` (list of agent names and descriptions), `{{$history}}` (recent conversation). Defaults to a built-in prompt if omitted. |

---

### magentic

A two-level orchestration loop driven by a dedicated manager LLM. The manager gathers facts about the task, creates a plan, and then coordinates participant agents round by round — selecting the right agent each turn, detecting stalls, and replanning when progress stops. No routing keywords or JSON conditions are required; the manager reasons about the conversation history to decide what happens next.

```yaml
Selection:
  Type: magentic
  Magentic:
    Model:
      ModelId: gpt-4o
    MaxRoundCount: 20
    MaxStallCount: 3
    MaxResetCount: 2
    EnablePlanReview: false
```

**How it works**

1. **Orientation (outer loop — once):** before the first inner-loop round the manager reads the conversation history and calls `magentic_orientation` to produce a brief: a task summary, known facts, an initial plan, an immediate next step, and an initial completion check.
2. **Coordination (inner loop — each round):** the manager:
   - Calls `magentic_ledger_update` to assess progress: checks whether the task is complete, whether the team is stalling, and which facts have been established.
   - Selects the best participant for the next step via `magentic_select_speaker`.
   - Invokes the selected participant with a focused, concrete instruction.
   - Detects stalls: if `MaxStallCount` consecutive rounds make no forward progress, a replan is triggered. After `MaxResetCount` replans the session ends with a stall message.
   - Detects completion: if the ledger update sets `task_complete: true`, the session ends cleanly.

**Agent instructions**

Unlike keyword or structured strategies, Magentic participants do not need to emit special keywords or JSON objects — they just do the work. The manager reads their output and decides what happens next. Keep agent instructions focused on capabilities and behavior, not on routing signals.

**`Termination` is ignored**

For `Selection.Type: magentic`, the entire `Termination` section is ignored. Session end is controlled exclusively by `MaxRoundCount`, `MaxStallCount`, and `MaxResetCount` in the `Magentic` block. A `Termination` section may be present in the config (e.g. to satisfy tooling or document intent) but has no effect. `fuseraft validate` emits a warning if it finds a non-default `Termination` config alongside a Magentic selection.

**`MagenticManagerConfig` fields**

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `Model` | string or object | — | **Required.** Model for the manager LLM. A reasoning-capable model (`o3`, `claude-opus-4-6`, `gemini-2.5-pro`) is strongly recommended — the manager drives all planning and evaluation. |
| `Instructions` | string | built-in | Optional system instructions for the manager. A well-tested default prompt is used when omitted. |
| `MaxRoundCount` | int | `20` | Hard cap on inner-loop coordination rounds before the session terminates. |
| `MaxStallCount` | int | `3` | Consecutive rounds without forward progress before a replan is triggered. |
| `MaxResetCount` | int | `2` | Maximum number of replanning cycles. After this limit the session terminates with a stall message rather than looping indefinitely. |
| `EnablePlanReview` | bool | `false` | When `true`, the session pauses after the initial plan is generated and waits for HITL review before proceeding to the coordination loop. |

---

### adversarial

A GAN-inspired pipeline where generator agents produce artifacts and critic agents review them in fully isolated context windows. Each stage runs a tight generate → critique → revise loop before the approved artifact is passed to the next stage.

The defining property is the **context firewall**: the critic never sees the generator's reasoning chain, prior shared history, or any other session state. It receives only its own system instructions and the artifact under review. This produces independent feedback rather than rubber-stamping — the same mechanism that makes a fresh code reviewer more effective than an author self-reviewing their own work.

```yaml
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
```

**How it works**

1. **Stage loop:** stages execute sequentially. The approved artifact from each stage is appended to a shared history that subsequent generators receive as prior context. Critics never see this history.
2. **Initial generation:** the generator is invoked with its system instructions, any prior-stage artifacts, and the original task.
3. **Critique round:** the critic is invoked with a fresh context — only its system instructions and the artifact. No shared history, no generator reasoning.
4. **Pass check:** if the critic emits `PassKeyword` (default `APPROVED`) on its own line, the stage exits early and the artifact is promoted.
5. **Revision:** if the critic rejects and rounds remain, the generator is re-invoked with its previous artifact and the critique injected as a user message. A new critique round follows.
6. **Exhaustion:** if all `Rounds` are consumed without approval, the last reviewed artifact is promoted anyway (with a warning log and event) so the pipeline does not stall.

**Rounds semantics**

`Rounds` is the number of critique rounds per stage. With `Rounds: 3`:

- Round 1 → critique → revise (round 1 < 3)
- Round 2 → critique → revise (round 2 < 3)
- Round 3 → critique → **no revision** (final round — the last critiqued artifact is promoted)

The generator receives `Rounds - 1` revision opportunities. The promoted artifact is always the most recent one that was actually reviewed — not an unreviewed revision.

**Agent instructions**

Critic agents should be instructed to either approve or provide specific, actionable feedback — and nothing in between. The default template ships critics with instructions like:

```
If the artifact meets all requirements, respond with exactly:
APPROVED

Otherwise, list specific, actionable defects. Be precise — describe what is wrong and why.
```

Generator agents work as normal. On revision turns the orchestrator injects the critique as a user message so the generator knows exactly what to address.

**`AdversarialConfig` fields**

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `Stages` | array | — | **Required.** Ordered list of generator/critic stage pairs. |
| `Rounds` | int | `3` | Maximum critique rounds per stage. Generator gets `Rounds - 1` revision opportunities. Must be ≥ 1. |
| `PassKeyword` | string | `"APPROVED"` | Keyword the critic emits on its own line to approve the artifact and exit the stage early. Case-insensitive. |

**`AdversarialStageConfig` fields**

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `Generator` | string | — | **Required.** Name of the agent that produces and revises artifacts. Must match an agent name in `Agents`. |
| `Critic` | string | — | **Required.** Name of the agent that reviews with a fresh context window. Must match an agent name in `Agents`. Must be distinct from `Generator`. |
| `Label` | string | `"{Generator} → {Critic}"` | Human-readable label shown in event logs and message tags. |

---

## Termination strategies

Configured under `Termination.Type`. The run stops when any enabled strategy fires.

### regex

Stops when a message content matches a regular expression.

```yaml
Termination:
  Type: regex
  Pattern: \bAPPROVED\b
  AgentNames:
    - Reviewer
```

| Field | Required | Description |
|-------|----------|-------------|
| `Pattern` | yes | .NET regex pattern applied to message content. |
| `AgentNames` | no | If set, only messages from these agents are evaluated. Useful to restrict the termination check to a specific role (e.g. only the Reviewer can approve). |

**Common patterns**

| Pattern | Matches |
|---------|---------|
| `\bAPPROVED\b` | Word "APPROVED" (whole word) |
| `TASK COMPLETE` | Literal substring |
| `\b(DONE\|COMPLETE\|FINISHED)\b` | Any of three words |

### maxiterations

Stops after a fixed number of agent turns, regardless of content.

```yaml
Termination:
  Type: maxiterations
  MaxIterations: 20
```

All strategy types also respect `MaxIterations` as a hard safety cap. The `maxiterations` type only fires on that count; the others also respect it.

### composite

Stops when any child strategy fires. This is the recommended type for most configs because it combines a content-based check (the task is done) with a safety cap (prevent infinite loops).

```yaml
Termination:
  Type: composite
  MaxIterations: 40
  Strategies:
    - Type: regex
      Pattern: \bAPPROVED\b
      AgentNames:
        - Reviewer
    - Type: maxiterations
      MaxIterations: 40
```

Child strategies can themselves be composite.

---

## Choosing a strategy

### Sequential

Use sequential when the flow never changes: the same agents always run in the same order. Good for single-agent configs and simple two-agent pipelines where there is no branching and no conditional routing.

Avoid it once you need any of: loops, early exit, conditional next-agent, or evidence-gated handoffs. Sequential has no routing logic — it cycles unconditionally.

### State machine

Use state machine for **explicit, deterministic pipelines** where the routing topology is known in advance, hallucination-resistant routing matters, and evidence contracts should gate transitions. State machine is a strict upgrade from keyword routing for complex, multi-phase workflows.

Choose state machine over keyword when:

- You want agents to be unable to route to an unexpected state — the state machine ignores signals that don't belong to the current state's transitions
- You need evidence contracts on transitions (contracts are first-class in state machine; they require a workaround in keyword routing)
- You want lossless compaction — state machine position and contract evaluations are durable state that `ContextRebuilder` can reconstruct verbatim
- You want the `Verifier` meta-agent to audit automatically on suspicious transitions

State machine and keyword routing handle signals the same way internally (keyword on own line, or `handoff()` plugin call). Migrating an existing keyword config to state machine requires mapping routes to states and transitions.

### Keyword

Use keyword for **role-based pipelines** where each agent has a defined phase and the handoff is a deliberate signal ("I am done, next phase"). The keyword is noise-free, unambiguous, and easy to enforce — it either appears on its own line or it doesn't.

Keyword is the right default for development teams, review pipelines, and any workflow where:

- The routing decision is predetermined ("when the Developer finishes, always go to Tester")
- You need validators to gate handoffs with real evidence before the route fires
- You want `SourceAgents` to enforce role boundaries mechanically
- Loop-back paths exist (bugs found → developer, revision required → developer)
- Stuck detection and HITL escalation matter

The cost is that agents must be reliably instructed to emit the exact keyword. Models generally do this well — a keyword on its own line is a simpler constraint than producing valid structured JSON under pressure.

### Structured

Use structured when the **routing decision is a content decision** — when the agent's output itself carries the answer and routing is a consequence of what that answer says, not a side-channel signal added on top.

Structured routing fits naturally when:

- The agent already produces JSON for downstream use and the routing field is part of that output (e.g. a classifier returning `{"category": "billing"}`)
- The next agent depends on a computed value, not a predetermined phase (e.g. route to an escalation agent when `confidence < 0.7`, route to different specialists based on `entity_type`)
- Multiple conditions combine to determine the route (field A exists AND field B equals a value)
- The workflow is a decision tree or triage pipeline, not a sequential team of roles

**What structured routing trades away:** validators. Structured routes have no `Validator` field — there is no mechanism to block a route until evidence is present on disk. Enforcement must come entirely from agent instructions. For workflows where evidence gating matters (write a file, run a test, pass a build) keyword routing with validators is more reliable.

**The ambiguity risk:** if an agent produces multiple JSON-looking blocks in a single response (tool call results, echoed file contents, intermediate data), the strategy takes the first parseable object. Keyword routing has no equivalent ambiguity — a keyword on its own line is unambiguous regardless of what else appears in the response.

### LLM

Use LLM selection when the routing logic is too complex or context-dependent to express statically. The orchestrator calls a separate model each turn to decide which agent speaks next, based on the conversation history.

It is the most flexible strategy but also the least predictable and the most expensive — every agent turn incurs an additional LLM call. Use it when keyword or structured routing would require an unwieldy number of routes to cover all cases, or when the right next agent genuinely depends on nuanced conversation context that cannot be captured by a field value or keyword.

### Magentic

Use Magentic when you want a fully autonomous, self-directing team where the orchestrator — not the config — decides the plan and execution order. Rather than declaring routes or conditions, you describe the agents' capabilities and hand the task to the manager.

Magentic fits naturally when:

- The task is exploratory or open-ended and you cannot predict the right execution order in advance
- You want the manager to adapt automatically when agents get stuck, rather than requiring you to encode every failure path as a loop-back route
- Agents are specialists with distinct capabilities (researcher, developer, analyst) and the manager should choose the right one each round based on what has already been done
- You want built-in replanning: if three consecutive rounds make no progress, the manager rethinks the plan rather than repeating the same stuck agent

**What Magentic trades away:** the determinism and evidence-gating of keyword routing. There are no validators, no `SourceAgents` restrictions, no `RequireHumanApproval` on routes. The manager decides everything. If correctness gates matter (e.g. "the developer must actually run the build before handing off to the tester"), keyword routing with validators is more reliable.

Magentic is also more expensive than keyword routing per round: each inner-loop iteration makes at least two manager LLM calls (`magentic_ledger_update` + `magentic_select_speaker`) in addition to the participant's call.

### Adversarial

Use adversarial when **output quality matters more than throughput** and you want independent review rather than self-review. The context firewall is the key mechanism — because the critic receives no shared history, it cannot be influenced by the generator's framing or reasoning, and it approaches the artifact the way a real independent reviewer would.

Adversarial fits naturally when:

- You have a linear pipeline where each phase produces a discrete artifact (plan, code, document) that should be verified before the next phase begins
- You want to catch issues that single-pass generation consistently misses: phantom dependencies, incomplete error handling, architectural deviations, placeholder tests
- The quality of later stages depends heavily on the quality of earlier stages — adversarial stages enforce that dependency by only promoting approved artifacts
- You can afford the additional LLM calls (each stage uses at least 2 calls per round: one generate, one critique)

**What adversarial trades away:** dynamic routing and self-correction. The pipeline is fixed at config time — stages always execute in order. There are no loop-back edges between stages, no validator-gated handoffs, and no mechanism for a stage to signal that a prior stage needs to restart. Each stage is isolated. If a critic's feedback reveals a fundamental problem with a previous stage's artifact, the orchestrator has no mechanism to restart that stage — it promotes the best artifact the current stage produced and continues.

Adversarial is also not a substitute for running real tests. A critic LLM reviewing code is a heuristic check, not a compiler or test suite. Use it alongside `RequireShellPass` validators in a keyword or graph pipeline if you need evidence-gated progression.

### Graph

Use graph when you need **explicit back-edge topology** — when different failure modes should route back to different prior nodes, or when you want the routing structure to be visible in the config rather than implied by keyword conventions.

Graph fits naturally when:

- A single agent can route backward to **different** targets depending on the outcome (e.g. a Reviewer that sends minor issues back to the Developer but sends scope changes back to the Planner)
- You want loop-back paths to be unambiguous in the config, not inferred from keyword scan order
- The pipeline is a directed graph, not a strict linear sequence — phases fan out or converge in ways that are cleaner to express as nodes and edges than as a flat route table
- You still want validators on individual edges (graph edges support the full `Validators` / `RequiredCommandPattern` surface, the same as keyword routes)

Graph and keyword routing use the same `handoff()` plugin for typed signalling, but their text-scan rules differ: keyword routing uses relaxed matching (keyword at start of line followed by whitespace or punctuation also fires), while graph uses strict matching (keyword must be alone on its own line, no trailing text). Migrating an existing keyword config to graph requires mapping agents to node IDs and routes to edges. The main addition is the explicit `EntryNode` and the flat `Edges` list with `From`/`To` fields.

**What graph trades away:** lossless compaction and Verifier integration. For hallucination-resistant routing where agents cannot route themselves to an unexpected node, state machine remains the stronger choice.

---

## Choosing between keyword, state machine, structured, graph, and adversarial

| | Keyword | State machine | Structured | Graph | Adversarial |
|---|---|---|---|---|---|
| Handoff signal | Keyword on own line (relaxed) | Signal on own line (same as keyword) | JSON field value | Keyword alone on own line (strict) | PassKeyword from critic |
| Evidence gating | Validators (per-route) | Contracts (per-transition, typed) | Instructions only | Validators (per-edge) | None (critic LLM only) |
| Routing topology | All routes active at once | Only current state's transitions active | All routes active at once | Only current node's edges active | Fixed sequential stages |
| Ghost signals | Possible — any agent can emit any keyword | Impossible — wrong-state signals are ignored | N/A | Reduced — wrong-node keywords are ignored | N/A — critic approval is the only signal |
| Multi-target back-edges | Implicit (keyword scan order) | N/A (no back-edges) | N/A | Explicit — each back-edge has a distinct target node | No back-edges between stages |
| Critic context isolation | No | No | No | No | Yes — critics receive no shared history |
| Lossless compaction | No | Yes (requires EvidenceStore) | No | No | No |
| Verifier integration | No | Yes | No | No | No |
| Failure classification | Yes | Yes | No | Yes | No |
| Best for | Phased pipelines, dev teams | Same + hallucination-resistant routing | Classifiers, triage | Explicit multi-target loop-back topology | Quality gates on discrete artifacts |

For a human-like team of roles (Planner, Developer, Tester, Reviewer):
- Start with **keyword** if you want a simple, validator-gated pipeline quickly
- Move to **state machine** when you need hallucination-resistant routing, contracts, lossless compaction, or the Verifier meta-agent
- Choose **graph** when different failure outcomes must route back to different nodes and you want that topology explicit in the config

For a pipeline where an agent computes a value and routing follows from it, prefer **structured**. The JSON is already the output — the routing field costs nothing to add.

For a linear pipeline where each phase produces a discrete artifact (plan, code, document) and you want independent review between phases, prefer **adversarial**. The context firewall is the key mechanism — critics approach the artifact with no inherited assumptions from the generator.

---

## Designing agent handoff flows

The combination of keyword routing, validators, and source-agent restrictions lets you build deterministic pipelines where agents can only advance when they have real evidence, and role boundaries are enforced mechanically.

```
User task
    ↓
Planner  ──HANDOFF TO DEVELOPER [RequireBrief]──→  Developer
         ←──────────────REPLAN REQUIRED────────────────────────────────────┐
                                                       │                   │
                                          (RequireWriteFile + RequireShellPass)
                                                       │                   │
                                                    Tester  ←──BUGS FOUND──┐
                                                       │                   │
                                          (TestReportValid validator)      │
                                                       │                   │
                                                   Reviewer ──REVISION REQUIRED──┘
                                                       │
                                        (RequireShellPass + RequireReviewJudgement)
                                                       │
                                                   APPROVED → session ends
```

Each arrow is a keyword route. Guards in parentheses are validators that block the route until evidence is present. `SourceAgents` restrictions enforce role boundaries — for example, Developer cannot emit `BUGS FOUND` (only the Tester can), and the Tester cannot emit `REVISION REQUIRED` (only the Reviewer can).

**Stuck detection** is built in: if an agent produces no valid keyword — or a keyword that belongs to a different role — for 3 consecutive turns, a `ValidatorStuckException` is raised and the session stops with a descriptive error. The same counter covers validator failures, missing keywords, and ambiguous multi-keyword responses; the counters do not reset each other, so alternating failure modes are caught at the same threshold.
