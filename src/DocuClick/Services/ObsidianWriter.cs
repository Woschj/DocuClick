using System.Drawing;
using System.IO;
using System.Text;

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
        var entry = $"{EscapeMarkdown(description)}{Environment.NewLine}![{imageFileName}]({relativeImagePath}){Environment.NewLine}{Environment.NewLine}";
        File.AppendAllText(notePath, entry);
    }

    /// <summary>
    /// Neutralizes characters with block-level meaning in Markdown (heading,
    /// list, blockquote, code fence, table, strikethrough) wherever they
    /// start a line, plus any literal backslash (so the escaping itself
    /// can't be subverted by one already present). <paramref name="text"/>
    /// is a click description built from an arbitrary clicked window/
    /// element's title — content DocuClick has no control over — so it must
    /// never be able to restructure the note it's written into (e.g. a
    /// window titled "# Fake Heading" silently becoming a real heading).
    /// </summary>
    private static string EscapeMarkdown(string text)
    {
        var sb = new StringBuilder(text.Length);
        var atLineStart = true;
        foreach (var c in text)
        {
            if (c == '\\' || (atLineStart && c is '#' or '-' or '*' or '+' or '>' or '`' or '|' or '~'))
            {
                sb.Append('\\');
            }

            sb.Append(c);
            atLineStart = c == '\n';
        }

        return sb.ToString();
    }
}
