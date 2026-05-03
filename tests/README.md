# Tests

All tests live in `FuseraftCli.Tests` and target `net10.0`. The suite uses **xUnit** with **Moq** for mocking and **coverlet** for coverage collection.

## Running

```bash
dotnet test tests/FuseraftCli.Tests
```

## Test files

| File | What it covers |
|------|----------------|
| `AgentFactoryTests.cs` | `AgentFactory` rejects invalid configs before any network call |
| `AgentStateIntegrationTests.cs` | Agent state transitions through the workflow runtime |
| `ChangeTrackerTests.cs` | `ChangeTracker` records and serialises file-write events |
| `ContextCapFractionTests.cs` | Context-cap fraction clamping and edge cases |
| `ContextWindowFilterTests.cs` | Context window filtering trims messages to stay within token limits |
| `FileSystemPluginTests.cs` | `FileSystemPlugin` read/write/list tool behaviour |
| `HandoffToolRoutingTests.cs` | Handoff tool calls are routed to the correct target agent |
| `HandoffToReviewerValidatorTests.cs` | `HandoffToReviewer` validator enforces required fields |
| `HandoffToTesterValidatorTests.cs` | `HandoffToTester` validator enforces required fields |
| `HttpPluginTests.cs` | `HttpPlugin` GET/POST tools, error handling, and response mapping |
| `JsonSessionStoreTests.cs` | `JsonSessionStore` persists and restores session state |
| `KeywordSelectionStrategyTests.cs` | Keyword-based agent selection picks the correct agent |
| `MagenticOrchestratorTests.cs` | Magentic orchestrator turn loop and termination conditions |
| `MemoryExtractorTests.cs` | Memory extractor parses facts from model responses |
| `MemoryStoreTests.cs` | `MemoryStore` CRUD and scoped recall |
| `ProcessHelperTests.cs` | `ProcessHelper` captures stdout/stderr and exit codes |
| `RequireAllFilesWrittenValidatorTests.cs` | Validator blocks handoff until all expected files are written |
| `RequireBriefValidatorTests.cs` | Validator requires a brief field before handoff |
| `RequireReviewJudgementValidatorTests.cs` | Validator requires an explicit review judgement |
| `RequireShellPassValidatorTests.cs` | Validator blocks handoff if the shell check did not pass |
| `SagaOrchestratorTests.cs` | Saga orchestrator step sequencing and compensation |
| `ShellPluginTests.cs` | `ShellPlugin` executes commands and surfaces results |
| `StateHandoffTests.cs` | State is transferred correctly between agents on handoff |
| `StrategyFactoryTests.cs` | `StrategyFactory` resolves the right selection strategy per config |
| `ValidateConfigCommandTests.cs` | `validate-config` CLI command catches malformed configs |
