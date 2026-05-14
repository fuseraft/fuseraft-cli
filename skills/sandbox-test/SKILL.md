---
name: sandbox-test
description: "Test a code change in an isolated throwaway harness before touching production source files. Use this skill whenever a logic fix or behavioral change is uncertain, involves non-obvious side effects, or requires iterating on behavior before committing. Trigger when: debugging a defect, verifying a hypothesis, testing edge-case handling, or any time confidence in a change must be established mechanically before applying it to real code."
---

# Sandbox Test

Build and run a throwaway harness in the same stack to verify logic before modifying production code.

## Purpose

A code change without a prior run is a claim, not a fact. This skill turns every non-trivial change into a verifiable, iterative experiment before it touches any real file. The harness is cheap to create, safe to throw away, and makes the eventual change to production code a known-good application rather than a guess.

## When to Use

Use this skill when:
- The correct behavior of a fix or new logic is uncertain or non-obvious
- A change involves branching logic, state mutation, or collection handling
- The goal is to *understand* current behavior before altering it
- More than one approach is plausible and a quick experiment can settle it

Do **not** use this skill for trivial edits: renames, comment updates, whitespace changes, or one-liner fixes with obvious outcomes.

## Workflow

Follow these steps in order. Do not modify production source files until Step 5 (Apply) is reached.

### Step 1: Detect the Stack

Run the `detect_stack` script, passing the project root as the first argument:

```bash
python3 scripts/detect_stack.py /path/to/project
```

The script scans for marker files in priority order and returns a JSON object with `stack`, `display`, `markers`, `shell`, `temp_dir`, `scaffold`, `build`, `run`, `cleanup`, and `debug_idiom` fields — everything needed for Steps 2–4. Use these values directly rather than reconstructing them from `references/stack-patterns.md`.

If the script returns `"stack": "unknown"`, read `references/stack-patterns.md` and identify the stack manually from the marker table.

### Step 2: Create the Harness

Create a minimal, self-contained harness under the `temp_dir` reported by the `detect_stack` script:

```
<temp_dir>/harness-<short-descriptor>-<unix-timestamp>/
```

The harness must:
- Reproduce only the logic under test — not the whole application
- Restate or inline only the types and functions needed for the test
- Be buildable and runnable in a single command, without external services

Scaffold the harness using the stack's standard tooling. See `references/stack-patterns.md` for exact scaffold commands.

Prefer inlining the relevant logic over importing production code directly. The harness is a controlled environment — coupling it to the production codebase makes it harder to isolate the behavior under test.

### Step 3: Instrument with Debug Output

Write the harness code. Insert a labeled debug line at every meaningful boundary — function entry, conditional branch, important value, collection size. Use the stack's idiomatic print/log mechanism:

```csharp
// C# example
Console.WriteLine($"[DBG] input={JsonSerializer.Serialize(input)}");
var result = Compute(input);
Console.WriteLine($"[DBG] result={result}");
```

Use the `[DBG]` prefix consistently so debug lines are easy to scan in captured output. Emit:
- Every value the logic depends on, before it is used
- Which branch is taken at each condition
- Collection counts and first few elements
- Any exception message and stack trace

More output is better than less at this stage — it is cheaper to ignore a line than to re-run because a value was not logged.

### Step 4: Build and Run — Capture All Output

Use the `build` and `run` commands from the `detect_stack` output. Run build first; if it succeeds, run the program. Redirect stderr into stdout (`2>&1` works on both bash and PowerShell) so debug lines and error output appear together in the captured result.

If `build` is an empty string, skip directly to `run` — no separate build step is needed for that stack.

If the build fails, fix compilation errors before addressing runtime behavior. Build errors are deterministic and faster to resolve; runtime behavior cannot be observed until the binary exists.

### Step 5: Iterate

Analyze the captured output against the expected behavior:
- Does the output match what the fix should produce?
- Which branch was taken at each condition?
- Are any values null, empty, or out of expected range?
- Are there exceptions or unexpected output lines?

Modify the harness and re-run. Each iteration should narrow the hypothesis — eliminate one explanation per run. Stop when:
- The behavior is fully understood and the correct fix is clear, **or**
- 5 iterations pass without resolution → stop, report findings, and ask for guidance

### Step 6: Apply and Clean Up

Before modifying any production file, state:
1. What the harness revealed (key output lines, confirmed behavior)
2. The exact change to apply and the mechanical reason it is correct

Then:
1. Apply the change to the real source file(s)
2. Remove the harness using the `cleanup` command from the `detect_stack` output, substituting the actual harness path for `<harness_dir>`

## References

- `references/stack-patterns.md` — Detection markers, scaffold commands, build/run patterns, and debug idioms for each supported stack

## Scripts

- `scripts/detect_stack.py [path]` — Scans `path` (default: cwd) for stack markers and returns a JSON object with `stack`, `display`, `markers`, `shell`, `temp_dir`, `scaffold`, `build`, `run`, `cleanup`, and `debug_idiom` fields ready for use in Steps 2–4
