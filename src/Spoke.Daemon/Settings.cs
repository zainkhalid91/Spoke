using System.Text.Json;

namespace Spoke.Daemon;

/// <summary>Tiny persisted preferences until the full settings.json model lands in M1.</summary>
internal sealed class Settings
{
    public string Theme = "Aurora";

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Spoke");
    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        var s = new Settings();
        try
        {
            if (!File.Exists(FilePath)) return s;
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            if (doc.RootElement.TryGetProperty("theme", out var t) && t.ValueKind == JsonValueKind.String) s.Theme = t.GetString()!;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Log.Write("settings.json unreadable, using defaults: " + ex.Message);
        }
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, $"{{\n  \"theme\": \"{Theme}\"\n}}\n");
        }
        catch (IOException ex)
        {
            Log.Write("Could not save settings: " + ex.Message);
        }
    }
}
