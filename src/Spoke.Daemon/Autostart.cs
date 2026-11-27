using Microsoft.Win32;

namespace Spoke.Daemon;

/// <summary>
/// "Start with Windows": a per-user Run entry (HKCU, no admin rights). The registry is the only source of
/// truth, so Task Manager's Startup tab and Spoke's tray menu always agree. Only changed from the tray menu.
/// </summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "Spoke";

    private static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Name) is string v && string.Equals(v.Trim(), Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled) key.SetValue(Name, Command);
            else key.DeleteValue(Name, throwOnMissingValue: false);
            Log.Write($"Start with Windows: {(enabled ? "on" : "off")}");
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Write("Autostart change failed: " + ex.Message);
            return false;
        }
    }
}
