using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Spoke.Daemon;

/// <summary>
/// The low-level keyboard hook and foreground watcher that exist only while a Spoke overlay (wheel or
/// capture) is open. The hook swallows just the keys the current overlay uses and passes everything else
/// straight through. The callback only posts a message: no allocation, no locks, so it never risks
/// Windows' LowLevelHooksTimeout even under heavy load.
/// </summary>
internal static unsafe class KeyHook
{
    public const uint WM_APP_KEY = PInvoke.WM_APP + 1;   // wParam = vk
    public const uint WM_APP_FG = PInvoke.WM_APP + 2;    // foreground window changed

    private static HHOOK _hook;
    private static HWINEVENTHOOK _fg;
    private static HWND _target;
    private static readonly bool* Swallow = (bool*)NativeMemory.AllocZeroed(256);
    public static long LastKeyTicks;

    public static bool Installed => !_hook.IsNull;

    /// <summary>
    /// Typing mode (the wheel's search box): every key except modifiers and lock keys is swallowed and posted
    /// with its scan code (lParam), so the overlay can turn it into text without ever taking focus.
    /// Modifiers pass through so Shift/AltGr/Caps state stays real for ToUnicodeEx.
    /// </summary>
    public static volatile bool TextMode;

    public static void Install(HWND target, ReadOnlySpan<VIRTUAL_KEY> keys)
    {
        SetKeys(keys);
        _target = target;
        if (_hook.IsNull) _hook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_KEYBOARD_LL, &Hook, HINSTANCE.Null, 0);
        if (_fg.IsNull) _fg = PInvoke.SetWinEventHook(PInvoke.EVENT_SYSTEM_FOREGROUND, PInvoke.EVENT_SYSTEM_FOREGROUND, HMODULE.Null, &Foreground, 0, 0, PInvoke.WINEVENT_OUTOFCONTEXT);
    }

    /// <summary>Changes the swallowed keys without reinstalling (e.g. switching into the Clips wheel).</summary>
    public static void SetKeys(ReadOnlySpan<VIRTUAL_KEY> keys)
    {
        NativeMemory.Clear(Swallow, 256);
        foreach (var k in keys) Swallow[(byte)k] = true;
    }

    public static void Uninstall()
    {
        if (!_hook.IsNull) { PInvoke.UnhookWindowsHookEx(_hook); _hook = default; }
        if (!_fg.IsNull) { PInvoke.UnhookWinEvent(_fg); _fg = default; }
        _target = HWND.Null;
        TextMode = false;
    }

    private static bool IsModifier(uint vk) => vk is
        0x10 or 0x11 or 0x12 or (>= 0xA0 and <= 0xA5) or   // Shift, Ctrl, Alt (and left/right)
        0x5B or 0x5C or                                    // Win
        0x14 or 0x90 or 0x91;                              // Caps, Num, Scroll lock

    /// <summary>
    /// The text a key produces with the current keyboard layout and modifier state (empty for keys that
    /// type nothing, and for dead keys, which this simple box doesn't compose).
    /// </summary>
    public static string TextOf(uint vk, uint scan)
    {
        var state = stackalloc byte[256];
        foreach (var k in (ReadOnlySpan<int>)[0x10, 0xA0, 0xA1, 0x11, 0xA2, 0xA3, 0x12, 0xA4, 0xA5])
            if ((PInvoke.GetAsyncKeyState(k) & 0x8000) != 0) state[k] = 0x80;
        if ((PInvoke.GetKeyState(0x14) & 1) != 0) state[0x14] = 1;   // Caps Lock
        // Use the layout of the app being typed over, as if it received the keys.
        HWND fg = PInvoke.GetForegroundWindow();
        uint tid = PInvoke.GetWindowThreadProcessId(fg, null);
        var hkl = PInvoke.GetKeyboardLayout(tid);
        var buf = stackalloc char[8];
        // Flag 0x4: don't change the keyboard state (no dead-key side effects for the real app later).
        int n = PInvoke.ToUnicodeEx(vk, scan, state, new PWSTR(buf), 8, 0x4, hkl);
        if (n <= 0) return "";
        var s = new string(buf, 0, n);
        return s.Length == 1 && char.IsControl(s[0]) ? "" : s;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT Hook(int nCode, WPARAM wParam, LPARAM lParam)
    {
        if (nCode == (int)PInvoke.HC_ACTION && !_target.IsNull)
        {
            var kb = (KBDLLHOOKSTRUCT*)lParam.Value;
            uint vk = kb->vkCode;
            // Alt+Tab always reaches Windows (Tab switches wheels only on its own).
            bool altTab = vk == 0x09 && ((uint)kb->flags & 0x20) != 0;   // LLKHF_ALTDOWN
            if (vk < 256 && !altTab && (TextMode ? !IsModifier(vk) : Swallow[vk]))
            {
                uint m = (uint)wParam.Value;
                if (m == PInvoke.WM_KEYDOWN || m == PInvoke.WM_SYSKEYDOWN)
                {
                    LastKeyTicks = Stopwatch.GetTimestamp();
                    PInvoke.PostMessage(_target, WM_APP_KEY, (WPARAM)(nuint)vk, (LPARAM)(nint)(kb->scanCode | ((uint)kb->flags & 1) << 16));
                }
                return (LRESULT)1;   // swallow down and up
            }
        }
        return PInvoke.CallNextHookEx(HHOOK.Null, nCode, wParam, lParam);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void Foreground(HWINEVENTHOOK hook, uint evt, HWND hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (!_target.IsNull) PInvoke.PostMessage(_target, WM_APP_FG, default, default);
    }

    public static bool IsDown(VIRTUAL_KEY vk) => (PInvoke.GetAsyncKeyState((int)vk) & 0x8000) != 0;

    public static readonly VIRTUAL_KEY[] Digits =
    [
        VIRTUAL_KEY.VK_1, VIRTUAL_KEY.VK_2, VIRTUAL_KEY.VK_3, VIRTUAL_KEY.VK_4, VIRTUAL_KEY.VK_5, VIRTUAL_KEY.VK_6, VIRTUAL_KEY.VK_7, VIRTUAL_KEY.VK_8, VIRTUAL_KEY.VK_9,
        VIRTUAL_KEY.VK_NUMPAD1, VIRTUAL_KEY.VK_NUMPAD2, VIRTUAL_KEY.VK_NUMPAD3, VIRTUAL_KEY.VK_NUMPAD4, VIRTUAL_KEY.VK_NUMPAD5, VIRTUAL_KEY.VK_NUMPAD6, VIRTUAL_KEY.VK_NUMPAD7, VIRTUAL_KEY.VK_NUMPAD8, VIRTUAL_KEY.VK_NUMPAD9,
        VIRTUAL_KEY.VK_0, VIRTUAL_KEY.VK_NUMPAD0,
    ];

    /// <summary>1-based digit for 1–9 / numpad 1–9, else 0.</summary>
    public static int DigitOf(VIRTUAL_KEY vk) =>
        vk >= VIRTUAL_KEY.VK_1 && vk <= VIRTUAL_KEY.VK_9 ? vk - VIRTUAL_KEY.VK_1 + 1 :
        vk >= VIRTUAL_KEY.VK_NUMPAD1 && vk <= VIRTUAL_KEY.VK_NUMPAD9 ? vk - VIRTUAL_KEY.VK_NUMPAD1 + 1 :
        vk is VIRTUAL_KEY.VK_0 or VIRTUAL_KEY.VK_NUMPAD0 ? 10 : 0;

    /// <summary>
    /// Sends Ctrl+V to the foreground app. Waits (briefly) for the user to release modifier keys first so
    /// that e.g. a held Alt doesn't turn the paste into a menu shortcut.
    /// </summary>
    public static void SendPaste() => SendCtrl(VIRTUAL_KEY.VK_V);
    public static void SendCopy() => SendCtrl(VIRTUAL_KEY.VK_C);

    /// <summary>Ctrl+key into the focused app, once Alt/Shift/Win from the shortcut are released.</summary>
    private static void SendCtrl(VIRTUAL_KEY vk)
    {
        for (int i = 0; i < 40 && (IsDown(VIRTUAL_KEY.VK_MENU) || IsDown(VIRTUAL_KEY.VK_SHIFT) || IsDown(VIRTUAL_KEY.VK_LWIN) || IsDown(VIRTUAL_KEY.VK_RWIN)); i++)
            Thread.Sleep(10);
        var inputs = stackalloc INPUT[4];
        inputs[0] = Key(VIRTUAL_KEY.VK_CONTROL, false);
        inputs[1] = Key(vk, false);
        inputs[2] = Key(vk, true);
        inputs[3] = Key(VIRTUAL_KEY.VK_CONTROL, true);
        PInvoke.SendInput(4, inputs, sizeof(INPUT));
    }

    /// <summary>Presses and releases one key (media and volume keys).</summary>
    public static void Tap(VIRTUAL_KEY vk)
    {
        var inputs = stackalloc INPUT[2];
        inputs[0] = Key(vk, false);
        inputs[1] = Key(vk, true);
        PInvoke.SendInput(2, inputs, sizeof(INPUT));
    }

    private static INPUT Key(VIRTUAL_KEY vk, bool up) => new()
    {
        type = INPUT_TYPE.INPUT_KEYBOARD,
        Anonymous = new INPUT._Anonymous_e__Union { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0 } },
    };
}
