using System.Diagnostics;
using System.Text.Json;
using Spoke.Core;
using Windows.Win32;
using Windows.Win32.System.Com;

namespace Spoke.Daemon;

/// <summary>
/// <c>Spoke.exe --worker job.json</c>: runs one file job in its own short-lived process, so big images and Office
/// never grow the daemon's memory, and a hung conversion can simply be killed. Writes a <see cref="FileJobResult"/>.
/// </summary>
internal static unsafe class Worker
{
    public static int Run(string jobPath)
    {
        PInvoke.CoInitializeEx(null, COINIT.COINIT_APARTMENTTHREADED);
        var result = new FileJobResult();
        FileJob? job = null;
        try
        {
            job = JsonSerializer.Deserialize(File.ReadAllText(jobPath), FileJobJson.Default.FileJob);
            if (job == null) return 2;
            long t0 = Stopwatch.GetTimestamp();
            if (job.Op == "office") ExecuteWithTimeout(job, result);
            else Execute(job, result);
            Log.Write($"[worker] {job.Op} {job.Arg} · {job.Inputs.Count} in, {result.Outputs.Count} out, {result.Errors.Count} errors · {Log.Ms(t0, Stopwatch.GetTimestamp()):F0} ms");
        }
        catch (Exception ex)
        {
            result.Errors.Add(ex.Message);
            Log.Write("[worker] failed: " + ex);
        }
        if (job?.ResultPath is { Length: > 0 } rp)
            File.WriteAllText(rp, JsonSerializer.Serialize(result, FileJobJson.Default.FileJobResult));
        return 0;
    }

    /// <summary>
    /// Office can stall without showing anything (an unactivated Office opening a PDF never finishes). Each file gets
    /// 90 s; after that the worker reports why, ends the invisible Office it started, and exits.
    /// </summary>
    private static void ExecuteWithTimeout(FileJob job, FileJobResult r)
    {
        var started = DateTime.Now;
        var t = new Thread(() =>
        {
            PInvoke.CoInitializeEx(null, COINIT.COINIT_APARTMENTTHREADED);
            Execute(job, r);
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        if (t.Join(TimeSpan.FromSeconds(90 * Math.Max(1, job.Inputs.Count)))) return;
        lock (r)
        {
            r.Errors.Add(FileKinds.Common(job.Inputs) == FileKind.Pdf
                ? "Word didn't finish. Opening PDFs for editing needs an activated Microsoft Office."
                : "Office didn't finish in time. Try opening the file in Office once to check it.");
        }
        Office.KillHidden(started);
    }

    private static void Execute(FileJob job, FileJobResult r)
    {
        var img = new Lazy<Imaging>(() => new Imaging());
        if (job.Op == "combinePdf")
        {
            CombinePdf(img.Value, job.Inputs, r);
            return;
        }
        foreach (string input in job.Inputs)
        {
            try
            {
                r.BytesIn += new FileInfo(input).Length;
                string? output = job.Op switch
                {
                    "convert" => ConvertImage(img.Value, input, job.Arg),
                    "resize" => ResizeImage(img.Value, input, job.Arg),
                    "compress" => Compress(img.Value, input),
                    "strip" => Strip(img.Value, input),
                    "office" => ConvertDocument(input, job.Arg),
                    _ => throw new NotSupportedException("Unknown job " + job.Op),
                };
                if (output == null) continue;
                r.Outputs.Add(output);
                r.BytesOut += new FileInfo(output).Length;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                r.Errors.Add($"{Path.GetFileName(input)}: {Friendly(ex)}");
                Log.Write($"[worker] {Path.GetFileName(input)}: {ex}");
            }
        }
    }

    private static string Friendly(Exception ex) => ex switch
    {
        System.Runtime.InteropServices.COMException c when (uint)c.HResult == 0x88982F50 => "Windows can't read this image format (a codec may be missing).",
        System.Runtime.InteropServices.COMException c when (uint)c.HResult == 0x80070005 => "access denied.",
        UnauthorizedAccessException => "access denied.",
        System.Runtime.InteropServices.COMException c => $"Windows reported error 0x{(uint)c.HResult:X8}.",
        _ => ex.Message,
    };

    private static void Write(string path, byte[] bytes)
    {
        // Write to a temp name first, so a failure never leaves a half-written file behind.
        string tmp = path + ".spoke-tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path);
    }

    private static string ConvertImage(Imaging img, string input, string toExt)
    {
        var fmt = Imaging.FormatOf(toExt) ?? throw new NotSupportedException($"Can't write .{toExt} images.");
        string output = OutputNames.For(input, null, Imaging.ExtOf(fmt));
        var px = img.LoadUpright(input);
        if (fmt is ImageFormat.WebP or ImageFormat.Avif) { Ffmpeg(img, px, output, fmt); return output; }
        Write(output, img.Encode(px, fmt));
        return output;
    }

    private static string? ResizeImage(Imaging img, string input, string spec)
    {
        var px = img.LoadUpright(input);
        var size = ResizeSpec.Target(spec, px.Width, px.Height) ?? throw new InvalidOperationException($"already {px.Width} × {px.Height}, nothing to shrink.");
        var small = img.Resize(px, size.W, size.H);
        var fmt = Imaging.FormatOf(Path.GetExtension(input)) is { } f && f is not (ImageFormat.Heic or ImageFormat.WebP or ImageFormat.Avif or ImageFormat.Ico)
            ? f : Imaging.HasAlpha(small) ? ImageFormat.Png : ImageFormat.Jpeg;
        string tag = spec.EndsWith('%') ? spec : spec.EndsWith('w') ? spec : $"{size.W}×{size.H}";
        string output = OutputNames.For(input, tag, Imaging.ExtOf(fmt));
        Write(output, img.Encode(small, fmt, 0.9f));
        return output;
    }

    private static string? Compress(Imaging img, string input)
    {
        var px = img.LoadUpright(input);
        // Very large photos are also brought down to 4K on the long side: nobody needs 8K for sharing.
        const int maxSide = 3840;
        if (Math.Max(px.Width, px.Height) > maxSide)
        {
            var t = ResizeSpec.Target(maxSide.ToString(), px.Width, px.Height)!.Value;
            px = img.Resize(px, t.W, t.H);
        }
        byte[] bytes = img.Encode(px, ImageFormat.Jpeg, 0.75f);
        long before = new FileInfo(input).Length;
        if (bytes.Length >= before) throw new InvalidOperationException($"already small ({OutputNames.Size(before)}); left as is.");
        string output = OutputNames.For(input, "small", "jpg");
        Write(output, bytes);
        return output;
    }

    private static string? Strip(Imaging img, string input)
    {
        byte[] bytes = File.ReadAllBytes(input);
        string ext = Path.GetExtension(input).ToLowerInvariant();
        byte[]? clean = ext switch
        {
            ".jpg" or ".jpeg" or ".jfif" when Metadata.JpegOrientation(bytes) == 1 => Metadata.StripJpeg(bytes),
            ".jpg" or ".jpeg" or ".jfif" => img.Encode(img.LoadUpright(input), ImageFormat.Jpeg, 0.95f),   // keep it upright
            ".png" => Metadata.StripPng(bytes),
            ".bmp" or ".gif" or ".tif" or ".tiff" => img.Encode(img.LoadUpright(input), Imaging.FormatOf(ext)!.Value),
            _ => throw new NotSupportedException("removing metadata works on JPEG, PNG, TIFF, GIF and BMP."),
        };
        if (clean == null) throw new InvalidDataException("the file looks damaged.");
        string output = OutputNames.For(input, "clean", ext.TrimStart('.'));
        Write(output, clean);
        return output;
    }

    private static void CombinePdf(Imaging img, List<string> inputs, FileJobResult r)
    {
        var pages = new List<PdfWriter.Page>();
        foreach (string input in inputs)
        {
            try
            {
                r.BytesIn += new FileInfo(input).Length;
                var px = img.LoadUpright(input);
                // Keep PDFs a sensible size: pages are A4, so 2480 px on the long side is already 300 dpi.
                if (ResizeSpec.Target("2480", px.Width, px.Height) is { } t) px = img.Resize(px, t.W, t.H);
                pages.Add(new PdfWriter.Page(img.Encode(px, ImageFormat.Jpeg, 0.88f), px.Width, px.Height));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                r.Errors.Add($"{Path.GetFileName(input)}: {Friendly(ex)}");
            }
        }
        if (pages.Count == 0) return;
        string first = inputs[0];
        string output = inputs.Count == 1 ? OutputNames.For(first, null, "pdf")
            : OutputNames.For(Path.Combine(Path.GetDirectoryName(first) ?? "", Path.GetFileNameWithoutExtension(first) + $" + {inputs.Count - 1} more"), null, "pdf");
        Write(output, PdfWriter.Build(pages, Path.GetFileNameWithoutExtension(output)));
        r.Outputs.Add(output);
        r.BytesOut += new FileInfo(output).Length;
    }

    private static string ConvertDocument(string input, string toExt)
    {
        string output = OutputNames.For(input, null, toExt);
        Office.Convert(Path.GetFullPath(input), output, toExt);
        if (!File.Exists(output)) throw new IOException("Office didn't produce the file.");
        return output;
    }

    // ───────────────────────── ffmpeg (WebP / AVIF) ─────────────────────────

    public static string? FfmpegPath
    {
        get
        {
            char* buf = stackalloc char[520];
            fixed (char* name = "ffmpeg.exe")
            {
                uint n = PInvoke.SearchPath(null, name, null, 520, buf, null);
                return n is > 0 and < 520 ? new string(buf, 0, (int)n) : null;
            }
        }
    }

    private static void Ffmpeg(Imaging img, Pixels px, string output, ImageFormat fmt)
    {
        string ff = FfmpegPath ?? throw new NotSupportedException("WebP and AVIF need ffmpeg on PATH.");
        string tmpPng = Path.Combine(Path.GetTempPath(), "Spoke", Guid.NewGuid().ToString("N") + ".png");
        Directory.CreateDirectory(Path.GetDirectoryName(tmpPng)!);
        File.WriteAllBytes(tmpPng, img.Encode(px, ImageFormat.Png));
        string tmpOut = output + ".spoke-tmp" + Path.GetExtension(output);
        try
        {
            var psi = new ProcessStartInfo(ff) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", tmpPng }) psi.ArgumentList.Add(a);
            string[] codec = fmt == ImageFormat.WebP
                ? ["-c:v", "libwebp", "-quality", "88", "-frames:v", "1"]
                : ["-c:v", "libaom-av1", "-still-picture", "1", "-crf", "30", "-b:v", "0", "-pix_fmt", "yuv420p", "-frames:v", "1"];
            foreach (var a in codec) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add(tmpOut);
            using var p = Process.Start(psi)!;
            string err = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(120_000)) { p.Kill(true); throw new TimeoutException("ffmpeg took too long."); }
            if (p.ExitCode != 0 || !File.Exists(tmpOut)) throw new IOException("ffmpeg failed: " + err.Trim().Split('\n').LastOrDefault());
            File.Move(tmpOut, output);
        }
        finally
        {
            File.Delete(tmpPng);
            if (File.Exists(tmpOut)) File.Delete(tmpOut);
        }
    }
}

/// <summary>Daemon side: hands a job to a worker process and reports the outcome as a toast.</summary>
internal static class FileJobs
{
    public static void Start(FileJob job, string what, Actions.NotifyFn notify)
    {
        var thread = new Thread(() => RunJob(job, what, notify)) { IsBackground = true, Name = "Spoke file job" };
        thread.Start();
    }

    private static void RunJob(FileJob job, string what, Actions.NotifyFn notify)
    {
        string dir = Path.Combine(Path.GetTempPath(), "Spoke", "jobs");
        Directory.CreateDirectory(dir);
        string id = Guid.NewGuid().ToString("N");
        string jobPath = Path.Combine(dir, id + ".json");
        job.ResultPath = Path.Combine(dir, id + ".result.json");
        File.WriteAllText(jobPath, JsonSerializer.Serialize(job, FileJobJson.Default.FileJob));
        bool slow = job.Op == "office" || job.Inputs.Count > 3;
        if (slow) notify(what + "…", job.Op == "office" ? "Working in the background with Office." : $"{job.Inputs.Count} files, in the background.", false);
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("--worker");
            psi.ArgumentList.Add(jobPath);
            var started = DateTime.Now;
            using var p = Process.Start(psi)!;
            int timeout = job.Op == "office" ? 180_000 * Math.Max(1, Math.Min(job.Inputs.Count, 5)) : 600_000;
            if (!p.WaitForExit(timeout))
            {
                try { p.Kill(true); } catch (InvalidOperationException) { }
                if (job.Op == "office") Office.KillHidden(started);
                notify("Conversion stopped", "It took too long and was cancelled.", true);
                return;
            }
            var r = File.Exists(job.ResultPath) ? JsonSerializer.Deserialize(File.ReadAllText(job.ResultPath), FileJobJson.Default.FileJobResult) : null;
            Report(r, what, notify);
        }
        catch (Exception ex) when (ex is IOException or JsonException or System.ComponentModel.Win32Exception)
        {
            notify("Conversion failed", ex.Message, true);
        }
        finally
        {
            try { File.Delete(jobPath); File.Delete(job.ResultPath); } catch (IOException) { }
        }
    }

    private static void Report(FileJobResult? r, string what, Actions.NotifyFn notify)
    {
        if (r == null) { notify("Conversion failed", "The worker stopped without a result. See spoke.log.", true); return; }
        int ok = r.Outputs.Count;
        if (ok == 0)
        {
            notify(r.Errors.Count == 1 ? "Couldn't do that" : $"Couldn't do that ({r.Errors.Count} files)", r.Errors.FirstOrDefault() ?? "Nothing was saved.", true);
            return;
        }
        string names = ok == 1 ? Path.GetFileName(r.Outputs[0]) : $"{ok} files saved beside the originals";
        string sizes = r.BytesIn > 0 && r.BytesOut > 0 ? $" · {OutputNames.Size(r.BytesIn)} → {OutputNames.Size(r.BytesOut)}" : "";
        string errors = r.Errors.Count > 0 ? $" · {r.Errors.Count} failed: {r.Errors[0]}" : "";
        notify(what + (r.Errors.Count > 0 ? " (partly)" : " ✓"), names + sizes + errors, r.Errors.Count > 0);
    }
}
