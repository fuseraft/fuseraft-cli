# Tests

All tests live in `FuseraftCli.Tests` and target `net10.0`. The suite uses **xUnit** with **Moq** for mocking and **coverlet** for coverage collection.

## Running

```bash
dotnet test tests/FuseraftCli.Tests
```

## Test files

| File | What it covers |
|------|----------------|
| `TeamCompilerTests.cs` | Team files load strictly (case-insensitive keys, no duplicates) and compile to the state machine, gates and prompts |
| `LegacyConfigConverterTests.cs` | `fuseraft migrate` turns old `Orchestration:` files into camelCase team files that compile |
| `InitTemplatesTests.cs` | Every `config/examples` file and `fuseraft init` template compiles and names only real routes |
| `HandoffRouteFunctionTests.cs` | The `handoff` tool's `route_keyword` is an enum of the step's routes |
| `HandoffPluginTests.cs` | Handoff tool calls drive state-machine routing |
| `StateMachineSelectionStrategyTests.cs` | Gate failures, approvals, parallel gates, and corrections that reach handoff agents |
| `OrchestratorConfigLoaderTests.cs` | Global provider defaults only fill models on the same provider |
| `ApiKeyValidatorTests.cs` | Startup key probes use the inferred endpoint and Anthropic's headers |
| `AgentFactoryTests.cs` | `AgentFactory` rejects invalid configs before any network call |
| `ChangeTrackerTests.cs` | `ChangeTracker` records and serialises file-write events |
| `ContextWindowFilterTests.cs` | Context window filtering trims messages to stay within token limits |
| `FileSystemPluginTests.cs` | `FileSystemPlugin` read/write/list tool behaviour |
| `AnyFileWrittenValidatorTests.cs` | The `anyFileWritten` gate: a file written this turn, or a matching shell command |
| `HttpPluginTests.cs` | `HttpPlugin` GET/POST tools, error handling, and response mapping |
| `JsonSessionStoreTests.cs` | `JsonSessionStore` persists and restores session state |
| `MagenticOrchestratorTests.cs` | Magentic orchestrator turn loop and termination conditions |
| `MemoryExtractorTests.cs` | Memory extractor parses facts from model responses |
| `MemoryStoreTests.cs` | `MemoryStore` CRUD and scoped recall |
| `ProcessHelperTests.cs` | `ProcessHelper` captures stdout/stderr and exit codes |
| `RequireReviewJudgementValidatorTests.cs` | Validator requires an explicit review judgement |
| `ShellPluginTests.cs` | `ShellPlugin` executes commands and surfaces results |
| `StrategyFactoryTests.cs` | `StrategyFactory` resolves the right selection strategy per config |
| `ValidateConfigCommandTests.cs` | `validate-config` CLI command catches malformed configs |
| `KnowledgeLayerRoundTripTests.cs` | Full knowledge layer round-trip: graph build → ADR creation → graph traversal → broker context assembly → provenance claim recording → lifecycle GC; also covers `ConfidenceComputer` tiers and GC dry-run correctness |
