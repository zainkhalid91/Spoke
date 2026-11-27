using System.Text.Json.Nodes;
using Spoke.Core;
using Xunit;

namespace Spoke.Core.Tests;

public class WithConfigTests
{
    [Fact]
    public void Replaces_known_keys_keeps_unknown_ones()
    {
        const string file = """
        {
          // a comment
          "Theme": "Ember",
          "myNote": "keep me",
          "size": "L",
          "wheels": [ { "name": "Old", "slices": [ { "type": "tool", "tool": "clips" }, { "type": "tool", "tool": "screenshot" } ] } ],
        }
        """;
        var c = ConfigLoader.Parse(file).Config!;
        c.Theme = "Paper";
        c.Size = null;                          // removed
        c.Hotkey = "Ctrl+Alt+Q";                // added
        c.Wheels![0].Name = "New";

        var root = JsonNode.Parse(ConfigLoader.WithConfig(file, c))!.AsObject();
        Assert.Equal("Paper", (string?)root["theme"]);
        Assert.False(root.ContainsKey("Theme"));          // no duplicate key with the old casing
        Assert.False(root.ContainsKey("size"));
        Assert.Equal("Ctrl+Alt+Q", (string?)root["hotkey"]);
        Assert.Equal("keep me", (string?)root["myNote"]);
        Assert.Equal("New", (string?)root["wheels"]![0]!["name"]);

        var again = ConfigLoader.Parse(ConfigLoader.WithConfig(file, c));
        Assert.True(again.Ok);
        Assert.Equal(2, again.Slices.Count);
    }

    [Fact]
    public void Replaces_a_file_that_is_not_json()
    {
        var c = ConfigLoader.Default();
        var r = ConfigLoader.Parse(ConfigLoader.WithConfig("{ oops", c));
        Assert.True(r.Ok);
        Assert.Equal(12, r.Slices.Count);
    }

    [Fact]
    public void Clone_is_deep()
    {
        var a = ConfigLoader.Default();
        var b = ConfigLoader.Clone(a);
        b.Wheels![0].Slices![0].Label = "changed";
        Assert.Null(a.Wheels![0].Slices![0].Label);
    }
}

public class HotkeyRecordingTests
{
    [Theory]
    [InlineData(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 'Q', "Ctrl+Alt+Q")]
    [InlineData(HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, 0x20, "Ctrl+Shift+Space")]
    [InlineData(HotkeyModifiers.Alt, 0x71, "Alt+F2")]
    public void FromKeys_builds_parseable_shortcuts(HotkeyModifiers mods, uint vk, string expected)
    {
        var hk = Hotkey.FromKeys(mods, vk);
        Assert.NotNull(hk);
        Assert.Equal(expected, hk.Value.ToString());
        Assert.True(Hotkey.TryParse(expected, out var back));
        Assert.Equal(hk.Value, back);
    }

    [Theory]
    [InlineData(HotkeyModifiers.None, 'Q')]          // no modifier
    [InlineData(HotkeyModifiers.Ctrl, 0x2E)]         // Delete: not a key Spoke can store
    [InlineData(HotkeyModifiers.Ctrl, 0x1B)]         // Esc
    public void FromKeys_rejects(HotkeyModifiers mods, uint vk) => Assert.Null(Hotkey.FromKeys(mods, vk));

    [Theory]
    [InlineData("Ctrl+C", true)]
    [InlineData("Win+Q", true)]
    [InlineData("Alt+F4", true)]
    [InlineData("Shift+Space", true)]
    [InlineData("Ctrl+Alt+Space", false)]
    [InlineData("Ctrl+Shift+Alt+Z", false)]
    public void Warns_about_risky_shortcuts(string text, bool warns)
    {
        Assert.True(Hotkey.TryParse(text, out var hk));
        Assert.Equal(warns, hk.Warning() != null);
    }

    [Fact]
    public void Suggestions_are_valid_and_safe()
    {
        foreach (var s in Hotkey.Suggestions)
        {
            Assert.True(Hotkey.TryParse(s, out var hk), s);
            Assert.Null(hk.Warning());
        }
    }
}

public class IpcFramingTests
{
    [Fact]
    public void Request_and_reply_round_trip()
    {
        using var ms = new MemoryStream();
        Ipc.WriteRequest(ms, new IpcRequest { Cmd = "autostart", Arg = "on" });
        Ipc.WriteReply(ms, new IpcReply { Ok = true, Hotkey = "Ctrl+Alt+Space", Errors = ["x"], ApplyMs = 3.5 });
        ms.Position = 0;
        var req = Ipc.ReadRequest(ms)!;
        Assert.Equal("autostart", req.Cmd);
        Assert.Equal("on", req.Arg);
        var rep = Ipc.ReadReply(ms)!;
        Assert.True(rep.Ok);
        Assert.Equal("Ctrl+Alt+Space", rep.Hotkey);
        Assert.Equal(["x"], rep.Errors);
        Assert.Equal(3.5, rep.ApplyMs);
        Assert.Null(Ipc.ReadFrame(ms));   // end of stream
    }

    [Fact]
    public void Rejects_oversized_and_truncated_frames()
    {
        using var big = new MemoryStream([0xFF, 0xFF, 0xFF, 0x7F, 1, 2, 3]);
        Assert.Null(Ipc.ReadFrame(big));
        using var cut = new MemoryStream([10, 0, 0, 0, 1, 2]);
        Assert.Null(Ipc.ReadFrame(cut));
    }

    [Fact]
    public void Pipe_name_is_per_user()
    {
        Assert.StartsWith("Spoke-", Ipc.PipeName);
        Assert.True(Ipc.PipeName.Length > "Spoke-".Length);
    }
}
