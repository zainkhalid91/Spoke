using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Spoke.Daemon;

/// <summary>What the overlay does with a drag of files over it (all calls arrive on the UI thread).</summary>
internal interface IDropHandler
{
    /// <summary>A drag came over the overlay. Returns the drop effect to show (DROPEFFECT_COPY or none).</summary>
    uint DragEnter(string[] files, int x, int y);
    uint DragOver(int x, int y);
    void DragLeave();
    uint Drop(string[] files, int x, int y);
}

/// <summary>
/// The overlay as an OLE drop target (<c>RegisterDragDrop</c>), so files dragged from Explorer can be dropped on a
/// slice. A hand-built COM object (a vtable of unmanaged function pointers), which needs no COM interop support and
/// so works under Native AOT. Also drives the shell's drag image, so the dragged file's picture keeps following the
/// pointer over the wheel. Screen coordinates are physical pixels (the process is per-monitor DPI aware).
/// </summary>
internal static unsafe partial class DropTarget
{
    public const uint DropEffectNone = 0, DropEffectCopy = 1;

    private struct Obj
    {
        public void** Vtbl;
        public int Refs;
    }

    private static Obj* _obj;
    private static IDropHandler? _handler;
    private static HWND _hwnd;
    private static void* _helper;   // IDropTargetHelper* (drag images), or null

    private static readonly Guid IidUnknown = new(0x00000000, 0, 0, 0xC0, 0, 0, 0, 0, 0, 0, 0x46);
    private static readonly Guid IidDropTarget = new(0x00000122, 0, 0, 0xC0, 0, 0, 0, 0, 0, 0, 0x46);
    private static readonly Guid ClsidDragDropHelper = new(0x4657278A, 0x411B, 0x11D2, 0x83, 0x9A, 0x00, 0xC0, 0x4F, 0xD9, 0x18, 0xD0);
    private static readonly Guid IidDropTargetHelper = new(0x4657278B, 0x411B, 0x11D2, 0x83, 0x9A, 0x00, 0xC0, 0x4F, 0xD9, 0x18, 0xD0);

    [LibraryImport("ole32.dll")] private static partial int OleInitialize(void* reserved);
    [LibraryImport("ole32.dll")] private static partial int RegisterDragDrop(nint hwnd, void* target);
    [LibraryImport("ole32.dll")] private static partial int RevokeDragDrop(nint hwnd);
    [LibraryImport("ole32.dll")] private static partial void ReleaseStgMedium(StgMedium* medium);
    [LibraryImport("ole32.dll")] private static partial int CoCreateInstance(Guid* clsid, void* outer, uint context, Guid* iid, void** obj);
    [LibraryImport("shell32.dll", EntryPoint = "DragQueryFileW")] private static partial uint DragQueryFile(nint drop, uint index, char* file, uint cch);

    [StructLayout(LayoutKind.Sequential)]
    private struct FormatEtc
    {
        public ushort Format;
        public void* Device;
        public uint Aspect;
        public int Index;
        public uint Tymed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StgMedium
    {
        public uint Tymed;
        public nint Handle;
        public void* UnkForRelease;
    }

    /// <summary>Makes <paramref name="hwnd"/> a drop target. The thread must be the window's (an STA).</summary>
    public static bool Register(HWND hwnd, IDropHandler handler)
    {
        int hr = OleInitialize(null);   // after CoInitializeEx(STA) this just adds OLE (S_FALSE)
        if (hr < 0) { Log.Write($"Drop target: OleInitialize 0x{hr:X8}"); return false; }
        var vt = (void**)NativeMemory.Alloc((nuint)(7 * sizeof(void*)));
        vt[0] = (delegate* unmanaged<Obj*, Guid*, void**, int>)&QueryInterface;
        vt[1] = (delegate* unmanaged<Obj*, uint>)&AddRef;
        vt[2] = (delegate* unmanaged<Obj*, uint>)&Release;
        vt[3] = (delegate* unmanaged<Obj*, void*, uint, long, uint*, int>)&OnDragEnter;
        vt[4] = (delegate* unmanaged<Obj*, uint, long, uint*, int>)&OnDragOver;
        vt[5] = (delegate* unmanaged<Obj*, int>)&OnDragLeave;
        vt[6] = (delegate* unmanaged<Obj*, void*, uint, long, uint*, int>)&OnDrop;
        _obj = (Obj*)NativeMemory.Alloc((nuint)sizeof(Obj));
        _obj->Vtbl = vt;
        _obj->Refs = 1;   // ours; OLE adds its own while registered
        _handler = handler;
        _hwnd = hwnd;
        hr = RegisterDragDrop(hwnd, _obj);
        if (hr < 0) { Log.Write($"Drop target: RegisterDragDrop 0x{hr:X8}"); return false; }

        Guid clsid = ClsidDragDropHelper, iid = IidDropTargetHelper;
        void* helper;
        if (CoCreateInstance(&clsid, null, 1 /* CLSCTX_INPROC_SERVER */, &iid, &helper) >= 0) _helper = helper;
        return true;
    }

    public static void Revoke()
    {
        if (_obj == null) return;
        RevokeDragDrop(_hwnd);
        if (_helper != null) { ((delegate* unmanaged[Stdcall]<void*, uint>)(*(void***)_helper)[2])(_helper); _helper = null; }
        _handler = null;
    }

    /// <summary>The files in a dragged data object (CF_HDROP), or none if it carries something else.</summary>
    private static string[] Files(void* data)
    {
        if (data == null) return [];
        var fe = new FormatEtc { Format = 15 /* CF_HDROP */, Aspect = 1 /* DVASPECT_CONTENT */, Index = -1, Tymed = 1 /* TYMED_HGLOBAL */ };
        StgMedium sm;
        int hr = ((delegate* unmanaged[Stdcall]<void*, FormatEtc*, StgMedium*, int>)(*(void***)data)[3])(data, &fe, &sm);
        if (hr < 0) return [];
        try
        {
            uint n = DragQueryFile(sm.Handle, 0xFFFFFFFF, null, 0);
            var files = new List<string>((int)Math.Min(n, 1000));
            char* buf = stackalloc char[1024];
            for (uint i = 0; i < n && i < 1000; i++)
            {
                uint len = DragQueryFile(sm.Handle, i, buf, 1024);
                if (len > 0) files.Add(new string(buf, 0, (int)len));
            }
            return [.. files];
        }
        finally { ReleaseStgMedium(&sm); }
    }

    private struct Pt(int x, int y)
    {
        public int X = x, Y = y;
    }

    private static (int X, int Y) Point(long pt) => ((int)(pt & 0xFFFFFFFF), (int)(pt >> 32));

    // ───────────────────────── IUnknown ─────────────────────────

    [UnmanagedCallersOnly]
    private static int QueryInterface(Obj* self, Guid* iid, void** obj)
    {
        if (*iid == IidUnknown || *iid == IidDropTarget)
        {
            Interlocked.Increment(ref self->Refs);
            *obj = self;
            return 0;
        }
        *obj = null;
        return unchecked((int)0x80004002);   // E_NOINTERFACE
    }

    [UnmanagedCallersOnly]
    private static uint AddRef(Obj* self) => (uint)Interlocked.Increment(ref self->Refs);

    // Never freed: one object for the life of the process (the overlay lives as long as Spoke does).
    [UnmanagedCallersOnly]
    private static uint Release(Obj* self) => (uint)Math.Max(1, Interlocked.Decrement(ref self->Refs));

    // ───────────────────────── IDropTarget ─────────────────────────

    [UnmanagedCallersOnly]
    private static int OnDragEnter(Obj* self, void* data, uint keys, long pt, uint* effect)
    {
        var (x, y) = Point(pt);
        uint e = DropEffectNone;
        try { if (_handler != null) e = _handler.DragEnter(Files(data), x, y) & *effect; }
        catch (Exception ex) { Log.Write("DragEnter: " + ex); }
        if (_helper != null)
        {
            var p = new Pt(x, y);
            ((delegate* unmanaged[Stdcall]<void*, nint, void*, Pt*, uint, int>)(*(void***)_helper)[3])(_helper, _hwnd, data, &p, e);
        }
        *effect = e;
        return 0;
    }

    [UnmanagedCallersOnly]
    private static int OnDragOver(Obj* self, uint keys, long pt, uint* effect)
    {
        var (x, y) = Point(pt);
        uint e = DropEffectNone;
        try { if (_handler != null) e = _handler.DragOver(x, y) & *effect; }
        catch (Exception ex) { Log.Write("DragOver: " + ex); }
        if (_helper != null)
        {
            var p = new Pt(x, y);
            ((delegate* unmanaged[Stdcall]<void*, Pt*, uint, int>)(*(void***)_helper)[5])(_helper, &p, e);
        }
        *effect = e;
        return 0;
    }

    [UnmanagedCallersOnly]
    private static int OnDragLeave(Obj* self)
    {
        if (_helper != null) ((delegate* unmanaged[Stdcall]<void*, int>)(*(void***)_helper)[4])(_helper);
        try { _handler?.DragLeave(); }
        catch (Exception ex) { Log.Write("DragLeave: " + ex); }
        return 0;
    }

    [UnmanagedCallersOnly]
    private static int OnDrop(Obj* self, void* data, uint keys, long pt, uint* effect)
    {
        var (x, y) = Point(pt);
        uint e = DropEffectNone;
        string[] files = Files(data);
        if (_helper != null)
        {
            var p = new Pt(x, y);
            ((delegate* unmanaged[Stdcall]<void*, void*, Pt*, uint, int>)(*(void***)_helper)[6])(_helper, data, &p, *effect & DropEffectCopy);
        }
        try { if (_handler != null) e = _handler.Drop(files, x, y) & *effect; }
        catch (Exception ex) { Log.Write("Drop: " + ex); }
        *effect = e;
        return 0;
    }

    /// <summary>The primary mouse button is held: a drag may be under way (GetAsyncKeyState reads physical buttons).</summary>
    public static bool PrimaryDown()
    {
        bool swapped = PInvoke.GetSystemMetrics(Windows.Win32.UI.WindowsAndMessaging.SYSTEM_METRICS_INDEX.SM_SWAPBUTTON) != 0;
        var vk = swapped ? Windows.Win32.UI.Input.KeyboardAndMouse.VIRTUAL_KEY.VK_RBUTTON : Windows.Win32.UI.Input.KeyboardAndMouse.VIRTUAL_KEY.VK_LBUTTON;
        return (PInvoke.GetAsyncKeyState((int)vk) & 0x8000) != 0;
    }
}
