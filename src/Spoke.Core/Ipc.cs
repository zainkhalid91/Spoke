using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Spoke.Core;

/// <summary>A request from Spoke Settings to the daemon.</summary>
public sealed class IpcRequest
{
    /// <summary>status | reload | preview | autostart</summary>
    public string Cmd { get; set; } = "status";
    /// <summary>autostart: "on" or "off".</summary>
    public string? Arg { get; set; }
}

/// <summary>The daemon's answer: always its current state, so every reply doubles as a status update.</summary>
public sealed class IpcReply
{
    public bool Ok { get; set; }
    public string? Message { get; set; }
    /// <summary>The shortcut the daemon actually holds ("" when none is free).</summary>
    public string Hotkey { get; set; } = "";
    public bool Autostart { get; set; }
    /// <summary>Errors in settings.json the daemon is currently ignoring (it keeps the last good config).</summary>
    public List<string> Errors { get; set; } = [];
    public int Slices { get; set; }
    /// <summary>reload: how long applying the new config took inside the daemon.</summary>
    public double ApplyMs { get; set; }
    public int Pid { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(IpcRequest))]
[JsonSerializable(typeof(IpcReply))]
internal sealed partial class IpcJson : JsonSerializerContext;

/// <summary>
/// Settings ⇄ daemon over a named pipe <c>Spoke-{user SID}</c>: one request and one reply per connection,
/// each a 4-byte little-endian length followed by UTF-8 JSON. The pipe is created with CurrentUserOnly,
/// so other accounts on the machine can neither connect nor impersonate the server.
/// </summary>
public static class Ipc
{
    public const int MaxMessage = 64 * 1024;

    public static string PipeName { get; } = "Spoke-" + (CurrentSid() ?? Environment.UserName);

    private static string? CurrentSid()
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var id = WindowsIdentity.GetCurrent();
        return id.User?.Value;
    }

    public static void WriteRequest(Stream s, IpcRequest r) => WriteFrame(s, JsonSerializer.SerializeToUtf8Bytes(r, IpcJson.Default.IpcRequest));
    public static void WriteReply(Stream s, IpcReply r) => WriteFrame(s, JsonSerializer.SerializeToUtf8Bytes(r, IpcJson.Default.IpcReply));
    public static IpcRequest? ReadRequest(Stream s) => ReadFrame(s) is { } b ? JsonSerializer.Deserialize(b, IpcJson.Default.IpcRequest) : null;
    public static IpcReply? ReadReply(Stream s) => ReadFrame(s) is { } b ? JsonSerializer.Deserialize(b, IpcJson.Default.IpcReply) : null;

    public static void WriteFrame(Stream s, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxMessage) throw new InvalidDataException("IPC message too large");
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(len, payload.Length);
        s.Write(len);
        s.Write(payload);
        s.Flush();
    }

    /// <summary>Null when the other side hung up or sent something malformed.</summary>
    public static byte[]? ReadFrame(Stream s)
    {
        Span<byte> len = stackalloc byte[4];
        if (!ReadExactly(s, len)) return null;
        int n = BinaryPrimitives.ReadInt32LittleEndian(len);
        if (n is < 0 or > MaxMessage) return null;
        var buf = new byte[n];
        return ReadExactly(s, buf) ? buf : null;
    }

    private static bool ReadExactly(Stream s, Span<byte> buf)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int r = s.Read(buf[got..]);
            if (r == 0) return false;
            got += r;
        }
        return true;
    }

    /// <summary>Why the last <see cref="Send"/> failed, if it wasn't simply that the daemon isn't running.</summary>
    public static string? LastError { get; private set; }

    /// <summary>Sends one request. Null if the daemon isn't running or didn't answer in time.</summary>
    public static IpcReply? Send(IpcRequest request, int timeoutMs = 1500)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            pipe.Connect(Math.Min(timeoutMs, 500));
            // Synchronous pipes don't support ReadTimeout (setting it throws). The server answers within 3 s
            // even when its UI thread is busy, so a blocking read is bounded.
            WriteRequest(pipe, request);
            return ReadReply(pipe);
        }
        catch (Exception ex)
        {
            // Not running is normal (TimeoutException); anything else is worth knowing about.
            LastError = ex is TimeoutException ? null : ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }
}
