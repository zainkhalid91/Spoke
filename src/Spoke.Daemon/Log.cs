using System.Diagnostics;

namespace Spoke.Daemon;

/// <summary>Tiny local-only log in %LOCALAPPDATA%\Spoke. Never called on the hot path.</summary>
internal static class Log
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Spoke");

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.AppendAllText(Path.Combine(Dir, "spoke.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch { /* logging must never crash the daemon */ }
    }

    public static double Ms(long fromTicks, long toTicks) => (toTicks - fromTicks) * 1000.0 / Stopwatch.Frequency;
}
