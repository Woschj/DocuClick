using System.IO;

namespace DocuClick.Services;

/// <summary>Used by CanvasFlowWriter to save every click's screenshot, into the session's own folder — the same one its .html file lives in.</summary>
public static class AttachmentSaver
{
    /// <summary>
    /// Saves a screenshot under &lt;attachmentsDir&gt;/&lt;sessionName&gt;/
    /// instead of directly in the attachments folder, so screenshots from
    /// different sessions sharing one folder don't all pile up flat
    /// together. <paramref name="attachmentsDir"/> is the folder images go to
    /// (see CanvasFlowWriter.AttachmentsDirectory); <paramref name="sessionName"/>
    /// is normally the target file's name without extension.
    /// </summary>
    /// <returns>The saved file's full path and the raw image bytes.</returns>
    public static (string FullPath, byte[] PngBytes) SaveScreenshot(string attachmentsDir, ScreenshotImage screenshot, DateTime timestamp, string sessionName)
    {
        var folder = Path.Combine(attachmentsDir, SanitizeSessionName(sessionName));
        Directory.CreateDirectory(folder);

        // No "screenshot_" prefix and no date: the enclosing session
        // subfolder already carries both (it's named after the session,
        // which itself is dated) — repeating either in every single
        // filename was pure redundancy. Time-of-day + milliseconds is still
        // enough to stay unique within one session's folder.
        var fullPath = Path.Combine(folder, $"{timestamp:HHmmss_fff}.{screenshot.Extension}");
        var bytes = screenshot.Data;
        File.WriteAllBytes(fullPath, bytes);
        return (fullPath, bytes);
    }

    /// <summary>
    /// Copies an external image file into &lt;attachmentsDir&gt;/&lt;sessionName&gt;/
    /// and returns its full path and bytes.
    /// </summary>
    public static (string FullPath, byte[] ImageBytes) SaveImage(string attachmentsDir, string sourceFilePath, string sessionName)
    {
        var folder = Path.Combine(attachmentsDir, SanitizeSessionName(sessionName));
        Directory.CreateDirectory(folder);

        var ext = Path.GetExtension(sourceFilePath);
        if (string.IsNullOrEmpty(ext)) ext = ".png";
        var fileNameWithoutExt = Path.GetFileNameWithoutExtension(sourceFilePath);
        var fullPath = Path.Combine(folder, $"{DateTime.Now:HHmmss_fff}_{SanitizeSessionName(fileNameWithoutExt)}{ext}");

        var bytes = File.ReadAllBytes(sourceFilePath);
        File.WriteAllBytes(fullPath, bytes);
        return (fullPath, bytes);
    }

    // Beyond filesystem-invalid characters, this subfolder name ends up
    // embedded as a literal path segment in Canvas file-nodes — "#"
    // (heading/block anchor) and "^" (block reference) are valid on-disk
    // but have special meaning in
    // Obsidian's own link syntax, and silently break the reference if left
    // in (everything after "#" gets parsed as an anchor, not a path).
    private static readonly char[] ObsidianLinkSpecialChars = { '#', '^' };

    private static string SanitizeSessionName(string sessionName)
    {
        var name = string.IsNullOrWhiteSpace(sessionName) ? "Session" : sessionName;
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalidChar, '_');
        }

        foreach (var specialChar in ObsidianLinkSpecialChars)
        {
            name = name.Replace(specialChar, '_');
        }

        return name;
    }
}
