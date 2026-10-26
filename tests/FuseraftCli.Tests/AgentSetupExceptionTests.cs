using fuseraft.Core.Exceptions;

namespace FuseraftCli.Tests;

/// <summary>
/// <see cref="AgentSetupException.FindIn"/> — a setup error is found however an orchestrator
/// wrapped it, and nothing else is mistaken for one.
/// </summary>
public sealed class AgentSetupExceptionTests
{
    [Fact]
    public void FindIn_ReturnsTheExceptionItself()
    {
        var setup = new AgentSetupException("card 401");

        Assert.Same(setup, AgentSetupException.FindIn(setup));
    }

    [Fact]
    public void FindIn_FindsOneWrappedAsAnInnerException()
    {
        var setup = new AgentSetupException("card 401");

        Assert.Same(setup, AgentSetupException.FindIn(new Exception("workflow failed", new InvalidOperationException("step", setup))));
    }

    [Fact]
    public void FindIn_FindsOneInsideAnAggregate()
    {
        var setup = new AgentSetupException("card 401");

        Assert.Same(setup, AgentSetupException.FindIn(new AggregateException(new TimeoutException(), setup)));
    }

    [Fact]
    public void FindIn_ReturnsNullForOtherErrors()
    {
        Assert.Null(AgentSetupException.FindIn(new InvalidOperationException("bug", new NullReferenceException())));
    }
}
