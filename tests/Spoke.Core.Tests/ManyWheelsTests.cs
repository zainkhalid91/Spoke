using Spoke.Core;

namespace Spoke.Core.Tests;

public class ManyWheelsTests
{
    private const string Two = """
    {
      "hotkey": "Ctrl+Alt+Z",
      "wheels": [
        { "name": "Dev", "slices": [ { "type": "app", "target": "code.exe" }, { "type": "url", "target": "github.com" } ] },
        { "name": "Web", "hotkey": "Ctrl+Alt+B", "slices": [ { "type": "url", "target": "a.com" }, { "type": "url", "target": "b.com" } ] }
      ]
    }
    """;

    [Fact]
    public void Every_wheel_is_read_with_its_shortcut()
    {
        var r = ConfigLoader.Parse(Two);
        Assert.Empty(r.Errors);
        Assert.Equal(["Dev", "Web"], r.Wheels.Select(w => w.Name));
        Assert.Equal("Ctrl+Alt+Z", r.Wheels[0].Hotkey);     // the first wheel takes the top-level shortcut
        Assert.Equal("Ctrl+Alt+B", r.Wheels[1].Hotkey);
        Assert.Equal("Dev", r.WheelName);
        Assert.Same(r.Wheels[0].Slices, r.Slices);
    }

    [Fact]
    public void Errors_name_the_wheel_when_there_are_several()
    {
        var r = ConfigLoader.Parse(Two.Replace("\"b.com\"", "\"\""));
        Assert.Contains(r.Errors, e => e.StartsWith("Wheel 2 (Web): Slice 2"));
    }

    [Fact]
    public void Two_wheels_cannot_share_a_name_or_a_shortcut()
    {
        Assert.Contains(ConfigLoader.Parse(Two.Replace("\"Web\"", "\"dev\"")).Errors, e => e.Contains("already called"));
        Assert.Contains(ConfigLoader.Parse(Two.Replace("Ctrl+Alt+B", "Ctrl+Alt+Z")).Errors, e => e.Contains("already opens"));
    }

    [Fact]
    public void A_bad_wheel_shortcut_is_reported()
        => Assert.Contains(ConfigLoader.Parse(Two.Replace("Ctrl+Alt+B", "Banana")).Errors, e => e.Contains("Banana"));

    [Fact]
    public void Unnamed_wheels_get_a_name()
    {
        var r = ConfigLoader.Parse(Two.Replace("\"name\": \"Web\", ", ""));
        Assert.Equal("Wheel 2", r.Wheels[1].Name);
    }

    // The group plus a plain slice, so the wheel has the two slices it needs.
    private static Slice? Group(string json, out string? error)
    {
        var r = ConfigLoader.Parse(json);
        error = r.Errors.FirstOrDefault();
        return r.Slices.FirstOrDefault(s => s.Kind == SliceKind.Group);
    }

    private static string OneSlice(string slice) => $$"""{ "wheels": [ { "slices": [ {{slice}}, { "type": "url", "target": "x.com" } ] } ] }""";

    [Fact]
    public void A_group_holds_its_children()
    {
        var g = Group(OneSlice("""{ "type": "group", "label": "Dev", "children": [ { "type": "url", "target": "github.com" }, { "type": "search", "engine": "google" } ] }"""), out var e);
        Assert.Null(e);
        Assert.Equal(SliceKind.Group, g!.Kind);
        Assert.Equal(2, g.Children.Count);
        Assert.False(g.RunAll);
        Assert.Equal("github.com · Search", g.Detail);
    }

    [Fact]
    public void Run_all_keeps_its_delay_and_refuses_interactive_children()
    {
        var g = Group(OneSlice("""{ "type": "group", "runAll": true, "delay": 800, "children": [ { "type": "url", "target": "a.com" }, { "type": "app", "target": "x.exe" } ] }"""), out var e);
        Assert.Null(e);
        Assert.True(g!.RunAll);
        Assert.Equal(800, g.DelayMs);

        Group(OneSlice("""{ "type": "group", "runAll": true, "children": [ { "type": "search", "engine": "google" } ] }"""), out e);
        Assert.Contains("runAll", e);
    }

    [Theory]
    [InlineData("""{ "type": "group", "children": [] }""", "1–12")]
    [InlineData("""{ "type": "group", "children": [ { "type": "group", "children": [ { "type": "url", "target": "a.com" } ] } ] }""", "another group")]
    [InlineData("""{ "type": "group", "children": [ { "type": "url" } ] }""", "child 1")]
    [InlineData("""{ "type": "group", "runAll": true, "delay": 99999, "children": [ { "type": "url", "target": "a.com" } ] }""", "delay")]
    public void Bad_groups_are_explained(string slice, string expected)
    {
        Group(OneSlice(slice), out var e);
        Assert.Contains(expected, e);
    }
}

public class WheelFileTests
{
    private static WheelConfig Sample() => new()
    {
        Name = "Dev",
        Hotkey = "Ctrl+Alt+D",
        Slices =
        [
            new() { Type = "app", Label = "Code", Target = @"C:\Users\pat\AppData\Local\Programs\VS Code\Code.exe", WorkDir = @"C:\Users\pat\src" },
            new() { Type = "url", Target = "https://github.com" },
            new() { Type = "group", Label = "More", Children = [new() { Type = "folder", Target = @"C:\Users\pat\Downloads" }] },
        ],
    };

    private static string? Env(string name) => name switch
    {
        "USERPROFILE" => @"C:\Users\pat",
        "LOCALAPPDATA" => @"C:\Users\pat\AppData\Local",
        "APPDATA" => @"C:\Users\pat\AppData\Roaming",
        _ => null,
    };

    [Theory]
    [InlineData(@"C:\Users\pat\AppData\Local\Programs\x.exe", @"%LOCALAPPDATA%\Programs\x.exe")]
    [InlineData(@"C:\Users\pat\Downloads", @"%USERPROFILE%\Downloads")]
    [InlineData(@"c:\users\PAT", "%USERPROFILE%")]
    [InlineData(@"D:\Projects", @"D:\Projects")]
    [InlineData(@"%USERPROFILE%\x", @"%USERPROFILE%\x")]
    [InlineData(@"C:\Users\patrick\x", @"C:\Users\patrick\x")]   // only whole folder names match
    public void Paths_become_portable(string path, string expected) => Assert.Equal(expected, WheelFile.Portable(path, Env));

    [Fact]
    public void Export_then_import_round_trips_without_the_shortcut()
    {
        string file = WheelFile.Export(Sample());
        Assert.Contains("\"spoke\": 1", file);
        Assert.DoesNotContain("Ctrl+Alt+D", file);
        var back = WheelFile.Import(file, ["Spoke"], out var error);
        Assert.Null(error);
        Assert.Equal("Dev", back!.Name);
        Assert.Null(back.Hotkey);
        Assert.Equal(3, back.Slices!.Count);
        Assert.Single(back.Slices[2].Children!);
    }

    [Fact]
    public void Import_picks_a_free_name()
    {
        var back = WheelFile.Import(WheelFile.Export(Sample()), ["dev", "Dev 2"], out _);
        Assert.Equal("Dev 3", back!.Name);
    }

    [Theory]
    [InlineData("not json", "isn't valid JSON")]
    [InlineData("""{ "spoke": 1 }""", "no \"wheel\"")]
    [InlineData("""{ "spoke": 9, "wheel": { "slices": [] } }""", "newer Spoke")]
    [InlineData("""{ "spoke": 1, "wheel": { "slices": [ { "type": "url" } ] } }""", "slices")]
    public void Import_refuses_bad_files(string json, string expected)
    {
        Assert.Null(WheelFile.Import(json, [], out var error));
        Assert.Contains(expected, error);
    }

    [Fact]
    public void Missing_lists_full_paths_that_do_not_exist()
    {
        var missing = WheelFile.Missing(Sample(), p => p.EndsWith("Downloads"));
        Assert.Single(missing);
        Assert.StartsWith("Code:", missing[0]);
        // Bare exe names are found on PATH at run time, so they aren't checked.
        Assert.Empty(WheelFile.Missing(new WheelConfig { Slices = [new() { Type = "app", Target = "wt.exe" }] }, _ => false));
    }
}
