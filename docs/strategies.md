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
3. **Strict matching** — a text keyword matches only when it appears **alone on its own line** (after stripping markdown formatting characters `*` and `_`). A keyword embedded in a sentence or used as a prose section header (e.g. `BUGS FOUND: 3 failures`) does not match. This prevents accidental routing when agents reference another role's keyword in their output.
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

**Signal detection rules** are the same as keyword routing: the signal must appear alone on its own line (after stripping `*`/`_` markdown). Agents may also use the `Handoff` plugin (`handoff(route_keyword: "SIGNAL")`) for typed, unambiguous signalling.

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
| `To` | string | — | Name of the target state. Must exist in `States`. |
| `Signal` | string | — | Signal the current agent must emit to trigger this transition. When omitted, the transition fires automatically (no signal required) — useful for unconditional handoffs. |
| `Contract` | string | — | Single named contract that must pass. Referenced by name from `Orchestration.Contracts`. |
| `Contracts` | array | — | Multiple named contracts (AND semantics — all must pass). Use instead of or together with `Contract`. |
| `SourceAgents` | array | any | Optional. Restrict this transition to messages authored by agents in this list. |

**Contracts on transitions**

Transition contracts are evaluated in the same order as `Contracts` (then `Contract`). All must pass for the transition to fire. Contract definitions live in `Orchestration.Contracts`. See [Evidence contracts](configuration.md#evidence-contracts).

```yaml
Orchestration:
  Contracts:
    - Name: ImplementationComplete
      Requires:
        - FilesWritten:
            Source: .fuseraft/brief.json
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

---

### graph

A declarative directed graph where each agent is bound to a named node and edges carry routing keywords. Forward edges (to nodes in later BFS layers) activate the target agent in the current phase. Back-edges (to nodes in equal or earlier BFS layers) break the current phase and restart execution from the target node. Loop-back paths — revision cycles, bug-fix loops, replanning triggers — are explicit in the topology rather than encoded as implicit keyword conventions.

```yaml
Selection:
  Type: graph
  Graph:
    Entry: planner
    Nodes:
      - Id: planner
        Agent: Planner
        Edges:
          - To: developer
            Keyword: "HANDOFF TO DEVELOPER"
            Validators:
              - RequireBrief

      - Id: developer
        Agent: Developer
        Edges:
          - To: tester
            Keyword: "HANDOFF TO TESTER"
            Validators:
              - RequireWriteFile
              - RequireShellPass
          - To: planner
            Keyword: REPLAN REQUIRED

      - Id: tester
        Agent: Tester
        Edges:
          - To: reviewer
            Keyword: "HANDOFF TO REVIEWER"
            Validators:
              - TestReportValid
          - To: developer
            Keyword: BUGS FOUND

      - Id: reviewer
        Agent: Reviewer
        Edges:
          - To: approved
            Keyword: APPROVED
            Validators:
              - RequireReviewJudgement
          - To: developer
            Keyword: REVISION REQUIRED

      - Id: approved
        Agent: Reviewer
        Terminal: true
```

**How it works**

1. **BFS layer assignment:** at startup, fuseraft computes a BFS layer for every node from the entry node following only forward edges. Edges are classified as *forward* (target layer > source layer) or *back-edges* (target layer ≤ source layer).
2. **Forward edges** activate the target agent in the current multi-agent phase via normal framework messaging.
3. **Back-edges** break the current phase. When a back-edge keyword is detected and all validators pass, the orchestrator terminates the active phase and restarts execution from the target node.
4. **Keyword detection** uses the same rules as keyword routing: the keyword must appear alone on its own line (after stripping `*`/`_` markdown), or be emitted via the `Handoff` plugin (`handoff(route_keyword: "KEYWORD")`). Only the current node's outgoing edges are checked — keywords that belong to other nodes are ignored.
5. **Terminal nodes** (`Terminal: true`) invoke the termination check before keyword detection. Back-edges on a terminal node are unreachable — if you need a terminal outcome with evidence gating, use a routing node whose forward edge points to a separate terminal node with validators on that edge.
6. **Unconditional edges** (no `Keyword`) fire automatically after the agent's turn without keyword scanning. Unconditional forward edges hand off immediately; unconditional back-edges break the phase immediately.

**Multi-target back-edges**

A single node may declare back-edges to different target nodes — the key differentiator from keyword routing's loop-back conventions. In the example below the `reviewer` node routes back to two different targets depending on which keyword fires:

```yaml
- Id: reviewer
  Agent: Reviewer
  Edges:
    - To: approved
      Keyword: APPROVED
      Validators:
        - RequireReviewJudgement
    - To: developer
      Keyword: REVISION REQUIRED     # back-edge → developer
    - To: planner
      Keyword: REPLAN REQUIRED       # back-edge → planner (different target)

- Id: approved
  Agent: Reviewer
  Terminal: true
```

In keyword routing this pattern requires two separate loop-back routes and depends on keyword scanning order. In graph routing the topology is explicit: each edge has a distinct target.

**`GraphConfig` fields**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `Entry` | string | yes | Node ID of the first node to execute. |
| `Nodes` | array | yes | Ordered list of `GraphNodeConfig`. At least one node required. |

**`GraphNodeConfig` fields**

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `Id` | string | — | Unique node identifier. Referenced by edges' `To` field and by `Entry`. |
| `Agent` | string | — | Agent name from the `Agents` list to invoke at this node. Multiple nodes may share the same agent. |
| `Terminal` | bool | `false` | When `true`, the termination check fires before keyword detection. No outgoing edges are evaluated after termination fires. |
| `Edges` | array | `[]` | Outgoing edges from this node. Empty means the agent runs until the `Termination` strategy fires. |

**`GraphEdgeConfig` fields**

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `To` | string | — | Target node ID. Must exist in `Graph.Nodes`. Forward vs. back-edge classification is computed automatically from BFS layer topology. |
| `Keyword` | string | — | Routing keyword. Must appear alone on its own line. When omitted, the edge is *unconditional* — it fires after the agent's turn without keyword scanning. |
| `Validator` | string | — | Optional single validator. Blocks the edge until validation passes. |
| `Validators` | array | — | Optional multiple validators (AND semantics). |
| `SourceAgents` | array | any | Optional. Edge only fires when the message author is in this list. |
| `RequiredCommandPattern` | string | — | Used with `RequireShellPass`. The passing command must contain at least one pipe-separated substring. |
| `ShellFallbackPattern` | string | — | Fallback command pattern if `RequiredCommandPattern` fails. |
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

### Graph

Use graph when you need **explicit back-edge topology** — when different failure modes should route back to different prior nodes, or when you want the routing structure to be visible in the config rather than implied by keyword conventions.

Graph fits naturally when:

- A single agent can route backward to **different** targets depending on the outcome (e.g. a Reviewer that sends minor issues back to the Developer but sends scope changes back to the Planner)
- You want loop-back paths to be unambiguous in the config, not inferred from keyword scan order
- The pipeline is a directed graph, not a strict linear sequence — phases fan out or converge in ways that are cleaner to express as nodes and edges than as a flat route table
- You still want validators on individual edges (graph edges support the full `Validators` / `RequiredCommandPattern` surface, the same as keyword routes)

Graph and keyword routing use the same signal mechanism (keyword on own line, or `handoff()` plugin). Migrating an existing keyword config to graph requires mapping agents to node IDs and routes to edges. The main addition is the explicit `Entry` node and the `Id`/`To` structure on each edge.

**What graph trades away:** lossless compaction and Verifier integration. For hallucination-resistant routing where agents cannot route themselves to an unexpected node, state machine remains the stronger choice.

---

## Choosing between keyword, state machine, structured, and graph

| | Keyword | State machine | Structured | Graph |
|---|---|---|---|---|
| Handoff signal | Keyword on own line | Signal on own line (same matching) | JSON field value | Keyword on own line (same matching) |
| Evidence gating | Validators (per-route) | Contracts (per-transition, typed) | Instructions only | Validators (per-edge) |
| Routing topology | All routes active at once | Only current state's transitions active | All routes active at once | Only current node's edges active |
| Ghost signals | Possible — any agent can emit any keyword | Impossible — wrong-state signals are ignored | N/A | Reduced — wrong-node keywords are ignored |
| Multi-target back-edges | Implicit (keyword scan order) | N/A (no back-edges) | N/A | Explicit — each back-edge has a distinct target node |
| Lossless compaction | No | Yes (requires EvidenceStore) | No | No |
| Verifier integration | No | Yes | No | No |
| Failure classification | Yes | Yes | No | Yes |
| Best for | Phased pipelines, dev teams | Same + hallucination-resistant routing | Classifiers, triage | Explicit multi-target loop-back topology |

For a human-like team of roles (Planner, Developer, Tester, Reviewer):
- Start with **keyword** if you want a simple, validator-gated pipeline quickly
- Move to **state machine** when you need hallucination-resistant routing, contracts, lossless compaction, or the Verifier meta-agent
- Choose **graph** when different failure outcomes must route back to different nodes and you want that topology explicit in the config

For a pipeline where an agent computes a value and routing follows from it, prefer **structured**. The JSON is already the output — the routing field costs nothing to add.

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
