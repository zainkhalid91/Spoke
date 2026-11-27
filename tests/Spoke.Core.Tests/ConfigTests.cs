using Spoke.Core;

namespace Spoke.Core.Tests;

public class ConfigTests
{
    [Fact]
    public void Empty_file_gives_the_default_wheel()
    {
        var r = ConfigLoader.Parse("");
        Assert.True(r.Ok);
        Assert.True(r.UsedDefaultWheel);
        Assert.Equal(12, r.Slices.Count);
        Assert.Equal("Spoke", r.WheelName);
        Assert.Equal([SliceKind.Tool, SliceKind.Tool, SliceKind.Tool, SliceKind.Tool, SliceKind.App, SliceKind.Folder, SliceKind.Search, SliceKind.Url, SliceKind.Text,
            SliceKind.Tool, SliceKind.Tool, SliceKind.Tool], r.Slices.Select(s => s.Kind));
    }

    [Fact]
    public void Theme_only_file_from_m1a_still_loads_and_keeps_theme()
    {
        var r = ConfigLoader.Parse("{ \"theme\": \"Ember\" }");
        Assert.True(r.Ok);
        Assert.True(r.UsedDefaultWheel);
        Assert.Equal("Ember", r.Config!.Theme);

        string upgraded = ConfigLoader.WithDefaultWheel("{ \"theme\": \"Ember\" }");
        var again = ConfigLoader.Parse(upgraded);
        Assert.False(again.UsedDefaultWheel);
        Assert.Equal("Ember", again.Config!.Theme);
        Assert.Equal(12, again.Slices.Count);
    }

    [Fact]
    public void Default_config_round_trips()
    {
        string json = ConfigLoader.Serialize(ConfigLoader.Default());
        Assert.Contains("\"wheels\"", json);
        Assert.DoesNotContain("null", json);
        Assert.Contains("today's", json);   // no ' escapes in a hand-edited file
        var r = ConfigLoader.Parse(json);
        Assert.True(r.Ok, string.Join("; ", r.Errors));
        Assert.Equal(12, r.Slices.Count);
    }

    [Fact]
    public void Comments_and_trailing_commas_are_allowed()
    {
        const string json = """
        {
          // my wheel
          "wheels": [ { "name": "Dev", "slices": [
            { "type": "app", "target": "code.exe", },
            { "type": "url", "target": "github.com" },
          ] } ],
        }
        """;
        var r = ConfigLoader.Parse(json);
        Assert.True(r.Ok, string.Join("; ", r.Errors));
        Assert.Equal("Dev", r.WheelName);
        Assert.Equal("Code", r.Slices[0].Label);
        Assert.Equal("App · code.exe", r.Slices[0].Detail);
        Assert.Equal("https://github.com", r.Slices[1].Target);
        Assert.Equal("github.com", r.Slices[1].Label);
    }

    [Fact]
    public void Bad_json_reports_the_line()
    {
        var r = ConfigLoader.Parse("{\n  \"theme\": \"Aurora\"\n  \"hotkey\": 1\n}");
        Assert.False(r.Ok);
        Assert.Contains("near line 3", r.Errors[0]);
        Assert.Contains("missing comma", r.Errors[0]);
    }

    [Theory]
    [InlineData("""{ "type": "app" }""", "needs a \"target\"")]
    [InlineData("""{ "type": "rocket" }""", "unknown")]
    [InlineData("""{ "label": "x" }""", "\"type\" is missing")]
    [InlineData("""{ "type": "tool", "tool": "laser" }""", "screenshot, colorPicker, clips")]
    [InlineData("""{ "type": "folder", "target": "C:\\x", "open": "teleport" }""", "explorer, terminal or copyPath")]
    [InlineData("""{ "type": "search", "engine": "askjeeves" }""", "{query}")]
    [InlineData("""{ "type": "text" }""", "needs some \"text\"")]
    [InlineData("""{ "type": "url", "target": "not a url" }""", "not a web address")]
    public void Invalid_slices_explain_themselves(string slice, string expected)
    {
        var r = ConfigLoader.Parse($$"""{ "wheels": [ { "slices": [ {{slice}}, { "type": "url", "target": "a.com" } ] } ] }""");
        Assert.False(r.Ok);
        Assert.Contains(expected, r.Errors[0]);
        Assert.StartsWith("Slice 1", r.Errors[0]);
    }

    [Fact]
    public void Slice_count_is_limited()
    {
        var r = ConfigLoader.Parse("""{ "wheels": [ { "slices": [ { "type": "url", "target": "a.com" } ] } ] }""");
        Assert.Contains("2–12 slices", r.Errors[0]);
    }

    [Fact]
    public void Bad_hotkey_is_an_error()
    {
        var r = ConfigLoader.Parse("""{ "hotkey": "Ctrl+Banana" }""");
        Assert.Contains("hotkey", r.Errors[0]);
    }

    [Fact]
    public void Folder_modes_and_details()
    {
        var r = ConfigLoader.Parse("""
        { "wheels": [ { "slices": [
          { "type": "folder", "target": "%USERPROFILE%\\Downloads" },
          { "type": "folder", "target": "D:\\Projects\\Spoke", "open": "terminal" },
          { "type": "folder", "target": "C:\\", "open": "copy-path" }
        ] } ] }
        """);
        Assert.True(r.Ok, string.Join("; ", r.Errors));
        Assert.Equal(("Downloads", "Folder · Downloads"), (r.Slices[0].Label, r.Slices[0].Detail));
        Assert.Equal(FolderOpen.Terminal, r.Slices[1].Open);
        Assert.Equal("Terminal in Spoke", r.Slices[1].Detail);
        Assert.Equal(FolderOpen.CopyPath, r.Slices[2].Open);
        Assert.Equal("C:", r.Slices[2].Label);
    }

    [Fact]
    public void Custom_search_template()
    {
        var r = ConfigLoader.Parse("""{ "wheels": [ { "slices": [ { "type": "search", "engine": "https://www.npmjs.com/search?q={query}" }, { "type": "search" } ] } ] }""");
        Assert.True(r.Ok, string.Join("; ", r.Errors));
        Assert.Equal("Search npmjs.com", r.Slices[0].Detail);
        Assert.Equal("Search Google", r.Slices[1].Detail);
    }

    [Fact]
    public void WithValue_updates_one_key_and_keeps_the_rest()
    {
        const string json = """{ "Theme": "Aurora", "hotkey": "Ctrl+Alt+Z", "wheels": [] }""";
        string updated = ConfigLoader.WithValue(json, "theme", "Paper");
        var r = ConfigLoader.Parse(updated);
        Assert.Equal("Paper", r.Config!.Theme);
        Assert.Equal("Ctrl+Alt+Z", r.Config.Hotkey);
        Assert.DoesNotContain("Aurora", updated);
    }

    [Fact]
    public void WithValue_leaves_a_broken_file_alone()
    {
        const string broken = "{ \"theme\": ";
        Assert.Equal(broken, ConfigLoader.WithValue(broken, "theme", "Paper"));
    }

    [Theory]
    [InlineData("terminal", '\uE756')]
    [InlineData("E943", '\uE943')]
    [InlineData("U+E774", '\uE774')]
    [InlineData("nonsense", '\uE721')]   // falls back to the kind's glyph (search)
    public void Glyph_names_and_code_points(string icon, char expected)
    {
        var s = new Slice(SliceKind.Search, "S", "d", icon);
        Assert.Equal(expected, Glyphs.For(s));
    }
}

public class TokenTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 14, 5, 9, TimeSpan.FromHours(5));

    [Theory]
    [InlineData("{date}", "2026-11-25")]
    [InlineData("{time}", "14:05")]
    [InlineData("{iso}", "2026-11-25T14:05:09+05:00")]
    [InlineData("{unix}", "1795597509")]
    [InlineData("{date:dd/MM/yyyy}", "25/11/2026")]
    [InlineData("Hi {DATE}!", "Hi 2026-11-25!")]
    [InlineData("{{date}}", "{date}")]
    [InlineData("{nope} stays", "{nope} stays")]
    [InlineData("open { brace", "open { brace")]
    [InlineData("{uuid}", "00000000-0000-0000-0000-000000000001")]
    [InlineData("[{clipboard}]", "[copied]")]
    public void Expands(string text, string expected)
    {
        var guid = Guid.Parse("00000000-0000-0000-0000-000000000001");
        Assert.Equal(expected, Tokens.Expand(text, Now, () => "copied", () => guid));
    }

    [Fact]
    public void Clipboard_is_read_only_when_used()
    {
        int reads = 0;
        Tokens.Expand("{date}", Now, () => { reads++; return "x"; });
        Assert.Equal(0, reads);
        Assert.True(Tokens.UsesClipboard("a {Clipboard} b"));
    }
}

public class SearchTests
{
    [Fact]
    public void Builds_an_escaped_url()
    {
        Assert.True(SearchEngines.TryResolve("youtube", out var t, out var name));
        Assert.Equal("YouTube", name);
        Assert.Equal("https://www.youtube.com/results?search_query=lo-fi%20%26%20rain", SearchEngines.Url(t, "  lo-fi & rain "));
    }

    [Fact]
    public void Rejects_non_http_templates()
    {
        Assert.False(SearchEngines.TryResolve("file:///C:/{query}", out _, out _));
        Assert.False(SearchEngines.TryResolve("javascript:{query}", out _, out _));
    }
}
