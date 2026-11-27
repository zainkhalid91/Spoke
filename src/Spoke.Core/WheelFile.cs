using System.Text.Json;

namespace Spoke.Core;

/// <summary>A <c>.spoke</c> file: one wheel, shared between people or PCs. <c>{ "spoke": 1, "wheel": { … } }</c>.</summary>
public sealed class SpokeFile
{
    public int Spoke { get; set; } = 1;
    public WheelConfig? Wheel { get; set; }
}

/// <summary>
/// Export and import of single wheels. On export, paths under this user's folders become <c>%ENV%</c> forms so the
/// wheel works for someone else; the shortcut is left out (it's personal and may clash). Import checks the wheel,
/// gives it a free name, and lists apps and folders that don't exist on this PC.
/// </summary>
public static class WheelFile
{
    public const string Extension = ".spoke";

    // Longest first, so C:\Users\me\AppData\Local wins over C:\Users\me.
    private static readonly string[] PortableVars = ["LOCALAPPDATA", "APPDATA", "USERPROFILE", "ProgramFiles(x86)", "ProgramFiles", "ProgramData", "SystemRoot"];

    public static string Export(WheelConfig wheel)
    {
        var copy = Clone(wheel);
        copy.Hotkey = null;
        foreach (var s in All(copy.Slices)) MakePortable(s);
        var file = new SpokeFile { Wheel = copy };
        return JsonSerializer.Serialize(file, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<SpokeFile>)ConfigLoader.WriteOptions.GetTypeInfo(typeof(SpokeFile))) + Environment.NewLine;
    }

    /// <summary>
    /// Reads a .spoke file. Returns null with <paramref name="error"/> set when it isn't one or the wheel isn't valid.
    /// <paramref name="existingNames"/> are the wheels already there: a clashing name gets " 2", " 3"….
    /// </summary>
    public static WheelConfig? Import(string json, IEnumerable<string> existingNames, out string? error)
    {
        error = null;
        SpokeFile? file;
        try { file = JsonSerializer.Deserialize(json, ConfigJson.Default.SpokeFile); }
        catch (JsonException) { error = "This isn't a Spoke wheel file (it isn't valid JSON)."; return null; }
        if (file?.Wheel is not { } wheel || file.Spoke < 1) { error = "This isn't a Spoke wheel file (there's no \"wheel\" in it)."; return null; }
        if (file.Spoke > 1) { error = "This wheel was saved by a newer Spoke. Update Spoke to import it."; return null; }

        wheel.Hotkey = null;
        var check = ConfigLoader.Parse(JsonSerializer.Serialize(new SpokeConfig { Wheels = [wheel] }, ConfigJson.Default.SpokeConfig));
        if (!check.Ok) { error = check.Errors[0]; return null; }

        string name = string.IsNullOrWhiteSpace(wheel.Name) ? "Imported" : wheel.Name.Trim();
        var taken = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        string unique = name;
        for (int n = 2; taken.Contains(unique); n++) unique = $"{name} {n}";
        wheel.Name = unique;
        return wheel;
    }

    /// <summary>App and folder targets with a full path that doesn't exist here (bare exe names and Store apps aren't checked).</summary>
    public static List<string> Missing(WheelConfig wheel, Func<string, bool>? exists = null)
    {
        exists ??= p => File.Exists(p) || Directory.Exists(p);
        var missing = new List<string>();
        foreach (var s in All(wheel.Slices))
        {
            string type = (s.Type ?? "").Trim().ToLowerInvariant();
            if (type is not ("app" or "folder") || string.IsNullOrWhiteSpace(s.Target)) continue;
            string t = s.Target.Trim();
            if (t.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) continue;
            string path = Environment.ExpandEnvironmentVariables(t);
            if (!Path.IsPathFullyQualified(path)) continue;   // "wt.exe": found on PATH at run time
            if (!exists(path)) missing.Add($"{s.Label ?? Path.GetFileName(path.TrimEnd('\\'))}: {t}");
        }
        return missing;
    }

    /// <summary>Rewrites one path to use %ENV% for this user's folders. Already-portable paths are left alone.</summary>
    public static string Portable(string path, Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        if (path.Contains('%')) return path;
        foreach (var name in PortableVars)
        {
            string? dir = env(name)?.TrimEnd('\\');
            if (string.IsNullOrEmpty(dir)) continue;
            if (path.Equals(dir, StringComparison.OrdinalIgnoreCase)) return $"%{name}%";
            if (path.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase)) return $"%{name}%" + path[dir.Length..];
        }
        return path;
    }

    private static void MakePortable(SliceConfig s)
    {
        if (s.Target is { } t && !t.Contains("://", StringComparison.Ordinal)) s.Target = Portable(t);
        if (s.WorkDir is { } w) s.WorkDir = Portable(w);
        if (s.Browser is { } b) s.Browser = Portable(b);
    }

    private static IEnumerable<SliceConfig> All(List<SliceConfig>? slices)
    {
        foreach (var s in slices ?? [])
        {
            if (s is null) continue;
            yield return s;
            foreach (var c in s.Children ?? []) if (c is not null) yield return c;
        }
    }

    private static WheelConfig Clone(WheelConfig w) =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(new SpokeFile { Wheel = w }, ConfigJson.Default.SpokeFile), ConfigJson.Default.SpokeFile)!.Wheel!;
}
