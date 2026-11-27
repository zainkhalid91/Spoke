using Spoke.Core;
using Windows.Win32.Graphics.Direct2D;

namespace Spoke.Daemon;

/// <summary>A slice as the daemon draws and runs it: the validated config plus its glyph and (for apps) icon.</summary>
internal sealed unsafe class SliceDef
{
    public required Slice Def;
    public required char Glyph;
    /// <summary>The app's own icon (premultiplied, GPU), loaded in the background; null until then or if none.</summary>
    public ID2D1Bitmap1* Icon;

    public string Label => Def.Label;
    public string Detail => Def.Detail;
    public ToolKind Tool => Def.Tool;

    public static SliceDef[] From(IReadOnlyList<Slice> slices) =>
        [.. slices.Select(s => new SliceDef { Def = s, Glyph = Glyphs.For(s) })];
}
