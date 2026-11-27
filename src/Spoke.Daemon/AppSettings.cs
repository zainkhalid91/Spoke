using System.Diagnostics;
using Spoke.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Spoke.Daemon;

/// <summary>Spoke Settings: launching it, and answering its pipe requests (on the UI thread).</summary>
internal sealed unsafe partial class App
{
    private const string SettingsTitle = "Spoke Settings";

    /// <summary>Brings an open Settings window forward, or starts SpokeSettings.exe from Spoke's folder.</summary>
    private void OpenSettings()
    {
        HWND existing;
        fixed (char* title = SettingsTitle) existing = PInvoke.FindWindow(null, title);
        if (!existing.IsNull)
        {
            if (PInvoke.IsIconic(existing)) PInvoke.ShowWindow(existing, SHOW_WINDOW_CMD.SW_RESTORE);
            PInvoke.SetForegroundWindow(existing);
            return;
        }
        string exe = Path.Combine(AppContext.BaseDirectory, "SpokeSettings.exe");
        if (!File.Exists(exe))
        {
            // A build without the settings app: fall back to the file itself.
            Notify("Spoke Settings isn't installed", "Opening settings.json instead.", false);
            OnCommand(CmdEditSettings);
            return;
        }
        try
        {
            PInvoke.AllowSetForegroundWindow(unchecked((uint)-1));   // ASFW_ANY: let the new window take focus
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Write("Couldn't start Spoke Settings: " + ex.Message);
            Notify("Couldn't open Spoke Settings", ex.Message, true);
        }
    }

    private void ServePipe()
    {
        while (_pipe != null && _pipe.Pending.TryDequeue(out var item))
        {
            IpcReply reply;
            try { reply = Handle(item.Request); }
            catch (Exception ex)
            {
                Log.Write("Pipe request failed: " + ex);
                reply = Status(false, "Spoke couldn't do that: " + ex.Message);
            }
            item.Reply.TrySetResult(reply);
        }
    }

    private IpcReply Handle(IpcRequest r)
    {
        switch (r.Cmd)
        {
            case "status":
                return Status(true);
            case "reload":
            {
                // Settings just saved: apply now instead of waiting for the file watcher's debounce, and quietly
                // (Settings shows the result itself). The watcher's own reload then finds the file unchanged.
                long t0 = Stopwatch.GetTimestamp();
                PInvoke.KillTimer(_tray, ConfigTimerId);
                ReloadConfig(quiet: true);
                var reply = Status(_configErrors.Count == 0, _configErrors.Count == 0 ? null : _configErrors[0]);
                reply.ApplyMs = Log.Ms(t0, Stopwatch.GetTimestamp());
                return reply;
            }
            case "preview":
                if (_paused) return Status(false, "Spoke is paused. Resume it from the tray icon to see the wheel.");
                if (IsOpen) Close();
                Open(Stopwatch.GetTimestamp(), wheel: int.TryParse(r.Arg, out int w) ? w : 0);
                return Status(true);
            case "autostart":
            {
                bool want = r.Arg == "on";
                bool ok = Autostart.Set(want);
                return Status(ok, ok ? null : "Windows didn't allow the change.");
            }
            default:
                return Status(false, $"Unknown request \"{r.Cmd}\".");
        }
    }

    private IpcReply Status(bool ok, string? message = null) => new()
    {
        Ok = ok,
        Message = message,
        Hotkey = _hotkeyOk ? ActiveHotkey.ToString() : "",
        Autostart = Autostart.IsEnabled,
        Errors = [.. _configErrors],
        Slices = _settings.Current.Slices.Count,
        Wheels = _wheelSets.Count,
        Pid = Environment.ProcessId,
    };
}
