namespace FuseraftCli.Tests;

/// <summary>
/// Groups every test class that mutates the process-wide <c>FUSERAFT_HOME</c> environment
/// variable into one xUnit collection so they run sequentially instead of racing each other.
/// xUnit parallelizes across collections by default, and each test class is its own collection
/// unless grouped like this — without it, e.g. <see cref="FuseraftPathsHomeOverrideTests"/>
/// setting <c>FUSERAFT_HOME</c> to null could interleave with <see cref="UserConfigStoreLegacyKeyFileTests"/>
/// expecting its own override, causing the latter to read the real <c>~/.fuseraft/config</c>.
/// </summary>
[CollectionDefinition("FuseraftHomeEnv")]
public sealed class FuseraftHomeEnvCollection;
