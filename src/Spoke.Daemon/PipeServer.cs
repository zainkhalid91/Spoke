using System.Collections.Concurrent;
using System.IO.Pipes;
using Spoke.Core;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Spoke.Daemon;

/// <summary>
/// Serves Spoke Settings over <see cref="Ipc.PipeName"/>. The pipe thread only reads and writes bytes; each
/// request is handed to the UI thread (WM_APP_PIPE) and the thread waits for its answer, so every change to
/// the daemon's state happens where the rest of it does. Idle cost: one blocked thread.
/// </summary>
internal sealed class PipeServer
{
    public readonly ConcurrentQueue<(IpcRequest Request, TaskCompletionSource<IpcReply> Reply)> Pending = new();
    private readonly HWND _notify;
    private volatile bool _stopping;

    public PipeServer(HWND notify)
    {
        _notify = notify;
        var t = new Thread(Loop) { IsBackground = true, Name = "Spoke pipe" };
        t.Start();
    }

    private void Loop()
    {
        while (!_stopping)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(Ipc.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                pipe.WaitForConnection();
                if (Ipc.ReadRequest(pipe) is not { } request) continue;
                var tcs = new TaskCompletionSource<IpcReply>(TaskCreationOptions.RunContinuationsAsynchronously);
                Pending.Enqueue((request, tcs));
                PInvoke.PostMessage(_notify, App.WM_APP_PIPE, default, default);
                var reply = tcs.Task.Wait(3000) ? tcs.Task.Result : new IpcReply { Ok = false, Message = "Spoke is busy." };
                Ipc.WriteReply(pipe, reply);
                pipe.WaitForPipeDrain();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
            {
                // A client that hung up mid-message, or a second Spoke owning the name: try again shortly.
                if (ex is UnauthorizedAccessException) { Log.Write("Pipe unavailable: " + ex.Message); Thread.Sleep(2000); }
            }
        }
    }

    public void Stop() => _stopping = true;
}
