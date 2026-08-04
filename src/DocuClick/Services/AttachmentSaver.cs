using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace DocuClick.Services;

/// <summary>Shared by ObsidianWriter (note mode) and CanvasFlowWriter (canvas mode).</summary>
public static class AttachmentSaver
{
    public static string SaveScreenshot(AppConfig config, Bitmap screenshot, DateTime timestamp)
    {
        if (string.IsNullOrWhiteSpace(config.VaultPath))
        {
            throw new InvalidOperationException("Kein Obsidian-Vault-Pfad konfiguriert.");
        }

        var attachmentsDir = Path.Combine(config.VaultPath, config.AttachmentsFolder);
        Directory.CreateDirectory(attachmentsDir);

        var imageFileName = $"screenshot_{timestamp:yyyyMMdd_HHmmss_fff}.png";
        screenshot.Save(Path.Combine(attachmentsDir, imageFileName), ImageFormat.Png);
        return imageFileName;
    }
}
