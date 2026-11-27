using System.Runtime.InteropServices;

namespace Spoke.Settings;

/// <summary>The few Win32 calls Settings needs (source-generated, AOT-safe).</summary>
internal static partial class Native
{
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(nint hwnd, int id);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint FindWindow(string? className, string title);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(nint hwnd, int cmd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint hwnd);

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(nint hwnd);

    private const uint MOD_NOREPEAT = 0x4000;
    private const int ProbeId = 0x5350;   // "SP"

    /// <summary>
    /// True if no other app holds this shortcut. Registering it for an instant is the only reliable test:
    /// Windows has no API that lists who owns which hotkey.
    /// </summary>
    public static bool IsHotkeyFree(nint hwnd, Spoke.Core.Hotkey hk)
    {
        if (!RegisterHotKey(hwnd, ProbeId, (uint)hk.Modifiers | MOD_NOREPEAT, hk.VirtualKey)) return false;
        UnregisterHotKey(hwnd, ProbeId);
        return true;
    }
}
