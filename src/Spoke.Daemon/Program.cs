using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.System.Threading;

namespace Spoke.Daemon;

internal static unsafe class Program
{
    private static SafeHandle? _singleInstance;

    [STAThread]
    private static int Main(string[] args)
    {
        long t0 = Stopwatch.GetTimestamp();
        if (args.Contains("--focus-target")) { FocusTarget.Run(); return 0; }
        int wi = Array.IndexOf(args, "--worker");
        if (wi >= 0 && wi + 1 < args.Length) return Worker.Run(args[wi + 1]);   // a file job, not the daemon
        if (args.Contains("--render"))
        {
            PInvoke.CoInitializeEx(null, COINIT.COINIT_APARTMENTTHREADED);
            return RenderTool.Run(args);   // draws the wheel to a PNG, offscreen
        }

        _singleInstance = PInvoke.CreateMutex(null, false, "Local\\Spoke.Daemon.SingleInstance");
        if (Marshal.GetLastPInvokeError() == (int)WIN32_ERROR.ERROR_ALREADY_EXISTS) return 1;

        try
        {
            PInvoke.CoInitializeEx(null, COINIT.COINIT_APARTMENTTHREADED);
            StayResponsive();

            int hi = Array.IndexOf(args, "--hotkey");
            var app = new App(forceWarp: args.Contains("--warp"), hi >= 0 && hi + 1 < args.Length ? args[hi + 1] : null);
            var mem = Bench.Memory();
            Log.Write($"Started in {Log.Ms(t0, Stopwatch.GetTimestamp()):F1} ms (Main → tray + hotkey ready). " +
                      $"Renderer {(app.Gfx.UsingWarp ? "WARP" : "GPU")}. WS {mem.ws / 1048576.0:F1} MB, private {mem.priv / 1048576.0:F1} MB");

            int bi = Array.IndexOf(args, "--bench");
            if (bi >= 0)
            {
                int runs = bi + 1 < args.Length && int.TryParse(args[bi + 1], out int n) ? n : 200;
                int li = Array.IndexOf(args, "--label");
                Bench.Start(app, runs, li >= 0 && li + 1 < args.Length ? args[li + 1] : "", args.Contains("--trim"));
            }

            App.RunMessageLoop();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Write("Fatal: " + ex);
            fixed (char* text = "Spoke hit an error and has to close. Details are in %LOCALAPPDATA%\\Spoke\\spoke.log.")
            fixed (char* cap = "Spoke")
                PInvoke.MessageBox(HWND.Null, text, cap, Windows.Win32.UI.WindowsAndMessaging.MESSAGEBOX_STYLE.MB_ICONERROR);
            return 2;
        }
    }

    /// <summary>
    /// Keep the hotkey → frame path fast when the machine is busy: high priority for the UI thread, and
    /// opt out of EcoQoS so Windows doesn't park us on efficiency cores or throttle us as "background".
    /// </summary>
    private static void StayResponsive()
    {
        PInvoke.SetThreadPriority(PInvoke.GetCurrentThread(), THREAD_PRIORITY.THREAD_PRIORITY_HIGHEST);
        var state = new PROCESS_POWER_THROTTLING_STATE
        {
            Version = PInvoke.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            ControlMask = PInvoke.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
            StateMask = 0,
        };
        PInvoke.SetProcessInformation(PInvoke.GetCurrentProcess(), PROCESS_INFORMATION_CLASS.ProcessPowerThrottling, &state, (uint)sizeof(PROCESS_POWER_THROTTLING_STATE));
    }
}
