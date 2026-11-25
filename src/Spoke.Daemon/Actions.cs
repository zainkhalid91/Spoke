using Windows.Win32;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Spoke.Daemon;

internal static unsafe class Actions
{
    /// <summary>
    /// Launches an app or URL through the shell. The wheel is already hidden when this runs, and the
    /// launched app is allowed to take the foreground (we just received the hotkey, so we may grant it).
    /// Spike note: this runs on the UI thread; M1 moves actions to a dedicated STA action thread.
    /// </summary>
    public static void Launch(SliceDef s)
    {
        PInvoke.AllowSetForegroundWindow(PInvoke.ASFW_ANY);
        fixed (char* verb = "open")
        fixed (char* file = s.Target)
        {
            var info = new SHELLEXECUTEINFOW
            {
                cbSize = (uint)sizeof(SHELLEXECUTEINFOW),
                fMask = PInvoke.SEE_MASK_FLAG_NO_UI,
                lpVerb = verb,
                lpFile = file,
                nShow = 1, // SW_SHOWNORMAL
            };
            if (!PInvoke.ShellExecuteEx(&info))
                Log.Write($"Launch failed: {s.Target} (error {System.Runtime.InteropServices.Marshal.GetLastSystemError()})");
        }
    }
}
