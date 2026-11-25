using System.Drawing;
using System.Text.Json;
using Spoke.Core.ThirdParty.PowerToys;

namespace Spoke.Core;

public sealed record ColorFormat(string Name, string Format)
{
    public string Render(Color c) => ColorPickerConfig.Render(c, Format);
}

/// <summary>
/// Which colour formats the picker shows and which one a click copies. If the user has PowerToys
/// Color Picker settings, Spoke uses the same visible formats and copy format so both tools agree.
/// </summary>
public sealed class ColorPickerConfig
{
    public required IReadOnlyList<ColorFormat> Formats { get; init; }
    public required string CopyFormat { get; init; }
    public required string Source { get; init; }   // "PowerToys" or "default"

    public static readonly ColorPickerConfig Default = new()
    {
        Formats =
        [
            new("HEX", ColorFormatHelper.GetDefaultFormat("HEX")),
            new("RGB", ColorFormatHelper.GetDefaultFormat("RGB")),
            new("HSL", ColorFormatHelper.GetDefaultFormat("HSL")),
        ],
        CopyFormat = "HEX",
        Source = "default",
    };

    public static string DefaultPowerToysPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "PowerToys", "ColorPicker", "settings.json");

    public ColorFormat Copy => Formats.FirstOrDefault(f => f.Name == CopyFormat) ?? Formats[0];

    public static string Render(Color c, string format) =>
        ColorFormatHelper.GetStringRepresentation(c, format.Replace(ColorFormatHelper.GetColorNameParameter(), "", StringComparison.Ordinal));

    /// <summary>Reads PowerToys Color Picker settings JSON. Anything missing or malformed falls back to defaults.</summary>
    public static ColorPickerConfig FromPowerToysJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("properties", out var props)) return Default;

            var formats = new List<ColorFormat>();
            if (props.TryGetProperty("visiblecolorformats", out var vis) && vis.ValueKind == JsonValueKind.Object)
            {
                foreach (var f in vis.EnumerateObject())
                {
                    // Each entry is {"Key": visible, "Value": format string}.
                    if (f.Value.ValueKind != JsonValueKind.Object) continue;
                    bool visible = f.Value.TryGetProperty("Key", out var k) && k.ValueKind == JsonValueKind.True;
                    string fmt = f.Value.TryGetProperty("Value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
                    if (fmt.Length == 0) fmt = ColorFormatHelper.GetDefaultFormat(f.Name);
                    if (visible && fmt.Length > 0) formats.Add(new ColorFormat(f.Name, fmt));
                }
            }
            if (formats.Count == 0) return Default;

            string copy = props.TryGetProperty("copiedcolorrepresentation", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : formats[0].Name;
            if (!formats.Any(f => f.Name == copy)) copy = formats[0].Name;
            return new ColorPickerConfig { Formats = formats, CopyFormat = copy, Source = "PowerToys" };
        }
        catch (JsonException)
        {
            return Default;
        }
    }

    public static ColorPickerConfig Load(string? powerToysSettingsPath = null)
    {
        string path = powerToysSettingsPath ?? DefaultPowerToysPath;
        try
        {
            return File.Exists(path) ? FromPowerToysJson(File.ReadAllText(path)) : Default;
        }
        catch (IOException)
        {
            return Default;
        }
    }
}

/// <summary>Recently picked colours, newest first. Stored as a JSON array of ARGB integers.</summary>
public sealed class ColorHistory
{
    public const int Limit = 20;
    private readonly List<Color> _items = [];
    private readonly string? _path;

    public ColorHistory(string? path = null) { _path = path; }

    public IReadOnlyList<Color> Items => _items;

    public void Add(Color c)
    {
        c = Color.FromArgb(255, c.R, c.G, c.B);
        _items.RemoveAll(x => x.ToArgb() == c.ToArgb());
        _items.Insert(0, c);
        if (_items.Count > Limit) _items.RemoveRange(Limit, _items.Count - Limit);
    }

    /// <summary>Loads our own history; if there is none yet, seeds from PowerToys' "A|R|G|B" history so recent colours carry over.</summary>
    public void Load(string? powerToysHistoryPath = null)
    {
        _items.Clear();
        try
        {
            if (_path != null && File.Exists(_path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(_path));
                foreach (var e in doc.RootElement.EnumerateArray())
                    if (e.TryGetInt32(out int argb)) _items.Add(Color.FromArgb(argb));
                return;
            }
            if (powerToysHistoryPath != null && File.Exists(powerToysHistoryPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(powerToysHistoryPath));
                foreach (var e in doc.RootElement.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String && TryParsePowerToys(e.GetString()!, out var c) && _items.Count < Limit) _items.Add(c);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            _items.Clear();
        }
    }

    public void Save()
    {
        if (_path == null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "[" + string.Join(",", _items.Select(c => c.ToArgb())) + "]");
    }

    public static bool TryParsePowerToys(string s, out Color c)
    {
        c = default;
        var parts = s.Split('|');
        if (parts.Length != 4) return false;
        var v = new int[4];
        for (int i = 0; i < 4; i++)
            if (!int.TryParse(parts[i], out v[i]) || v[i] < 0 || v[i] > 255) return false;
        c = Color.FromArgb(v[0], v[1], v[2], v[3]);
        return true;
    }
}
