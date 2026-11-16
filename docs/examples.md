---
title: "Examples"
description: "Ready-to-run team files for common structures, each validated by the test suite."
---

Ready-to-run team files for common structures. The complete files live in [`config/examples/`](https://github.com/fuseraft/fuseraft-cli/tree/main/config/examples); the test suite loads every one of them, so they always match the current format. Copy one into `.fuseraft/config/` and edit it, or start from a template with `fuseraft init`.

```bash
fuseraft run --config config/examples/orchestration.yaml "Add pagination to the users API"
```

---

## Software development team

**File:** `config/examples/orchestration.yaml`

Planner scopes the work, Developer implements it, Tester verifies it, Reviewer approves it. Every handoff is gated on evidence on disk.

- The brief and the test report are artifacts with shapes, so each agent is told exactly what to write and a malformed file is caught at the handoff.
- The Developer can't hand off until the brief's `verify_command` ran and passed.
- The Tester can't hand off until the report has no failures and every PASS names a command that actually ran; after two failed attempts the Developer is called in.
- A Verifier audits claims against the change log every five turns.

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
          - commandSucceeded: brief.verify_command
            fallback: build|compile|test|check
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
        when: every criterion is verified
      - to: Developer
        when: the code or tests need fixing
      - to: Planner
        when: the plan is fundamentally wrong
```

`config/examples/dev-team-structured.yaml` is the same team with a stricter Tester and a Reviewer that writes a structured JSON verdict. `config/examples/open-webui.yaml` runs it against an organization's Open WebUI instance.

---

## Brownfield pipeline

**File:** `config/examples/brownfield.yaml`

For large existing codebases. An Archaeologist surveys the code once, writing a discovery brief and a convention profile; every later agent reads those instead of re-exploring. Writes outside the discovered scope are blocked, and the Developer can't hand off until the tests related to its changes pass.

```yaml
    Developer:
      - to: Tester
        requires:
          - filesWritten: brief.files_to_change
          - commandSucceeded: brief.verify_command
          - relatedTestsPass
```

See [Brownfield mode](configuration.md#brownfield-mode).

---

## Single agent

The smallest useful team: one agent that finishes when the task is done.

```yaml
team: Solo
agents:
  Assistant:
    tools: [FileSystem, Shell]
    instructions: |
      You are a careful assistant. Break the task into steps and complete them.
      If an action fails, try a different approach instead of repeating it.
flow:
  start: Assistant
  steps:
    Assistant:
      - to: end
limits:
  maxTurns: 20
```

---

## Research team

**File:** `config/examples/research-team.yaml`

A Researcher gathers sources and writes structured findings to disk; a Writer turns them into a report. The Writer can't start until the findings exist.

---

## Review loop with shaped artifacts

**File:** `config/examples/article-review-pipeline.yaml`

A Writer drafts, an Editor approves or sends it back, a Publisher saves the result. The draft and the review are artifacts with shapes, so a handoff is blocked until each file parses into the expected fields, and the Editor's verdict can only be `approved` or `revision_needed`.

```yaml
artifacts:
  draft:
    path: .fuseraft/artifacts/draft.json
    shape: { title: string, content: string, word_count: int }
  review:
    path: .fuseraft/artifacts/review.json
    shape: { verdict: approved|revision_needed, feedback: string }

flow:
  start: Writer
  steps:
    Writer:
      - to: Editor
        requires: [draft]
    Editor:
      - to: Publisher
        when: the verdict is approved
        requires: [review]
      - to: Writer
        when: the verdict is revision_needed
        requires: [review]
    Publisher:
      - to: end
        requires:
          - fileExists: output/article.md
```

---

## Local models (Ollama)

Two agents running entirely on local models. No API keys required.

```yaml
team: LocalTeam
models:
  planner: { modelId: "llama3.2", maxTokens: 4096 }
  coder:   { modelId: "qwen2.5-coder:7b", maxTokens: 8192 }
agents:
  Planner:
    model: planner
    instructions: Analyse the task and write a clear plan with numbered steps.
  Developer:
    model: coder
    tools: [FileSystem, Shell]
    instructions: Follow the plan from the Planner and implement it with the tools available.
flow:
  start: Planner
  steps:
    Planner:
      - to: Developer
    Developer:
      - to: end
limits:
  maxTurns: 20
```

The plan travels in the Planner's `handoff` goal. For more structure, declare a plan artifact and gate on it.

---

## Mixed providers

Different agents on different providers: one model for planning, another for implementation, a local one for review.

```yaml
team: MixedProviderTeam
models:
  planner:  { modelId: gpt-4o, apiKeyEnvVar: OPENAI_API_KEY }
  builder:  { modelId: claude-sonnet-4-6, apiKeyEnvVar: ANTHROPIC_API_KEY }
  reviewer: { modelId: "llama3.1:70b" }
artifacts:
  brief:
    path: .fuseraft/artifacts/brief.json
    shape: { goal: string, files_to_change: [string], verify_command: string }
agents:
  Planner:  { model: planner,  tools: [FileSystem, Search], writes: [brief], instructions: Scope the task and write the brief. }
  Developer: { model: builder, tools: [FileSystem, Shell, Git], reads: [brief], instructions: Implement the brief and run its verify_command. }
  Reviewer: { model: reviewer, tools: [FileSystem, Changes], reads: [brief], instructions: Review the change against the brief. }
flow:
  start: Planner
  steps:
    Planner:
      - to: Developer
        requires: [brief]
    Developer:
      - to: Reviewer
        requires:
          - commandSucceeded: brief.verify_command
    Reviewer:
      - to: end
        when: the change is correct
      - to: Developer
        when: the change needs fixing
```

See [Models](models.md) for providers and endpoints.

---

## Magentic team

**File:** `config/examples/magentic-team.yaml`

A manager model plans the work and picks who works each round, replanning when progress stalls. Agents just do the work; there are no routes to declare.

```yaml
flow:
  pattern: magentic
  model: manager
  maxRoundCount: 20
  maxStallCount: 3
  maxResetCount: 2
```

See [Flows → magentic](strategies.md#magentic).

---

## With MCP tools

An MCP server's tools, listed under `tools` by the server's name:

```yaml
team: WebAgent
mcpServers:
  - name: Browser
    transport: stdio
    command: npx
    args: ["-y", "@modelcontextprotocol/server-puppeteer"]
agents:
  WebScraper:
    tools: [Browser, FileSystem, Json]
    instructions: Navigate the pages in the task, extract the data, and save it to files.
flow:
  start: WebScraper
  steps:
    WebScraper:
      - to: end
limits:
  maxTurns: 15
```

`config/examples/playwright-mcp.yaml` drives a browser through the Playwright MCP server, and `config/examples/production-mcp-http.yaml` connects to an authenticated remote MCP server over HTTP. See [MCP](mcp.md).

---

## With a sandbox and a cost cap

Safe for untrusted tasks: file access restricted to one project, HTTP limited to known hosts, and a hard token cap.

```yaml
limits:
  maxTurns: 30
  maxTokens: 50000
security:
  fileSystemSandboxPath: /home/user/projects/sandboxed-project
  httpAllowedHosts: [api.github.com, pypi.org]
```

---

## With compaction for long tasks

For tasks that run many turns, such as large refactors or migrations. `hybrid` mode combines a deterministic reconstruction from the evidence graph with an LLM narrative.

```yaml
limits:
  maxTurns: 100
advanced:
  compaction:
    triggerTurnCount: 40
    keepRecentTurns: 10
    mode: hybrid
```

| Mode | When to use |
|------|-------------|
| `lossless` | Steps flows. No LLM call; the current step and gate status are rebuilt from disk. |
| `hybrid` | Steps flows that also benefit from a narrative of what happened. |
| `llm` | Any flow. An LLM summarizes the compacted turns. |

---

## Remote agent (A2A)

Two local agents work with a reviewer hosted as an independent A2A service. A remote agent hands off by writing a route name on its own line; fuseraft tells it which ones it has.

```yaml
team: FederatedReview
agents:
  Planner:
    model: { modelId: gpt-4o-mini }
    instructions: Break the task into clear steps for the Developer.
  Developer:
    model: { modelId: gpt-4o-mini }
    tools: [FileSystem, Shell, Git]
    instructions: Implement what the Planner specifies.
  Reviewer:
    instructions: Assess correctness, security and style. List specific changes if any are needed.
    remoteAgent:
      url: https://reviewer.internal
      timeoutSeconds: 90
flow:
  start: Planner
  steps:
    Planner:
      - to: Developer
    Developer:
      - to: Reviewer
    Reviewer:
      - to: end
        when: the work is acceptable
      - to: Developer
        when: changes are needed
```

The remote side can be another fuseraft team served with `fuseraft serve --a2a`. See [`remoteAgent`](configuration.md#remoteagent).

---

## Scatter-gather: multi-expert review

Three reviewers assess the same document independently and in parallel; a lead combines their findings.

```yaml
team: ExpertReview
agents:
  LegalReviewer:     { tools: [FileSystem], instructions: Review the document for legal risk. }
  TechnicalReviewer: { tools: [FileSystem], instructions: Review the document for technical accuracy. }
  BusinessReviewer:  { tools: [FileSystem], instructions: Review the document for business impact. }
  LeadReviewer:      { instructions: "Combine the three reviews into one verdict, noting disagreements." }
flow:
  pattern: scattergather
  participants: [LegalReviewer, TechnicalReviewer, BusinessReviewer]
  synthesizer: LeadReviewer
```

---

## Map-reduce: parallel document analysis

A splitter lists the documents, a mapper analyses each in parallel, a reducer writes the summary.

```yaml
team: DocAnalysis
agents:
  Splitter:
    tools: [FileSystem]
    instructions: 'List the documents to analyse. Reply with only {"items": ["path1", "path2"]}.'
  Mapper:
    tools: [FileSystem, Document]
    instructions: Analyse the one document named in your task and report its key findings.
  Reducer:
    instructions: Combine the findings into one summary with the most important points first.
flow:
  pattern: mapreduce
  splitter: Splitter
  mapper: Mapper
  reducer: Reducer
  maxConcurrency: 4
```

---

## Event-driven ETL pipeline

**File:** `config/examples/etl-pipeline.yaml`

An Extractor validates raw input and a Transformer writes the output plus a machine-checkable test report, for runs started by a script, cron or webhook. `advanced.output.json` prints one JSON summary per run, and `--ci` exits non-zero on any FAIL in the report. See [Scripting & Automation](scripting.md).

---

## Cross-system flow analysis

**File:** `config/examples/cross-system-flow-analyzer.yaml`

Seven specialists trace data across code, SQL, APIs and documents, then synthesize and diagram the result, ending with an output validator. A long linear flow on two model aliases (`heavy`, `scout`) with a context budget and intent-based compaction.

---

## DevOps team

**File:** `config/examples/devops-team.yaml`

Architect plans, Engineer implements and validates with real shell commands, Operator deploys or sends the work back.

---

## Team designer

**File:** `config/examples/fuseraft-designer.yaml`

A single agent that designs a team file for your use case, writes it and runs `fuseraft validate` until it passes.

```bash
fuseraft run -c config/examples/fuseraft-designer.yaml "a team that triages GitHub issues and drafts fixes"
```
