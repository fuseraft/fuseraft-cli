namespace FuseraftCli.Tests;

/// <summary>
/// Groups every test class that grabs a loopback TCP port into one xUnit collection so they run
/// sequentially instead of racing each other. Finding a free port and then binding it are two
/// steps (probe a port, release it, bind it a moment later), so two classes picking a port
/// concurrently can be handed the same one and one of them fails with "Address already in use".
/// xUnit parallelizes across collections by default, and each test class is its own collection
/// unless grouped like this.
/// </summary>
[CollectionDefinition("LoopbackPorts")]
public sealed class LoopbackPortsCollection;
