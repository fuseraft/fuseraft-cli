using System.Diagnostics;
using fuseraft.Cli.Serve;

namespace FuseraftCli.Tests;

public sealed class ServeHostTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("fuseraft-pid-").FullName;

    private string PidFile => Path.Combine(_dir, "serve.pid");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void APidfileNamingThisProcess_IsStale()
    {
        // What a container's PID 1 daemon finds after it was killed and started again.
        File.WriteAllText(PidFile, Environment.ProcessId.ToString());

        ServeHost.AcquirePidFile(PidFile);

        Assert.Equal(Environment.ProcessId.ToString(), File.ReadAllText(PidFile));
    }

    [Fact]
    public void APidfileNamingAnotherLiveProcess_MeansOneIsRunning()
    {
        if (OperatingSystem.IsWindows()) return;
        using var other = Process.Start(new ProcessStartInfo("sleep", "30"))!;
        try
        {
            File.WriteAllText(PidFile, other.Id.ToString());

            Assert.Contains("already running", Assert.Throws<InvalidOperationException>(() => ServeHost.AcquirePidFile(PidFile)).Message);
        }
        finally { other.Kill(); }
    }
}
