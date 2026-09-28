using System.IO;

namespace DocuClick.Services;

/// <summary>
/// Retries a file save a few times with a short backoff before giving up —
/// covers the common case of the target file being briefly locked by
/// another process (draw.io Desktop, Obsidian, a OneDrive sync or antivirus
/// scan reacting to the just-changed file) at the exact moment DocuClick
/// tries to write it. Without this, a single locked-file moment turned into
/// a hard failure for that click: the card only ever existed in memory from
/// then on, silently lost for good if the session was stopped before the
/// next successful save happened to flush it along.
/// </summary>
public static class FileSaveRetry
{
    private static readonly TimeSpan[] Backoffs = { TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(400) };

    /// <summary>
    /// Runs <paramref name="save"/>, retrying on <see cref="IOException"/>
    /// up to <see cref="Backoffs"/>.Length extra times with a short sleep
    /// between attempts. If every attempt fails, rethrows as an
    /// <see cref="IOException"/> with a message that names the likely cause
    /// instead of the raw, technical exception text.
    /// </summary>
    public static void Save(string filePath, Action save)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                save();
                return;
            }
            catch (IOException) when (attempt < Backoffs.Length)
            {
                Thread.Sleep(Backoffs[attempt]);
            }
            catch (IOException ex)
            {
                throw new IOException(
                    $"Die Datei \"{Path.GetFileName(filePath)}\" konnte nicht gespeichert werden — sie ist vermutlich gerade in einem anderen Programm geöffnet (z. B. draw.io, Obsidian) oder wird von einer Synchronisierung/einem Virenscanner gesperrt. Bitte dort schließen und den letzten Schritt erneut versuchen.",
                    ex);
            }
        }
    }
}
