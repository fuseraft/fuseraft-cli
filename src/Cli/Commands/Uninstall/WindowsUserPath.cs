using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace fuseraft.Cli.Commands.Uninstall;

/// <summary>Edits the user PATH install.ps1 added to, in the registry, so a <c>REG_EXPAND_SZ</c> value and its <c>%VARS%</c> survive.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsUserPath
{
    /// <summary>Removes <paramref name="dir"/> from the user PATH; false when it wasn't on it.</summary>
    public static bool Remove(string dir)
    {
        using var key = Registry.CurrentUser.OpenSubKey("Environment", writable: true);
        if (key?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string value) return false;

        var (updated, removed) = UninstallPlan.RemovePathEntry(value, dir, Environment.ExpandEnvironmentVariables);
        if (!removed) return false;

        if (updated.Length == 0) key.DeleteValue("Path", throwOnMissingValue: false);
        else key.SetValue("Path", updated, key.GetValueKind("Path") == RegistryValueKind.ExpandString
            ? RegistryValueKind.ExpandString
            : RegistryValueKind.String);

        // Tells Explorer, and so every terminal opened from now on, that the environment changed.
        SendMessageTimeout(new IntPtr(0xffff), 0x001A, IntPtr.Zero, "Environment", 0x0002, 5000, out _);
        return true;
    }

    /// <summary>Whether <paramref name="dir"/> is on the user PATH.</summary>
    public static bool Contains(string dir)
    {
        using var key = Registry.CurrentUser.OpenSubKey("Environment");
        return key?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string value
               && UninstallPlan.RemovePathEntry(value, dir, Environment.ExpandEnvironmentVariables).Removed;
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
}
