using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace DocuClick.Services;

/// <summary>Used by CanvasFlowWriter to save every click's screenshot.</summary>
public static class AttachmentSaver
{
    /// <summary>
    /// Saves a screenshot under Attachments/&lt;sessionName&gt;/ instead of
    /// directly in Attachments/, so screenshots from different sessions
    /// don't all pile up flat in one folder. <paramref name="sessionName"/>
    /// is normally the target file's name without extension.
    /// </summary>
    /// <returns>A tuple of the saved file's path relative to Attachments and the raw PNG bytes.</returns>
    public static (string RelativePath, byte[] PngBytes) SaveScreenshot(AppConfig config, Bitmap screenshot, DateTime timestamp, string sessionName)
    {
        if (string.IsNullOrWhiteSpace(config.OutputPath))
        {
            throw new InvalidOperationException("Kein Ausgabeordner konfiguriert.");
        }

        var subfolder = SanitizeSessionName(sessionName);
        var attachmentsDir = Path.Combine(config.OutputPath, config.AttachmentsFolder, subfolder);
        Directory.CreateDirectory(attachmentsDir);

        // No "screenshot_" prefix and no date: the enclosing session
        // subfolder already carries both (it's named after the session,
        // which itself is dated) — repeating either in every single
        // filename was pure redundancy. Time-of-day + milliseconds is still
        // enough to stay unique within one session's folder.
        var imageFileName = $"{timestamp:HHmmss_fff}.png";
        var fullPath = Path.Combine(attachmentsDir, imageFileName);

        using var ms = new MemoryStream();
        screenshot.Save(ms, ImageFormat.Png);
        var bytes = ms.ToArray();
        File.WriteAllBytes(fullPath, bytes);

        return (Path.Combine(subfolder, imageFileName), bytes);
    }

    /// <summary>
    /// Copies an external image file to Attachments/&lt;sessionName&gt;/ and returns its relative path and bytes.
    /// </summary>
    public static (string RelativePath, byte[] ImageBytes) SaveImage(AppConfig config, string sourceFilePath, string sessionName)
    {
        if (string.IsNullOrWhiteSpace(config.OutputPath))
        {
            throw new InvalidOperationException("Kein Ausgabeordner konfiguriert.");
        }

        var subfolder = SanitizeSessionName(sessionName);
        var attachmentsDir = Path.Combine(config.OutputPath, config.AttachmentsFolder, subfolder);
        Directory.CreateDirectory(attachmentsDir);

        var ext = Path.GetExtension(sourceFilePath);
        if (string.IsNullOrEmpty(ext)) ext = ".png";
        var fileNameWithoutExt = Path.GetFileNameWithoutExtension(sourceFilePath);
        var imageFileName = $"{DateTime.Now:HHmmss_fff}_{SanitizeSessionName(fileNameWithoutExt)}{ext}";
        var fullPath = Path.Combine(attachmentsDir, imageFileName);

        var bytes = File.ReadAllBytes(sourceFilePath);
        File.WriteAllBytes(fullPath, bytes);

        return (Path.Combine(subfolder, imageFileName), bytes);
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
