---
template: home.html
hide:
  - navigation
  - toc
---

<div class="fuseraft-section" markdown>

## What it does

Define teams of AI agents in YAML. fuseraft-cli drives them through a coordinated pipeline — from planning to implementation to review — until the task is done.
{: .fuseraft-section-lead }

<div class="grid cards" markdown>

-   :material-robot-outline:{ .lg .middle } **Agent teams as YAML**

    ---

    Define each agent's name, model, system prompt, and plugins in a single YAML config. The coordinator routes work between them automatically.

    [:octicons-arrow-right-24: Configuration](configuration.md)

-   :material-swap-horizontal:{ .lg .middle } **Model-agnostic**

    ---

    Mix frontier LLMs and local SLMs per agent in the same team — Anthropic, OpenAI, Google, Mistral, xAI, DeepSeek, Azure OpenAI, or any model served through Ollama. Rotate API keys automatically on rate limits.

    [:octicons-arrow-right-24: Models & Providers](models.md)

-   :material-toolbox-outline:{ .lg .middle } **Rich plugin ecosystem**

    ---

    Every agent can call filesystem, shell, git, HTTP, JSON, search, and Docker sandbox tools out of the box. Connect any external MCP server.

    [:octicons-arrow-right-24: Plugins](plugins.md)

-   :material-content-save-outline:{ .lg .middle } **Resilient sessions**

    ---

    Sessions checkpoint after every turn. Interrupt anytime and resume exactly where you left off — no work is lost.

    [:octicons-arrow-right-24: Sessions](sessions.md)

-   :material-file-document-check-outline:{ .lg .middle } **Spec-driven development**

    ---

    Use `--spec` to anchor the team to an agreed specification before implementation begins. Routing validators block handoffs until evidence is present.

    [:octicons-arrow-right-24: Spec-Driven Development](spec-driven.md)

-   :material-shield-check-outline:{ .lg .middle } **Governance & cost control**

    ---

    Track token usage and estimated cost per turn. Enforce hard spending caps. Apply execution rings, prompt injection detection, and a hash-chain audit log.

    [:octicons-arrow-right-24: Governance](governance.md)

</div>
</div>

---

## Quick start

=== "Linux / macOS"

    ```bash
    curl -fsSL https://raw.githubusercontent.com/fuseraft/fuseraft-cli/main/install.sh | bash
    ```

    Then run the setup wizard on first launch:

    ```
    fuseraft
    ```

    ```
    No configuration found at ~/.fuseraft/config

    Provider setup
    Configure your provider and API key, then pick a model.

    Provider URL  (http://localhost:11434): https://api.anthropic.com/v1
    API Key       (leave blank for Ollama): ••••••••

    Model  (2 available from https://api.anthropic.com/v1)
    > claude-sonnet-4-6
      claude-opus-4-6

    >
    ```

=== "Windows"

    ```powershell
    irm https://raw.githubusercontent.com/fuseraft/fuseraft-cli/main/install.ps1 | iex
    ```

    Then run the setup wizard on first launch:

    ```
    fuseraft
    ```

Generate a team config and run your first task:

```bash
fuseraft init
fuseraft run -c .fuseraft/config/orchestration.yaml "Add a hello-world endpoint to this project"
```

[:octicons-arrow-right-24: Full installation guide](getting-started.md)

---

## Documentation

| Doc | What it covers |
|-----|----------------|
| [Getting Started](getting-started.md) | Prerequisites, installation, first run |
| [Writing Effective Tasks](writing-tasks.md) | Task descriptions that produce correct, verifiable results |
| [Spec-Driven Development](spec-driven.md) | Using `--spec` to anchor agents before implementation begins |
| [CLI Reference](cli-reference.md) | All commands and flags |
| [Configuration](configuration.md) | Full config schema (YAML and JSON) |
| [Models & Providers](models.md) | Model configuration and auto-detection |
| [Plugins](plugins.md) | All built-in tools agents can call |
| [Strategies](strategies.md) | Selection and termination strategies |
| [Routing Validators](validators.md) | Anti-hallucination handoff guards |
| [Harness Engineering](harness-engineering.md) | Building configs that enforce correctness mechanically |
| [MCP Integration](mcp.md) | Connecting external MCP servers |
| [Security & Sandbox](security.md) | File and network containment |
| [Governance](governance.md) | Execution rings, audit log, circuit breaker, SLO tracking |
| [Evals](evals.md) | Running agent teams against scored test cases; CI integration |
| [Sessions](sessions.md) | Resumption, HITL, cost tracking, compaction |
| [Context Management](context-management.md) | How fuseraft manages context across a long session |
| [Context Store](context-store.md) | Importing reference material for agents |
| [Skills](skills.md) | Portable skill packages and cross-session skill index |
| [Examples](examples.md) | Ready-to-use config examples |

---

## VS Code Extension

The [Fuseraft VS Code extension](https://github.com/fuseraft/fuseraft-vscode) brings the CLI into your editor — run tasks, browse sessions, validate configs, and get YAML/JSON IntelliSense, all from the activity bar.
