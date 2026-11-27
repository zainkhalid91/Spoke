using System.Text;
using Spoke.Core;

namespace Spoke.Core.Tests;

public class AppearanceTests
{
    [Fact]
    public void Defaults_when_nothing_is_set()
    {
        var r = ConfigLoader.Parse("{}");
        Assert.Equal(Appearance.Default, r.Appearance);
        Assert.Equal("M", r.Appearance.SizeName);
    }

    [Fact]
    public void Auto_theme_follows_windows()
    {
        var a = ConfigLoader.Parse("""{ "theme": "auto", "lightTheme": "paper", "darkTheme": "ember" }""").Appearance;
        Assert.True(a.FollowsSystem);
        Assert.Equal("Paper", a.ThemeFor(systemLight: true));
        Assert.Equal("Ember", a.ThemeFor(systemLight: false));
    }

    [Theory]
    [InlineData("S", 0.8f)]
    [InlineData("xl", 1.4f)]
    [InlineData("1.1", 1.1f)]
    [InlineData("120%", 1.2f)]
    [InlineData("90", 0.9f)]
    public void Sizes(string size, float scale)
    {
        Assert.True(Appearance.TryParseSize(size, out float s));
        Assert.Equal(scale, s, 3);
    }

    [Theory]
    [InlineData("huge")]
    [InlineData("3")]
    [InlineData("0.2")]
    public void Bad_sizes_are_reported(string size)
    {
        var r = ConfigLoader.Parse($$"""{ "size": "{{size}}" }""");
        Assert.Contains(r.Errors, e => e.Contains("\"size\""));
    }

    [Fact]
    public void Accent_windows_and_custom()
    {
        Assert.Equal(AccentMode.Windows, ConfigLoader.Parse("""{ "accent": "Windows" }""").Appearance.Accent);
        var custom = ConfigLoader.Parse("""{ "accent": "#3b82f6" }""").Appearance;
        Assert.Equal((AccentMode.Custom, 0x3B82F6u), (custom.Accent, custom.AccentRgb));
        Assert.Equal(0xFFAA00u, ConfigLoader.Parse("""{ "accent": "#fa0" }""").Appearance.AccentRgb);
        Assert.Contains(ConfigLoader.Parse("""{ "accent": "teal-ish" }""").Errors, e => e.Contains("accent"));
    }

    [Fact]
    public void Unknown_theme_is_reported()
    {
        Assert.Contains(ConfigLoader.Parse("""{ "theme": "Neon" }""").Errors, e => e.Contains("not a theme"));
    }

    [Fact]
    public void New_tools_resolve()
    {
        var r = ConfigLoader.Parse("""{ "wheels": [ { "slices": [ { "type": "tool", "tool": "files" }, { "type": "tool", "tool": "text" }, { "type": "tool", "tool": "generate" }, { "type": "tool", "tool": "system" } ] } ] }""");
        Assert.True(r.Ok, string.Join("; ", r.Errors));
        Assert.Equal([ToolKind.Files, ToolKind.TextTools, ToolKind.Generate, ToolKind.System], r.Slices.Select(s => s.Tool));
        Assert.All(r.Slices, s => Assert.NotEqual('\uE8B9', Glyphs.For(s)));
    }
}

public class TextToolTests
{
    private static string Run(TextTools.Op op, string text, bool alt = false) => TextTools.Apply(op, text, out _, alt)!;

    [Theory]
    [InlineData("the lord of the rings", "The Lord of the Rings")]
    [InlineData("a tale: of two cities", "A Tale: Of Two Cities")]
    [InlineData("my iPhone and the WORLD", "My iPhone and the World")]
    [InlineData("don't stop", "Don't Stop")]
    public void Title_case(string input, string expected) => Assert.Equal(expected, Run(TextTools.Op.Title, input));

    [Fact]
    public void Sentence_case() => Assert.Equal("Hello there. I am here! Ok?", Run(TextTools.Op.Sentence, "HELLO THERE. i AM HERE! ok?"));

    [Fact]
    public void Clean_spaces()
    {
        Assert.Equal("a b\n\nc", Run(TextTools.Op.CleanSpaces, "  a   b \n\n\n\t c  \n\n"));
        Assert.Equal("x y\r\nz", Run(TextTools.Op.CleanSpaces, "x\u00A0 y\r\nz"));
    }

    [Fact]
    public void Json_pretty_and_minify()
    {
        Assert.Equal("{\n  \"a\": [\n    1,\n    2\n  ]\n}".ReplaceLineEndings(), Run(TextTools.Op.PrettyJson, "{\"a\":[1,2]}").ReplaceLineEndings());
        Assert.Equal("{\"a\":\"é\"}", Run(TextTools.Op.PrettyJson, "{ \"a\" : \"é\" }", alt: true));
        Assert.Null(TextTools.Apply(TextTools.Op.PrettyJson, "{nope", out var err));
        Assert.Contains("JSON", err);
    }

    [Fact]
    public void Base64_round_trip_and_url_safe()
    {
        string enc = Run(TextTools.Op.Base64Encode, "héllo ✓");
        Assert.Equal("héllo ✓", Run(TextTools.Op.Base64Decode, enc));
        Assert.Equal("hi?", Run(TextTools.Op.Base64Decode, "aGk_"));          // URL-safe alphabet, no padding
        Assert.Null(TextTools.Apply(TextTools.Op.Base64Decode, "not base64!!", out _));
        Assert.Null(TextTools.Apply(TextTools.Op.Base64Decode, Convert.ToBase64String([0xFF, 0xFE, 0x00, 0xC3]), out var e));
        Assert.Contains("binary", e);
    }

    [Fact]
    public void Url_encoding() => Assert.Equal("a b&c", Run(TextTools.Op.UrlDecode, Run(TextTools.Op.UrlEncode, "a b&c")));

    [Fact]
    public void Count_summary()
    {
        Assert.Equal("3 words · 15 characters · 2 lines", TextTools.Summary("one two\nthree  "));
        Assert.Equal("1 word · 2 characters · 1 line", TextTools.Summary("👍🏽x"));   // an emoji with skin tone is one character
    }

    [Fact]
    public void Passwords_are_strong()
    {
        for (int i = 0; i < 50; i++)
        {
            string p = TextTools.Password(20);
            Assert.Equal(20, p.Length);
            Assert.Contains(p, char.IsLower);
            Assert.Contains(p, char.IsUpper);
            Assert.Contains(p, char.IsDigit);
            Assert.Contains(p, c => !char.IsLetterOrDigit(c));
            Assert.DoesNotContain('{', p);
            Assert.DoesNotContain('l', p);   // no look-alikes
        }
    }
}

public class FileOpsTests
{
    [Theory]
    [InlineData(@"C:\a\photo.HEIC", FileKind.Image)]
    [InlineData(@"C:\a\report.docx", FileKind.WordDoc)]
    [InlineData(@"C:\a\data.csv", FileKind.Spreadsheet)]
    [InlineData(@"C:\a\deck.pptx", FileKind.Presentation)]
    [InlineData(@"C:\a\scan.pdf", FileKind.Pdf)]
    [InlineData(@"C:\a\setup.exe", FileKind.Other)]
    public void Kinds(string path, FileKind kind) => Assert.Equal(kind, FileKinds.Of(path));

    [Fact]
    public void Common_kind() =>
        Assert.Equal((FileKind.Image, FileKind.Other), (FileKinds.Common([@"a.png", @"b.JPG"]), FileKinds.Common([@"a.png", @"b.pdf"])));

    [Fact]
    public void Output_names_never_overwrite()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\p\photo.png", @"C:\p\photo (2).png", @"C:\p\photo (1280w).jpg" };
        bool Exists(string p) => taken.Contains(p);
        Assert.Equal(@"C:\p\photo.jpg", OutputNames.For(@"C:\p\photo.heic", null, "jpg", Exists));
        Assert.Equal(@"C:\p\photo (3).png", OutputNames.For(@"C:\p\photo.heic", null, "png", Exists));
        Assert.Equal(@"C:\p\photo (1280w 2).jpg", OutputNames.For(@"C:\p\photo.jpg", "1280w", ".jpg", Exists));
        // Converting to its own format must not return the input path.
        Assert.Equal(@"C:\p\shot (2).png", OutputNames.For(@"C:\p\shot.png", null, "png", _ => false));
    }

    [Theory]
    [InlineData("50%", 4000, 3000, 2000, 1500)]
    [InlineData("1280w", 4000, 3000, 1280, 960)]
    [InlineData("512", 3000, 4000, 384, 512)]
    public void Resize_targets(string spec, int w, int h, int ew, int eh) => Assert.Equal((ew, eh), ResizeSpec.Target(spec, w, h));

    [Fact]
    public void Resize_never_enlarges()
    {
        Assert.Null(ResizeSpec.Target("1920w", 800, 600));
        Assert.Null(ResizeSpec.Target("512", 300, 200));
        Assert.Null(ResizeSpec.Target("150%", 300, 200));
    }

    [Fact]
    public void Size_text() => Assert.Equal(("512 bytes", "830 KB", "12.4 MB"), (OutputNames.Size(512), OutputNames.Size(830 * 1024), OutputNames.Size((long)(12.4 * 1048576))));

    [Fact]
    public void Pdf_structure()
    {
        byte[] fakeJpeg = [0xFF, 0xD8, 0xFF, 0xD9];
        var pdf = PdfWriter.Build([new(fakeJpeg, 400, 300), new(fakeJpeg, 300, 400)], "Holiday (2026)");
        string text = Encoding.Latin1.GetString(pdf);
        Assert.StartsWith("%PDF-1.4", text);
        Assert.Contains("/Count 2", text);
        Assert.Contains("/MediaBox [0 0 842 595]", text);   // landscape image → landscape page
        Assert.Contains("/MediaBox [0 0 595 842]", text);
        Assert.Contains(@"/Title (Holiday \(2026\))", text);
        Assert.EndsWith("%%EOF\n", text);
        // Every xref offset points at "n 0 obj".
        int x = text.LastIndexOf("xref\n", StringComparison.Ordinal);
        var lines = text[x..].Split('\n').Skip(3).TakeWhile(l => l.EndsWith(" n ")).ToList();
        for (int i = 0; i < lines.Count; i++)
            Assert.StartsWith($"{i + 1} 0 obj", text[int.Parse(lines[i][..10])..]);
    }

    [Fact]
    public void Ico_header()
    {
        var ico = IcoWriter.Build([(16, new byte[10]), (256, new byte[20])]);
        Assert.Equal(new byte[] { 0, 0, 1, 0, 2, 0 }, ico[..6]);
        Assert.Equal(16, ico[6]);
        Assert.Equal(0, ico[22]);                                   // 256 is stored as 0
        Assert.Equal(6 + 32, BitConverter.ToInt32(ico, 6 + 12));   // first image right after the directory
        Assert.Equal(6 + 32 + 10 + 20, ico.Length);
    }

    private static byte[] Jpeg(params byte[][] segments)
    {
        var ms = new MemoryStream();
        ms.Write([0xFF, 0xD8]);
        foreach (var s in segments) ms.Write(s);
        ms.Write([0xFF, 0xDA, 0x00, 0x02, 0x12, 0x34, 0xFF, 0x00, 0x56, 0xFF, 0xD9]);   // SOS + data with a stuffed FF + EOI
        ms.Write([0xFF, 0xD8, 0xAA, 0xFF, 0xD9]);   // an appended second image (depth map)
        return ms.ToArray();
    }

    private static byte[] Seg(byte marker, byte[] body) => [0xFF, marker, (byte)((body.Length + 2) >> 8), (byte)(body.Length + 2), .. body];

    private static byte[] Exif(int orientation)
    {
        // "Exif\0\0" + little-endian TIFF with one IFD entry: Orientation (SHORT).
        byte[] tiff = [(byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, 1, 0, 0x12, 0x01, 3, 0, 1, 0, 0, 0, (byte)orientation, 0, 0, 0, 0, 0, 0, 0];
        return [.. "Exif\0\0"u8, .. tiff];
    }

    [Fact]
    public void Jpeg_orientation()
    {
        Assert.Equal(6, Metadata.JpegOrientation(Jpeg(Seg(0xE1, Exif(6)))));
        Assert.Equal(1, Metadata.JpegOrientation(Jpeg(Seg(0xE0, "JFIF\0"u8.ToArray()))));
    }

    [Fact]
    public void Jpeg_strip_keeps_profile_and_image_drops_the_rest()
    {
        byte[] icc = [.. "ICC_PROFILE\0"u8, 1, 1, 9, 9];
        byte[] mpf = [.. "MPF\0"u8, 7, 7];
        var input = Jpeg(Seg(0xE0, "JFIF\0"u8.ToArray()), Seg(0xE1, Exif(1)), Seg(0xE2, icc), Seg(0xE2, mpf), Seg(0xED, [1, 2]), Seg(0xFE, "hi"u8.ToArray()), Seg(0xDB, [0, 1]));
        var clean = Metadata.StripJpeg(input)!;
        var expected = new MemoryStream();
        expected.Write([0xFF, 0xD8]);
        expected.Write(Seg(0xE0, "JFIF\0"u8.ToArray()));
        expected.Write(Seg(0xE2, icc));
        expected.Write(Seg(0xDB, [0, 1]));
        expected.Write([0xFF, 0xDA, 0x00, 0x02, 0x12, 0x34, 0xFF, 0x00, 0x56, 0xFF, 0xD9]);
        Assert.Equal(expected.ToArray(), clean);
        Assert.Null(Metadata.StripJpeg([1, 2, 3]));
    }

    private static byte[] Chunk(string type, byte[] data) =>
        [(byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length, .. Encoding.ASCII.GetBytes(type), .. data, 0, 0, 0, 0];

    [Fact]
    public void Png_strip()
    {
        byte[] sig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        byte[] input = [.. sig, .. Chunk("IHDR", new byte[13]), .. Chunk("tEXt", "Author\0me"u8.ToArray()), .. Chunk("iCCP", [1]),
            .. Chunk("eXIf", [2]), .. Chunk("IDAT", [3, 4]), .. Chunk("IEND", [])];
        byte[] expected = [.. sig, .. Chunk("IHDR", new byte[13]), .. Chunk("iCCP", [1]), .. Chunk("IDAT", [3, 4]), .. Chunk("IEND", [])];
        Assert.Equal(expected, Metadata.StripPng(input));
    }
}
