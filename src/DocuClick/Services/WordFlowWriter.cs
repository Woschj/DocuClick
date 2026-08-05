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
/// Writes clicks as a sequential Word document (.docx) — one Heading3 +
/// screenshot per click, appended top-to-bottom, instead of a spatial
/// diagram canvas. This is deliberately NOT a diagram layout (no columns,
/// no boxes-and-arrows positioning): long flows are the exact case where a
/// fixed canvas (Obsidian Canvas, draw.io) becomes unreadable, while a
/// plain scrolling document handles any length gracefully and stays fully
/// editable in Word/SharePoint.
///
/// Branches and "resume from point" can't reposition content (Word has no
/// spatial coordinates), so they're made navigable instead: every click is
/// a Heading3 under a Heading1 ("Hauptablauf") or Heading2 ("Abzweigung:
/// &lt;name&gt;") section, which turns Word's own Navigation Pane into a
/// working outline of the flow. A branch point also gets a forward
/// reference inserted right where it happens (see
/// <see cref="InsertForwardReferenceAfterAnchor"/>), and the branch's new
/// section links back to it — both directions are one click away, not
/// just discoverable by scrolling. Each click's heading+image block is
/// wrapped in a Word bookmark (named by node id) so these links can target it.
/// </summary>
public sealed class WordFlowWriter : IFlowWriter
{
    private const long EmuPerPixel = 9525; // OOXML drawing units at 96 DPI
    private const double MaxImageWidthPx = 560;
    private const string BranchMarkerPrefix = "Branch: ";

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
        _branchAnchors.Clear();
        _currentBranchName = null;
        _nextBookmarkId = 0;
        foreach (var bookmarkStart in GetBody().Elements<W.BookmarkStart>())
        {
            if (int.TryParse(bookmarkStart.Id, out var id) && id >= _nextBookmarkId)
            {
                _nextBookmarkId = id + 1;
            }

            var name = bookmarkStart.Name?.Value;
            var label = name is null ? null : ExtractLabel(bookmarkStart);
            if (name is null || label is null)
            {
                continue;
            }

            _labels[name] = label;

            // Rebuild branch anchors by scanning for their marker
            // paragraphs (see MarkBranchAnchor) instead of relying on
            // in-memory state, so a Stop()/Start() cycle on the same file
            // doesn't lose them.
            if (label.StartsWith(BranchMarkerPrefix, StringComparison.Ordinal))
            {
                var branchName = label[BranchMarkerPrefix.Length..].Trim();
                if (branchName.Length > 0)
                {
                    AddOrReplaceAnchor(new BranchAnchor(branchName, name));
                }
            }
        }

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

    /// <summary>
    /// Appends a small, visible "Branch: &lt;name&gt;" marker paragraph
    /// (its own bookmark) — an explicit waypoint in the document rather
    /// than hidden state, so it shows up when reading the file and
    /// survives a Stop()/Start() cycle (see StartSession). Doesn't move
    /// the cursor; only <see cref="JumpToAnchor"/> actually jumps to a
    /// marker. Re-marking an existing name appends a fresh marker (the
    /// newest one wins on the next reload, same as in-memory re-marking).
    /// </summary>
    public BranchActionResult MarkBranchAnchor(string branchName)
    {
        if (_cursorNodeId is null)
        {
            return new BranchActionResult(false, _branchAnchors.Count, null);
        }

        var markerNodeId = "n" + Guid.NewGuid().ToString("N");
        var bookmarkId = (_nextBookmarkId++).ToString();

        var body = GetBody();
        body.Append(new W.BookmarkStart { Id = bookmarkId, Name = markerNodeId });
        body.Append(BuildBranchMarkerParagraph(branchName));
        body.Append(new W.BookmarkEnd { Id = bookmarkId });

        var label = TruncateLabel($"{BranchMarkerPrefix}{branchName}");
        _labels[markerNodeId] = label;
        AddOrReplaceAnchor(new BranchAnchor(branchName, markerNodeId));
        Save();

        return new BranchActionResult(true, _branchAnchors.Count, branchName);
    }

    public List<string> ListBranchAnchorNames() => _branchAnchors.Select(a => a.Name).ToList();

    private void AddOrReplaceAnchor(BranchAnchor anchor)
    {
        var existingIndex = _branchAnchors.FindIndex(a => a.Name == anchor.Name);
        if (existingIndex >= 0)
        {
            _branchAnchors[existingIndex] = anchor;
        }
        else
        {
            _branchAnchors.Add(anchor);
        }
    }

    /// <summary>
    /// Word can't reposition content spatially like Canvas, so a branch is
    /// represented by two structural cues instead: a forward-reference
    /// inserted right at the anchor point (so a reader passing through the
    /// main flow sees "a branch happens here" immediately, not just a bare
    /// marker), and a new Heading2 section — appended at the current end of
    /// the document, since Word has no way to grow an earlier section
    /// in-place — with a backward link to where it started. Both, plus the
    /// Heading1/2/3 outline, make Word's Navigation Pane a substitute for a
    /// spatial diagram: branches are distinguishable, and both directions
    /// are one click away instead of only the anchor being linkable.
    /// </summary>
    public BranchActionResult JumpToAnchor(string branchName)
    {
        var anchor = _branchAnchors.FirstOrDefault(a => a.Name == branchName);
        if (anchor is null)
        {
            return new BranchActionResult(false, _branchAnchors.Count, null);
        }

        InsertForwardReferenceAfterAnchor(anchor.NodeId, branchName);

        GetBody().Append(BuildSectionHeading($"Abzweigung: {branchName}", "Heading2"));
        AppendJumpMarker(anchor.NodeId, "Ausgangspunkt");

        _cursorNodeId = anchor.NodeId;
        _currentBranchName = branchName;
        Save();

        return new BranchActionResult(true, _branchAnchors.Count, branchName);
    }

    /// <summary>
    /// Inserts "→ siehe Abzweigung '&lt;name&gt;'" directly after the
    /// anchor's bookmark, in place — the one spot in this writer that
    /// isn't a plain append, so the branch point itself stays discoverable
    /// while reading straight through instead of only showing up if you
    /// already know to look for it further down. Idempotent per branch
    /// name; stacks additional lines if the same point branches more than
    /// once, in the order each branch was created.
    /// </summary>
    private void InsertForwardReferenceAfterAnchor(string anchorNodeId, string branchName)
    {
        var body = GetBody();
        var bookmarkStart = body.Elements<W.BookmarkStart>().FirstOrDefault(b => b.Name == anchorNodeId);
        if (bookmarkStart is null)
        {
            return;
        }

        OpenXmlElement insertAfterTarget = (OpenXmlElement?)body.Elements<W.BookmarkEnd>().FirstOrDefault(b => b.Id == bookmarkStart.Id) ?? bookmarkStart;
        var referenceText = $"→ siehe Abzweigung „{branchName}“";

        while (insertAfterTarget.NextSibling() is W.Paragraph sibling && sibling.InnerText.StartsWith("→ siehe Abzweigung ", StringComparison.Ordinal))
        {
            if (sibling.InnerText == referenceText)
            {
                return; // already referenced from here
            }

            insertAfterTarget = sibling;
        }

        insertAfterTarget.InsertAfterSelf(new W.Paragraph(
            new W.ParagraphProperties(new W.SpacingBetweenLines { Before = "80", After = "160" }),
            new W.Run(
                new W.RunProperties(new W.Italic(), new W.Color { Val = "7C3AED" }),
                new W.Text(referenceText) { Space = SpaceProcessingModeValues.Preserve })));
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

    private static W.Paragraph BuildBranchMarkerParagraph(string branchName) =>
        new(
            new W.ParagraphProperties(new W.SpacingBetweenLines { Before = "240", After = "80" }),
            new W.Run(
                new W.RunProperties(new W.Bold(), new W.Italic(), new W.Color { Val = "7C3AED" }),
                new W.Text($"{BranchMarkerPrefix}{branchName}") { Space = SpaceProcessingModeValues.Preserve }));

    // Each click's step is itself a (Heading3) node in the Navigation Pane
    // outline, one level below "Hauptablauf"/branch section headings.
    private static W.Paragraph BuildHeadingParagraph(string text) =>
        BuildSectionHeading(text, "Heading3");

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
                    EnsureStylesPart();
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
        EnsureStylesPart();
        GetBody().Append(BuildSectionHeading("Hauptablauf", "Heading1"));
    }

    /// <summary>
    /// Defines Heading1/2/3 (main flow / branch section / click step) so
    /// Word's own Navigation Pane becomes a working outline of the flow —
    /// the closest a linear document gets to the box-and-column diagrams
    /// Canvas/draw.io use. No-op if already present (existing files).
    /// </summary>
    private void EnsureStylesPart()
    {
        if (_wordDoc!.MainDocumentPart!.StyleDefinitionsPart is not null)
        {
            return;
        }

        var stylesPart = _wordDoc.MainDocumentPart.AddNewPart<StyleDefinitionsPart>();
        stylesPart.Styles = new W.Styles(
            new W.Style(new W.StyleName { Val = "Normal" }) { Type = W.StyleValues.Paragraph, StyleId = "Normal", Default = true },
            BuildHeadingStyle("Heading1", "heading 1", outlineLevel: 0, fontSizeHalfPoints: "32", color: "1F1F23"),
            BuildHeadingStyle("Heading2", "heading 2", outlineLevel: 1, fontSizeHalfPoints: "26", color: "7C3AED"),
            BuildHeadingStyle("Heading3", "heading 3", outlineLevel: 2, fontSizeHalfPoints: "24", color: "1F1F23"));
    }

    private static W.Style BuildHeadingStyle(string styleId, string name, int outlineLevel, string fontSizeHalfPoints, string color) =>
        new(
            new W.StyleName { Val = name },
            new W.BasedOn { Val = "Normal" },
            new W.NextParagraphStyle { Val = "Normal" },
            new W.StyleParagraphProperties(
                new W.KeepNext(),
                new W.SpacingBetweenLines { Before = "240", After = "80" },
                new W.OutlineLevel { Val = outlineLevel }),
            new W.StyleRunProperties(
                new W.Bold(),
                new W.Color { Val = color },
                new W.FontSize { Val = fontSizeHalfPoints }))
        {
            Type = W.StyleValues.Paragraph,
            StyleId = styleId
        };

    private static W.Paragraph BuildSectionHeading(string text, string styleId) =>
        new(
            new W.ParagraphProperties(new W.ParagraphStyleId { Val = styleId }),
            new W.Run(new W.Text(text) { Space = SpaceProcessingModeValues.Preserve }));

    private void Save()
    {
        _wordDoc!.MainDocumentPart!.Document!.Save();
        _wordDoc.Save();
        File.WriteAllBytes(_filePath!, _stream!.ToArray());
    }
}
