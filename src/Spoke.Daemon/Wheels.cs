using Spoke.Core;
using Windows.Win32.Graphics.Direct2D;

namespace Spoke.Daemon;

/// <summary>
/// A slice as the daemon draws and runs it: the validated config plus its glyph and (for apps) icon. Generated
/// slices (sub-wheel items) carry their own behaviour in <see cref="OnRun"/> or <see cref="OnOpen"/>.
/// </summary>
internal sealed unsafe class SliceDef
{
    public required Slice Def;
    public required char Glyph;
    /// <summary>The app's own icon (premultiplied, GPU), loaded in the background; null until then or if none.</summary>
    public ID2D1Bitmap1* Icon;

    /// <summary>Generated item: what choosing it does (runs after the wheel closes, unless <see cref="KeepOpen"/>).</summary>
    public Action? OnRun;
    /// <summary>Generated item: opens another wheel in place (null result = nothing to show; the item explains why).</summary>
    public Func<SubWheel?>? OnOpen;
    /// <summary>Stay open after running (volume steps), so it can be chosen again.</summary>
    public bool KeepOpen;

    public string Label => Def.Label;
    public string Detail => Def.Detail;
    public ToolKind Tool => Def.Tool;

    /// <summary>Group: its children, ready to show as a band or a wheel.</summary>
    public SliceDef[] Children = [];

    public static SliceDef[] From(IReadOnlyList<Slice> slices) =>
        [.. slices.Select(s => new SliceDef { Def = s, Glyph = Glyphs.For(s), Children = s.Children.Count > 0 ? From(s.Children) : [] })];

    /// <summary>These slices and every group's children (for loading and releasing app icons).</summary>
    public static IEnumerable<SliceDef> Flatten(IEnumerable<SliceDef> slices)
    {
        foreach (var s in slices)
        {
            yield return s;
            foreach (var c in s.Children) yield return c;
        }
    }

    public static SliceDef Item(string label, string detail, char glyph, Action run, bool keepOpen = false) =>
        new() { Def = new Slice(SliceKind.Tool, label, detail, null), Glyph = glyph, OnRun = run, KeepOpen = keepOpen };

    public static SliceDef Sub(string label, string detail, char glyph, Func<SubWheel?> open) =>
        new() { Def = new Slice(SliceKind.Tool, label, detail, null), Glyph = glyph, OnOpen = open };
}

/// <summary>A generated wheel shown in place of the current one (Files ▸ Convert ▸ …). Backspace/Esc goes back.</summary>
/// <summary>One configured wheel as the daemon holds it.</summary>
internal sealed class WheelSet(string name, string? hotkey, SliceDef[] slices)
{
    public string Name { get; } = name;
    public string? Hotkey { get; } = hotkey;
    public SliceDef[] Slices { get; } = slices;
}

internal sealed class SubWheel(string title, SliceDef[] items)
{
    public string Title { get; } = title;
    public SliceDef[] Items { get; } = items;
}
