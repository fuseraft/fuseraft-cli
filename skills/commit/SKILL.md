---
name: commit
description: Stage and commit changes using the conventional commit format. Trigger when an agent needs to commit work — after implementation, after a fix, or when the Developer or Tester instructions say to commit. Ensures the message follows type: description format with a well-written body.
---

# Git Commit

Stage and commit changes with a correctly-formatted conventional commit message.

## When to Use

Use this skill when:
- The Developer has finished implementing and needs to commit
- An agent is instructed to `git_commit` as part of its workflow
- A prior commit attempt failed due to format issues

Do **not** use this skill to:
- Push to remote — use `shell_run("git push")` separately if needed
- Amend a prior commit — use `shell_run("git commit --amend")` directly

## Workflow

### Step 1: Check what changed

Call `shell_run` with:

```bash
git status --short && git diff HEAD
```

If nothing is staged or modified, report that to the calling agent and stop.

### Step 2: Choose the commit type

| Type | When to use |
|---|---|
| `feat` | New capability added |
| `fix` | Defect corrected |
| `refactor` | Restructured without behavior change |
| `docs` | Documentation only |
| `chore` | Config, deps, tooling — no production code |
| `test` | Tests added or fixed |
| `perf` | Measurable performance improvement |
| `build` | Build system or packaging changes |

### Step 3: Write the subject line

Rules — all must hold:
- Format: `type: description` or `type(scope): description`
- ≤ 72 characters total
- Description in imperative mood: "add", "fix", "remove" — not "added" or "adds"
- Lowercase first word after the colon
- No trailing period

Good: `feat: add Redis caching to customer lookup`
Bad:  `Added Redis caching`
Bad:  `feat: Added Redis caching.`

### Step 4: Write the body (when needed)

Include a body when the change is non-trivial or bundles multiple things.

- One blank line between subject and body
- Each bullet starts with `- `
- Explain *why*, not *what* — the diff already shows what changed
- Record constraints, trade-offs, or workarounds a future reader would not guess

Skip the body for obvious single-file changes.

### Step 5: Stage and commit

First stage the relevant files:

```bash
git add <specific files listed in brief.json or changed files>
```

Do not use `git add -A` or `git add .` — stage only the files that belong to this change.

Then commit using `git_commit` with the formatted message. If the `Git` plugin is unavailable, use `shell_run`:

```bash
git commit -m "type: description

- Body line if needed"
```

### Step 6: Verify

Call `shell_run("git log --oneline -1")` and confirm the commit appears with the correct message. Report the commit hash and subject to the calling agent or user.
