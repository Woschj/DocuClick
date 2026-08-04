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
        // Screenshots land in Attachments/<session>/ (session = target file
        // name without extension) instead of flat in Attachments/, so
        // different sessions' images don't all pile into one folder.
        var sessionName = Path.GetFileNameWithoutExtension(noteFileName);
        var imageRelativeToAttachments = AttachmentSaver.SaveScreenshot(_config, screenshot, timestamp, sessionName);

        var notePath = Path.Combine(_config.VaultPath, noteFileName);
        var noteDirectory = Path.GetDirectoryName(notePath) ?? _config.VaultPath;
        var imagePath = Path.Combine(_config.VaultPath, _config.AttachmentsFolder, imageRelativeToAttachments);

        // Standard Markdown image syntax with a relative path, not
        // Obsidian's own ![[wikilink]] embed — Obsidian renders both, but
        // only the standard syntax also works in GitHub/GitLab wikis and
        // plain CommonMark viewers.
        var relativeImagePath = Path.GetRelativePath(noteDirectory, imagePath).Replace('\\', '/');
        var imageFileName = Path.GetFileName(imageRelativeToAttachments);
        var entry = $"{description}{Environment.NewLine}![{imageFileName}]({relativeImagePath}){Environment.NewLine}{Environment.NewLine}";
        File.AppendAllText(notePath, entry);
    }
}
