---
name: skill-author
description: Write a new fuseraft skill from scratch. Trigger when the user wants to create a skill, capture a reusable procedure as a skill, or understand how to structure a SKILL.md file.
---

# Skill Author

Gather what the skill should do, write a well-structured `SKILL.md`, decide whether it needs reference files or bundled scripts, and install it where the user wants it.

## When to Use

Use this skill when:
- The user wants to capture a workflow or procedure as a reusable skill
- The user asks how to write or structure a skill
- A session produced a multi-step debugging or problem-solving pattern worth preserving

Do **not** create a skill for:
- Procedures that are specific to one project and won't generalize
- Tasks that are a single tool call (just do it; no skill needed)
- Anything already covered by a shipped skill (`sandbox-test`, `craft-orchestration`, `debug-session`, `config-audit`, `mcp-setup`, `skill-author`, `build-docx`)

## Workflow

### Step 1: Gather Requirements

Ask these questions. Extract answers from the user's description if already given.

1. **What does the skill do?** One sentence describing the outcome.
2. **When should it trigger?** What does the user say or what situation arises that should activate this skill? This becomes the `description` field.
3. **What are the steps?** Walk through the procedure at a high level. If the user can describe a recent session where this came up, use that as the basis.
4. **Does it need reference material?** Long tables, schemas, pattern libraries, or stack-specific details that the agent loads on demand belong in `references/`.
5. **Does it need a script?** If a step requires running a program (detection logic, validation, data transformation), it belongs in `scripts/` rather than as inline shell commands.
6. **Where should it live?**
   - **Project-local (`.fuseraft/skills/`)** — only available in this project; not shared
   - **Shared with team (`.agents/skills/`)** — committed to the repo; available to all Agent Skills–compatible tools
   - **Global (`~/.fuseraft/skills/`)** — available in all your projects

### Step 2: Write the Frontmatter

```markdown
---
name: <slug>
description: <one or two sentences>
---
```

**`name`:** A short, lowercase kebab-case slug (e.g. `debug-session`, `mcp-setup`) — letters, digits, and single hyphens only, no leading/trailing/double hyphens. This is used as the install directory name when running `fuseraft skills add`. Keep it to 1–3 words, and **make it identical to the skill's directory name**: the REPL loader ignores `name:` and uses the directory name as the slug, but `fuseraft run` orchestration sessions use a stricter loader that silently drops the skill from the catalog if `name:` doesn't exactly match the directory name (or isn't valid kebab-case, or `description:` is empty). Matching them keeps the skill working identically in both surfaces.

**`description`:** This is the most important field — fuseraft injects only the name and description into the agent's catalog at session start. The agent reads this to decide whether the skill is relevant. Write it so it covers:
- What the skill produces or accomplishes
- The types of user requests that should activate it

Bad (too vague):
```
description: Help with databases.
```

Good (specific trigger + outcome):
```
description: Set up a new PostgreSQL schema migration using Flyway. Trigger when the user wants to add a migration, rename a column, or scaffold a new table in a Flyway-managed database.
```

### Step 3: Write the Body

Structure the body as a Markdown document with these sections:

```markdown
# <Skill Title>

One sentence on what this skill does and why it exists.

## When to Use

Bullet list: specific situations that should trigger this skill.
Include a short "Do not use" list to prevent false activations.

## Workflow

### Step 1: <First Action>
...

### Step N: <Last Action>
...

## References   ← only if references/ files exist

- `references/<file>.md` — what it contains and when to load it
```

**Writing steps:**
- Name each step with a verb (Gather, Read, Build, Validate, Apply, Report).
- Each step should direct the agent to call a specific tool or make a specific decision.
- Inline only information the agent needs to act on that step — move large tables or schemas to `references/`.
- End the last step with a concrete deliverable (a file written, a command run, a message reported to the user).
- Keep the total body under ~200 lines. Longer bodies are loaded entirely on activation and consume significant context.

### Step 4: Add Reference Files (If Needed)

Create `references/` inside the skill directory for material that is too large for the main body or is only needed for some steps.

```
my-skill/
├── SKILL.md
└── references/
    └── field-reference.md
```

In `SKILL.md`, tell the agent when to load each reference file:

```markdown
### Step 3: Configure the Widget

Apply these settings. Load `references/field-reference.md` for the full field list if needed.
```

The agent calls `load_skill` to get `SKILL.md`, then calls `read_skill_resource("<slug>", "references/<file>.md")` to load a reference file on demand — not `read_file`, which has no way to know where the skill directory lives on disk. Keep reference files focused — one topic per file.

### Step 5: Add Scripts (If Needed)

Place executable scripts in `scripts/` alongside `SKILL.md`. The agent runs them with `run_skill_script("<slug>", "<filename>")`.

```
my-skill/
├── SKILL.md
└── scripts/
    └── detect_thing.py
```

Scripts are useful when:
- A step requires environment detection or data collection that is tedious to do with raw shell commands
- The same logic would need to be reproduced in multiple skill steps
- The output needs to be structured (e.g. JSON) for the agent to parse

Keep scripts minimal and self-contained. They should accept arguments and write structured output to stdout. See `sandbox-test/scripts/detect_stack.py` for a working example.

In `SKILL.md`, document the script's call signature and output format:

```markdown
Run the detection script, passing the project root as the first argument:

\```bash
python3 scripts/detect_thing.py /path/to/project
\```

Returns a JSON object with `field_a`, `field_b`, and `field_c`.
```

### Step 6: Write the Skill to Disk

Use `write_file` to create `SKILL.md` (and any reference or script files) at the chosen install location:

**Project-local:**
```
<project>/.fuseraft/skills/<slug>/SKILL.md
```

**Shared with team:**
```
<project>/.agents/skills/<slug>/SKILL.md
```

**Global (install with CLI):** Write to the source directory first, then install:
```bash
fuseraft skills add <path-to-skill-directory>
```

Or write directly to `~/.fuseraft/skills/<slug>/SKILL.md` — fuseraft loads from that directory at session start regardless of how the file got there.

### Step 7: Verify

For **REPL sessions**, start or restart fuseraft and run `/tools`. The skill should appear under the `Skills` category with its name and description.

For **orchestration sessions**, run `fuseraft validate` on the config first, then do a one-turn dry run:

```bash
fuseraft run --config <path> --max-iterations 1 "List your available skills."
```

The agent should name the skill in its response. If it does not appear, check:
- `SKILL.md` is directly inside the skill directory (not nested deeper)
- The install path is one of the five recognized locations (project `.fuseraft/skills/`, project `.agents/skills/`, user `.fuseraft/skills/`, user `.agents/skills/`, or shipped built-in)
- **Orchestration-only:** `name:` in the frontmatter exactly matches the directory name (case-sensitive), is valid lowercase kebab-case, and `description:` is non-empty — a mismatch here loads fine in the REPL but is silently dropped by `fuseraft run`'s stricter loader with no error to the user, only a log entry

### Step 8: Refine the Description

After the first test, evaluate whether the description correctly triggers (and doesn't over-trigger) the skill. Adjust it if:
- The agent loads the skill when it shouldn't — the description is too broad
- The agent misses cases where it should load the skill — the description is too narrow or doesn't mention the right trigger phrases

Good trigger coverage: name the user phrases, file types, or problem patterns that should activate the skill, not just the abstract purpose.

## References

- Skill loading and precedence: `docs/skills.md`
- Skill curation (automatic skill generation): `docs/skills.md#automatic-skill-generation`
