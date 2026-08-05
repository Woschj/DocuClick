using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using P = DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;

namespace DocuClick.Services;

/// <summary>
/// Writes clicks as a real spatial flowchart in a PowerPoint (.pptx) deck —
/// boxes, images, and connector lines with actual x/y coordinates, unlike
/// Word which has none. A single PPTX slide has fixed dimensions though (no
/// infinite canvas like Canvas/Excalidraw/draw.io), so the mapping used here
/// is one slide per "column": the main flow is one slide ("Hauptablauf"),
/// and each named branch gets its own slide ("Abzweigung: &lt;name&gt;"),
/// created lazily on first <see cref="JumpToAnchor"/>. All slides share one
/// presentation-wide width/height (PPTX has no per-slide size), so the
/// height is grown as needed and never shrunk.
///
/// Branch navigation is two hyperlinks, both jumping to a slide (PPTX
/// hyperlinks can't target a position within a slide, only the slide
/// itself): a forward reference appended into the *existing* branch-marker
/// shape's text (so marking a branch doesn't reserve dead space for a link
/// that might never be added, and appending later doesn't disturb any other
/// shape's fixed position), and a backward link on the branch's own slide
/// pointing back to the marker's slide.
/// </summary>
public sealed class PowerPointFlowWriter : IFlowWriter
{
    private const long EmuPerPixel = 9525; // OOXML drawing units at 96 DPI
    private const double NodeWidthPx = 380;
    private const double MarginPx = 60;
    private const double TitleHeightPx = 50;
    private const double LabelHeightPx = 50;
    private const double LabelGapPx = 8;
    private const double SequentialSpacingPx = 50;
    private const double InitialSlideHeightPx = 800;
    private const string BranchMarkerPrefix = "Branch: ";
    private const string MainColumnKey = "";
    private const string StepNamePrefix = "step_";
    private const string BranchMarkerNamePrefix = "branch_";

    private sealed class ColumnState
    {
        public required SlidePart Part;
        public required P.ShapeTree Tree;
        public double NextY;
        public string? LastShapeName;
        public bool HasBackLink;
    }

    private sealed record BranchAnchor(string Name, string NodeId, string ColumnKey);

    private readonly AppConfig _config;

    private string? _filePath;
    private MemoryStream? _stream;
    private PresentationDocument? _pptDoc;
    private SlideLayoutPart? _slideLayoutPart;
    private uint _nextShapeId = 10;
    private uint _nextSlideId = 256;
    private double _slideHeightPx = InitialSlideHeightPx;

    private readonly Dictionary<string, ColumnState> _columns = new();
    private readonly Dictionary<string, string> _labels = new();
    private readonly Dictionary<string, string> _nodeColumn = new();
    private readonly List<BranchAnchor> _branchAnchors = new();
    private string? _currentBranchName;
    private string? _pendingResumeAnchor;

    public PowerPointFlowWriter(AppConfig config)
    {
        _config = config;
    }

    public int BranchDepth => _branchAnchors.Count;

    public string? CurrentBranchName => _currentBranchName;

    public string? CurrentNodeLabel =>
        _columns.TryGetValue(ColumnKey(_currentBranchName), out var column) && column.LastShapeName is not null
            ? _labels.GetValueOrDefault(column.LastShapeName)
            : null;

    private static string ColumnKey(string? branchName) => branchName ?? MainColumnKey;

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
            using var doc = PresentationDocument.Open(path, isEditable: false);
            var presentationPart = doc.PresentationPart;
            if (presentationPart is null)
            {
                return new List<ResumableNode>();
            }

            var result = new List<ResumableNode>();
            double order = 0;
            foreach (var slidePart in presentationPart.SlideParts)
            {
                var tree = slidePart.Slide?.CommonSlideData?.ShapeTree;
                if (tree is null)
                {
                    continue;
                }

                foreach (var shape in tree.Elements<P.Shape>())
                {
                    var name = shape.NonVisualShapeProperties?.NonVisualDrawingProperties?.Name?.Value;
                    if (name is null || !name.StartsWith(StepNamePrefix, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var text = ExtractShapeText(shape);
                    result.Add(new ResumableNode(name, string.IsNullOrEmpty(text) ? "(ohne Beschreibung)" : TruncateLabel(text), 0, order++));
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            LogService.Log($"PowerPoint-Datei konnte für Fortsetzung nicht gelesen werden: {ex.Message}");
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
        _nodeColumn.Clear();
        _branchAnchors.Clear();
        _columns.Clear();
        _currentBranchName = null;
        _nextShapeId = 10;
        _nextSlideId = 256;

        var presentationPart = _pptDoc!.PresentationPart!;
        _slideHeightPx = (presentationPart.Presentation!.SlideSize?.Cy?.Value ?? (int)(InitialSlideHeightPx * EmuPerPixel)) / (double)EmuPerPixel;

        foreach (var slideId in presentationPart.Presentation.SlideIdList?.Elements<P.SlideId>() ?? Enumerable.Empty<P.SlideId>())
        {
            if (slideId.Id?.Value is uint id && id >= _nextSlideId)
            {
                _nextSlideId = id + 1;
            }
        }

        foreach (var slidePart in presentationPart.SlideParts)
        {
            RebuildColumnFromSlide(slidePart);
        }

        if (_pendingResumeAnchor is { } resume && _nodeColumn.TryGetValue(resume, out var resumeColumnKey)
            && _columns.TryGetValue(resumeColumnKey, out var resumeColumn))
        {
            _currentBranchName = resumeColumnKey == MainColumnKey ? null : resumeColumnKey;
            // NextY was already computed from this column's true current
            // bottom (see RebuildColumnFromSlide), so the next click can't
            // overlap anything regardless of where the resumed node
            // physically sits — this only affects which label shows up as
            // "current" in status messages.
            resumeColumn.LastShapeName = resume;
        }

        _pendingResumeAnchor = null;
    }

    private void RebuildColumnFromSlide(SlidePart slidePart)
    {
        var tree = slidePart.Slide?.CommonSlideData?.ShapeTree;
        if (tree is null)
        {
            return;
        }

        string? columnKey = null;
        double maxBottomPx = 0;
        string? lastLabelName = null;
        double lastLabelY = -1;

        foreach (var shape in tree.Elements<P.Shape>())
        {
            var nvProps = shape.NonVisualShapeProperties?.NonVisualDrawingProperties;
            var name = nvProps?.Name?.Value;
            if (nvProps?.Id?.Value is uint id && id >= _nextShapeId)
            {
                _nextShapeId = id + 1;
            }

            var (y, height) = GetGeometryPx(shape.ShapeProperties);
            maxBottomPx = Math.Max(maxBottomPx, y + height);

            if (name == "title")
            {
                var text = ExtractShapeText(shape);
                if (text == "Hauptablauf")
                {
                    columnKey = MainColumnKey;
                }
                else if (text.StartsWith("Abzweigung: ", StringComparison.Ordinal))
                {
                    columnKey = text["Abzweigung: ".Length..];
                }
            }
            else if (name is not null && name.StartsWith(StepNamePrefix, StringComparison.Ordinal))
            {
                _labels[name] = TruncateLabel(ExtractShapeText(shape));
                if (y > lastLabelY)
                {
                    lastLabelY = y;
                    lastLabelName = name;
                }
            }
            else if (name is not null && name.StartsWith(BranchMarkerNamePrefix, StringComparison.Ordinal))
            {
                var firstLine = ExtractShapeText(shape).Split('\n', 2)[0];
                if (firstLine.StartsWith(BranchMarkerPrefix, StringComparison.Ordinal))
                {
                    var branchName = firstLine[BranchMarkerPrefix.Length..].Trim();
                    if (branchName.Length > 0)
                    {
                        _labels[name] = TruncateLabel(firstLine);
                        AddOrReplaceAnchor(new BranchAnchor(branchName, name, columnKey ?? MainColumnKey));
                    }
                }

                if (y > lastLabelY)
                {
                    lastLabelY = y;
                    lastLabelName = name;
                }
            }
        }

        foreach (var picture in tree.Elements<P.Picture>())
        {
            var (y, height) = GetGeometryPx(picture.ShapeProperties);
            maxBottomPx = Math.Max(maxBottomPx, y + height);
        }

        var key = columnKey ?? MainColumnKey;
        var column = new ColumnState
        {
            Part = slidePart,
            Tree = tree,
            NextY = maxBottomPx > 0 ? maxBottomPx + SequentialSpacingPx : MarginPx + TitleHeightPx + SequentialSpacingPx,
            LastShapeName = lastLabelName,
            HasBackLink = tree.Elements<P.Shape>().Any(s => s.NonVisualShapeProperties?.NonVisualDrawingProperties?.Name?.Value == "backlink")
        };
        _columns[key] = column;

        if (lastLabelName is not null)
        {
            _nodeColumn[lastLabelName] = key;
        }
    }

    private static (double Y, double Height) GetGeometryPx(P.ShapeProperties? shapeProperties)
    {
        var offset = shapeProperties?.Transform2D?.Offset;
        var extents = shapeProperties?.Transform2D?.Extents;
        var y = (offset?.Y?.Value ?? 0) / (double)EmuPerPixel;
        var height = (extents?.Cy?.Value ?? 0) / (double)EmuPerPixel;
        return (y, height);
    }

    public void Stop()
    {
        _currentBranchName = null;
        _branchAnchors.Clear();
        _pptDoc?.Dispose();
        _pptDoc = null;
        _stream?.Dispose();
        _stream = null;
    }

    public void AddClickNode(string description, Bitmap screenshot, DateTime timestamp)
    {
        if (_pptDoc is null)
        {
            throw new InvalidOperationException("PowerPoint-Session wurde nicht gestartet.");
        }

        var columnKey = ColumnKey(_currentBranchName);
        var column = GetOrCreateColumn(columnKey, columnKey == MainColumnKey ? "Hauptablauf" : $"Abzweigung: {columnKey}");

        var imageHeightPx = screenshot.Height * (NodeWidthPx / screenshot.Width);
        var topY = column.NextY;

        if (column.LastShapeName is not null)
        {
            column.Tree.Append(BuildConnector(topY - SequentialSpacingPx, topY));
        }

        var nodeId = StepNamePrefix + Guid.NewGuid().ToString("N");
        column.Tree.Append(BuildLabelShape(nodeId, description, topY));
        column.Tree.Append(BuildImageShape(column.Part, screenshot, topY + LabelHeightPx + LabelGapPx, imageHeightPx));

        var blockBottom = topY + LabelHeightPx + LabelGapPx + imageHeightPx;
        column.NextY = blockBottom + SequentialSpacingPx;
        column.LastShapeName = nodeId;

        _labels[nodeId] = TruncateLabel(description);
        _nodeColumn[nodeId] = columnKey;

        EnsureSlideHeight(blockBottom + MarginPx);
        Save();
    }

    public BranchActionResult MarkBranchAnchor(string branchName)
    {
        var columnKey = ColumnKey(_currentBranchName);
        if (!_columns.TryGetValue(columnKey, out var column) || column.LastShapeName is null)
        {
            return new BranchActionResult(false, _branchAnchors.Count, null);
        }

        var markerY = column.NextY;
        var markerName = BranchMarkerNamePrefix + Guid.NewGuid().ToString("N");

        column.Tree.Append(BuildConnector(markerY - SequentialSpacingPx, markerY));
        column.Tree.Append(BuildMarkerShape(markerName, $"{BranchMarkerPrefix}{branchName}", markerY));

        column.NextY = markerY + LabelHeightPx + SequentialSpacingPx;
        column.LastShapeName = markerName;

        _labels[markerName] = TruncateLabel($"{BranchMarkerPrefix}{branchName}");
        _nodeColumn[markerName] = columnKey;
        AddOrReplaceAnchor(new BranchAnchor(branchName, markerName, columnKey));

        EnsureSlideHeight(column.NextY + MarginPx);
        Save();

        return new BranchActionResult(true, _branchAnchors.Count, branchName);
    }

    /// <summary>
    /// Word can grow a section in-place because it reflows; a PPTX slide
    /// can't — every shape has a fixed position, so each branch instead
    /// gets its own dedicated slide. Navigation between them is two
    /// hyperlinks (PPTX can only jump to a slide, not a position within
    /// it): a forward reference appended into the marker's own text (so it
    /// never needs to displace anything placed after the marker) and,
    /// once, a backward link on the branch's slide.
    /// </summary>
    public BranchActionResult JumpToAnchor(string branchName)
    {
        var anchor = _branchAnchors.FirstOrDefault(a => a.Name == branchName);
        if (anchor is null)
        {
            return new BranchActionResult(false, _branchAnchors.Count, null);
        }

        var branchColumn = GetOrCreateColumn(branchName, $"Abzweigung: {branchName}");

        AppendForwardReference(anchor, branchName, branchColumn.Part);

        if (!branchColumn.HasBackLink)
        {
            AppendBackLink(branchColumn, anchor);
            branchColumn.HasBackLink = true;
            EnsureSlideHeight(branchColumn.NextY + MarginPx);
        }

        _currentBranchName = branchName;
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

    private void AppendForwardReference(BranchAnchor anchor, string branchName, SlidePart targetSlidePart)
    {
        if (!_columns.TryGetValue(anchor.ColumnKey, out var anchorColumn))
        {
            return;
        }

        var markerShape = anchorColumn.Tree.Elements<P.Shape>()
            .FirstOrDefault(s => s.NonVisualShapeProperties?.NonVisualDrawingProperties?.Name?.Value == anchor.NodeId);
        if (markerShape?.TextBody is null)
        {
            return;
        }

        var referenceText = $"→ siehe Folie „Abzweigung: {branchName}“";
        if (markerShape.TextBody.Elements<A.Paragraph>().Any(p => p.InnerText == referenceText))
        {
            return; // already referenced from here
        }

        anchorColumn.Part.AddPart(targetSlidePart);
        var relId = anchorColumn.Part.GetIdOfPart(targetSlidePart);

        // A run-level hyperlink appended into the *existing* marker shape's
        // text body — its Transform2D (position/size) is untouched, so this
        // can never overlap whatever else was placed on the slide since the
        // marker was created.
        markerShape.TextBody.Append(new A.Paragraph(
            new A.Run(
                new A.RunProperties(new A.HyperlinkOnClick { Id = relId, Action = "ppaction://hlinksldjump" }) { Language = "de-DE", FontSize = 1200 },
                new A.Text(referenceText))));
    }

    private void AppendBackLink(ColumnState branchColumn, BranchAnchor anchor)
    {
        if (!_columns.TryGetValue(anchor.ColumnKey, out var anchorColumn))
        {
            return;
        }

        branchColumn.Part.AddPart(anchorColumn.Part);
        var relId = branchColumn.Part.GetIdOfPart(anchorColumn.Part);
        var label = _labels.GetValueOrDefault(anchor.NodeId, "(ohne Beschreibung)");

        var y = branchColumn.NextY;
        var shape = new P.Shape(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = _nextShapeId++, Name = "backlink" },
                new P.NonVisualShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.ShapeProperties(
                new A.Transform2D(
                    new A.Offset { X = (long)(MarginPx * EmuPerPixel), Y = (long)(y * EmuPerPixel) },
                    new A.Extents { Cx = (long)(NodeWidthPx * EmuPerPixel), Cy = (long)(LabelHeightPx * EmuPerPixel) }),
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }),
            new P.TextBody(
                new A.BodyProperties { Wrap = A.TextWrappingValues.Square },
                new A.ListStyle(),
                new A.Paragraph(
                    new A.Run(
                        new A.RunProperties(new A.HyperlinkOnClick { Id = relId, Action = "ppaction://hlinksldjump" }) { Language = "de-DE", FontSize = 1200, Italic = true },
                        new A.Text($"↩ Ausgangspunkt: {label}")))));

        branchColumn.Tree.Append(shape);
        branchColumn.NextY = y + LabelHeightPx + SequentialSpacingPx;
    }

    private P.ConnectionShape BuildConnector(double fromY, double toY)
    {
        var centerX = (long)((MarginPx + NodeWidthPx / 2) * EmuPerPixel);
        return new P.ConnectionShape(
            new P.NonVisualConnectionShapeProperties(
                new P.NonVisualDrawingProperties { Id = _nextShapeId++, Name = "conn" },
                new P.NonVisualConnectorShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.ShapeProperties(
                new A.Transform2D(
                    new A.Offset { X = centerX, Y = (long)(fromY * EmuPerPixel) },
                    new A.Extents { Cx = 0, Cy = (long)((toY - fromY) * EmuPerPixel) }),
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Line },
                new A.Outline(new A.SolidFill(new A.RgbColorModelHex { Val = "9A9AA2" })) { Width = 12700 }));
    }

    private P.Shape BuildLabelShape(string name, string text, double y) => new(
        new P.NonVisualShapeProperties(
            new P.NonVisualDrawingProperties { Id = _nextShapeId++, Name = name },
            new P.NonVisualShapeDrawingProperties(),
            new P.ApplicationNonVisualDrawingProperties()),
        new P.ShapeProperties(
            new A.Transform2D(
                new A.Offset { X = (long)(MarginPx * EmuPerPixel), Y = (long)(y * EmuPerPixel) },
                new A.Extents { Cx = (long)(NodeWidthPx * EmuPerPixel), Cy = (long)(LabelHeightPx * EmuPerPixel) }),
            new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle },
            new A.SolidFill(new A.RgbColorModelHex { Val = "F5F5F5" })),
        new P.TextBody(
            new A.BodyProperties { Wrap = A.TextWrappingValues.Square },
            new A.ListStyle(),
            new A.Paragraph(new A.Run(new A.RunProperties { Language = "de-DE", Bold = true, FontSize = 1400 }, new A.Text(text)))));

    private P.Shape BuildMarkerShape(string name, string text, double y) => new(
        new P.NonVisualShapeProperties(
            new P.NonVisualDrawingProperties { Id = _nextShapeId++, Name = name },
            new P.NonVisualShapeDrawingProperties(),
            new P.ApplicationNonVisualDrawingProperties()),
        new P.ShapeProperties(
            new A.Transform2D(
                new A.Offset { X = (long)(MarginPx * EmuPerPixel), Y = (long)(y * EmuPerPixel) },
                new A.Extents { Cx = (long)(NodeWidthPx * EmuPerPixel), Cy = (long)(LabelHeightPx * EmuPerPixel) }),
            new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle },
            new A.NoFill()),
        new P.TextBody(
            new A.BodyProperties { Wrap = A.TextWrappingValues.Square },
            new A.ListStyle(),
            new A.Paragraph(
                new A.Run(
                    new A.RunProperties(new A.SolidFill(new A.RgbColorModelHex { Val = "7C3AED" }))
                    { Language = "de-DE", Bold = true, Italic = true, FontSize = 1200 },
                    new A.Text(text)))));

    private P.Shape BuildTitleShape(string text) => new(
        new P.NonVisualShapeProperties(
            new P.NonVisualDrawingProperties { Id = _nextShapeId++, Name = "title" },
            new P.NonVisualShapeDrawingProperties(),
            new P.ApplicationNonVisualDrawingProperties()),
        new P.ShapeProperties(
            new A.Transform2D(
                new A.Offset { X = (long)(MarginPx * EmuPerPixel), Y = (long)(MarginPx * EmuPerPixel) },
                new A.Extents { Cx = (long)(NodeWidthPx * EmuPerPixel), Cy = (long)(TitleHeightPx * EmuPerPixel) }),
            new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }),
        new P.TextBody(
            new A.BodyProperties { Wrap = A.TextWrappingValues.Square },
            new A.ListStyle(),
            new A.Paragraph(new A.Run(new A.RunProperties { Language = "de-DE", Bold = true, FontSize = 2000 }, new A.Text(text)))));

    private P.Picture BuildImageShape(SlidePart slidePart, Bitmap screenshot, double y, double heightPx)
    {
        var imagePart = slidePart.AddImagePart(ImagePartType.Png);
        using (var ms = new MemoryStream())
        {
            screenshot.Save(ms, ImageFormat.Png);
            ms.Position = 0;
            imagePart.FeedData(ms);
        }

        var relId = slidePart.GetIdOfPart(imagePart);
        return new P.Picture(
            new P.NonVisualPictureProperties(
                new P.NonVisualDrawingProperties { Id = _nextShapeId++, Name = "screenshot.png" },
                new P.NonVisualPictureDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.BlipFill(new A.Blip { Embed = relId }, new A.Stretch(new A.FillRectangle())),
            new P.ShapeProperties(
                new A.Transform2D(
                    new A.Offset { X = (long)(MarginPx * EmuPerPixel), Y = (long)(y * EmuPerPixel) },
                    new A.Extents { Cx = (long)(NodeWidthPx * EmuPerPixel), Cy = (long)(heightPx * EmuPerPixel) }),
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }));
    }

    private ColumnState GetOrCreateColumn(string columnKey, string title)
    {
        if (_columns.TryGetValue(columnKey, out var existing))
        {
            return existing;
        }

        var presentationPart = _pptDoc!.PresentationPart!;
        var slidePart = presentationPart.AddNewPart<SlidePart>();
        slidePart.AddPart(_slideLayoutPart!);

        var tree = new P.ShapeTree(
            new P.NonVisualGroupShapeProperties(
                new P.NonVisualDrawingProperties { Id = 1, Name = "" },
                new P.NonVisualGroupShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.GroupShapeProperties());

        tree.Append(BuildTitleShape(title));

        slidePart.Slide = new P.Slide(new P.CommonSlideData(tree), new P.ColorMapOverride(new A.MasterColorMapping()));

        var slideIdList = presentationPart.Presentation!.SlideIdList!;
        slideIdList.Append(new P.SlideId { Id = _nextSlideId++, RelationshipId = presentationPart.GetIdOfPart(slidePart) });

        var column = new ColumnState
        {
            Part = slidePart,
            Tree = tree,
            NextY = MarginPx + TitleHeightPx + SequentialSpacingPx,
            LastShapeName = null
        };
        _columns[columnKey] = column;
        return column;
    }

    private void EnsureSlideHeight(double requiredPx)
    {
        if (requiredPx <= _slideHeightPx)
        {
            return;
        }

        _slideHeightPx = requiredPx;
        _pptDoc!.PresentationPart!.Presentation!.SlideSize!.Cy = (int)(_slideHeightPx * EmuPerPixel);
    }

    private static string ExtractShapeText(P.Shape shape) =>
        string.Join("\n", shape.TextBody?.Elements<A.Paragraph>().Select(p => p.InnerText) ?? Enumerable.Empty<string>());

    private static string TruncateLabel(string text)
    {
        var firstLine = text.Split('\n', 2)[0];
        return firstLine.Length > 70 ? firstLine[..70] + "…" : firstLine;
    }

    private void OpenOrCreateDocument()
    {
        _pptDoc?.Dispose();
        _stream?.Dispose();
        _stream = new MemoryStream();

        if (File.Exists(_filePath))
        {
            try
            {
                var bytes = File.ReadAllBytes(_filePath!);
                _stream.Write(bytes, 0, bytes.Length);
                _stream.Position = 0;
                var opened = PresentationDocument.Open(_stream, isEditable: true);
                var masterPart = opened.PresentationPart?.SlideMasterParts.FirstOrDefault();
                var layoutPart = masterPart?.SlideLayoutParts.FirstOrDefault();
                if (opened.PresentationPart?.Presentation?.SlideIdList is not null && layoutPart is not null)
                {
                    _pptDoc = opened;
                    _slideLayoutPart = layoutPart;
                    return;
                }

                opened.Dispose();
            }
            catch (Exception ex)
            {
                LogService.Log($"PowerPoint-Datei konnte nicht gelesen werden, beginne neu: {ex.Message}");
            }

            _stream = new MemoryStream();
        }

        CreateFreshDocument();
    }

    private void CreateFreshDocument()
    {
        _pptDoc = PresentationDocument.Create(_stream!, PresentationDocumentType.Presentation, autoSave: true);
        var presentationPart = _pptDoc.AddPresentationPart();
        presentationPart.Presentation = new P.Presentation();

        var themePart = presentationPart.AddNewPart<ThemePart>();
        themePart.Theme = BuildTheme();

        var slideMasterPart = presentationPart.AddNewPart<SlideMasterPart>();
        slideMasterPart.AddPart(themePart);

        _slideLayoutPart = slideMasterPart.AddNewPart<SlideLayoutPart>();
        _slideLayoutPart.SlideLayout = BuildBlankSlideLayout();

        slideMasterPart.SlideMaster = BuildSlideMaster(slideMasterPart.GetIdOfPart(_slideLayoutPart));

        presentationPart.Presentation.Append(new P.SlideMasterIdList(
            new P.SlideMasterId { Id = 2147483648U, RelationshipId = presentationPart.GetIdOfPart(slideMasterPart) }));
        presentationPart.Presentation.Append(new P.SlideIdList());
        presentationPart.Presentation.Append(new P.SlideSize
        {
            Cx = (int)((NodeWidthPx + 2 * MarginPx) * EmuPerPixel),
            Cy = (int)(InitialSlideHeightPx * EmuPerPixel)
        });
        presentationPart.Presentation.Append(new P.NotesSize { Cx = 6858000, Cy = 9144000 });
    }

    private static A.Theme BuildTheme()
    {
        var colorScheme = new A.ColorScheme(
            new A.Dark1Color(new A.SystemColor { Val = A.SystemColorValues.WindowText, LastColor = "000000" }),
            new A.Light1Color(new A.SystemColor { Val = A.SystemColorValues.Window, LastColor = "FFFFFF" }),
            new A.Dark2Color(new A.RgbColorModelHex { Val = "44546A" }),
            new A.Light2Color(new A.RgbColorModelHex { Val = "E7E6E6" }),
            new A.Accent1Color(new A.RgbColorModelHex { Val = "4472C4" }),
            new A.Accent2Color(new A.RgbColorModelHex { Val = "ED7D31" }),
            new A.Accent3Color(new A.RgbColorModelHex { Val = "A5A5A5" }),
            new A.Accent4Color(new A.RgbColorModelHex { Val = "FFC000" }),
            new A.Accent5Color(new A.RgbColorModelHex { Val = "5B9BD5" }),
            new A.Accent6Color(new A.RgbColorModelHex { Val = "70AD47" }),
            new A.Hyperlink(new A.RgbColorModelHex { Val = "0563C1" }),
            new A.FollowedHyperlinkColor(new A.RgbColorModelHex { Val = "954F72" }))
        { Name = "Office" };

        var fontScheme = new A.FontScheme(
            new A.MajorFont(new A.LatinFont { Typeface = "Calibri Light" }, new A.EastAsianFont { Typeface = "" }, new A.ComplexScriptFont { Typeface = "" }),
            new A.MinorFont(new A.LatinFont { Typeface = "Calibri" }, new A.EastAsianFont { Typeface = "" }, new A.ComplexScriptFont { Typeface = "" }))
        { Name = "Office" };

        var formatScheme = new A.FormatScheme(
            new A.FillStyleList(
                new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }),
                new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }),
                new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor })),
            new A.LineStyleList(
                new A.Outline(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor })) { Width = 6350 },
                new A.Outline(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor })) { Width = 12700 },
                new A.Outline(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor })) { Width = 19050 }),
            new A.EffectStyleList(
                new A.EffectStyle(new A.EffectList()),
                new A.EffectStyle(new A.EffectList()),
                new A.EffectStyle(new A.EffectList())),
            new A.BackgroundFillStyleList(
                new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }),
                new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }),
                new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor })))
        { Name = "Office" };

        return new A.Theme(new A.ThemeElements(colorScheme, fontScheme, formatScheme)) { Name = "Office Theme" };
    }

    private static P.SlideMaster BuildSlideMaster(string layoutRelId)
    {
        var commonSlideData = new P.CommonSlideData(
            new P.ShapeTree(
                new P.NonVisualGroupShapeProperties(
                    new P.NonVisualDrawingProperties { Id = 1, Name = "" },
                    new P.NonVisualGroupShapeDrawingProperties(),
                    new P.ApplicationNonVisualDrawingProperties()),
                new P.GroupShapeProperties()));

        return new P.SlideMaster(
            commonSlideData,
            new P.ColorMap
            {
                Background1 = A.ColorSchemeIndexValues.Light1,
                Text1 = A.ColorSchemeIndexValues.Dark1,
                Background2 = A.ColorSchemeIndexValues.Light2,
                Text2 = A.ColorSchemeIndexValues.Dark2,
                Accent1 = A.ColorSchemeIndexValues.Accent1,
                Accent2 = A.ColorSchemeIndexValues.Accent2,
                Accent3 = A.ColorSchemeIndexValues.Accent3,
                Accent4 = A.ColorSchemeIndexValues.Accent4,
                Accent5 = A.ColorSchemeIndexValues.Accent5,
                Accent6 = A.ColorSchemeIndexValues.Accent6,
                Hyperlink = A.ColorSchemeIndexValues.Hyperlink,
                FollowedHyperlink = A.ColorSchemeIndexValues.FollowedHyperlink
            },
            new P.SlideLayoutIdList(new P.SlideLayoutId { Id = 2147483649U, RelationshipId = layoutRelId }));
    }

    private static P.SlideLayout BuildBlankSlideLayout()
    {
        var commonSlideData = new P.CommonSlideData(
            new P.ShapeTree(
                new P.NonVisualGroupShapeProperties(
                    new P.NonVisualDrawingProperties { Id = 1, Name = "" },
                    new P.NonVisualGroupShapeDrawingProperties(),
                    new P.ApplicationNonVisualDrawingProperties()),
                new P.GroupShapeProperties()));

        return new P.SlideLayout(commonSlideData) { Type = P.SlideLayoutValues.Blank };
    }

    private void Save()
    {
        _pptDoc!.PresentationPart!.Presentation!.Save();
        _pptDoc.Save();
        File.WriteAllBytes(_filePath!, _stream!.ToArray());
    }
}
