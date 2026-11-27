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

    /// <summary>Keys a shortcut can end with (what <see cref="TryParse"/> and <see cref="ToString"/> understand).</summary>
    public static bool IsSupportedKey(uint vk) =>
        vk is >= 'A' and <= 'Z' or >= '0' and <= '9' or >= 0x70 and <= 0x87 or 0x20 or 0xC0 or 0x09 or 0x0D;

    /// <summary>From a key press while recording. Null when the key or the modifiers can't make a shortcut.</summary>
    public static Hotkey? FromKeys(HotkeyModifiers mods, uint vk) =>
        mods != HotkeyModifiers.None && IsSupportedKey(vk) ? new Hotkey(mods, vk) : null;

    /// <summary>
    /// Why this combination is a poor choice even if it's free, or null. Windows keeps most Win+ shortcuts for
    /// itself (some can't be registered at all), and Ctrl+letter is the everyday shortcut of nearly every app.
    /// </summary>
    public string? Warning()
    {
        var m = Modifiers;
        char k = (char)VirtualKey;
        if (m == HotkeyModifiers.Alt && VirtualKey is 0x09 or 0x73) return "Windows uses this to switch or close windows.";
        if (m.HasFlag(HotkeyModifiers.Win)) return "Windows reserves most Win shortcuts; this may stop working after an update.";
        if (m == HotkeyModifiers.Ctrl && k is 'A' or 'C' or 'V' or 'X' or 'Z' or 'Y' or 'S' or 'F' or 'P' or 'N' or 'O' or 'W' or 'T')
            return $"Ctrl+{k} is an everyday shortcut in most apps; Spoke would take it from all of them.";
        if (m == HotkeyModifiers.Ctrl || m == HotkeyModifiers.Shift || m == HotkeyModifiers.Alt)
            return "Shortcuts with a single modifier often clash with apps. Ctrl+Alt+… is safer.";
        if (m == (HotkeyModifiers.Ctrl | HotkeyModifiers.Shift) && VirtualKey == 0x1B) return "Windows uses this for Task Manager.";
        return null;
    }

    /// <summary>Shortcuts worth suggesting when the chosen one is taken, best first.</summary>
    public static readonly string[] Suggestions =
        ["Ctrl+Alt+Space", "Ctrl+Alt+Z", "Ctrl+Alt+Q", "Ctrl+Alt+`", "Ctrl+Shift+Alt+Space", "Ctrl+Alt+F1", "Ctrl+Shift+Space", "Ctrl+Alt+W"];

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
