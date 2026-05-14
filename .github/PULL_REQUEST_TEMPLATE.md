## Summary

<!-- What does this PR do and why? One short paragraph is enough for most changes.
     For larger changes, a few bullet points work well. -->

## Type of change

- [ ] Bug fix
- [ ] New feature
- [ ] Plugin
- [ ] Config example
- [ ] Documentation
- [ ] Refactor / cleanup
- [ ] Other: <!-- describe -->

## Related issues

<!-- Closes #, Fixes #, or "No related issue" -->

## Changes

<!-- List the meaningful changes. Skip obvious ones (e.g. "updated tests") unless the
     test approach is non-obvious and worth explaining. -->

-

## Testing

<!-- How did you verify this? Check all that apply. -->

- [ ] Added or updated unit tests
- [ ] All tests pass locally (`./build.sh --target=Test`)
- [ ] Tested manually end-to-end
- [ ] Documentation updated where relevant
- [ ] `config/examples/` updated if config schema changed

## Invariants

<!-- For changes to orchestration, strategies, or validators — confirm these hold. -->

- [ ] Execution order unchanged (Selection → Validation → Failure handling → Termination → Iteration cap)
- [ ] Any new file/shell/git tool is wrapped by `ChangeTracker`
- [ ] Any new validator is deterministic, side-effect free, and idempotent
- [ ] Physical history is not stripped or reordered outside of compaction

<!-- Not applicable to most PRs — uncheck or remove the section if so. -->

## Notes for reviewers

<!-- Anything that would help a reviewer understand context, trade-offs, or what to focus on. -->
