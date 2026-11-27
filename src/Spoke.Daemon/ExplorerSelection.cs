using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;
using IServiceProvider = Windows.Win32.System.Com.IServiceProvider;

namespace Spoke.Daemon;

/// <summary>
/// The files the user means: the selection in the File Explorer window (active tab) or on the desktop that had
/// focus when the wheel opened, else files copied with Ctrl+C. Shell COM, vtable calls only (AOT-safe).
/// </summary>
internal static unsafe class ExplorerSelection
{
    private static readonly Guid ClsidShellWindows = new(0x9BA05972, 0xF6A8, 0x11CF, 0xA4, 0x42, 0x00, 0xA0, 0xC9, 0x0A, 0x8F, 0x39);

    /// <summary>A minimal VARIANT (16 bytes on x64 is 24; we only use the first 16 and pass a full-size buffer).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Var
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public int I4;
    }

    /// <param name="clipboard">Fall back to files copied with Ctrl+C (not for drop mode: a drag isn't the clipboard).</param>
    public static string[] Get(HWND foreground, HWND clipboardOwner, out string source, bool clipboard = true)
    {
        source = "";
        try
        {
            string cls = ClassOf(PInvoke.GetAncestor(foreground, GET_ANCESTOR_FLAGS.GA_ROOT));
            if (cls is "CabinetWClass" or "ExploreWClass")
            {
                var files = FromExplorer(PInvoke.GetAncestor(foreground, GET_ANCESTOR_FLAGS.GA_ROOT));
                if (files.Length > 0) { source = "File Explorer"; return files; }
            }
            else if (cls is "Progman" or "WorkerW")
            {
                var files = FromDesktop();
                if (files.Length > 0) { source = "desktop"; return files; }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Log.Write("Explorer selection failed: " + ex.Message);
        }
        if (!clipboard) return [];
        var copied = ClipboardIO.GetFiles(clipboardOwner);
        if (copied.Length > 0) source = "clipboard";
        return copied;
    }

    private static string ClassOf(HWND h)
    {
        if (h.IsNull) return "";
        char* buf = stackalloc char[64];
        int n = PInvoke.GetClassName(h, buf, 64);
        return new string(buf, 0, n);
    }

    private static string[] FromExplorer(HWND frame)
    {
        IShellWindows* windows = null;
        try
        {
            if (!Create(out windows)) return [];
            int count = windows->Count;
            for (int i = 0; i < count; i++)
            {
                var v = new Var { Vt = 3 /* VT_I4 */, I4 = i };
                IDispatch* disp;
                try { disp = windows->Item(*(Windows.Win32.System.Variant.VARIANT*)&v); }
                catch (COMException) { continue; }
                if (disp == null) continue;
                try
                {
                    var files = SelectionOf(disp, frame);
                    if (files != null) return files;
                }
                finally { disp->Release(); }
            }
            return [];
        }
        finally { if (windows != null) windows->Release(); }
    }

    private static string[] FromDesktop()
    {
        IShellWindows* windows = null;
        try
        {
            if (!Create(out windows)) return [];
            var loc = new Var { Vt = 3, I4 = 0 /* CSIDL_DESKTOP */ };
            var empty = new Var();
            int hwnd;
            IDispatch* disp = windows->FindWindowSW((Windows.Win32.System.Variant.VARIANT*)&loc, (Windows.Win32.System.Variant.VARIANT*)&empty,
                ShellWindowTypeConstants.SWC_DESKTOP, &hwnd, ShellWindowFindWindowOptions.SWFO_NEEDDISPATCH);
            if (disp == null) return [];
            try { return SelectionOf(disp, HWND.Null) ?? []; }
            finally { disp->Release(); }
        }
        finally { if (windows != null) windows->Release(); }
    }

    private static bool Create(out IShellWindows* windows)
    {
        Guid clsid = ClsidShellWindows, iid = IShellWindows.IID_Guid;
        IShellWindows* w;
        var hr = PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_LOCAL_SERVER, &iid, (void**)&w);
        windows = hr.Succeeded ? w : null;
        return hr.Succeeded;
    }

    /// <summary>
    /// The selected file-system paths of one shell window, or null when it isn't the one we want: for Explorer,
    /// the window must belong to <paramref name="frame"/> and be its visible (active) tab.
    /// </summary>
    private static string[]? SelectionOf(IDispatch* disp, HWND frame)
    {
        IServiceProvider* sp = null;
        IShellBrowser* browser = null;
        IShellView* view = null;
        IFolderView2* folder = null;
        IShellItemArray* items = null;
        try
        {
            Guid iidSp = IServiceProvider.IID_Guid;
            if (((IUnknown*)disp)->QueryInterface(&iidSp, (void**)&sp).Failed) return null;
            Guid sid = PInvoke.SID_STopLevelBrowser, iidBrowser = IShellBrowser.IID_Guid;
            sp->QueryService(&sid, &iidBrowser, (void**)&browser);
            if (!frame.IsNull)
            {
                HWND w;
                browser->GetWindow(&w);
                if (PInvoke.GetAncestor(w, GET_ANCESTOR_FLAGS.GA_ROOT) != frame) return null;
            }
            browser->QueryActiveShellView(&view);
            if (!frame.IsNull)
            {
                HWND vw;
                view->GetWindow(&vw);
                if (!PInvoke.IsWindowVisible(vw)) return null;   // a background tab of the same window
            }
            Guid iidFolder = IFolderView2.IID_Guid;
            if (((IUnknown*)view)->QueryInterface(&iidFolder, (void**)&folder).Failed) return [];
            try { folder->GetSelection(false, &items); }
            catch (COMException) { return []; }   // nothing selected
            if (items == null) return [];
            uint n;
            items->GetCount(&n);
            var paths = new List<string>((int)n);
            for (uint i = 0; i < n && i < 500; i++)
            {
                IShellItem* item;
                items->GetItemAt(i, &item);
                try
                {
                    PWSTR p;
                    item->GetDisplayName(SIGDN.SIGDN_FILESYSPATH, &p);
                    paths.Add(new string(p));
                    Marshal.FreeCoTaskMem((nint)p.Value);
                }
                catch (COMException) { }   // not a file (e.g. "This PC")
                finally { item->Release(); }
            }
            return [.. paths];
        }
        finally
        {
            if (items != null) items->Release();
            if (folder != null) folder->Release();
            if (view != null) view->Release();
            if (browser != null) browser->Release();
            if (sp != null) sp->Release();
        }
    }
}
