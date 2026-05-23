# Getting Started

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10)
- An API key for at least one supported LLM provider (see [Models & Providers](models.md))
- Docker Desktop (only required for the `CodeExecution` plugin)
- Git (only required for the `Git` plugin)

## Build

```bash
git clone <repo-url>
cd fuseraft-cli
./build.sh          # Linux/macOS
.\build.ps1         # Windows
```

The default target compiles, tests, and publishes a self-contained single-file binary to `bin/fuseraft` (Linux/macOS) or `bin\fuseraft.exe` (Windows).

Other targets:

```bash
./build.sh --target=Build                        # compile only
./build.sh --target=Test                         # compile + run tests
./build.sh --target=Pack --runtime=linux-x64     # single-file archive
./build.sh --target=Lint                         # format check
./build.sh --configuration=Debug --target=Build  # debug build
```

## Set your API key

### Option A — user config (recommended)

`fuseraft` (or `fuseraft repl`) detects first-time usage and walks you through a short setup wizard before starting the session. It asks for a model ID, provider URL, and API key, then stores them in `~/.fuseraft/config` (without the key) and your OS keychain (for the key):

```
$ fuseraft
No configuration found at ~/.fuseraft/config

Provider setup
Configure your default model and API key.

Model ID      [claude-sonnet-4-6]:
Provider URL  [https://api.anthropic.com/v1]:
API Key:      ••••••••

>
```

The config is saved after the first successful reply. Once saved, subsequent `fuseraft` invocations start immediately using those defaults. Use `/provider setup` inside the REPL to change settings at any time.

The API key is stored in the OS keychain — never in the config file on disk:

| Platform | Store |
|----------|-------|
| Linux | GNOME Keyring (`secret-tool` / libsecret) |
| macOS | Keychain (`security` CLI) |
| Windows | Credential Manager (Win32 API, works in Git Bash) |
| Fallback | `~/.fuseraft/.key` (plain file, mode 600) if no keychain is available |

See [Security — API key storage](security.md#api-key-storage) for details.

### Option B — environment variable

Export the key for your provider before running:

```bash
export ANTHROPIC_API_KEY=<your-key>
# or OPENAI_API_KEY, XAI_API_KEY, GOOGLE_AI_API_KEY, MISTRAL_API_KEY, DEEPSEEK_API_KEY
```

For other providers see [Models & Providers](models.md).

### Option C — VS Code extension

The [fuseraft VS Code extension](https://github.com/fuseraft/fuseraft-vscode) stores your API key in VS Code's built-in secure storage (backed by the OS credential store on each platform). When the extension launches a terminal or runs a command, it automatically injects the key as `FUSERAFT_API_KEY` and passes `--vscode` to the CLI. The CLI then reads the key from that environment variable instead of the OS keychain.

You do not need to set anything manually — configure your provider once via **fuseraft: Set Up Provider** in the VS Code command palette and the key is available to all fuseraft commands run through the extension.

## Run your first session

### Option A — generate a config with `init`

The fastest way to get started is `fuseraft init`. It walks you through a short wizard and writes a ready-to-run YAML config:

```bash
./bin/fuseraft init
```

You'll be prompted to pick a team template, confirm a model (auto-detected from your API keys), confirm a provider URL (defaults to the endpoint saved in `~/.fuseraft/config`), and choose an output path. Then:

```bash
./bin/fuseraft run -c .fuseraft/config/orchestration.yaml "Add a hello-world endpoint to this project"
```

For non-interactive or CI use:

```bash
./bin/fuseraft init --template minimal --no-interactive
./bin/fuseraft run -c .fuseraft/config/orchestration.yaml "Your task here"
```

### Option B — copy an example config

```bash
cp config/examples/orchestration.yaml .fuseraft/config/orchestration.yaml
./bin/fuseraft run -c .fuseraft/config/orchestration.yaml "Add a hello-world endpoint to this project"
```

---

If no task is given you are prompted interactively:

```bash
./bin/fuseraft run -c .fuseraft/config/orchestration.yaml
```

The orchestrator loads the config, prints a summary of the team, and streams agent responses as they arrive.

## Start a REPL session

For quick questions or single-model chat, run fuseraft with no subcommand:

```bash
fuseraft
```

No config file needed. The REPL auto-detects your provider from the API key stored in `~/.fuseraft/config` (or runs the setup wizard on first use). Type a message and press Enter. Use `/help` inside the session to see available commands.

Every session is auto-saved after each turn. Resume a previous session at any time:

```bash
# List resumable sessions from inside the REPL
/sessions

# Resume by ID (shown in the header at startup)
fuseraft repl --resume a87569bcd7b0
```

---

## Understand the output

Each agent turn is prefixed with its name:

```
[Planner] Reading the task…
[Developer] Writing the implementation…
[Tester] Running tests…
[Reviewer] APPROVED
```

Token counts and estimated cost appear after each turn in `--verbose` mode, and in the transcript written by `--output`.

## Resume an interrupted session

Sessions are checkpointed after every turn. If a run is interrupted (`Ctrl+C`, network error, etc.) resume with:

```bash
./bin/fuseraft run --resume
```

You are shown a list of incomplete sessions; select one and the run picks up exactly where it left off. See [Sessions](sessions.md) for more detail.

## Validate your config

Before running an unfamiliar config:

```bash
./bin/fuseraft validate .fuseraft/config/orchestration.yaml
```

This checks field types, agent names, strategy references, and plugin names without making any API calls.

## Keep up to date

If you installed a prebuilt binary, keep it current with:

```bash
fuseraft update          # download and install the latest release
fuseraft update --check  # check for a newer release without installing
```

On Linux and macOS the binary is replaced atomically in place. On Windows a separate `fuseraft-update.exe` process (bundled in the release archive) handles the swap after all fuseraft instances exit. See [CLI Reference — fuseraft update](cli-reference.md#fuseraft-update) for full details.

## Next steps

- Edit `.fuseraft/config/orchestration.yaml` to change agent instructions, models, or plugins
- Read [Writing Effective Tasks](writing-tasks.md) to learn how to write task descriptions that produce correct, verifiable results
- Read [Configuration](configuration.md) for the complete schema reference
- Read [Plugins](plugins.md) for every tool agents can call
- Read [Examples](examples.md) for ready-to-use team configs
