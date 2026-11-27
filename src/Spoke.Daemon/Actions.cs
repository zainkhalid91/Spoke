using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Spoke.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.Shell;

namespace Spoke.Daemon;

/// <summary>
/// Runs slice actions on their own STA thread, so a slow launch (a sleeping network drive, a cold app)
/// never freezes the wheel. Problems come back to the UI thread as toasts through the notify callback.
/// </summary>
internal sealed unsafe class Actions
{
    public delegate void NotifyFn(string title, string detail, bool error);

    private readonly BlockingCollection<Action> _queue = new(boundedCapacity: 16);
    private readonly HWND _clipboardOwner;
    private readonly NotifyFn _notify;
    private Thread? _thread;

    public Actions(HWND clipboardOwner, NotifyFn notify)
    {
        _clipboardOwner = clipboardOwner;
        _notify = notify;
    }

    /// <summary>Runs a slice (search needs its query: use <see cref="Search"/>). Call after the wheel has closed.</summary>
    public void Run(Slice s)
    {
        // We just got the user's input, so we may let the app we start come to the front.
        PInvoke.AllowSetForegroundWindow(PInvoke.ASFW_ANY);
        Post(() => Execute(s));
    }

    /// <summary>
    /// A "run all" group: each child in order on the action thread, <see cref="Slice.DelayMs"/> apart, so an app
    /// can start before the next step (text typed after launching an editor lands in it). One failing step is
    /// reported and the rest still run.
    /// </summary>
    public void RunAll(Slice group)
    {
        PInvoke.AllowSetForegroundWindow(PInvoke.ASFW_ANY);
        Post(() =>
        {
            for (int i = 0; i < group.Children.Count; i++)
            {
                if (i > 0 && group.DelayMs > 0) Thread.Sleep(group.DelayMs);
                var child = group.Children[i];
                try { Execute(child); }
                catch (Exception ex)
                {
                    Log.Write($"Run all ({group.Label}) step {i + 1} failed: {ex}");
                    _notify($"{group.Label}: step {i + 1} failed", $"{child.Label}: {ex.Message}", true);
                }
            }
        });
    }

    public void Search(Slice s, string query)
    {
        PInvoke.AllowSetForegroundWindow(PInvoke.ASFW_ANY);
        string url = SearchEngines.Url(s.SearchTemplate, query);
        Post(() => OpenUrl(url, "", s.Label));
    }

    private void Post(Action a)
    {
        if (_thread == null)
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "Spoke actions" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }
        if (!_queue.TryAdd(a)) Log.Write("Action dropped: queue full");
    }

    private void Loop()
    {
        foreach (var a in _queue.GetConsumingEnumerable())
        {
            try { a(); }
            catch (Exception ex)
            {
                Log.Write("Action failed: " + ex);
                _notify("Something went wrong", ex.Message, true);
            }
        }
    }

    private void Execute(Slice s)
    {
        switch (s.Kind)
        {
            case SliceKind.App:
                Shell(Expand(s.Target), Expand(s.Args), Expand(s.WorkDir), s.Label);
                break;
            case SliceKind.Url:
                OpenUrl(s.Target, s.Browser, s.Label);
                break;
            case SliceKind.Folder:
                OpenFolder(s);
                break;
            case SliceKind.Text:
                TypeText(s.Text);
                break;
        }
    }

    // ───────────────────────── launching ─────────────────────────

    private static string Expand(string s) => s.Length == 0 ? s : Environment.ExpandEnvironmentVariables(s);

    /// <summary>ShellExecuteEx: exe names, paths, shortcuts, shell:AppsFolder\AUMID, URLs. Returns false (after a toast) on failure.</summary>
    private bool Shell(string file, string args, string dir, string label, bool report = true)
    {
        fixed (char* verb = "open")
        fixed (char* f = file)
        fixed (char* a = args.Length > 0 ? args : null)
        fixed (char* d = dir.Length > 0 ? dir : null)
        {
            var info = new SHELLEXECUTEINFOW
            {
                cbSize = (uint)sizeof(SHELLEXECUTEINFOW),
                fMask = PInvoke.SEE_MASK_FLAG_NO_UI | PInvoke.SEE_MASK_NOASYNC,
                lpVerb = verb,
                lpFile = f,
                lpParameters = a,
                lpDirectory = d,
                nShow = 1,   // SW_SHOWNORMAL
            };
            if (PInvoke.ShellExecuteEx(&info)) return true;
        }
        int err = Marshal.GetLastSystemError();
        Log.Write($"Launch failed: {file} {args} (error {err})");
        if (report) _notify($"Couldn't open {label}", Reason(err, file), true);
        return false;
    }

    private static string Reason(int err, string file) => err switch
    {
        2 or 3 => $"{Path.GetFileName(file.TrimEnd('\\'))} wasn't found. Check the slice's target in settings.json.",
        5 => "Windows denied access.",
        1155 => "No app is set to open this.",
        1223 => "Cancelled.",
        _ => $"Windows error {err}.",
    };

    private void OpenUrl(string url, string browser, string label)
    {
        if (browser.Length > 0) Shell(Expand(browser), Quote(url), "", label);
        else Shell(url, "", "", label);
    }

    private void OpenFolder(Slice s)
    {
        string path = Expand(s.Target);
        if (!Directory.Exists(path))
        {
            _notify($"Couldn't open {s.Label}", $"The folder {path} doesn't exist.", true);
            return;
        }
        switch (s.Open)
        {
            case FolderOpen.Explorer:
                Shell(path, "", "", s.Label);
                break;
            case FolderOpen.Terminal:
                // "-d ." starts in the working folder, which sidesteps quoting a path that ends in a backslash.
                if (!Shell("wt.exe", "-d .", path, s.Label, report: false))
                    Shell("powershell.exe", "-NoExit", path, s.Label);
                break;
            case FolderOpen.CopyPath:
                if (ClipboardIO.SetText(_clipboardOwner, path)) _notify("Path copied", path, false);
                break;
        }
    }

    private static string Quote(string s) => "\"" + s.Replace("\"", "%22") + "\"";

    // ───────────────────────── typing text ─────────────────────────

    private const int TypeLimit = 2000;   // longer text is pasted instead of typed

    /// <summary>
    /// Types the text into the app that has focus (the wheel never took it). Short text goes in as Unicode
    /// keystrokes, which leaves the clipboard alone; long text is pasted, then the clipboard is put back.
    /// </summary>
    private void TypeText(string template) =>
        TypeRaw(Tokens.Expand(template, DateTimeOffset.Now, () => ClipboardIO.GetText(_clipboardOwner)));

    /// <summary>Types text exactly as given (no tokens): generators, text-tool results.</summary>
    public void Type(string text) => Post(() => TypeRaw(text));

    private void TypeRaw(string text)
    {
        if (text.Length == 0) return;
        WaitForModifiersUp();

        if (text.Length <= TypeLimit)
        {
            SendUnicode(text);
            return;
        }
        var saved = ClipboardIO.Snapshot(_clipboardOwner);
        if (!ClipboardIO.SetText(_clipboardOwner, text, markOwn: true)) return;
        KeyHook.SendPaste();
        if (saved == null) return;
        Thread.Sleep(400);   // let the target app read the clipboard before it changes back
        ClipboardIO.Restore(_clipboardOwner, saved);
    }

    // ───────────────────────── text tools ─────────────────────────

    /// <summary>
    /// Applies a text tool to the selection in the focused app: copy it (Ctrl+C), transform, paste the result over
    /// it, then put the user's clipboard back. With nothing selected it works on the clipboard text instead and
    /// leaves the result there. Terminals get no Ctrl+C (it would stop the running program): clipboard only.
    /// </summary>
    public void Transform(TextTools.Op op, string label, bool alternate, bool terminal) => Post(() => TransformSelection(op, label, alternate, terminal));

    private void TransformSelection(TextTools.Op op, string label, bool alternate, bool terminal)
    {
        WaitForModifiersUp();
        List<(uint, byte[])>? saved = null;
        bool fromSelection = false;
        if (!terminal)
        {
            saved = ClipboardIO.Snapshot(_clipboardOwner);
            uint seq = PInvoke.GetClipboardSequenceNumber();
            KeyHook.SendCopy();
            for (int i = 0; i < 40 && !fromSelection; i++)
            {
                Thread.Sleep(10);
                fromSelection = PInvoke.GetClipboardSequenceNumber() != seq;
            }
            if (fromSelection) Thread.Sleep(30);   // let the app finish writing every format
        }
        string? text = ClipboardIO.GetText(_clipboardOwner);
        void PutBack() { if (fromSelection && saved != null) ClipboardIO.Restore(_clipboardOwner, saved); }

        if (string.IsNullOrEmpty(text))
        {
            PutBack();
            _notify(label, "Select some text first (or copy it), then choose the tool.", true);
            return;
        }
        if (op == TextTools.Op.Count)
        {
            PutBack();
            _notify(fromSelection ? "Selection" : "Clipboard text", TextTools.Summary(text), false);
            return;
        }
        string? result = TextTools.Apply(op, text, out string? error, alternate);
        if (result == null)
        {
            PutBack();
            _notify(label, error ?? "That didn't work on this text.", true);
            return;
        }
        if (fromSelection)
        {
            if (!ClipboardIO.SetText(_clipboardOwner, result, markOwn: true)) return;
            KeyHook.SendPaste();
            Thread.Sleep(400);   // let the app read it before the clipboard changes back
            PutBack();
        }
        else if (ClipboardIO.SetText(_clipboardOwner, result))
        {
            string one = result.ReplaceLineEndings(" ");
            _notify(label + " · copied", one.Length > 60 ? one[..59] + "…" : one, false);
        }
    }

    /// <summary>Waits (up to ~1 s) until Ctrl/Alt/Shift/Win from the shortcut are released, so they don't combine with the text.</summary>
    private static void WaitForModifiersUp()
    {
        for (int i = 0; i < 100; i++)
        {
            if (!KeyHook.IsDown(VIRTUAL_KEY.VK_CONTROL) && !KeyHook.IsDown(VIRTUAL_KEY.VK_MENU) && !KeyHook.IsDown(VIRTUAL_KEY.VK_SHIFT) &&
                !KeyHook.IsDown(VIRTUAL_KEY.VK_LWIN) && !KeyHook.IsDown(VIRTUAL_KEY.VK_RWIN)) return;
            Thread.Sleep(10);
        }
    }

    private static void SendUnicode(string text)
    {
        var inputs = new List<INPUT>(text.Length * 2);
        foreach (char c in text)
        {
            if (c == '\r') continue;
            if (c == '\n') { AddVk(inputs, VIRTUAL_KEY.VK_RETURN); continue; }
            if (c == '\t') { AddVk(inputs, VIRTUAL_KEY.VK_TAB); continue; }
            inputs.Add(Unicode(c, false));
            inputs.Add(Unicode(c, true));
        }
        var span = CollectionsMarshal.AsSpan(inputs);
        // Chunks keep each SendInput batch small enough that slow apps don't drop keys.
        for (int i = 0; i < span.Length; i += 200)
        {
            var chunk = span.Slice(i, Math.Min(200, span.Length - i));
            fixed (INPUT* p = chunk) PInvoke.SendInput((uint)chunk.Length, p, sizeof(INPUT));
            if (i + 200 < span.Length) Thread.Sleep(5);
        }
    }

    private static void AddVk(List<INPUT> list, VIRTUAL_KEY vk)
    {
        list.Add(new INPUT { type = INPUT_TYPE.INPUT_KEYBOARD, Anonymous = new() { ki = new KEYBDINPUT { wVk = vk } } });
        list.Add(new INPUT { type = INPUT_TYPE.INPUT_KEYBOARD, Anonymous = new() { ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP } } });
    }

    private static INPUT Unicode(char c, bool up) => new()
    {
        type = INPUT_TYPE.INPUT_KEYBOARD,
        Anonymous = new() { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYBD_EVENT_FLAGS.KEYEVENTF_UNICODE | (up ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0) } },
    };
}
