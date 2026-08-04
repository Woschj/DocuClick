using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace DocuClick.Services;

/// <summary>
/// Writes clicks as a sequential Word document (.docx) — one heading +
/// screenshot per click, appended top-to-bottom, instead of a spatial
/// diagram canvas. This is deliberately NOT a diagram layout (no columns,
/// no boxes-and-arrows positioning): long flows are the exact case where a
/// fixed canvas (Obsidian Canvas, draw.io) becomes unreadable, while a
/// plain scrolling document handles any length gracefully and stays fully
/// editable in Word/SharePoint.
///
/// Branches and "resume from point" don't reposition content (Word has no
/// spatial coordinates) — instead a small heading with an internal
/// hyperlink back to the anchor is appended, and new clicks continue
/// sequentially from there. Each click's heading+image block is wrapped in
/// a Word bookmark (named by node id) so later branch/resume actions can
/// link back to it.
/// </summary>
public sealed class WordFlowWriter : IFlowWriter
{
    private const long EmuPerPixel = 9525; // OOXML drawing units at 96 DPI
    private const double MaxImageWidthPx = 560;

    private readonly AppConfig _config;

    private string? _filePath;
    private MemoryStream? _stream;
    private WordprocessingDocument? _wordDoc;

    private sealed record BranchAnchor(string Name, string NodeId);

    private string? _cursorNodeId;
    private int _nextBookmarkId;
    private uint _nextDrawingId = 1;
    private string? _currentBranchName;
    private readonly Dictionary<string, string> _labels = new();
    private readonly List<BranchAnchor> _branchAnchors = new();
    private string? _pendingResumeAnchor;

    public WordFlowWriter(AppConfig config)
    {
        _config = config;
    }

    public int BranchDepth => _branchAnchors.Count;

    public string? CurrentBranchName => _currentBranchName;

    public string? CurrentNodeLabel => _cursorNodeId is null ? null : _labels.GetValueOrDefault(_cursorNodeId);

    public List<ResumableNode> ListNodesForResume(string fileName)
    {
        if (string.IsNullOrWhiteSpace(_config.VaultPath))
        {
            return new List<ResumableNode>();
        }

        var path = Path.Combine(_config.VaultPath, fileName);
        if (!File.Exists(path))
        {
            return new List<ResumableNode>();
        }

        try
        {
            using var doc = WordprocessingDocument.Open(path, isEditable: false);
            var body = doc.MainDocumentPart?.Document?.Body;
            if (body is null)
            {
                return new List<ResumableNode>();
            }

            var result = new List<ResumableNode>();
            double order = 0;
            foreach (var bookmarkStart in body.Elements<W.BookmarkStart>())
            {
                var name = bookmarkStart.Name?.Value;
                if (name is null)
                {
                    continue;
                }

                result.Add(new ResumableNode(name, ExtractLabel(bookmarkStart) ?? "(ohne Beschreibung)", 0, order++));
            }

            return result;
        }
        catch (Exception ex)
        {
            LogService.Log($"Word-Datei konnte für Fortsetzung nicht gelesen werden: {ex.Message}");
            return new List<ResumableNode>();
        }
    }

    public void SetResumeAnchor(ResumableNode node) => _pendingResumeAnchor = node.Id;

    public void StartSession(string fileName)
    {
        if (string.IsNullOrWhiteSpace(_config.VaultPath))
        {
            throw new InvalidOperationException("Kein Zielordner konfiguriert.");
        }

        _filePath = Path.Combine(_config.VaultPath, fileName);
        OpenOrCreateDocument();

        _labels.Clear();
        _nextBookmarkId = 0;
        foreach (var bookmarkStart in GetBody().Elements<W.BookmarkStart>())
        {
            if (int.TryParse(bookmarkStart.Id, out var id) && id >= _nextBookmarkId)
            {
                _nextBookmarkId = id + 1;
            }

            var name = bookmarkStart.Name?.Value;
            var label = name is null ? null : ExtractLabel(bookmarkStart);
            if (name is not null && label is not null)
            {
                _labels[name] = label;
            }
        }

        _branchAnchors.Clear();
        _currentBranchName = null;

        if (_pendingResumeAnchor is { } resume && _labels.ContainsKey(resume))
        {
            AppendJumpMarker(resume, "Fortsetzung ab");
            _cursorNodeId = resume;
            Save();
        }
        else
        {
            _cursorNodeId = null;
        }

        _pendingResumeAnchor = null;
    }

    public void Stop()
    {
        _cursorNodeId = null;
        _branchAnchors.Clear();
        _currentBranchName = null;
        _wordDoc?.Dispose();
        _wordDoc = null;
        _stream?.Dispose();
        _stream = null;
    }

    public void AddClickNode(string description, Bitmap screenshot, DateTime timestamp)
    {
        if (_filePath is null || _wordDoc is null)
        {
            throw new InvalidOperationException("Word-Session wurde nicht gestartet.");
        }

        var nodeId = "n" + Guid.NewGuid().ToString("N");
        var bookmarkId = (_nextBookmarkId++).ToString();

        var body = GetBody();
        body.Append(new W.BookmarkStart { Id = bookmarkId, Name = nodeId });
        body.Append(BuildHeadingParagraph(description));
        body.Append(BuildImageParagraph(screenshot));
        body.Append(new W.BookmarkEnd { Id = bookmarkId });

        _labels[nodeId] = TruncateLabel(description);
        _cursorNodeId = nodeId;

        Save();
    }

    public BranchActionResult MarkBranchAnchor(string branchName)
    {
        if (_cursorNodeId is null)
        {
            return new BranchActionResult(false, _branchAnchors.Count, null);
        }

        var anchor = new BranchAnchor(branchName, _cursorNodeId);
        var existingIndex = _branchAnchors.FindIndex(a => a.Name == branchName);
        if (existingIndex >= 0)
        {
            _branchAnchors[existingIndex] = anchor;
        }
        else
        {
            _branchAnchors.Add(anchor);
        }

        return new BranchActionResult(true, _branchAnchors.Count, branchName);
    }

    public List<string> ListBranchAnchorNames() => _branchAnchors.Select(a => a.Name).ToList();

    public BranchActionResult JumpToAnchor(string branchName)
    {
        var anchor = _branchAnchors.FirstOrDefault(a => a.Name == branchName);
        if (anchor is null)
        {
            return new BranchActionResult(false, _branchAnchors.Count, null);
        }

        AppendJumpMarker(anchor.NodeId, $"Abzweigung '{branchName}' von");
        _cursorNodeId = anchor.NodeId;
        _currentBranchName = branchName;
        Save();

        return new BranchActionResult(true, _branchAnchors.Count, branchName);
    }

    private void AppendJumpMarker(string anchorNodeId, string verb)
    {
        var label = _labels.GetValueOrDefault(anchorNodeId, "(ohne Beschreibung)");
        var hyperlink = new W.Hyperlink(
            new W.Run(
                new W.RunProperties(new W.Color { Val = "0563C1" }, new W.Underline { Val = W.UnderlineValues.Single }),
                new W.Text(label)))
        {
            Anchor = anchorNodeId,
            History = true
        };

        var paragraph = new W.Paragraph(
            new W.ParagraphProperties(new W.SpacingBetweenLines { Before = "240", After = "80" }),
            new W.Run(new W.RunProperties(new W.Italic()), new W.Text($"{verb}: ") { Space = SpaceProcessingModeValues.Preserve }),
            hyperlink);

        GetBody().Append(paragraph);
    }

    private static W.Paragraph BuildHeadingParagraph(string text) =>
        new(
            new W.ParagraphProperties(new W.SpacingBetweenLines { Before = "240", After = "80" }),
            new W.Run(
                new W.RunProperties(new W.Bold(), new W.FontSize { Val = "26" }),
                new W.Text(text) { Space = SpaceProcessingModeValues.Preserve }));

    private W.Paragraph BuildImageParagraph(Bitmap screenshot)
    {
        var mainPart = _wordDoc!.MainDocumentPart!;
        var imagePart = mainPart.AddImagePart(ImagePartType.Png);
        using (var ms = new MemoryStream())
        {
            screenshot.Save(ms, ImageFormat.Png);
            ms.Position = 0;
            imagePart.FeedData(ms);
        }

        var relationshipId = mainPart.GetIdOfPart(imagePart);

        var scale = screenshot.Width > MaxImageWidthPx ? MaxImageWidthPx / screenshot.Width : 1.0;
        var widthEmu = (long)(screenshot.Width * scale * EmuPerPixel);
        var heightEmu = (long)(screenshot.Height * scale * EmuPerPixel);
        var drawingId = _nextDrawingId++;

        // wp:inline (not w:drawing) carries the distT/B/L/R attributes.
        var inline = new DW.Inline(
                new DW.Extent { Cx = widthEmu, Cy = heightEmu },
                new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
                new DW.DocProperties { Id = drawingId, Name = "Screenshot" },
                new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
                new A.Graphic(
                    new A.GraphicData(
                        new PIC.Picture(
                            new PIC.NonVisualPictureProperties(
                                new PIC.NonVisualDrawingProperties { Id = 1U, Name = "Screenshot.png" },
                                new PIC.NonVisualPictureDrawingProperties()),
                            new PIC.BlipFill(
                                new A.Blip { Embed = relationshipId },
                                new A.Stretch(new A.FillRectangle())),
                            new PIC.ShapeProperties(
                                new A.Transform2D(
                                    new A.Offset { X = 0L, Y = 0L },
                                    new A.Extents { Cx = widthEmu, Cy = heightEmu }),
                                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
                    { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
            {
                DistanceFromTop = 0U,
                DistanceFromBottom = 0U,
                DistanceFromLeft = 0U,
                DistanceFromRight = 0U
            };

        return new W.Paragraph(new W.Run(new W.Drawing(inline)));
    }

    private static string? ExtractLabel(OpenXmlElement bookmarkStart)
    {
        if (bookmarkStart.NextSibling() is W.Paragraph paragraph)
        {
            var text = paragraph.InnerText;
            return string.IsNullOrEmpty(text) ? "(ohne Beschreibung)" : TruncateLabel(text);
        }

        return null;
    }

    private static string TruncateLabel(string text) => text.Length > 70 ? text[..70] + "…" : text;

    private W.Body GetBody() => _wordDoc!.MainDocumentPart!.Document!.Body!;

    private void OpenOrCreateDocument()
    {
        _wordDoc?.Dispose();
        _stream?.Dispose();
        _stream = new MemoryStream();

        if (File.Exists(_filePath))
        {
            try
            {
                var bytes = File.ReadAllBytes(_filePath!);
                _stream.Write(bytes, 0, bytes.Length);
                _stream.Position = 0;
                var opened = WordprocessingDocument.Open(_stream, isEditable: true);
                if (opened.MainDocumentPart?.Document?.Body is not null)
                {
                    _wordDoc = opened;
                    return;
                }

                opened.Dispose();
            }
            catch (Exception ex)
            {
                LogService.Log($"Word-Datei konnte nicht gelesen werden, beginne neu: {ex.Message}");
            }

            _stream = new MemoryStream();
        }

        _wordDoc = WordprocessingDocument.Create(_stream, WordprocessingDocumentType.Document, autoSave: true);
        var mainPart = _wordDoc.AddMainDocumentPart();
        mainPart.Document = new W.Document(new W.Body());
    }

    private void Save()
    {
        _wordDoc!.MainDocumentPart!.Document!.Save();
        _wordDoc.Save();
        File.WriteAllBytes(_filePath!, _stream!.ToArray());
    }
}
