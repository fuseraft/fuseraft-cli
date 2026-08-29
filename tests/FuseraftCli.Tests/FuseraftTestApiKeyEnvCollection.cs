namespace FuseraftCli.Tests;

/// <summary>
/// Groups every test class that mutates the process-wide <c>FUSERAFT_TEST_API_KEY</c>
/// environment variable into one xUnit collection so they run sequentially instead of racing
/// each other. xUnit parallelizes across collections by default, and each test class is its own
/// collection unless grouped like this — without it, <see cref="AgentFactoryTests"/> and
/// <see cref="MagenticOrchestratorTests"/> independently set and clear (to <c>null</c>) the same
/// variable in their constructors/<c>Dispose()</c>, so one class's teardown could clear the
/// variable out from under the other's still-running test, producing an intermittent
/// "API key environment variable 'FUSERAFT_TEST_API_KEY' is not set" failure with no relation to
/// the code under test. Mirrors <see cref="FuseraftHomeEnvCollection"/>'s reasoning exactly, for
/// a different shared environment variable.
/// </summary>
[CollectionDefinition("FuseraftTestApiKeyEnv")]
public sealed class FuseraftTestApiKeyEnvCollection;
