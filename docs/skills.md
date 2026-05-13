# Skills

Skills are portable packages of instructions, scripts, and resources that give agents specialized capabilities and domain knowledge. They follow the [Agent Skills open specification](https://agentskills.io) and work across any compatible agent runtime — including fuseraft, Claude Code, GitHub Copilot, Cursor, and others.

---

## Directory structure

A skill is a directory named after the skill, containing a required `SKILL.md` and optional resource subdirectories:

```
my-skill/
├── SKILL.md          # Required: frontmatter + instructions
├── scripts/          # Optional: executable code agents can run
├── references/       # Optional: documentation loaded on demand
├── assets/           # Optional: templates, static resources
└── ...               # Any additional files or directories
```

The skill directory name must match the `name` field in `SKILL.md`.

---

## `SKILL.md` format

`SKILL.md` is a Markdown file with YAML frontmatter:

```markdown
---
name: my-skill
description: What this skill does and when to use it.
---

# Instructions

Step-by-step guidance for the agent…
```

### Frontmatter fields

| Field | Required | Constraints |
|-------|----------|-------------|
| `name` | Yes | 1–64 characters. Lowercase letters, numbers, and hyphens only. No leading/trailing hyphens, no consecutive hyphens (`--`). Must match the parent directory name. |
| `description` | Yes | 1–1024 characters. Describes what the skill does and when to use it. Include keywords that help agents identify relevant tasks. |
| `license` | No | License name or reference to a bundled license file. |
| `compatibility` | No | 1–500 characters. Environment requirements — intended platform, system packages, network access needs. |
| `metadata` | No | Arbitrary key-value map for additional properties. |
| `allowed-tools` | No | Space-separated list of pre-approved tools. Experimental; support varies by runtime. |

Keep `SKILL.md` under 500 lines. Move detailed reference material to `references/` files.

### Resource subdirectories

**`scripts/`** — Executable code agents can run. Scripts must be self-contained or document their dependencies. Supported languages depend on the agent runtime; common options are Python, Bash, and JavaScript.

**`references/`** — Supplementary documentation loaded by the agent on demand. Keep individual files focused — agents load these one at a time, so smaller files use less context.

**`assets/`** — Static resources used in output: templates, configuration files, images, data files. Not loaded into context directly; agents copy or reference them as needed.

---

## Progressive disclosure

Agents load skills in stages to keep context lean:

1. **Discovery** (~100 tokens per skill) — At startup, only the `name` and `description` are loaded. The agent knows what skills exist without reading their instructions.
2. **Activation** (< 5,000 tokens recommended) — When a task matches a skill's description, the agent reads the full `SKILL.md` body.
3. **Resources** (as needed) — The agent reads scripts, references, and assets only when the task requires them.

---

## Bundled skill: `sandbox-test`

fuseraft ships a `sandbox-test` skill under `skills/sandbox-test/`. Use it to verify a code change in an isolated throwaway harness before touching production source files.

```
skills/sandbox-test/
├── SKILL.md
├── scripts/
│   └── detect_stack.py
└── references/
    └── stack-patterns.md
```

### When it triggers

The skill activates when an agent needs to test logic before applying a real change — debugging a defect, verifying a behavioral hypothesis, testing edge cases, or any situation where mechanical confidence is needed before modifying production code.

### Workflow

1. Run `detect_stack.py` to identify the project stack and get platform-correct commands.
2. Create a throwaway harness under the system temp directory.
3. Write harness code with `[DBG]`-prefixed debug output at every meaningful boundary.
4. Build (if required) then run, capturing stdout and stderr together.
5. Iterate — up to 5 runs — until the behavior is understood or guidance is needed.
6. State what the harness revealed, apply the change to real source files, remove the harness.

### `detect_stack.py`

```bash
python3 skills/sandbox-test/scripts/detect_stack.py [path]
```

Scans `path` (default: cwd) for stack marker files and returns a JSON object with everything needed for the harness:

```json
{
  "stack": "dotnet",
  "display": ".NET (C#)",
  "markers": ["fuseraft.sln"],
  "shell": "bash",
  "temp_dir": "/tmp",
  "scaffold": "dotnet new console -o /tmp/harness-<name>-<ts> --force",
  "build": "dotnet build",
  "run": "dotnet run",
  "cleanup": "rm -rf <harness_dir>",
  "debug_idiom": "Console.WriteLine($\"[DBG] label={value}\");"
}
```

Substitute `<name>`, `<ts>`, and `<harness_dir>` with the actual values when constructing commands. If the stack is not recognized, the script returns `"stack": "unknown"` with an `error` field directing the agent to `references/stack-patterns.md`.

**Supported stacks:** .NET (C#), TypeScript, Node.js, Go, Rust, Python, Java.

**Cross-platform:** `temp_dir` and command strings are resolved from the host OS at runtime. `scaffold` and `cleanup` use PowerShell syntax on Windows and bash syntax elsewhere. `2>&1` for stderr capture works on both shells.

---

## Adding skills to a project

Skills can be installed per working directory by placing them in `.fuseraft/skills/`. fuseraft discovers this directory automatically alongside the built-in `skills/` directory shipped with the binary, so any skill dropped there is immediately available to agents running in that working directory.

```
my-project/
└── .fuseraft/
    └── skills/
        ├── expense-report/
        │   └── SKILL.md
        └── my-custom-skill/
            ├── SKILL.md
            ├── scripts/
            └── references/
```

This is the recommended location for:

- **Third-party skills** downloaded from [agentskills.io](https://agentskills.io) or other sources
- **Project-specific skills** that encode team conventions, schemas, or workflows for this codebase
- **Experimental skills** you're iterating on before publishing

Skills in `.fuseraft/skills/` are local to that working directory and not committed to the repository unless you choose to include `.fuseraft/skills/` in version control. If a skill should be shared across a team, commit the `skills/` directory at the project root instead.

**Name conflicts:** If a skill in `.fuseraft/skills/` has the same `name` as a built-in skill, the local version takes precedence.

### Writing a skill

Create a directory under `.fuseraft/skills/` with a `SKILL.md`:

```bash
mkdir -p .fuseraft/skills/my-skill
```

```markdown
---
name: my-skill
description: What this skill does and when to use it.
---

# Instructions

Step-by-step guidance for the agent…
```

`SKILL.md` checklist:

- `name` matches the directory name exactly
- `description` covers both what the skill does and when to invoke it — this is the primary trigger signal
- Body is under 500 lines; detailed material lives in `references/` files
- All file references use relative paths from the skill root (e.g. `references/schema.md`, not absolute paths)

Validate against the [Agent Skills spec](https://agentskills.io/specification):

```bash
skills-ref validate .fuseraft/skills/my-skill
```

---

## MAF integration

Skills are provided to agents via MAF's `AgentSkillsProvider`. fuseraft discovers both skill directories at startup and builds a single merged provider:

```csharp
using Microsoft.Agents.AI;

var dirs = new[]
{
    Path.Combine(AppContext.BaseDirectory, "skills"),        // built-in
    Path.Combine(workingDirectory, ".fuseraft", "skills"),   // project-local
}.Where(Directory.Exists).ToArray();

var skillsProvider = new AgentSkillsProviderBuilder()
    .UseFileSkills(dirs)
    .UseFileScriptRunner(MyScriptRunner)   // see below
    .Build();
```

The provider is wired into the `IChatClient` pipeline as the **outermost** layer, wrapping the `FunctionInvokingChatClient`. This ordering is required: the context provider must inject skill tools into `ChatOptions` before the function-invoker processes the request, so that the function-invoker can execute `load_skill`, `run_skill_script`, and other provider-supplied tools when the model calls them.

```csharp
// Build the function-invoking pipeline first.
var functionInvokingClient = chatClient
    .AsBuilder()
    .UseFunctionInvocation()
    .Build();

// Wrap it with the skills context provider on the outside.
var agentChatClient = functionInvokingClient
    .AsBuilder()
    .UseAIContextProviders(skillsProvider)
    .Build();
```

`AgentSkillsProviderBuilder` requires a script runner delegate (`AgentFileSkillScriptRunner`) to execute file-based scripts. MAF 1.3.0 does not ship a built-in subprocess runner, so you need to provide one. A minimal implementation:

```csharp
static async Task<object?> MyScriptRunner(
    AgentFileSkill skill,
    AgentFileSkillScript script,
    AIFunctionArguments arguments,
    CancellationToken cancellationToken)
{
    var ext = Path.GetExtension(script.FullPath).ToLowerInvariant();
    var (program, scriptPath) = ext switch
    {
        ".py" => ("python3", script.FullPath),
        ".sh" => ("bash",    script.FullPath),
        ".js" => ("node",    script.FullPath),
        _     => (null, null)
    };
    if (program is null) return $"No runner for '{ext}'.";

    var argLine = string.Join(" ", arguments.Values.Select(v => v?.ToString() ?? "").Where(s => s.Length > 0));

    var psi = new ProcessStartInfo
    {
        FileName               = program,
        Arguments              = $"{scriptPath} {argLine}".TrimEnd(),
        RedirectStandardOutput = true,
        RedirectStandardError  = true,
        UseShellExecute        = false,
    };
    using var proc = Process.Start(psi)!;
    var stdout = await proc.StandardOutput.ReadToEndAsync(cancellationToken);
    var stderr = await proc.StandardError.ReadToEndAsync(cancellationToken);
    await proc.WaitForExitAsync(cancellationToken);
    return string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stdout}\nstderr: {stderr}";
}
```

To include only specific skills, add a filter before `.Build()`:

```csharp
var skillsProvider = new AgentSkillsProviderBuilder()
    .UseFileSkills(dirs)
    .UseFilter(s => s.Frontmatter.Name == "sandbox-test")
    .UseFileScriptRunner(MyScriptRunner)
    .Build();
```

> **Note:** `AgentSkillsProvider` and related types are marked `[Experimental]` in MAF 1.3.0. Add `<NoWarn>$(NoWarn);MAAI001</NoWarn>` to your project file to suppress the build diagnostic.

See the [MAF skills documentation](https://learn.microsoft.com/en-us/agent-framework/agents/skills) for the full API reference, including code-defined skills, class-based skills, and dependency injection.
