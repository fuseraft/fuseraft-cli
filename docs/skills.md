# Skills

Skills give agents specialized knowledge and step-by-step procedures for specific types of tasks. When you start a session, fuseraft automatically identifies which installed skills are relevant and loads them for the agent.

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

The `name` must match the directory name exactly. The `description` is what fuseraft uses to decide whether the skill is relevant to the current task — write it so it covers both what the skill does and the kinds of tasks that should trigger it.

If your instructions are long, move reference material into a `references/` subdirectory inside the skill folder. The agent loads those files on demand rather than all at once.

**If two installed skills share the same name**, the one in the higher-precedence location wins and a warning is logged.

---

## Automatic skill generation

When skill curation is enabled in your config, fuseraft automatically creates a new skill at the end of qualifying sessions. If the session produced a reusable procedure — a debugging workflow, a multi-step pattern, a problem-solving approach — fuseraft writes it to `~/.fuseraft/skills/` so future sessions can benefit from it.

Trivial or highly project-specific sessions typically produce no output. Generated skills are never overwritten — if a skill with the same name already exists, the session result is skipped.

See [Configuration → Skill curation](configuration.md#skill-curation) to enable or tune this behavior.
