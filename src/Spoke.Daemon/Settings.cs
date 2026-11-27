using Spoke.Core;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Spoke.Daemon;

/// <summary>
/// %APPDATA%\Spoke\settings.json: loads and validates it, watches it for edits (posting
/// <see cref="App.WM_APP_CONFIG"/> to the tray window), and writes single values back (the theme).
/// A file with errors never replaces the last good config.
/// </summary>
internal sealed class Settings : IDisposable
{
    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Spoke");
    public static string FilePath => Path.Combine(Dir, "settings.json");

    /// <summary>The last config that loaded without errors.</summary>
    public ConfigResult Current { get; private set; }
    private string _lastText = "\0";
    private FileSystemWatcher? _watcher;

    public Settings()
    {
        Current = ConfigLoader.Parse("");
    }

    /// <summary>
    /// Reads the file. Returns true if a new good config was applied to <see cref="Current"/>; on errors
    /// <paramref name="errors"/> lists them and the previous config stays. An unchanged file returns false.
    /// </summary>
    public bool Load(out IReadOnlyList<string> errors)
    {
        errors = [];
        string text;
        try
        {
            Directory.CreateDirectory(Dir);
            if (!File.Exists(FilePath))
            {
                text = ConfigLoader.Serialize(ConfigLoader.Default());
                AtomicFile.WriteAllText(FilePath, text);
                Log.Write("Created settings.json with the default wheel");
            }
            else text = ReadShared(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors = [$"Couldn't read settings.json: {ex.Message}"];
            return false;
        }
        if (text == _lastText) return false;
        _lastText = text;

        var r = ConfigLoader.Parse(text);
        if (!r.Ok)
        {
            errors = r.Errors;
            Log.Write("settings.json has errors, keeping the last good config: " + string.Join(" | ", r.Errors));
            return false;
        }
        if (r.UsedDefaultWheel)
        {
            // A file without a wheel (e.g. M1a's theme-only file): write the default wheel in, so it's there to edit.
            string upgraded = ConfigLoader.WithDefaultWheel(text);
            if (TryWrite(upgraded)) _lastText = upgraded;
        }
        Current = r;
        return true;
    }

    /// <summary>Watches the folder (editors often save via a temp file + rename). Events only post a message.</summary>
    public void Watch(HWND notify)
    {
        _watcher = new FileSystemWatcher(Dir, "settings.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            InternalBufferSize = 4096,
        };
        void Post() => PInvoke.PostMessage(notify, App.WM_APP_CONFIG, default, default);
        _watcher.Changed += (_, _) => Post();
        _watcher.Created += (_, _) => Post();
        _watcher.Renamed += (_, _) => Post();
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>Writes one top-level value (e.g. "theme") without disturbing the rest of the file.</summary>
    public void SaveValue(string key, string value)
    {
        string text;
        try { text = File.Exists(FilePath) ? ReadShared(FilePath) : ""; }
        catch (IOException ex) { Log.Write("Could not read settings to save: " + ex.Message); return; }
        string updated = ConfigLoader.WithValue(text, key, value);
        if (updated != text && TryWrite(updated))
        {
            _lastText = updated;
            var r = ConfigLoader.Parse(updated);
            if (r.Ok) Current = r;
        }
    }

    private static bool TryWrite(string text)
    {
        try
        {
            AtomicFile.WriteAllText(FilePath, text);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("Could not save settings: " + ex.Message);
            return false;
        }
    }

    /// <summary>Reads even while an editor holds the file open.</summary>
    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    public void Dispose() => _watcher?.Dispose();
}
