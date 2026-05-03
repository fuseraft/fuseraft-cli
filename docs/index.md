# fuseraft-cli Documentation

fuseraft-cli is a multi-agent orchestration CLI built on [Microsoft Agent Framework](https://github.com/microsoft/agents) and [Microsoft.Extensions.AI](https://github.com/dotnet/extensions). You define teams of AI agents in a YAML config — each agent has a system prompt, a model, and a set of plugins — and the orchestrator drives them through a conversation until the task is done.

This is a personal project and a work in progress, suited for experimentation and automating well-defined tasks.

## What it does

- Runs any number of agents in a coordinated loop driven by keyword routing, LLM-based selection, or fully autonomous Magentic orchestration
- Gives each agent access to tools: filesystem, shell, git, HTTP, JSON, search, Docker sandboxes, MCP servers
- Saves a checkpoint after every turn so sessions can always be resumed
- Tracks token usage and estimated cost; can enforce a hard spending cap
- Enforces correctness with routing validators that block handoffs unless evidence is present
- Sandboxes agent file and shell access to a configured directory tree
- Applies per-agent execution rings, prompt injection detection, and a hash-chain audit log via the Agent Governance Toolkit
- Supports mixing any combination of LLM providers per agent

## Guides

| Doc | What it covers |
|-----|---------------|
| [Getting Started](getting-started.md) | Prerequisites, installation, first run |
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
| [Sessions](sessions.md) | Resumption, HITL, cost tracking, compaction |
| [Context Store](context.md) | Importing reference material for agents |
| [Examples](examples.md) | Ready-to-use config examples |
