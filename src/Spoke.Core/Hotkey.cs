namespace Spoke.Core;

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x1,
    Ctrl = 0x2,
    Shift = 0x4,
    Win = 0x8,
}

/// <summary>A global shortcut such as "Ctrl+Alt+Space". Modifier values match Win32 MOD_* flags.</summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, uint VirtualKey)
{
    public static bool TryParse(string text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var mods = HotkeyModifiers.None;
        uint vk = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= HotkeyModifiers.Ctrl; break;
                case "alt": mods |= HotkeyModifiers.Alt; break;
                case "shift": mods |= HotkeyModifiers.Shift; break;
                case "win" or "windows": mods |= HotkeyModifiers.Win; break;
                default:
                    if (vk != 0) return false;          // two non-modifier keys
                    vk = KeyFromName(raw);
                    if (vk == 0) return false;
                    break;
            }
        }
        if (vk == 0 || mods == HotkeyModifiers.None) return false;   // a bare key would steal normal typing
        hotkey = new Hotkey(mods, vk);
        return true;
    }

    private static uint KeyFromName(string name)
    {
        string n = name.ToUpperInvariant();
        if (n.Length == 1 && n[0] is >= 'A' and <= 'Z' or >= '0' and <= '9') return n[0];
        if (n.Length is 2 or 3 && n[0] == 'F' && int.TryParse(n.AsSpan(1), out int f) && f is >= 1 and <= 24) return (uint)(0x70 + f - 1);
        return n switch
        {
            "SPACE" => 0x20,
            "`" or "BACKTICK" or "TILDE" => 0xC0,
            "TAB" => 0x09,
            "ENTER" => 0x0D,
            _ => 0,
        };
    }

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(VirtualKey switch
        {
            0x20 => "Space",
            0xC0 => "`",
            0x09 => "Tab",
            0x0D => "Enter",
            >= 0x70 and <= 0x87 => "F" + (VirtualKey - 0x70 + 1),
            _ => ((char)VirtualKey).ToString(),
        });
        return string.Join('+', parts);
    }
}
