# Writing Effective Tasks

The quality of a fuseraft session is bounded by the quality of the task you give it. A well-written task produces a complete implementation that the Reviewer can actually verify. A vague task produces an implementation that looks plausible but may silently fail at runtime.

This guide covers the most common authoring mistakes and how to fix them.

---

## Show the input AND the expected output

The most important rule: **when you give an example of syntax or a feature, also show what running it should produce.**

A code example tells the agents what to write. It does not tell them what the code should *do*. The agents — including the Reviewer — will fill that gap with the weakest possible interpretation.

**Insufficient:**

```
Add support for the new `include` assignment syntax:

```kiwi
x = include 'path/to/module'
x.foofunc()
```
```

The agents will make this syntax parse without crashing. That satisfies "Parser accepts `x = include`" literally. Whether `x.foofunc()` actually resolves is a separate runtime behavior that nobody was asked to verify.

**Effective:**

```
Add support for the new `include` assignment syntax. Running this program:

```kiwi
x = include 'path/to/module'
x.foofunc()
```

should produce:

```
foofunc result: 42
```

The `include` expression must return the module object (not a path string), so member access works immediately on the assigned variable.
```

The expected output is what the Reviewer needs to verify the feature actually works end-to-end, not just that it compiles.

---

## Write acceptance criteria that can be run, not read

Acceptance criteria are checked by the Reviewer and (when `expected_output_contains` is set) by the `RequireAcceptanceCriteriaPassed` validator. Prose criteria can only be "verified" by reading code. Criteria with expected output can be verified by running the program.

**Prose-only (weak):**

```json
"acceptance_criteria": [
  "Parser accepts x = include expression in assignment context",
  "Compiler emits Include instruction that leaves module ref on stack",
  "VM Include opcode returns populated module object",
  "x.foofunc() resolves on the returned object",
  "build.sh succeeds and tests pass"
]
```

These criteria are checkable by code inspection. A Reviewer can claim PASS on all five without running the feature. The first four require understanding the internals; the last one only requires `./build.sh` to exit 0.

**With expected output (strong):**

```json
"acceptance_criteria": [
  {
    "criterion": "x = include 'path' returns a module object so member access works",
    "run": "kiwi test_include.ki",
    "expected_output_contains": "foofunc result: 42"
  },
  {
    "criterion": "build.sh succeeds",
    "run": "./build.sh",
    "expected_output_contains": "BUILD SUCCEEDED"
  }
]
```

The `RequireAcceptanceCriteriaPassed` validator reads `expected_output_contains` from the brief and blocks `APPROVED` if any sentinel was never found in a session command output. The Reviewer is forced to run the program, not just read the code.

See [Routing Validators — RequireReviewJudgement](validators.md#requirereviewjudgement) for the coverage check that enforces one review entry per criterion.

---

## List the files explicitly

The `RequireAllFilesWritten` validator blocks developer handoff until every file in `files_to_change` has been written. If you omit a file from the list, the validator cannot catch the omission.

**Incomplete:**

```json
"files_to_change": [
  { "path": "src/VM/KiwiVM.cs",   "reason": "Add Include opcode handler" },
  { "path": "src/VM/Opcode.cs",   "reason": "Add Include opcode constant" }
]
```

The parser changes (`src/Parsing/Parser.cs`, `src/Parsing/AST/IncludeNode.cs`) are missing. The Developer will implement only what is listed and still pass the validator.

**Complete:**

```json
"files_to_change": [
  { "path": "src/Parsing/AST/IncludeNode.cs", "reason": "New AST node for include-as-expression" },
  { "path": "src/VM/Compiler.cs",             "reason": "Emit Include instruction for IncludeNode" },
  { "path": "src/VM/KiwiVM.cs",               "reason": "Add Include opcode handler returning module object" },
  { "path": "src/VM/Opcode.cs",               "reason": "Add Include opcode constant" }
]
```

If you are not sure which files need to change, let the Archaeologist discover them. Give the task to the full team (`Archaeologist → Planner → Developer → Reviewer`) and let the Planner write the brief after exploration. Correct the brief if the Planner misses a file.

---

## One criterion per verifiable behavior

Bundle criteria and you lose the ability to tell which one failed. Each criterion should correspond to a single command that either passes or fails.

**Bundled (hard to verify):**

```
"Parser accepts the syntax, compiler emits the right instruction, VM executes it correctly, and tests pass"
```

A Reviewer verifying this runs one command. If the output is wrong, there is no signal about which layer failed.

**Separated:**

```json
"acceptance_criteria": [
  { "criterion": "Parsing: x = include 'p' produces IncludeNode in AST",     "run": "...", "expected_output_contains": "..." },
  { "criterion": "Compilation: Include opcode appears in compiled bytecode",  "run": "...", "expected_output_contains": "..." },
  { "criterion": "Runtime: x.foofunc() resolves on the returned module",      "run": "...", "expected_output_contains": "..." },
  { "criterion": "Build: build.sh exits 0",                                   "run": "./build.sh", "expected_output_contains": "BUILD SUCCEEDED" }
]
```

When the runtime criterion fails, the Reviewer knows exactly which layer is broken and can report it without re-running the others.

---

## Use the right validators

| What you want to enforce | Validator |
|---|---|
| Developer wrote at least one file | `RequireWriteFile` |
| Developer wrote every file in the brief | `RequireAllFilesWritten` |
| Reviewer ran a shell command | `RequireShellPass` |
| Reviewer produced a per-criterion judgement block | `RequireReviewJudgement` |
| Reviewer covered every brief criterion | `RequireReviewJudgement` + `Validation.BriefPath` |
| Testable criteria were run and output matched | `RequireAcceptanceCriteriaPassed` |

`RequireWriteFile` is the cheapest check — use it when any file write is sufficient. `RequireAllFilesWritten` is stricter and requires `Validation.BriefPath` to be set. For the Reviewer, combine `RequireShellPass` and `RequireReviewJudgement` at minimum; add a `BriefPath` to enforce criterion coverage.

See [Routing Validators](validators.md) for configuration details.

---

## Quick checklist

Before running a session, verify your task covers:

- [ ] Goal is one sentence describing the runtime behavior, not the code change
- [ ] `files_to_change` lists every file that needs to be written or patched
- [ ] Each acceptance criterion names one verifiable behavior
- [ ] At least one criterion has `expected_output_contains` (or you have a test suite that covers the behavior)
- [ ] If you gave a code example, you also wrote what running it should produce
- [ ] `RequireAllFilesWritten` is on the developer handoff route (not just `RequireWriteFile`)
- [ ] `Validation.BriefPath` is set so `RequireReviewJudgement` enforces criterion coverage

---

## Spec-driven development

When a task is complex enough to require up-front design agreement — user journeys, API contracts, system boundaries — write a spec file first and pass it with `--spec`. All agents are anchored to the spec from the start; the Planner derives `brief.json` from it rather than synthesising a plan from a raw prompt.

```bash
fuseraft run --spec spec.md
fuseraft run --spec spec.md "Add authentication"
```

See [Spec-Driven Development](spec-driven.md) for the full workflow, spec file format, and when to use each level (spec-first, spec-anchored, spec-as-source).
