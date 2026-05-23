using System.Diagnostics;

// Usage: fuseraft-update <pending-binary-path> <install-path>
if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: fuseraft-update <pending-binary-path> <install-path>");
    return 1;
}

var pendingPath = args[0];
var installPath = args[1];
var installName = Path.GetFileName(installPath);
var backupPath  = installPath + ".backup";

Console.WriteLine("fuseraft updater");
Console.WriteLine();

if (!File.Exists(pendingPath))
{
    Console.Error.WriteLine($"Error: pending binary not found: {pendingPath}");
    return 1;
}

// Brief wait for the launching fuseraft process to exit before we start polling.
await Task.Delay(2000);

// ─────────────────────────────────────────────────────────────────────────────
// Wait for all fuseraft.exe instances to exit.
// ─────────────────────────────────────────────────────────────────────────────
while (true)
{
    var running = Process.GetProcessesByName("fuseraft")
                         .Where(p => { try { return !p.HasExited; } catch { return false; } })
                         .ToArray();

    if (running.Length == 0)
        break;

    Console.Write(
        $"  {running.Length} instance{(running.Length == 1 ? "" : "s")} of {installName} still running." +
        "  Kill now? [Y/n]: ");

    var key = Console.ReadKey(intercept: false);
    Console.WriteLine();

    if (key.Key == ConsoleKey.N)
    {
        Console.WriteLine("  Waiting 5 seconds...");
        await Task.Delay(5000);
    }
    else
    {
        int killed = 0;
        foreach (var p in running)
        {
            try { p.Kill(entireProcessTree: true); killed++; }
            catch { /* already gone */ }
        }
        Console.WriteLine($"  Killed {killed} process{(killed == 1 ? "" : "es")}.");
        await Task.Delay(1000);
    }
}

Console.WriteLine("Installing update...");

// ─────────────────────────────────────────────────────────────────────────────
// Rename the current binary to .backup so fuseraft can't be launched mid-swap.
// ─────────────────────────────────────────────────────────────────────────────
Console.Write($"  Backing up {installName} -> {installName}.backup ... ");
try
{
    if (File.Exists(backupPath)) File.Delete(backupPath);
    File.Move(installPath, backupPath);
    Console.WriteLine("done");
}
catch (Exception ex)
{
    Console.WriteLine();
    Console.Error.WriteLine($"Error: could not rename {installName}: {ex.Message}");
    return 1;
}

// ─────────────────────────────────────────────────────────────────────────────
// Move the pending binary into place.
// ─────────────────────────────────────────────────────────────────────────────
Console.Write($"  Installing new binary ...                    ");
try
{
    File.Move(pendingPath, installPath);
    Console.WriteLine("done");
}
catch (Exception ex)
{
    Console.WriteLine();
    Console.Error.WriteLine($"Error: could not install new binary: {ex.Message}");
    Console.Error.WriteLine($"The previous binary was preserved at: {backupPath}");

    // Attempt to restore the backup so fuseraft is usable again.
    try { File.Move(backupPath, installPath); }
    catch { /* best effort */ }

    return 1;
}

// Clean up the backup — it's only there to block launches during the swap.
try { File.Delete(backupPath); }
catch { /* non-fatal — leftover backup won't affect anything */ }

Console.WriteLine();
Console.WriteLine($"✓ Update complete.");
Console.WriteLine($"  Run 'fuseraft --version' to verify.");
Console.WriteLine();
Console.Write("Press any key to close...");
Console.ReadKey(intercept: true);
Console.WriteLine();
return 0;
