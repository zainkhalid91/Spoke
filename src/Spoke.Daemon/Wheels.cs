namespace Spoke.Daemon;

internal enum Tool { None, Screenshot, ColorPicker, Clips }

/// <summary>One slice of the wheel. M1 replaces the hard-coded list with settings.json.</summary>
internal sealed class SliceDef
{
    public required string Label;
    public required string Kind;      // "tool" | "app" | "web"
    public string Target = "";        // exe name, path or URL (apps and web)
    public required string Detail;    // second line in the hub
    public required char Glyph;       // Segoe Fluent Icons code point
    public Tool Tool = Tool.None;
}

internal static class Wheels
{
    public const string Name = "Spoke";

    public static readonly SliceDef[] Main =
    [
        new() { Label = "Screenshot",   Kind = "tool", Tool = Tool.Screenshot,  Detail = "Region, window, screen", Glyph = '' },
        new() { Label = "Color picker", Kind = "tool", Tool = Tool.ColorPicker, Detail = "Copy any pixel's colour", Glyph = '' },
        new() { Label = "Clips",        Kind = "tool", Tool = Tool.Clips,       Detail = "Your copied images",    Glyph = '' },
        new() { Label = "Terminal",     Kind = "app",  Target = "wt.exe",       Detail = "Windows Terminal",               Glyph = '' },
        new() { Label = "Files",        Kind = "app",  Target = "explorer.exe", Detail = "File Explorer",                  Glyph = '' },
        new() { Label = "Notepad",      Kind = "app",  Target = "notepad.exe",  Detail = "Quick notes",                        Glyph = '' },
        new() { Label = "GitHub",       Kind = "web",  Target = "https://github.com", Detail = "github.com",               Glyph = '' },
        new() { Label = "Settings",     Kind = "app",  Target = "ms-settings:", Detail = "Windows Settings",               Glyph = '' },
    ];
}
