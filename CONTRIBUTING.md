# Contributing to fuseraft-cli

Thanks for your interest. Contributions are welcome — bug fixes, new plugins,
documentation improvements, and new config examples are all good starting points.

---

## Prerequisites

- [.NET 10 SDK](https://dot.net)
- An API key for at least one supported provider (for manual testing)

---

## Build

```bash
./build.sh          # Linux / macOS  — runs tests then publishes to artifacts/
.\build.ps1         # Windows

# Other targets
./build.sh --target=Build   # compile only
./build.sh --target=Test    # run tests only
./build.sh --target=Lint    # lint only
./build.sh --target=Pack    # produce a versioned zip archive
```

The built binary lands at `bin/fuseraft` (Linux/macOS) or `bin\fuseraft.exe`
(Windows).

---

## Tests

```bash
./build.sh --target=Test
```

Tests live in `tests/FuseraftCli.Tests/`. Please add or update tests for any
behaviour you change. The CI gate requires all tests to pass.

---

## Making changes

1. Fork the repo and create a branch from `main`
2. Make your changes
3. Run `./build.sh --target=Test` to verify nothing is broken
4. Open a pull request against `main` with a clear description of what changed
   and why

For anything non-trivial (new plugin, new strategy, architecture change), open an
issue first so the direction can be agreed before you invest the time.

---

## Adding a plugin

Plugins live in `src/Infrastructure/Plugins/`. Each plugin is a plain C# class
with methods annotated with `[Description(...)]` — the SDK reflects on these to
generate the tool schema.

Steps:
1. Create `src/Infrastructure/Plugins/MyPlugin.cs`
2. Register it in `PluginRegistry.RegisterDefaults()` with a unique name
3. Add it to the plugin table in `docs/plugins.md`
4. Add tests in `tests/FuseraftCli.Tests/`

Look at `ScratchpadPlugin.cs` or `ChatroomPlugin.cs` for reference implementations.

---

## Code style

- Standard C# conventions; the project uses `dotnet format` (run via `--target=Lint`)
- No comments that restate what the code does — only add one when the *why* is
  non-obvious
- Match the surrounding file's style for anything you touch

---

## Reporting bugs

Open a GitHub issue with:
- The command you ran (or the config you used)
- What you expected to happen
- What actually happened
- Your OS, .NET SDK version, and provider

---

## License

By contributing you agree that your changes will be licensed under the project's
[MIT license](LICENSE.md).
