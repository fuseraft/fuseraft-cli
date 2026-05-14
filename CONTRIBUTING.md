# Contributing to fuseraft CLI

fuseraft-cli is actively maintained and in production use. Contributions are welcome and encouraged — whether you're fixing a bug, adding a plugin, improving documentation, or sharing a config example from your own workflows.

If you have questions before diving in, open an issue or start a discussion. The maintainers are responsive.

---

## Table of Contents

1. [Prerequisites](#prerequisites)
2. [Getting oriented](#getting-oriented)
3. [Build](#build)
4. [Tests](#tests)
5. [Good first contributions](#good-first-contributions)
6. [Contribution workflow](#contribution-workflow)
7. [Types of contributions](#types-of-contributions)
   - [Bug fixes](#bug-fixes)
   - [Plugins](#plugins)
   - [Orchestration strategies and validators](#orchestration-strategies-and-validators)
   - [Config examples](#config-examples)
   - [Documentation](#documentation)
8. [Code conventions](#code-conventions)
9. [Commit messages](#commit-messages)
10. [Invariants you must not break](#invariants-you-must-not-break)
11. [Reporting bugs](#reporting-bugs)
12. [License](#license)

---

## Prerequisites

- [.NET 10 SDK](https://dot.net)
- An API key for at least one supported LLM provider (Anthropic, OpenAI, Azure OpenAI, xAI, or Ollama) — needed only for manual end-to-end testing; the automated test suite requires no live keys

---

## Getting oriented

Before making changes, read:

- **[`AGENTS.md`](AGENTS.md)** — repository conventions, key abstractions, invariants, and where to look for things. This is the fastest way to build a working mental model.
- **[`docs/design.md`](docs/design.md)** — architecture and design decisions in depth.
- **[`docs/configuration.md`](docs/configuration.md)** — the config schema; useful if you're touching anything config-driven.

The directory layout:

```
src/
  Cli/            Commands, DevUI, OrchestratorBuilder, SessionRunner
  Core/           Interfaces, Models, Exceptions
  Infrastructure/ AgentFactory, ChatClientFactory, Plugins, MCP
  Orchestration/  Orchestrators, strategies, validators, contracts, compaction, Saga

tests/
  FuseraftCli.Tests/   xUnit tests — one file per class under test (~323 tests, ~1s)

config/
  examples/       Runnable YAML/JSON examples; kept in sync with the schema

docs/             User-facing documentation
```

---

## Build

```bash
./build.sh            # Linux / macOS — compile, test, publish to artifacts/
.\build.ps1           # Windows

# Specific targets
./build.sh --target=Build    # compile only
./build.sh --target=Test     # run tests only
./build.sh --target=Lint     # dotnet format check
./build.sh --target=Pack     # produce a versioned zip archive
```

Or run directly with the .NET CLI:

```bash
dotnet build
dotnet test
```

The built binary lands at `bin/fuseraft` (Linux/macOS) or `bin\fuseraft.exe` (Windows).

---

## Tests

```bash
./build.sh --target=Test
# or
dotnet test
```

Tests live in `tests/FuseraftCli.Tests/`. All tests must pass before opening a PR — the CI gate enforces this.

**Testing conventions:**

- One test file per class: `FooTests.cs` tests `Foo.cs`
- No live LLM calls — use fake agents and fake validators only
- No mocks of `ILogger` — pass `NullLogger<T>.Instance`
- Use `Assert.Contains(substring, actual)` with the shortest unique substring of an error message, not the full string — this survives minor wording changes
- Build history manually as `List<ChatMessage>` with `FunctionCallContent` / `FunctionResultContent` pairs for tool-call scenarios

Add or update tests for any behavior you change.

---

## Good first contributions

If you're looking for a place to start:

- **`docs/`** — clarifications, corrections, examples, or filling in gaps in existing pages
- **`config/examples/`** — a runnable config that demonstrates a pattern not already covered
- **`src/Infrastructure/Plugins/`** — a new plugin wrapping a tool or API you use
- **Open issues labeled `good first issue`** on GitHub

---

## Contribution workflow

1. **Fork** the repository and create a branch from `main`
2. **Read `AGENTS.md`** if you haven't — it will save you time
3. **Make your changes** and add or update tests
4. Run `./build.sh --target=Test` to confirm everything passes
5. Run `./build.sh --target=Lint` (or `dotnet format`) to fix formatting
6. **Open a pull request** against `main` with a clear description of what changed and why

For anything non-trivial — a new strategy, a new orchestrator, a meaningful architecture change — **open an issue first** so the direction can be agreed before you invest significant time.

PRs should be focused. A PR that does one thing is easier to review and faster to merge than one that does several. If you find yourself fixing unrelated issues while working on something, split them out.

---

## Types of contributions

### Bug fixes

Include a test that demonstrates the bug. If that's not feasible, explain why in the PR description.

### Plugins

Plugins live in `src/Infrastructure/Plugins/`. A plugin is a plain C# class with methods annotated with `[Description(...)]` — the SDK reflects on these to generate the tool schema.

Steps to add a plugin:

1. Create `src/Infrastructure/Plugins/MyPlugin.cs`
2. Register it in `PluginRegistry.RegisterDefaults()` with a unique name
3. Add it to the plugin table in `docs/plugins.md`
4. Add tests in `tests/FuseraftCli.Tests/`

Look at `ScratchpadPlugin.cs` or `ChatroomPlugin.cs` for reference implementations.

**Keep `[Description(...)]` attributes short.** Descriptions are embedded in the tool schema sent to every agent on every turn. Verbose descriptions inflate context windows and increase token cost for all users.

**Any plugin that writes files, runs shell commands, or commits to git must be wrapped by `ChangeTracker`.** See [the ChangeTracker invariant](#invariants-you-must-not-break) below.

### Orchestration strategies and validators

These are the most load-bearing parts of the system. Before touching them:

- Read the [execution order invariant](#invariants-you-must-not-break) — it must not change
- Read the [validator invariants](#invariants-you-must-not-break) — validators must be deterministic, side-effect-free, and idempotent
- Read `docs/design.md` sections 6–9

When adding a new `FailureAction` or `FailureType` value, update all of:
- `FailureHandlingConfig.cs` (enum + default config + `GetConfig` switch)
- `FailureClassifier.cs` (classification logic)
- Both strategy `HandleXxxFailure` methods
- Any `config/examples/` files that declare `FailureHandling`
- `docs/configuration.md` (actions table)

### Config examples

Examples in `config/examples/` are the primary reference for users and are checked by tests. If you add one:

- Make it runnable end-to-end (not just illustrative)
- Keep it in sync with the current schema
- Add a row to `docs/examples.md` describing what it demonstrates

### Documentation

`docs/` is user-facing. Keep it accurate and practical. If something in the docs no longer matches the code, fix the docs in the same PR that changes the code.

---

## Code conventions

- Standard C# style; the project uses `dotnet format` (run via `--target=Lint`)
- No comments that restate what the code does — only add one when the *why* is non-obvious: a hidden constraint, a workaround for a specific bug, a subtle invariant
- One-line `<summary>` tags are enough; no multi-paragraph docstrings
- Match the surrounding file's style for anything you touch
- No backwards-compatibility shims for removed code — if something is unused, delete it

---

## Commit messages

Follow the conventional commits style used in this repo:

```
<type>: <short imperative summary>
```

Types: `feat`, `fix`, `chore`, `docs`, `refactor`, `test`

Examples:

```
feat: add DockerSandboxPlugin with isolated shell execution
fix: keyword match no longer triggers on embedded occurrences
docs: add graph orchestration walkthrough to design.md
test: cover ValidatorStuckException escalation path
```

Keep the subject line under 72 characters. Use the body (separated by a blank line) for anything that needs more context — the *why*, not the *what*.

---

## Invariants you must not break

These invariants are load-bearing. Violating them causes silent misbehavior that is difficult to debug.

**Execution order** — for every agent turn, control layers run in this fixed sequence:

1. Selection (`IAgentSelector.SelectAsync`)
2. Validation — validators gate the route; failure injects a correction and re-invokes the source agent
3. Failure handling — policy applies if validation fails
4. Termination — evaluated only after a successful turn completes
5. Iteration cap — unconditional hard stop

Termination must never be evaluated before validators. Routing must never happen without running validators.

**Validator invariants** — all validators must be:

- **Deterministic** — same inputs, same result, always
- **Side-effect free** — must not mutate disk, history, or any external system
- **Idempotent** — safe to call multiple times in the same turn

Validators must not call LLMs or external services.

**ChangeTracker invariant** — all tools that modify external state (files, shell, git) must be wrapped by `ChangeTracker`. A tool that bypasses `ChangeTracker` silently breaks validators — they will see no evidence of the tool's actions and will block routes that should pass.

**Shared history invariant** — never strip or reorder messages in physical history outside of the compaction path. Doing so breaks routing, stale-signal detection, and turn-boundary markers.

---

## Reporting bugs

Open a GitHub issue with:

- The command you ran or the config you used (sanitize any secrets)
- What you expected to happen
- What actually happened, including any error output
- Your OS, .NET SDK version (`dotnet --version`), and LLM provider

---

## License

By contributing you agree that your changes will be licensed under the project's [MIT license](LICENSE.md).
