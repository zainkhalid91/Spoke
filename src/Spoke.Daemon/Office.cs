using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;

namespace Spoke.Daemon;

/// <summary>
/// Late-bound COM automation (IDispatch by hand: Native AOT has no <c>dynamic</c>). Just enough to drive Word,
/// Excel and PowerPoint: get/set properties and call methods with string, int, bool or omitted arguments.
/// </summary>
internal sealed unsafe class Dispatch : IDisposable
{
    private IDispatch* _p;
    private Dispatch(IDispatch* p) => _p = p;

    /// <summary>A VARIANT (24 bytes on x64): type tag, then the value at offset 8.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Var
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public int I4;
        [FieldOffset(8)] public short Bool;
        [FieldOffset(8)] public nint Ptr;
    }

    private const ushort VT_EMPTY = 0, VT_I4 = 3, VT_BSTR = 8, VT_DISPATCH = 9, VT_ERROR = 10, VT_BOOL = 11;
    private const int DISP_E_PARAMNOTFOUND = unchecked((int)0x80020004);

    /// <summary>Marks an optional argument as left out.</summary>
    public static readonly object Missing = new();

    public static bool IsRegistered(string progId)
    {
        using var key = Registry.ClassesRoot.OpenSubKey(progId + "\\CLSID");
        return key != null;
    }

    public static Dispatch Create(string progId)
    {
        Guid clsid, iid = IDispatch.IID_Guid;
        fixed (char* p = progId) PInvoke.CLSIDFromProgID(p, &clsid).ThrowOnFailure();
        IDispatch* d;
        PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_LOCAL_SERVER, &iid, (void**)&d).ThrowOnFailure();
        return new Dispatch(d);
    }

    public Dispatch Obj(string name, params object[] args) => Invoke(name, DISPATCH_FLAGS.DISPATCH_PROPERTYGET | DISPATCH_FLAGS.DISPATCH_METHOD, args) as Dispatch
        ?? throw new InvalidOperationException($"{name} returned no object");
    public object? Get(string name) => Invoke(name, DISPATCH_FLAGS.DISPATCH_PROPERTYGET, []);
    public void Set(string name, object value) => Invoke(name, DISPATCH_FLAGS.DISPATCH_PROPERTYPUT, [value]);
    public object? Call(string name, params object[] args) => Invoke(name, DISPATCH_FLAGS.DISPATCH_METHOD, args);

    private object? Invoke(string name, DISPATCH_FLAGS flags, object[] args)
    {
        if (_p == null) throw new ObjectDisposedException(nameof(Dispatch));
        int id;
        Guid none = Guid.Empty;
        fixed (char* n = name)
        {
            PWSTR pn = n;
            _p->GetIDsOfNames(&none, &pn, 1, 0x400, &id);
        }
        // Arguments go in reverse order.
        var vars = stackalloc Var[Math.Max(1, args.Length)];
        for (int i = 0; i < args.Length; i++) vars[args.Length - 1 - i] = ToVar(args[i]);
        int putId = -3;   // DISPID_PROPERTYPUT
        var dp = new DISPPARAMS
        {
            rgvarg = (Windows.Win32.System.Variant.VARIANT*)vars,
            cArgs = (uint)args.Length,
            rgdispidNamedArgs = flags == DISPATCH_FLAGS.DISPATCH_PROPERTYPUT ? &putId : null,
            cNamedArgs = flags == DISPATCH_FLAGS.DISPATCH_PROPERTYPUT ? 1u : 0u,
        };
        Var result = default;
        EXCEPINFO ex = default;
        uint argErr;
        try
        {
            _p->Invoke(id, &none, 0x400, flags, &dp, (Windows.Win32.System.Variant.VARIANT*)&result, &ex, &argErr);
        }
        catch (COMException e)
        {
            string? desc = ex.bstrDescription.Value != null ? new string(ex.bstrDescription.Value) : null;
            if (ex.bstrDescription.Value != null) PInvoke.SysFreeString(ex.bstrDescription);
            if (ex.bstrSource.Value != null) PInvoke.SysFreeString(ex.bstrSource);
            if (ex.bstrHelpFile.Value != null) PInvoke.SysFreeString(ex.bstrHelpFile);
            throw new COMException($"{name}: {desc ?? e.Message}", e.HResult);
        }
        finally
        {
            for (int i = 0; i < args.Length; i++)
                if (vars[i].Vt == VT_BSTR) PInvoke.SysFreeString(new BSTR((char*)vars[i].Ptr));
        }
        return FromVar(ref result);
    }

    private static Var ToVar(object o) => o switch
    {
        string s => new Var { Vt = VT_BSTR, Ptr = (nint)AllocBstr(s) },
        bool b => new Var { Vt = VT_BOOL, Bool = (short)(b ? -1 : 0) },
        int i => new Var { Vt = VT_I4, I4 = i },
        _ when ReferenceEquals(o, Missing) => new Var { Vt = VT_ERROR, I4 = DISP_E_PARAMNOTFOUND },
        _ => throw new ArgumentException("Unsupported automation argument " + o.GetType().Name),
    };

    private static char* AllocBstr(string s)
    {
        fixed (char* p = s) return PInvoke.SysAllocString(p).Value;
    }

    private static object? FromVar(ref Var v)
    {
        switch (v.Vt)
        {
            case VT_DISPATCH: return v.Ptr == 0 ? null : new Dispatch((IDispatch*)v.Ptr);   // takes the reference
            case VT_I4: return v.I4;
            case 2: return (int)(short)v.I4;   // VT_I2
            case VT_BOOL: return v.Bool != 0;
            case VT_BSTR:
            {
                string? s = v.Ptr == 0 ? null : new string((char*)v.Ptr);
                if (v.Ptr != 0) PInvoke.SysFreeString(new BSTR((char*)v.Ptr));
                return s;
            }
            case VT_EMPTY: return null;
            default:
                fixed (Var* pv = &v) PInvoke.VariantClear((Windows.Win32.System.Variant.VARIANT*)pv);
                return null;
        }
    }

    public void Dispose()
    {
        if (_p != null) { _p->Release(); _p = null; }
    }
}

/// <summary>
/// Document conversions through the user's own Microsoft Office (invisible, alerts off), or LibreOffice when
/// Office isn't installed. Office instances Spoke starts are closed afterwards; one the user already had open is
/// left running (only Spoke's own document is closed).
/// </summary>
internal static class Office
{
    public static bool HasWord => Dispatch.IsRegistered("Word.Application");
    public static bool HasExcel => Dispatch.IsRegistered("Excel.Application");
    public static bool HasPowerPoint => Dispatch.IsRegistered("PowerPoint.Application");

    public static string? LibreOffice
    {
        get
        {
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            {
                string p = Path.Combine(root, "LibreOffice", "program", "soffice.exe");
                if (File.Exists(p)) return p;
            }
            return null;
        }
    }

    /// <summary>True when something on this PC can do a conversion of this kind.</summary>
    public static bool CanConvert(Spoke.Core.FileKind from) => from switch
    {
        Spoke.Core.FileKind.WordDoc or Spoke.Core.FileKind.Pdf or Spoke.Core.FileKind.Text => HasWord || LibreOffice != null,
        Spoke.Core.FileKind.Spreadsheet => HasExcel || LibreOffice != null,
        Spoke.Core.FileKind.Presentation => HasPowerPoint || LibreOffice != null,
        _ => false,
    };

    // Word: wdFormatDocumentDefault 16, wdFormatPDF 17. Excel: xlOpenXMLWorkbook 51, xlTypePDF 0. PowerPoint: ppSaveAsOpenXMLPresentation 24, ppSaveAsPDF 32.
    public static void Convert(string input, string output, string toExt)
    {
        var kind = Spoke.Core.FileKinds.Of(input);
        bool office = kind switch
        {
            Spoke.Core.FileKind.WordDoc or Spoke.Core.FileKind.Pdf or Spoke.Core.FileKind.Text => HasWord,
            Spoke.Core.FileKind.Spreadsheet => HasExcel,
            Spoke.Core.FileKind.Presentation => HasPowerPoint,
            _ => false,
        };
        if (!office)
        {
            if (LibreOffice is { } so) { ConvertLibre(so, input, output, toExt); return; }
            throw new NotSupportedException("Converting this needs Microsoft Office or LibreOffice.");
        }
        switch (kind)
        {
            case Spoke.Core.FileKind.Spreadsheet: Excel(input, output, toExt); break;
            case Spoke.Core.FileKind.Presentation: PowerPoint(input, output, toExt); break;
            default: Word(input, output, toExt); break;
        }
    }

    private static void Word(string input, string output, string toExt)
    {
        // Opening a PDF shows "Word will now convert your PDF…", a modal that DisplayAlerts doesn't cover and that
        // would hang an invisible Word. Word's own "don't show again" option is set for the conversion, then restored.
        bool pdf = input.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
        using var warning = pdf ? new TempRegistryValue(WordOptionsKey(), "DisableConvertPdfWarning", 1) : null;
        WordConvert(input, output, toExt);
    }

    private static string WordOptionsKey()
    {
        using var cur = Registry.ClassesRoot.OpenSubKey(@"Word.Application\CurVer");
        string version = (cur?.GetValue(null) as string)?.Split('.').LastOrDefault() is { Length: > 0 } v ? v + ".0" : "16.0";
        return $@"Software\Microsoft\Office\{version}\Word\Options";
    }

    /// <summary>Sets a DWORD under HKCU for a while and puts the old value (or its absence) back on dispose.</summary>
    private sealed class TempRegistryValue : IDisposable
    {
        private readonly string _key, _name;
        private readonly object? _old;
        public TempRegistryValue(string key, string name, int value)
        {
            _key = key; _name = name;
            using var k = Registry.CurrentUser.CreateSubKey(key, writable: true);
            _old = k.GetValue(name);
            k.SetValue(name, value, RegistryValueKind.DWord);
        }
        public void Dispose()
        {
            using var k = Registry.CurrentUser.CreateSubKey(_key, writable: true);
            if (_old is int v) k.SetValue(_name, v, RegistryValueKind.DWord);
            else k.DeleteValue(_name, throwOnMissingValue: false);
        }
    }

    private static void WordConvert(string input, string output, string toExt)
    {
        using var app = Dispatch.Create("Word.Application");
        using var docs = app.Obj("Documents");
        bool wasRunning = (int)(docs.Get("Count") ?? 0) > 0;
        if (!wasRunning) app.Set("Visible", false);
        app.Set("DisplayAlerts", 0);   // wdAlertsNone: no "Word will now convert your PDF" prompt
        // Open(FileName, ConfirmConversions, ReadOnly, AddToRecentFiles, PasswordDocument, PasswordTemplate, Revert,
        //      WritePasswordDocument, WritePasswordTemplate, Format, Encoding, Visible)
        using var doc = docs.Obj("Open", input, false, true, false, "", "", false, "", "", Dispatch.Missing, Dispatch.Missing, false);
        try
        {
            doc.Call("SaveAs2", output, toExt == "pdf" ? 17 : 16);
        }
        finally
        {
            doc.Call("Close", 0);   // wdDoNotSaveChanges
            if (!wasRunning) app.Call("Quit", 0);
        }
    }

    private static void Excel(string input, string output, string toExt)
    {
        using var app = Dispatch.Create("Excel.Application");
        using var books = app.Obj("Workbooks");
        bool wasRunning = (int)(books.Get("Count") ?? 0) > 0;
        app.Set("DisplayAlerts", false);
        // Open(FileName, UpdateLinks, ReadOnly)
        using var book = books.Obj("Open", input, 0, true);
        try
        {
            if (toExt == "pdf") book.Call("ExportAsFixedFormat", 0, output);
            else book.Call("SaveAs", output, 51);
        }
        finally
        {
            book.Call("Close", false);
            if (!wasRunning) app.Call("Quit");
        }
    }

    private static void PowerPoint(string input, string output, string toExt)
    {
        using var app = Dispatch.Create("PowerPoint.Application");
        using var presentations = app.Obj("Presentations");
        bool wasRunning = (int)(presentations.Get("Count") ?? 0) > 0;
        // Open(FileName, ReadOnly = msoTrue, Untitled = msoFalse, WithWindow = msoFalse)
        using var pres = presentations.Obj("Open", input, -1, 0, 0);
        try
        {
            pres.Call("SaveAs", output, toExt == "pdf" ? 32 : 24);
        }
        finally
        {
            pres.Call("Close");
            if (!wasRunning) app.Call("Quit");
        }
    }

    private static void ConvertLibre(string soffice, string input, string output, string toExt)
    {
        // soffice names the output after the input and overwrites, so convert into a private folder, then move.
        string tmp = Path.Combine(Path.GetTempPath(), "Spoke", "lo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(soffice)
            {
                UseShellExecute = false, CreateNoWindow = true,
                ArgumentList = { "--headless", "--norestore", "--convert-to", toExt, "--outdir", tmp, input },
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            if (!p.WaitForExit(170_000)) { try { p.Kill(true); } catch (InvalidOperationException) { } throw new TimeoutException("LibreOffice took too long."); }
            string produced = Path.Combine(tmp, Path.GetFileNameWithoutExtension(input) + "." + toExt);
            if (!File.Exists(produced)) throw new IOException("LibreOffice couldn't convert this file.");
            File.Move(produced, output);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// A hung Office conversion leaves an invisible Word/Excel/PowerPoint behind. Only instances that started
    /// after this job and have no window are ended; the user's own Office windows are never touched.
    /// </summary>
    public static void KillHidden(DateTime since)
    {
        foreach (var name in new[] { "WINWORD", "EXCEL", "POWERPNT" })
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
                using (p)
                {
                    try
                    {
                        if (p.StartTime >= since.AddSeconds(-1) && p.MainWindowHandle == 0) { p.Kill(); Log.Write($"Ended a hung hidden {name} ({p.Id})"); }
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                }
    }
}
