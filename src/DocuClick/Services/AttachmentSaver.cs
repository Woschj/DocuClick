using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace DocuClick.Services;

/// <summary>Shared by ObsidianWriter (note mode) and CanvasFlowWriter (canvas mode).</summary>
public static class AttachmentSaver
{
    /// <summary>
    /// Saves a screenshot under Attachments/&lt;sessionName&gt;/ instead of
    /// directly in Attachments/, so screenshots from different sessions
    /// don't all pile up flat in one folder. <paramref name="sessionName"/>
    /// is normally the target file's name without extension.
    /// </summary>
    /// <returns>The saved file's path relative to the Attachments folder (e.g. "MySession/screenshot_....png").</returns>
    public static string SaveScreenshot(AppConfig config, Bitmap screenshot, DateTime timestamp, string sessionName)
    {
        if (string.IsNullOrWhiteSpace(config.VaultPath))
        {
            throw new InvalidOperationException("Kein Obsidian-Vault-Pfad konfiguriert.");
        }

        var subfolder = SanitizeSessionName(sessionName);
        var attachmentsDir = Path.Combine(config.VaultPath, config.AttachmentsFolder, subfolder);
        Directory.CreateDirectory(attachmentsDir);

        var imageFileName = $"screenshot_{timestamp:yyyyMMdd_HHmmss_fff}.png";
        screenshot.Save(Path.Combine(attachmentsDir, imageFileName), ImageFormat.Png);
        return Path.Combine(subfolder, imageFileName);
    }

    private static string SanitizeSessionName(string sessionName)
    {
        var name = string.IsNullOrWhiteSpace(sessionName) ? "Session" : sessionName;
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalidChar, '_');
        }

        return name;
    }
}
