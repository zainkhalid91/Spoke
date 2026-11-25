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
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT Hook(int nCode, WPARAM wParam, LPARAM lParam)
    {
        if (nCode == (int)PInvoke.HC_ACTION && !_target.IsNull)
        {
            var kb = (KBDLLHOOKSTRUCT*)lParam.Value;
            if (kb->vkCode < 256 && Swallow[kb->vkCode])
            {
                uint m = (uint)wParam.Value;
                if (m == PInvoke.WM_KEYDOWN || m == PInvoke.WM_SYSKEYDOWN)
                {
                    LastKeyTicks = Stopwatch.GetTimestamp();
                    PInvoke.PostMessage(_target, WM_APP_KEY, (WPARAM)(nuint)kb->vkCode, default);
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
    ];

    /// <summary>1-based digit for 1–9 / numpad 1–9, else 0.</summary>
    public static int DigitOf(VIRTUAL_KEY vk) =>
        vk >= VIRTUAL_KEY.VK_1 && vk <= VIRTUAL_KEY.VK_9 ? vk - VIRTUAL_KEY.VK_1 + 1 :
        vk >= VIRTUAL_KEY.VK_NUMPAD1 && vk <= VIRTUAL_KEY.VK_NUMPAD9 ? vk - VIRTUAL_KEY.VK_NUMPAD1 + 1 : 0;

    /// <summary>
    /// Sends Ctrl+V to the foreground app. Waits (briefly) for the user to release modifier keys first so
    /// that e.g. a held Alt doesn't turn the paste into a menu shortcut.
    /// </summary>
    public static void SendPaste()
    {
        for (int i = 0; i < 40 && (IsDown(VIRTUAL_KEY.VK_MENU) || IsDown(VIRTUAL_KEY.VK_SHIFT) || IsDown(VIRTUAL_KEY.VK_LWIN) || IsDown(VIRTUAL_KEY.VK_RWIN)); i++)
            Thread.Sleep(10);
        var inputs = stackalloc INPUT[4];
        inputs[0] = Key(VIRTUAL_KEY.VK_CONTROL, false);
        inputs[1] = Key(VIRTUAL_KEY.VK_V, false);
        inputs[2] = Key(VIRTUAL_KEY.VK_V, true);
        inputs[3] = Key(VIRTUAL_KEY.VK_CONTROL, true);
        PInvoke.SendInput(4, inputs, sizeof(INPUT));
    }

    private static INPUT Key(VIRTUAL_KEY vk, bool up) => new()
    {
        type = INPUT_TYPE.INPUT_KEYBOARD,
        Anonymous = new INPUT._Anonymous_e__Union { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0 } },
    };
}
