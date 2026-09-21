using fuseraft.Core.Models.Config;
using fuseraft.Infrastructure;

namespace FuseraftCli.Tests;

public sealed class TransportOptionsTests
{
    [Fact]
    public void From_NoConfig_UsesTheBuiltInDefaults()
    {
        var t = TransportOptions.From(null);

        Assert.Equal(TimeSpan.FromMinutes(20), t.RequestTimeout);
        Assert.Equal(TimeSpan.FromMinutes(5), t.StreamIdleTimeout);
        Assert.Equal(3, t.MaxRetries);
        Assert.False(t.RequestTimeoutConfigured);
    }

    [Fact]
    public void From_ConfiguredValues_Apply()
    {
        var t = TransportOptions.From(new UserConfig
        {
            RequestTimeoutSeconds = 90, StreamIdleTimeoutSeconds = 45, MaxRetries = 7,
        });

        Assert.Equal(TimeSpan.FromSeconds(90), t.RequestTimeout);
        Assert.Equal(TimeSpan.FromSeconds(45), t.StreamIdleTimeout);
        Assert.Equal(7, t.MaxRetries);
        Assert.True(t.RequestTimeoutConfigured);
    }

    [Fact]
    public void From_OutOfRangeValues_AreClampedNotRejected()
    {
        var low  = TransportOptions.From(new UserConfig { RequestTimeoutSeconds = 1, StreamIdleTimeoutSeconds = 1, MaxRetries = -4 });
        var high = TransportOptions.From(new UserConfig { RequestTimeoutSeconds = int.MaxValue, StreamIdleTimeoutSeconds = int.MaxValue, MaxRetries = 999 });

        Assert.Equal(TimeSpan.FromSeconds(TransportOptions.MinRequestTimeoutSeconds), low.RequestTimeout);
        Assert.Equal(TimeSpan.FromSeconds(TransportOptions.MinStreamIdleTimeoutSeconds), low.StreamIdleTimeout);
        Assert.Equal(0, low.MaxRetries);
        Assert.Equal(TimeSpan.FromSeconds(TransportOptions.MaxRequestTimeoutSeconds), high.RequestTimeout);
        Assert.Equal(TimeSpan.FromSeconds(TransportOptions.MaxStreamIdleTimeoutSeconds), high.StreamIdleTimeout);
        Assert.Equal(TransportOptions.MaxRetriesLimit, high.MaxRetries);
    }

    [Fact]
    public void ByteIdleTimeout_KeepsItsTwoMinuteFloor_WhenTheStreamIdleWindowIsShort()
    {
        var t = TransportOptions.From(new UserConfig { StreamIdleTimeoutSeconds = 30 });

        Assert.Equal(TimeSpan.FromSeconds(120), t.ByteIdleTimeout);
    }

    [Fact]
    public void ByteIdleTimeout_FollowsARaisedStreamIdleWindow()
    {
        // Otherwise the dead-socket check would fire first and the raised window would mean nothing.
        var t = TransportOptions.From(new UserConfig { StreamIdleTimeoutSeconds = 900 });

        Assert.Equal(TimeSpan.FromSeconds(900), t.ByteIdleTimeout);
    }
}
