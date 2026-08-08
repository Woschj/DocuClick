using System.Collections.Concurrent;
using System.IO;

namespace DocuClick.Services;

/// <summary>
/// Minimal file logger so failures are visible outside a debugger —
/// %APPDATA%/DocuClick/log.txt survives a normal double-click launch,
/// unlike Debug.WriteLine output.
///
/// <see cref="Log"/> only ever enqueues; the actual file write happens on a
/// dedicated background thread. This matters because callers include
/// MouseHookService/KeyboardHookService's own WH_MOUSE_LL/WH_KEYBOARD_LL
/// hook callbacks — Windows silently unhooks a low-level hook that takes
/// too long to return, and a synchronous File.AppendAllText under a lock
/// shared with the writer thread's own logging was exactly that kind of
/// risk (confirmed as a plausible cause of clicks silently no longer being
/// recorded partway through a session, with no visible error). Every
/// click/keypress on the whole system runs through this while a session is
/// recording, not just the ones DocuClick ends up using — so the enqueue
/// itself also needs to stay allocation-light and never block.
/// </summary>
public static class LogService
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DocuClick", "log.txt");

    private static readonly BlockingCollection<string> Queue = new();
    private static readonly Thread WriterThread;

    static LogService()
    {
        WriterThread = new Thread(RunQueue) { IsBackground = true, Name = "DocuClick-Logger" };
        WriterThread.Start();
    }

    public static void Log(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
        try
        {
            Queue.Add(line);
        }
        catch (Exception)
        {
            // Queue already completed (shutdown race) or otherwise
            // unavailable — logging must never be the reason the app
            // crashes or a hook callback stalls.
        }
    }

    private static void RunQueue()
    {
        foreach (var line in Queue.GetConsumingEnumerable())
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
            catch (Exception)
            {
                // Logging must never be the reason the app crashes.
            }
        }
    }
}
