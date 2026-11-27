using Microsoft.Win32;

namespace Spoke.Daemon;

/// <summary>
/// Windows' own look: whether apps use light mode, and the accent colour from Settings → Personalization → Colours.
/// Both are read from the registry (cheap, and no WinRT); the tray window re-reads them when Windows broadcasts
/// WM_SETTINGCHANGE ("ImmersiveColorSet") or WM_DWMCOLORIZATIONCOLORCHANGED.
/// </summary>
internal static class SystemLook
{
    private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string Dwm = @"Software\Microsoft\Windows\DWM";

    public static bool AppsUseLightTheme
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(Personalize);
            return key?.GetValue("AppsUseLightTheme") is int v ? v != 0 : false;
        }
    }

    /// <summary>The accent colour as 0xRRGGBB (the registry stores 0xAABBGGRR). Falls back to Windows blue.</summary>
    public static uint AccentRgb
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(Dwm);
            if (key?.GetValue("AccentColor") is not int abgr) return 0x0078D4;
            uint v = (uint)abgr;
            return ((v & 0xFF) << 16) | (v & 0xFF00) | ((v >> 16) & 0xFF);
        }
    }

    /// <summary>Switches Windows (apps and taskbar) between light and dark mode, then tells running apps.</summary>
    public static bool SetLightMode(bool light)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(Personalize, writable: true);
            key.SetValue("AppsUseLightTheme", light ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("SystemUsesLightTheme", light ? 1 : 0, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Write("Dark mode toggle failed: " + ex.Message);
            return false;
        }
        Broadcast("ImmersiveColorSet");
        return true;
    }

    private static unsafe void Broadcast(string area)
    {
        fixed (char* a = area)
            Windows.Win32.PInvoke.SendMessageTimeout((Windows.Win32.Foundation.HWND)(nint)0xFFFF, Windows.Win32.PInvoke.WM_SETTINGCHANGE, default, (nint)a,
                Windows.Win32.UI.WindowsAndMessaging.SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ABORTIFHUNG, 200, null);
    }
}
