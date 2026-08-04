using System.Drawing;
using System.IO;

namespace DocuClick.Services;

/// <summary>
/// Writes directly into the vault's filesystem — an Obsidian vault is just
/// a folder of Markdown files, so no plugin/REST API is required.
/// </summary>
public sealed class ObsidianWriter
{
    private readonly AppConfig _config;

    public ObsidianWriter(AppConfig config)
    {
        _config = config;
    }

    public void AppendEntry(string noteFileName, string description, Bitmap screenshot, DateTime timestamp)
    {
        var imageFileName = AttachmentSaver.SaveScreenshot(_config, screenshot, timestamp);

        var notePath = Path.Combine(_config.VaultPath, noteFileName);
        var entry = $"{description}{Environment.NewLine}![[{imageFileName}]]{Environment.NewLine}{Environment.NewLine}";
        File.AppendAllText(notePath, entry);
    }
}
