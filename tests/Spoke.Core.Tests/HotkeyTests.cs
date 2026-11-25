using Spoke.Core;

namespace Spoke.Core.Tests;

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+Space", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 0x20u)]
    [InlineData("ctrl + shift + k", HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, (uint)'K')]
    [InlineData("Win+Alt+F12", HotkeyModifiers.Win | HotkeyModifiers.Alt, 0x7Bu)]
    [InlineData("Alt+`", HotkeyModifiers.Alt, 0xC0u)]
    public void Parses(string text, HotkeyModifiers mods, uint vk)
    {
        Assert.True(Hotkey.TryParse(text, out var h));
        Assert.Equal(mods, h.Modifiers);
        Assert.Equal(vk, h.VirtualKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Space")]          // no modifier
    [InlineData("Ctrl+Alt")]       // no key
    [InlineData("Ctrl+A+B")]       // two keys
    [InlineData("Ctrl+Banana")]
    public void Rejects(string text) => Assert.False(Hotkey.TryParse(text, out _));

    [Theory]
    [InlineData("Ctrl+Alt+Space")]
    [InlineData("Ctrl+Shift+Win+F5")]
    [InlineData("Alt+`")]
    public void Round_trips(string text)
    {
        Assert.True(Hotkey.TryParse(text, out var h));
        Assert.Equal(text, h.ToString());
    }
}
