using System.IO;

namespace DocuClick.Services;

/// <summary>
/// Minimal file logger so failures are visible outside a debugger —
/// %APPDATA%/DocuClick/log.txt survives a normal double-click launch,
/// unlike Debug.WriteLine output.
/// </summary>
public static class LogService
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DocuClick", "log.txt");

    private static readonly object Lock = new();

    public static void Log(string message)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // Logging must never be the reason the app crashes.
        }
    }
}
