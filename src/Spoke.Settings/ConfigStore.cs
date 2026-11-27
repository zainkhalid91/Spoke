using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Spoke.Core;

namespace Spoke.Settings;

/// <summary>
/// The settings.json Settings is editing. Pages change <see cref="Config"/> and call <see cref="Save"/>; a save
/// only writes a config that validates, then asks the daemon to apply it now over the pipe. Edits made in a
/// text editor while Settings is open are picked up (<see cref="ReloadedFromDisk"/>).
/// </summary>
internal sealed class ConfigStore : IDisposable
{
    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Spoke");
    public static string FilePath => Path.Combine(Dir, "settings.json");

    /// <summary>The working copy. Null while the file on disk has errors (pages are read-only then).</summary>
    public SpokeConfig? Config { get; private set; }
    /// <summary>Why the file on disk can't be edited here (bad JSON, invalid values).</summary>
    public IReadOnlyList<string> FileErrors { get; private set; } = [];
    /// <summary>The daemon's last answer; null when it isn't running.</summary>
    public IpcReply? Daemon { get; private set; }

    public event Action? ReloadedFromDisk;
    /// <summary>After each save (or failed save): the message to show in the status line.</summary>
    public event Action<string, bool>? Saved;
    public event Action? DaemonChanged;

    private readonly DispatcherQueue _ui;
    private FileSystemWatcher? _watcher;
    private DispatcherQueueTimer? _debounce, _poll;
    private string _lastText = "";
    private string? _lastPipeError;

    public ConfigStore(DispatcherQueue ui)
    {
        _ui = ui;
        Load();
        Watch();
        _ = RefreshDaemonAsync();
        // The daemon can start or quit while Settings is open; a cheap status check keeps the banner honest.
        _poll = _ui.CreateTimer();
        _poll.Interval = TimeSpan.FromSeconds(3);
        _poll.Tick += (_, _) => _ = RefreshDaemonAsync();
        _poll.Start();
    }

    /// <summary>The wheel the Wheels page is editing.</summary>
    public int WheelIndex { get; set; }

    public List<WheelConfig> Wheels
    {
        get
        {
            var c = Config!;
            if (c.Wheels is not { Count: > 0 }) c.Wheels = [ConfigLoader.DefaultWheel()];
            foreach (var w in c.Wheels) w.Slices ??= [];
            c.Wheels[0].Slices = c.Wheels[0].Slices is { Count: > 0 } s ? s : ConfigLoader.DefaultWheel().Slices;
            return c.Wheels;
        }
    }

    public WheelConfig Wheel
    {
        get
        {
            var all = Wheels;
            WheelIndex = Math.Clamp(WheelIndex, 0, all.Count - 1);
            return all[WheelIndex];
        }
    }

    /// <summary>The shortcut of wheel <paramref name="i"/> as configured (the first wheel's lives in the top-level "hotkey").</summary>
    public string? HotkeyOf(int i) => i == 0 ? Wheels[0].Hotkey ?? Config!.Hotkey : i < Wheels.Count ? Wheels[i].Hotkey : null;

    public void SetHotkey(int i, string? value)
    {
        if (i == 0) { Config!.Hotkey = value; Wheels[0].Hotkey = null; }
        else Wheels[i].Hotkey = value;
    }

    /// <summary>A wheel name not used yet: "Dev", else "Dev 2", "Dev 3"….</summary>
    public string FreeName(string name)
    {
        var taken = new HashSet<string>(Wheels.Select(w => (w.Name ?? "").Trim()), StringComparer.OrdinalIgnoreCase);
        string unique = name;
        for (int n = 2; taken.Contains(unique); n++) unique = $"{name} {n}";
        return unique;
    }

    private void Load()
    {
        string text;
        try
        {
            Directory.CreateDirectory(Dir);
            text = File.Exists(FilePath) ? ReadShared(FilePath) : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Config = null;
            FileErrors = [$"Couldn't read settings.json: {ex.Message}"];
            return;
        }
        _lastText = text;
        var r = ConfigLoader.Parse(text);
        if (!r.Ok || r.Config is null)
        {
            Config = null;
            FileErrors = r.Errors;
            return;
        }
        FileErrors = [];
        Config = ConfigLoader.Clone(r.Config);
        if (r.UsedDefaultWheel) Config.Wheels = [ConfigLoader.DefaultWheel(), .. (Config.Wheels ?? []).Skip(1)];
    }

    /// <summary>Validates the whole working config. Empty when it's fine to save.</summary>
    public IReadOnlyList<string> Validate() =>
        Config is null ? FileErrors : ConfigLoader.Parse(ConfigLoader.Serialize(Config)).Errors;

    /// <summary>Writes the working config (if valid) and has the daemon apply it. Returns the problems, if any.</summary>
    public async Task<IReadOnlyList<string>> SaveAsync()
    {
        if (Config is null) return FileErrors;
        var errors = Validate();
        if (errors.Count > 0)
        {
            Saved?.Invoke("Not saved: " + errors[0], true);
            return errors;
        }
        string current;
        try { current = File.Exists(FilePath) ? ReadShared(FilePath) : ""; }
        catch (IOException ex) { Saved?.Invoke("Couldn't read settings.json: " + ex.Message, true); return [ex.Message]; }
        string text = ConfigLoader.WithConfig(current, Config);
        if (text == current) return [];
        try
        {
            File.WriteAllText(FilePath, text);
            _lastText = text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Saved?.Invoke("Couldn't save settings.json: " + ex.Message, true);
            return [ex.Message];
        }

        long t0 = Stopwatch.GetTimestamp();
        var reply = await Task.Run(() => Ipc.Send(new IpcRequest { Cmd = "reload" }));
        double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        SetDaemon(reply);
        if (reply is null) Saved?.Invoke("Saved. Spoke isn't running; it will use these settings when it starts.", false);
        else if (!reply.Ok) Saved?.Invoke("Saved, but Spoke couldn't apply it: " + reply.Message, true);
        else Saved?.Invoke($"Saved · Spoke updated in {ms:0} ms", false);
        return [];
    }

    /// <summary>Asks the daemon to do something (preview, autostart). Null if it isn't running.</summary>
    public async Task<IpcReply?> SendAsync(string cmd, string? arg = null)
    {
        var reply = await Task.Run(() => Ipc.Send(new IpcRequest { Cmd = cmd, Arg = arg }));
        SetDaemon(reply);
        return reply;
    }

    public async Task RefreshDaemonAsync()
    {
        var reply = await Task.Run(() => Ipc.Send(new IpcRequest { Cmd = "status" }, 800));
        SetDaemon(reply);
    }

    private void SetDaemon(IpcReply? reply)
    {
        if (reply is null && Ipc.LastError is { } err && err != _lastPipeError) Log.Write("Pipe: " + err);
        _lastPipeError = reply is null ? Ipc.LastError : null;
        bool changed = (Daemon is null) != (reply is null) || Daemon?.Hotkey != reply?.Hotkey || Daemon?.Autostart != reply?.Autostart
            || (Daemon?.Errors.Count ?? 0) != (reply?.Errors.Count ?? 0);
        Daemon = reply;
        if (changed) DaemonChanged?.Invoke();
    }

    private void Watch()
    {
        _watcher = new FileSystemWatcher(Dir, "settings.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        _debounce = _ui.CreateTimer();
        _debounce.Interval = TimeSpan.FromMilliseconds(250);
        _debounce.IsRepeating = false;
        _debounce.Tick += (_, _) => OnFileChanged();
        void Changed(object? s, FileSystemEventArgs e) => _ui.TryEnqueue(() => { _debounce.Stop(); _debounce.Start(); });
        _watcher.Changed += Changed;
        _watcher.Created += Changed;
        _watcher.Renamed += (s, e) => Changed(s, e);
        _watcher.EnableRaisingEvents = true;
    }

    private void OnFileChanged()
    {
        string text;
        try { text = ReadShared(FilePath); }
        catch (IOException) { return; }
        if (text == _lastText) return;   // our own save
        Load();
        ReloadedFromDisk?.Invoke();
    }

    public void ResetWheel()
    {
        if (Config is null) return;
        Config.Wheels = [ConfigLoader.DefaultWheel()];
    }

    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _poll?.Stop();
    }
}
