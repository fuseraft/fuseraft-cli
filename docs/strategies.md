---
title: "Flows"
description: "A team's Flow decides who works next. Steps draw the flow yourself, with gated handoffs; patterns give you built-in control logic for dynamic coordination and parallel work."
---

A team's `flow` decides who works next. It takes one of two forms:

- **Steps**: you draw the flow. Each agent lists the edges it may take, and gates on each edge block a handoff until the evidence exists. Use this for any pipeline you can sketch on a whiteboard.
- **Patterns**: built-in control logic for shapes a fixed diagram can't express: a manager that decides who works each round (`magentic`), fan-out over a list of items (`mapreduce`), parallel experts (`scattergather`), and generate-and-critique loops (`adversarial`).

Field references for both are in [Configuration → Flow](configuration.md#flow).

---

## Steps

```yaml
flow:
  start: Planner
  steps:
    Planner:
      - to: Developer
        requires: [brief]
    Developer:
      - to: Tester
        requires:
          - filesWritten: brief.files_to_change
          - commandSucceeded: brief.verify_command
      - to: Planner
        when: the plan needs rethinking
    Tester:
      - to: Reviewer
        when: every criterion passes
        requires:
          - testReport: testResults
        onStuck: Developer
      - to: Developer
        when: a criterion fails
    Reviewer:
      - to: end
        when: the work is approved
        requires: [reviewJudgement]
      - to: Developer
        when: the code needs fixing
      - to: Planner
        when: the plan is fundamentally wrong
```

```
Planner ──[brief]──▶ Developer ──[files written, verify passed]──▶ Tester ──[report]──▶ Reviewer ──[judgement]──▶ end
   ▲                   │  ▲                                          │                     │
   └───── replan ──────┘  └──────────── a criterion fails ───────────┘                     │
   ▲                      ▲                                                                │
   └──────────────────────┴──────────────── needs fixing / replan ─────────────────────────┘
```

### How a run moves

1. The `start` agent runs first, with the task.
2. When it finishes its work, it calls `handoff` with one of its step's routes. The tool only accepts those routes, each described by its edge's `when`.
3. fuseraft runs the gates on that edge. If they all pass, the target agent runs next and receives the `goal`, `background` and `constraints` the agent passed to `handoff`.
4. If a gate fails, the same agent runs again with a correction naming what's missing.
5. If an agent finishes a turn without handing off, it runs again with a reminder listing its routes.
6. An edge to `end` finishes the run as soon as its gates pass.

Agents never pick a route that doesn't exist, and never skip a gate: the routing, not the model, decides what happens next. fuseraft writes the routes and their gates into each agent's prompt, so instructions only need to describe the work.

### Loops and escalation

Back-edges, such as `Tester → Developer` or `Reviewer → Planner`, are ordinary edges. Two fields keep loops honest:

- **`onStuck`** on an edge calls in a named agent after its gates fail twice in a row. A Tester that can't produce a passing report because the code is broken gets the Developer, not another retry.
- **`maxRevisits`** on a back-edge caps how often it may fire. After that, the earlier agent is told how many times it has received the same critique and shown the outstanding objections, taken from the first artifact the reviewing agent `writes`. After a further two escalations the run stops.

```yaml
PlannerCritic:
  - to: Developer
    when: the brief is approved
    requires: [brief]
  - to: Planner
    when: the brief has blocking problems
    maxRevisits: 3
```

### Human approval

`approve: true` on an edge puts a person in the loop. Once the edge's gates pass, fuseraft asks before the handoff goes through; a refusal sends the work back to the agent with a note to keep working. Use it on the edge to `end`, or before a step that publishes or deploys, instead of approving every turn with `--hitl`.

```yaml
Reviewer:
  - to: end
    requires: [reviewJudgement]
    approve: true
```

### Parallel branches

```yaml
Architect:
  - to: [Backend, Frontend, Migrations]
    join: Integrator
    merge: { strategy: union }
```

Each branch agent runs one turn at the same time, with its own snapshot of the history. Their outputs are merged, then the `join` agent runs. Branch agents don't need their own `steps` entry.

### When an agent gets stuck

Gate failures are classified and escalate per kind (missing evidence, conflicting evidence, no progress) after a threshold, 3 by default and 2 for conflicting evidence. The run stops with a stuck error rather than looping forever. See [Gates → When an agent gets stuck](validators.md#when-an-agent-gets-stuck) and [Failure handling](configuration.md#failure-handling).

### Designing a step flow

- **Name the artifacts first.** Ask what each agent must leave behind for the next one, declare it under `artifacts` with a `shape`, and gate the handoff on it.
- **Gate on evidence, not on claims.** `commandSucceeded` and `testReport` check what actually ran. "Tests pass" in a reply is not a gate.
- **Give every loop a way out.** Use `onStuck` where a different agent can fix the problem, and `maxRevisits` where two agents can disagree forever.
- **Keep agents on `sees: handoff`** unless one genuinely needs the discussion. Artifacts carry the facts; the handoff carries the instructions.

---

## Patterns

Patterns end on their own. `limits` still applies.

### magentic

A manager model plans the work, then coordinates the team round by round: it picks who works next, gives them a concrete instruction, detects stalls, and replans when progress stops. Agents just do the work; they never call `handoff`.

```yaml
flow:
  pattern: magentic
  model: reasoning
  maxRoundCount: 20
  maxStallCount: 3
  maxResetCount: 2
  enablePlanReview: false
```

1. **Orientation, once:** the manager summarizes the task, the known facts, an initial plan and the first step.
2. **Each round:** the manager updates its progress ledger (is the task done? is the team stalling?), selects the best participant, and invokes it with a focused instruction.
3. **Stalls:** after `maxStallCount` rounds without progress the manager replans; after `maxResetCount` replans the run ends with a stall message.
4. **Completion:** the run ends when the ledger marks the task complete, or after `maxRoundCount` rounds.

| Field | Default | Description |
|-------|---------|-------------|
| `model` | — | Required. The manager's model; a reasoning-capable one is strongly recommended. |
| `instructions` | built-in | Custom manager instructions. |
| `maxRoundCount` | `20` | Cap on coordination rounds. |
| `maxStallCount` | `3` | Rounds without progress before a replan. |
| `maxResetCount` | `2` | Replans before the run ends. |
| `enablePlanReview` | `false` | Pause after the initial plan for human review. |

Every agent sees the shared conversation, since the manager's loop depends on it.

### mapreduce

A splitter breaks the input into items, a mapper processes each item in parallel, and a reducer combines the results.

```yaml
flow:
  pattern: mapreduce
  splitter: Splitter
  mapper: Mapper
  reducer: Reducer
  itemsJsonPath: items
  maxConcurrency: 4
  maxSplitterRetries: 3
```

1. **Split:** the splitter's reply must contain a JSON object with an array at `itemsJsonPath` (dot paths allowed), e.g. `{"items": ["a", "b"]}`. It is retried up to `maxSplitterRetries` times.
2. **Map:** the mapper runs once per item, with the item as its task and no history shared between items. `maxConcurrency` caps parallel runs; `0` means unlimited.
3. **Reduce:** the reducer gets every mapper output and writes the final answer.

| Field | Default | Description |
|-------|---------|-------------|
| `splitter`, `mapper`, `reducer` | — | Required agent names. |
| `itemsJsonPath` | `items` | Path to the array in the splitter's JSON. |
| `maxConcurrency` | `0` | Parallel mapper cap; `0` is unlimited. |
| `maxSplitterRetries` | `3` | Split attempts before the run fails. |

### scattergather

Every participant gets the same task in parallel, each with its own context; a synthesizer combines their independent answers.

```yaml
flow:
  pattern: scattergather
  participants: [LegalReviewer, TechnicalReviewer, BusinessReviewer]
  synthesizer: LeadReviewer
  maxConcurrency: 0
```

Use it for multi-expert review, ensembles (several solutions, one picks or reconciles), diversity sampling and redundancy checks. The synthesizer receives each output labeled `[Participant: Name]`.

| | scattergather | mapreduce | parallel Steps edge |
|---|---|---|---|
| Each agent receives | the same task | one item | the same handoff |
| Agents | different per participant | one mapper for all items | different per branch |
| Combined by | the synthesizer | the reducer | the `join` agent |

### adversarial

Generators produce artifacts; critics review them in a fully isolated context, seeing only their instructions and the artifact, never the generator's reasoning. Each stage loops generate, critique, revise, then hands its approved artifact to the next stage.

```yaml
flow:
  pattern: adversarial
  rounds: 3
  passKeyword: APPROVED
  stages:
    - { generator: Planner,   critic: PlanReviewer, label: Planning }
    - { generator: Developer, critic: CodeReviewer, label: Implementation }
```

1. The generator produces an artifact from the task and any earlier stages' artifacts.
2. The critic reviews it with a fresh context. Writing `passKeyword` on its own line approves it and ends the stage.
3. Otherwise the generator revises with the critique, up to `Rounds - 1` times. If no round approves, the last reviewed artifact moves on, with a warning event, so the pipeline never stalls.

Instruct critics to either write the pass keyword or list specific, actionable defects, and nothing in between.

| Field | Default | Description |
|-------|---------|-------------|
| `stages` | — | Required. Ordered `generator`/`critic` pairs, each with an optional `label`. |
| `rounds` | `3` | Critique rounds per stage. |
| `passKeyword` | `APPROVED` | What a critic writes on its own line to approve. |

---

## Choosing a flow

| You want | Use |
|----------|-----|
| A pipeline you can draw, with evidence at each handoff | Steps |
| One agent doing a task | Steps with a single `- to: end` |
| A model to decide who works next, round by round | `magentic` |
| The same work done over many items | `mapreduce` |
| Several independent opinions on one question | `scattergather` |
| Drafts that must survive an independent critic | `adversarial`, or Steps with a critic agent and `maxRevisits` |
