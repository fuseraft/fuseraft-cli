# fuseraft

<img src="docs/.assets/fuseraft-banner.png" alt="fuseraft — an agent orchestration framework">

fuseraft turns a YAML config into a running multi-agent pipeline. Define a team of AI agents — each with its own role, model, skills, and tools — and describe how they hand off to each other. Then give them a task.

Build a software development team that plans, writes, tests, and reviews its own code. A research pipeline that fans out to specialists and synthesizes their findings. A decision workflow with a human approval gate at every critical step. Whatever you can describe, fuseraft can coordinate.

Works with Anthropic, xAI, OpenAI, Azure OpenAI, Ollama, and any OpenAI-compatible provider. Agents can be local or remote — the [A2A protocol](https://a2a-protocol.org/) lets you federate agent slots to independently deployed services. Built on [Microsoft Agent Framework](https://github.com/microsoft/agents).

---

## What you can build

Pipelines range from a single task-routed assistant:

```mermaid
flowchart LR
    Task((Task)) --> Assistant[Assistant]
```

...to multi-agent workflows with conditional keyword routing and anti-hallucination validators enforced at every handoff:

```mermaid
flowchart TD
    Task((Task))
    Planner[Planner]
    Developer[Developer]
    Tester[Tester]
    Reviewer[Reviewer]
    Done(["✓ Done"])

    Task --> Planner
    Planner      -->|"HANDOFF TO DEVELOPER · RequireBrief"| Developer
    Developer    -->|"HANDOFF TO TESTER · RequireWriteFile · RequireShellPass"| Tester
    Tester       -->|"HANDOFF TO REVIEWER · TestReportValid"| Reviewer
    Reviewer     -->|"APPROVED"| Done
    Reviewer     -->|"REVISION REQUIRED"| Developer
    Reviewer     -->|"REPLAN REQUIRED"| Planner
    Tester       -->|"BUGS FOUND"| Developer
```

...to declarative directed-graph pipelines where back-edges express review cycles without duplicating states:

```mermaid
flowchart TD
    Planner([Planner])
    Developer([Developer])
    Tester([Tester])
    Reviewer([Reviewer])
    Terminal(["Reviewer\n✓ terminal"])

    Planner   -->|"HANDOFF TO DEVELOPER · RequireBrief"| Developer
    Developer -->|"HANDOFF TO TESTER · RequireWriteFile"| Tester
    Tester    -->|"HANDOFF TO REVIEWER · TestReportValid"| Reviewer
    Reviewer  -->|"APPROVED · RequireReviewJudgement"| Terminal
    Reviewer  -->|"REPLAN REQUIRED"| Planner
    Tester    -->|"BUGS FOUND"| Developer
    Reviewer  -->|"REVISION REQUIRED"| Developer
```

...to parallel fan-out/fan-in where a coordinator spawns concurrent workers that merge into a single downstream node:

```mermaid
flowchart TD
    Coordinator([Coordinator])
    AnalyzerA(["Analyzer A\nparallel"])
    AnalyzerB(["Analyzer B\nparallel"])
    Synthesizer(["Synthesizer\n✓ terminal"])

    Coordinator -->|"BEGIN PARALLEL ANALYSIS"| AnalyzerA
    Coordinator -->|"BEGIN PARALLEL ANALYSIS"| AnalyzerB
    AnalyzerA   -->|"ANALYSIS COMPLETE"| Synthesizer
    AnalyzerB   -->|"ANALYSIS COMPLETE"| Synthesizer
```

...to fully autonomous [Magentic](https://arxiv.org/abs/2411.04468) orchestration where a Manager dynamically selects agents and collects their reports:

```mermaid
flowchart LR
    Task((Task))
    Manager([Manager])
    Researcher[Researcher]
    Developer[Developer]

    Task       --> Manager
    Manager    -->|"selects"| Researcher
    Manager    -->|"selects"| Developer
    Researcher -.->|"reports"| Manager
    Developer  -.->|"reports"| Manager
```

...to adversarial pipelines where generator agents produce artifacts and critic agents review them with fresh, isolated context windows — no shared history, no inherited blind spots:

```mermaid
flowchart TD
    Task((Task))
    Planner["Planner\ngenerator"]
    PlanReviewer["PlanReviewer\ncritic · isolated context"]
    Developer["Developer\ngenerator"]
    CodeReviewer["CodeReviewer\ncritic · isolated context"]
    Done(["✓ Done"])

    Task         --> Planner
    Planner      -->|artifact| PlanReviewer
    PlanReviewer -->|"APPROVED"| Developer
    PlanReviewer -.->|revise| Planner
    Developer    -->|artifact| CodeReviewer
    CodeReviewer -->|"APPROVED"| Done
    CodeReviewer -.->|revise| Developer
```

---

## Quick start

```bash
# Open an interactive REPL session — no config needed
fuseraft

# Interactive wizard — describe your use case and get a config back
fuseraft init

# Or start from a built-in template
fuseraft init --template dev-team --model claude-sonnet-4-6
fuseraft init --template graph          # directed-graph pipeline with parallel fan-out
fuseraft init --template adversarial    # GAN-style generate → critique → revise pipeline
fuseraft init --template designer       # AI-assisted config designer

# Run a session
fuseraft run -c .fuseraft/config/orchestration.yaml "Build a REST API in Go with JWT authentication"

# Anchor all agents to a spec file as the authoritative source of truth
fuseraft run --spec spec.md
fuseraft run -c .fuseraft/config/orchestration.yaml --spec spec.md "Implement the specification"

# Resume the most recent incomplete session
fuseraft run --resume

# Validate a config — add --diagram for a Mermaid flowchart preview
fuseraft validate .fuseraft/config/orchestration.yaml --diagram
```

---

## Install

Prebuilt binaries are self-contained — no .NET installation required.

**Linux / macOS**

```bash
curl -fsSL https://raw.githubusercontent.com/fuseraft/fuseraft-cli/main/install.sh | bash
```

Add `--system` to install to `/usr/local/bin` instead of `~/.local/bin`:

```bash
curl -fsSL https://raw.githubusercontent.com/fuseraft/fuseraft-cli/main/install.sh | bash -s -- --system
```

**Windows (PowerShell)**

```powershell
irm https://raw.githubusercontent.com/fuseraft/fuseraft-cli/main/install.ps1 | iex
```

Both scripts download the latest release from [GitHub Releases](https://github.com/fuseraft/fuseraft-cli/releases), place the binary on your `PATH`, and confirm with a `fuseraft --version` on completion.

**Manual download**

Grab the archive for your platform from [Releases](https://github.com/fuseraft/fuseraft-cli/releases), extract the binary, and place it on your `PATH`.

**Updates**

Once installed, keep fuseraft current with:

```bash
fuseraft update          # download and install the latest release
fuseraft update --check  # check without installing
```

On Windows, `fuseraft update` launches a separate `fuseraft-update.exe` process (included in the release archive) that waits for running fuseraft instances to exit before replacing the binary. On Linux and macOS the replacement is atomic and happens in place.

**Build from source**

Requires the [.NET 10 SDK](https://dot.net):

```bash
./build.sh          # Linux / macOS
.\build.ps1         # Windows
```

The binary lands in `./bin/`.

---

## Features

**Orchestration**
- Nine routing modes: sequential, round-robin, keyword, structured (JSON-field routing), state machine, declarative directed graph (with parallel fan-out/fan-in), LLM-based selection, fully autonomous Magentic, and adversarial generate→critique→revise pipelines
- Routing validators that block handoffs unless real evidence is present on disk — no hallucinated progress
- `HandoffContext` on state machine transitions — inject targeted artifact snapshots into shared history at the moment a transition fires, so the receiving agent sees only what it needs
- Saga orchestration wraps any pipeline with compensating rollback if a step fails

**Agents**
- Declare agents inline or as standalone `AgentFile` YAML — reuse and version agent definitions across configs
- Mix any combination of LLM providers within a single pipeline
- Federate agent slots to remote services via the [A2A protocol](https://a2a-protocol.org/) — remote agents participate identically to local ones

**Tools**
- Built-in plugins: filesystem, shell, git, HTTP, JSON, search, Docker sandboxes, MCP servers, persistent scratchpad, and a shared chatroom
- Connect any MCP server — its tools are automatically registered and available to agents

**Reliability**
- Checkpoints after every turn — sessions can always be resumed exactly where they left off
- Token tracking per turn; enforce per-model context caps and a session-wide hard spending limit
- Conversation compaction keeps long sessions within context window limits
- Per-agent **`Context` spec** — declare exactly which artifact sources (files, brief fields, recent changes, own history) each agent receives instead of filtering the shared transcript. When set, history replay is skipped entirely; context cost is proportional to what you declare, not session length

**Governance**
- Per-agent execution rings, prompt injection detection, circuit breaker, and a hash-chain audit log
- Sandbox file and shell access to a configured directory tree
- Human-in-the-loop support at any point in a pipeline

**Developer experience**
- Browser-based DevUI (`--devui`) for real-time session visualization
- Interactive **Orchestration Designer** (`fuseraft init --template designer`) — describe your use case, get a validated config back
- VS Code extension with CodeLens, IntelliSense, and a session viewer

---

## Documentation

| Doc | What it covers |
|-----|----------------|
| [Getting Started](docs/getting-started.md) | Prerequisites, build, first run |
| [CLI Reference](docs/cli-reference.md) | All commands and flags |
| [Configuration](docs/configuration.md) | Full config schema (YAML and JSON) |
| [Models & Providers](docs/models.md) | Model configuration and provider auto-detection |
| [Plugins](docs/plugins.md) | All built-in tools agents can call |
| [Strategies](docs/strategies.md) | Selection and termination strategies |
| [Routing Validators](docs/validators.md) | Anti-hallucination handoff guards |
| [Harness Engineering](docs/harness-engineering.md) | Designing configs that enforce real progress mechanically |
| [MCP Integration](docs/mcp.md) | Connecting external MCP servers |
| [Security & Sandbox](docs/security.md) | File and network containment |
| [Governance](docs/governance.md) | Execution rings, audit log, circuit breaker, SLO tracking |
| [Context Store](docs/context-store.md) | Importing files and directories into the session context |
| [Sessions](docs/sessions.md) | Resumption, HITL, cost tracking, compaction |
| [Examples](docs/examples.md) | Ready-to-use config examples |
| [Design](docs/design.md) | Architecture, layer map, MAF usage, and decision log |

---

## VS Code Extension

The [fuseraft VS Code extension](https://github.com/fuseraft/fuseraft-vscode) brings the full CLI experience into your editor.

**Activity bar panel** — four persistent views:
- **Run Task** — compose a task, pick a config, set flags (`--hitl`, `--tools`, `--verbose`, `--devui`), and launch. Each task opens in its own named terminal; multiple tasks can run simultaneously.
- **Sessions** — lists sessions scoped to your workspace with status, age, and task preview. Click to resume; preview icon opens a formatted transcript with per-turn token usage and cost.
- **Configs** — auto-discovers every fuseraft config in your workspace. Click to open, or hit **+** to run the Initialize Config wizard.
- **Context** — manages reference material agents can access during sessions. Import files or folders; they're stored in `.fuseraft/context/` and available to any session in the workspace.

**CodeLens on config files** — three inline actions appear above the first line of any config:

```
▶ Run Task   ✓ Validate   ⎇ Diagram
```

**Task files** — right-click any `.md` or `.txt` file in the explorer or editor to run it directly as a fuseraft task. Write your task as a markdown spec, then run it without copying anything.

**REPL** — `fuseraft: Open REPL` starts an interactive single-agent chat session without a config file. Good for quick experiments.

**Set Up Provider** — a guided first-run panel for configuring your binary path, provider, model, endpoint, and API key. Runs automatically when the binary isn't found; available any time from the command palette.

**YAML / JSON IntelliSense** — full JSON Schema for fuseraft configs ships with the extension. Autocomplete, inline docs, and validation for every field — agents, models, plugins, routes, contracts, security, and more.

**Status bar** — a persistent `fuseraft` button always visible at the bottom of the editor. Click to run a task.

---

## Contributing

Contributions are welcome — bug fixes, new plugins, config examples, documentation, and new ideas. See [CONTRIBUTING.md](CONTRIBUTING.md) to get started.

---

## License

MIT
