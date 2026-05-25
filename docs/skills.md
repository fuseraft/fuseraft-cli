# Skills

Skills give agents specialized knowledge and step-by-step procedures for specific types of tasks. At REPL startup fuseraft scans your skill directories, injects a catalog of available skills into the system prompt, and exposes two tools the model can call to use them.

---

## Where skills come from

fuseraft loads skills from five locations, in precedence order (earlier entries win when two skills share the same name):

| Scope | Path |
|-------|------|
| Project (fuseraft) | `<project>/.fuseraft/skills/` |
| Project (shared) | `<project>/.agents/skills/` |
| User (fuseraft) | `~/.fuseraft/skills/` |
| User (shared) | `~/.agents/skills/` |
| Built-in | shipped with fuseraft |

---

## How skills work in the REPL

fuseraft uses a progressive-disclosure pattern to keep context lean:

1. **Catalog injection** — At session start, the names and descriptions of all discovered skills are appended to the system prompt so the model knows what is available without loading every full body.
2. **On-demand load** — When the model decides a skill is relevant, it calls `load_skill("<slug>")` to retrieve the full `SKILL.md` content, then follows those step-by-step instructions using its other tools.
3. **Script execution** — If a skill bundles executable scripts alongside its `SKILL.md`, the model can run them with `run_skill_script("<slug>", "<filename>")`.

At startup, the skill count appears in the compact info line alongside the active tool categories (e.g. `… · 3 skills · …`). Run `/tools` at any time to list all active tools by category, including the `Skills` category.

| Tool | Description |
|------|-------------|
| `load_skill` | Load the full `SKILL.md` for a skill by slug. |
| `run_skill_script` | Run a script bundled with a skill (`.sh`, `.py`, `.js`). |

If `--no-tools` is passed, skills are disabled for that session.

---

## Built-in skill: `sandbox-test`

fuseraft ships with a `sandbox-test` skill. It activates automatically when the agent needs to verify logic before touching real source files — for example, when debugging a defect, testing an edge case, or confirming a behavioral hypothesis.

When it triggers, the agent will:

1. Detect your project stack (.NET, Go, Rust, Python, TypeScript, Node.js, or Java).
2. Create a throwaway harness in the system temp directory.
3. Write and run harness code with debug output at key boundaries.
4. Iterate until the behavior is understood (up to 5 runs).
5. Apply the confirmed change to your real files and remove the harness.

You don't need to invoke this skill explicitly — it activates on its own when appropriate.

---

## Cross-session handoff: `/compact`

To pass context from the current REPL session to a new one, use the `/compact` command. `/compact` generates a concise summary of what was worked on, key decisions, current state, and what comes next; it then replaces the conversation history with that summary so the session can continue with a clean context window.

If you want to carry a summary to a *different* session or agent entirely, run `/compact` and copy the resulting summary into the new session as an opening message.

---

## Installing skills

### For a single project

Place a skill directory under `.fuseraft/skills/` in your working directory:

```
my-project/
└── .fuseraft/
    └── skills/
        └── my-skill/
            └── SKILL.md
```

Use `.agents/skills/` instead if you want the skill available to other Agent Skills–compatible tools (Claude Code, Cursor, Copilot) running in the same directory.

To share a skill with your team, commit the skill directory. `.agents/skills/` is the recommended location for shared skills.

> **Trust warning:** Skills travel with the repository. Treat `.fuseraft/skills/` and `.agents/skills/` the same as a `Makefile` or postinstall script — only run fuseraft in directories you trust. See [Security — Skills execution trust model](security.md#skills-execution-trust-model).

### For all your projects

Use `fuseraft skills add` to copy a skill into `~/.fuseraft/skills/` and register it in the global search index:

```bash
fuseraft skills add ../skills/productivity/handoff
fuseraft skills add ~/my-skills/triage
```

The command accepts a path to a skill directory (containing `SKILL.md`) or directly to a `SKILL.md` file. The slug is derived from the `name:` field in the frontmatter; if no `name:` field is present, the directory name is used. If a skill with the same slug already exists it is updated in place.

You can also install skills by placing them directly under `~/.fuseraft/skills/` without using the CLI — skills are loaded from that directory at session start regardless of how they got there.

---

## Writing a skill

Create a directory named after your skill and add a `SKILL.md` file:

```bash
mkdir -p .fuseraft/skills/my-skill
```

```markdown
---
name: my-skill
description: What this skill does and when to use it.
---

# Instructions

Step-by-step guidance for the agent...
```

The `name` field is used by `fuseraft skills add` to derive the destination directory name when installing a skill globally, so keeping it in sync with the directory name is strongly recommended. The runtime loader uses the **directory name** as the slug — the `name:` field in frontmatter is not read at load time. The `description` is what fuseraft uses to decide whether the skill is relevant to the current task — write it so it covers both what the skill does and the kinds of tasks that should trigger it.

If your instructions are long, move reference material into a `references/` subdirectory inside the skill folder. The agent loads those files on demand rather than all at once.

**If two installed skills share the same name**, the one in the higher-precedence location wins and a warning is logged.

---

## Automatic skill generation

When skill curation is enabled, fuseraft automatically creates or updates a skill at the end of qualifying sessions. If the session produced a reusable procedure — a debugging workflow, a multi-step pattern, a problem-solving approach — fuseraft writes it to `~/.fuseraft/skills/` so future sessions can benefit from it.

Trivial or highly project-specific sessions typically produce no output. If a skill with the same slug already exists it is updated in place, so the procedure is refined over time rather than duplicated.

### Enabling curation for REPL sessions

Add a `skillCuration` block to `~/.fuseraft/config`:

```json
{
  "modelId": "claude-sonnet-4-5",
  "skillCuration": {
    "enabled": true
  }
}
```

All the standard knobs are supported (`minTurns`, `digestTurns`, `model`, `libraryPath`, `indexTopN`, `logPath`). Note that skill injection at session start (surfacing relevant skills before the first turn) is only available in `fuseraft run` sessions — the REPL has no upfront task description to query against.

### Enabling curation for `fuseraft run` sessions

Set `SkillCuration.Enabled: true` in your orchestration YAML. See [Configuration → Skill curation](configuration.md#skill-curation) for the full field reference, start-of-session skill injection, and the curation log format.
