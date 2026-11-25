using System.Drawing;
using Spoke.Core;

namespace Spoke.Core.Tests;

public class ColorPickerConfigTests
{
    // The user's real PowerToys settings shape (trimmed).
    private const string PowerToysJson = """
        {"properties":{"copiedcolorrepresentation":"HEX",
          "visiblecolorformats":{"HEX":{"Key":true,"Value":"%Rex%Grx%Blx"},"RGB":{"Key":true,"Value":"rgb(%Re, %Gr, %Bl)"},
                                 "HSL":{"Key":true,"Value":"hsl(%Hu, %Sl%, %Ll%)"},"CMYK":{"Key":false,"Value":""}}},
         "name":"ColorPicker","version":"2.1"}
        """;

    [Fact]
    public void ReadsPowerToysVisibleFormatsAndCopyFormat()
    {
        var cfg = ColorPickerConfig.FromPowerToysJson(PowerToysJson);
        Assert.Equal("PowerToys", cfg.Source);
        Assert.Equal(["HEX", "RGB", "HSL"], cfg.Formats.Select(f => f.Name));
        Assert.Equal("HEX", cfg.Copy.Name);
    }

    [Theory]
    [InlineData("HEX", "3b82f6")]
    [InlineData("RGB", "rgb(59, 130, 246)")]
    [InlineData("HSL", "hsl(217, 91%, 60%)")]
    public void RendersLikePowerToys(string name, string expected)
    {
        var cfg = ColorPickerConfig.FromPowerToysJson(PowerToysJson);
        var c = Color.FromArgb(255, 0x3B, 0x82, 0xF6);
        Assert.Equal(expected, cfg.Formats.First(f => f.Name == name).Render(c));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"properties\":{\"visiblecolorformats\":{}}}")]
    public void FallsBackToDefaults(string json)
    {
        var cfg = ColorPickerConfig.FromPowerToysJson(json);
        Assert.Equal("default", cfg.Source);
        Assert.Equal("HEX", cfg.Copy.Name);
    }

    [Fact]
    public void UnknownCopyFormatUsesFirstVisible()
    {
        var cfg = ColorPickerConfig.FromPowerToysJson(PowerToysJson.Replace("\"copiedcolorrepresentation\":\"HEX\"", "\"copiedcolorrepresentation\":\"CMYK\""));
        Assert.Equal("HEX", cfg.Copy.Name);
    }
}

public class ColorHistoryTests
{
    [Fact]
    public void NewestFirstDedupedAndCapped()
    {
        var h = new ColorHistory();
        for (int i = 0; i < 25; i++) h.Add(Color.FromArgb(255, i, 0, 0));
        h.Add(Color.FromArgb(255, 10, 0, 0));
        Assert.Equal(ColorHistory.Limit, h.Items.Count);
        Assert.Equal(10, h.Items[0].R);
        Assert.Single(h.Items, c => c.R == 10);
    }

    [Fact]
    public void SeedsFromPowerToysThenUsesOwnFile()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        string pt = Path.Combine(dir, "colorHistory.json"), own = Path.Combine(dir, "colors.json");
        File.WriteAllText(pt, "[\"255|65|65|67\",\"bad\",\"255|116|92|166\"]");

        var h = new ColorHistory(own);
        h.Load(pt);
        Assert.Equal([65, 116], h.Items.Select(c => (int)c.R));

        h.Add(Color.Red);
        h.Save();
        var h2 = new ColorHistory(own);
        h2.Load(pt);
        Assert.Equal(Color.Red.ToArgb(), h2.Items[0].ToArgb());
        Assert.Equal(3, h2.Items.Count);
    }
}

public class ClipStoreTests
{
    private sealed class XorProtector : IBlobProtector
    {
        public byte[] Protect(byte[] d) => d.Select(b => (byte)(b ^ 0x5A)).ToArray();
        public byte[] Unprotect(byte[] d) => Protect(d);
    }

    private static ClipStore NewStore(out string dir, int limit = 40)
    {
        dir = Directory.CreateTempSubdirectory().FullName;
        return new ClipStore(dir, new XorProtector(), limit);
    }

    [Fact]
    public void AddsNewestFirstAndRoundTrips()
    {
        var s = NewStore(out _);
        s.Add([1, 2, 3], [9], 10, 20, 0xAA, 1000);
        var e = s.Add([4, 5], [8], 30, 40, 0xBB, 2000);
        Assert.Equal([0xBBUL, 0xAAUL], s.Entries.Select(x => x.Hash));
        Assert.Equal([4, 5], s.ReadImage(e));
        Assert.Equal([8], s.ReadThumb(e));
    }

    [Fact]
    public void StoresEncryptedNotPlain()
    {
        var s = NewStore(out string dir);
        s.Add([1, 2, 3], [9], 1, 1, 1, 1000);
        var raw = File.ReadAllBytes(Directory.GetFiles(dir, "*.img").Single());
        Assert.NotEqual([1, 2, 3], raw);
    }

    [Fact]
    public void SameImageMovesToTopInsteadOfDuplicating()
    {
        var s = NewStore(out string dir);
        s.Add([1], [1], 1, 1, 0xAA, 1000);
        s.Add([2], [2], 1, 1, 0xBB, 2000);
        s.Add([1], [1], 1, 1, 0xAA, 3000);
        Assert.Equal([0xAAUL, 0xBBUL], s.Entries.Select(x => x.Hash));
        Assert.Equal(3000, s.Entries[0].Time);
        Assert.Equal(2, Directory.GetFiles(dir, "*.img").Length);
    }

    [Fact]
    public void PrunesOldestBeyondLimitAndReloadsFromDisk()
    {
        var s = NewStore(out string dir, limit: 3);
        for (int i = 0; i < 5; i++) s.Add([(byte)i], [(byte)i], 1, 1, (ulong)i, 1000 + i);
        Assert.Equal([4UL, 3UL, 2UL], s.Entries.Select(x => x.Hash));
        Assert.Equal(3, Directory.GetFiles(dir, "*.thb").Length);

        var reopened = new ClipStore(dir, new XorProtector(), 3);
        Assert.Equal([4UL, 3UL, 2UL], reopened.Entries.Select(x => x.Hash));
        Assert.Equal([3], reopened.ReadImage(reopened.Entries[1]));
    }

    [Fact]
    public void RemoveAndClear()
    {
        var s = NewStore(out string dir);
        var a = s.Add([1], [1], 1, 1, 1, 1000);
        s.Add([2], [2], 1, 1, 2, 2000);
        s.Remove(a);
        Assert.Single(s.Entries);
        s.Clear();
        Assert.Empty(s.Entries);
        Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact]
    public void IdRoundTrips()
    {
        var e = new ClipEntry(1764000000000, 0x0123456789abcdef, 1920, 1080);
        Assert.True(ClipEntry.TryParse(e.Id, out var back));
        Assert.Equal(e, back);
        Assert.False(ClipEntry.TryParse("junk", out _));
    }

    [Fact]
    public void HashDependsOnPixelsAndSize()
    {
        byte[] p = [1, 2, 3, 4, 5, 6, 7, 8, 9];
        Assert.Equal(ClipStore.HashPixels(p, 3, 3), ClipStore.HashPixels(p, 3, 3));
        Assert.NotEqual(ClipStore.HashPixels(p, 3, 3), ClipStore.HashPixels(p, 9, 1));
        byte[] q = (byte[])p.Clone(); q[8] = 0;
        Assert.NotEqual(ClipStore.HashPixels(p, 3, 3), ClipStore.HashPixels(q, 3, 3));
    }

    [Theory]
    [InlineData(10_000, "now")]
    [InlineData(150_000, "3 min")]
    [InlineData(7_200_000, "2 h")]
    [InlineData(200_000_000, "2 d")]
    public void AgeText(long ago, string expected) => Assert.Equal(expected, ClipStore.Age(1_000_000_000 - ago, 1_000_000_000));

    [Theory]
    [InlineData(false, false, null, false, true)]
    [InlineData(true, false, null, false, false)]   // paused
    [InlineData(false, true, null, false, false)]   // password manager
    [InlineData(false, false, 0u, false, false)]    // app opted out of history
    [InlineData(false, false, 1u, false, true)]
    [InlineData(false, false, null, true, false)]   // viewer ignore
    public void Policy(bool paused, bool exclude, uint? canInclude, bool ignore, bool expected) =>
        Assert.Equal(expected, ClipboardPolicy.MayStore(paused, exclude, canInclude, ignore));
}
