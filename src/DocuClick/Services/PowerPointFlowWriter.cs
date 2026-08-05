using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using P = DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;

namespace DocuClick.Services;

/// <summary>
/// Writes clicks as a PowerPoint (.pptx) walkthrough deck: one slide per
/// click (caption + screenshot, scaled to fit a fixed standard 16:9 slide),
/// the way a person would actually build a click-through tutorial by hand —
/// not a single ever-growing "canvas" slide, which PowerPoint slides were
/// never meant to be (no in-slide scrolling, no meaningful thumbnail, and a
/// custom-grown slide size read back oddly in real PowerPoint).
///
/// Branches don't reposition anything (every column just runs top-to-bottom
/// through the slide deck) — instead, jumping to a branch inserts a small
/// "junction" slide (title + a link back to the anchor) and continues the
/// one-slide-per-click sequence from there; the anchor's own slide gets a
/// forward-reference line pointing at that junction. Because every jump
/// creates a *new* junction slide, the same anchor can be revisited any
/// number of times without ever re-linking to an existing target — each
/// slide-to-slide relationship is created exactly once.
/// </summary>
public sealed class PowerPointFlowWriter : IFlowWriter
{
    private const long EmuPerPixel = 9525; // OOXML drawing units at 96 DPI
    private const double SlideWidthPx = 1280; // 13.333in — standard PowerPoint widescreen
    private const double SlideHeightPx = 720; // 7.5in
    private const double MarginPx = 40;
    private const double TitleHeightPx = 110;
    private const double NoteHeightPx = 40;
    private const double ImageTopPx = MarginPx + TitleHeightPx + 10;
    private const double ImageAreaHeightPx = SlideHeightPx - ImageTopPx - MarginPx - NoteHeightPx - 10;
    private const double ImageAreaWidthPx = SlideWidthPx - 2 * MarginPx;
    private const string BranchMarkerPrefix = "Branch: ";
    private const string StepNamePrefix = "step_";
    private const string BranchMarkerNamePrefix = "branch_";
    private const string MainColumnKey = "";
    private const string MainEyebrow = "HAUPTABLAUF";
    private const string BranchEyebrowPrefix = "ABZWEIGUNG: ";

    private sealed class ColumnState
    {
        public SlidePart? LastSlidePart;
    }

    private sealed record BranchAnchor(string Name, string NodeId, SlidePart Slide);

    private readonly AppConfig _config;

    private string? _filePath;
    private MemoryStream? _stream;
    private PresentationDocument? _pptDoc;
    private SlideLayoutPart? _slideLayoutPart;
    private uint _nextShapeId = 10;
    private uint _nextSlideId = 256;

    private readonly Dictionary<string, ColumnState> _columns = new();
    private readonly Dictionary<string, string> _labels = new();
    private readonly Dictionary<string, SlidePart> _nodeSlide = new();
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

    public string? CurrentNodeLabel
    {
        get
        {
            if (!_columns.TryGetValue(ColumnKey(_currentBranchName), out var column) || column.LastSlidePart is null)
            {
                return null;
            }

            var name = FindStepOrMarkerName(column.LastSlidePart);
            return name is null ? null : _labels.GetValueOrDefault(name);
        }
    }

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

                    var lines = ExtractShapeText(shape).Split('\n', 2);
                    var caption = lines.Length > 1 ? lines[1] : "";
                    result.Add(new ResumableNode(name, string.IsNullOrEmpty(caption) ? "(ohne Beschreibung)" : TruncateLabel(caption), 0, order++));
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
        _nodeSlide.Clear();
        _nodeColumn.Clear();
        _branchAnchors.Clear();
        _columns.Clear();
        _currentBranchName = null;
        _nextShapeId = 10;
        _nextSlideId = 256;

        var presentationPart = _pptDoc!.PresentationPart!;

        foreach (var slideId in presentationPart.Presentation!.SlideIdList?.Elements<P.SlideId>() ?? Enumerable.Empty<P.SlideId>())
        {
            if (slideId.Id?.Value is uint id && id >= _nextSlideId)
            {
                _nextSlideId = id + 1;
            }
        }

        // Slides are scanned in deck order (not part order) so "last slide
        // with this column's eyebrow" is well-defined for continuing after
        // reopening the file.
        foreach (var slideId in presentationPart.Presentation.SlideIdList?.Elements<P.SlideId>() ?? Enumerable.Empty<P.SlideId>())
        {
            if (slideId.RelationshipId?.Value is not string relId
                || presentationPart.GetPartById(relId) is not SlidePart slidePart)
            {
                continue;
            }

            RebuildFromSlide(slidePart);
        }

        if (_pendingResumeAnchor is { } resume
            && _nodeSlide.TryGetValue(resume, out var resumeSlide)
            && _nodeColumn.TryGetValue(resume, out var resumeColumnKey))
        {
            _currentBranchName = resumeColumnKey == MainColumnKey ? null : resumeColumnKey;
            var label = _labels.GetValueOrDefault(resume, "(ohne Beschreibung)");
            var junction = CreateJunctionSlide($"Fortsetzung ab: {TruncateLabel(label)}", resumeSlide, label);
            GetOrCreateColumnState(resumeColumnKey).LastSlidePart = junction;
        }

        _pendingResumeAnchor = null;
    }

    private void RebuildFromSlide(SlidePart slidePart)
    {
        var tree = slidePart.Slide?.CommonSlideData?.ShapeTree;
        if (tree is null)
        {
            return;
        }

        // Two passes: a slide's column is only knowable once its step
        // shape (added before any branch marker on the same slide) has
        // been seen, but every shape name on the slide — including branch
        // markers — needs that same column recorded in _nodeColumn.
        string? columnKey = null;

        foreach (var shape in tree.Elements<P.Shape>())
        {
            var name = shape.NonVisualShapeProperties?.NonVisualDrawingProperties?.Name?.Value;
            if (name is not null && name.StartsWith(StepNamePrefix, StringComparison.Ordinal))
            {
                var eyebrow = ExtractShapeText(shape).Split('\n', 2)[0];
                columnKey = eyebrow == MainEyebrow
                    ? MainColumnKey
                    : eyebrow.StartsWith(BranchEyebrowPrefix, StringComparison.Ordinal)
                        ? eyebrow[BranchEyebrowPrefix.Length..]
                        : columnKey;
            }
        }

        foreach (var shape in tree.Elements<P.Shape>())
        {
            var nvProps = shape.NonVisualShapeProperties?.NonVisualDrawingProperties;
            var name = nvProps?.Name?.Value;
            if (nvProps?.Id?.Value is uint id && id >= _nextShapeId)
            {
                _nextShapeId = id + 1;
            }

            if (name is null)
            {
                continue;
            }

            if (name.StartsWith(StepNamePrefix, StringComparison.Ordinal))
            {
                var lines = ExtractShapeText(shape).Split('\n', 2);
                var caption = lines.Length > 1 ? lines[1] : ExtractShapeText(shape);

                _labels[name] = TruncateLabel(caption);
                _nodeSlide[name] = slidePart;
                if (columnKey is not null)
                {
                    _nodeColumn[name] = columnKey;
                }
            }
            else if (name.StartsWith(BranchMarkerNamePrefix, StringComparison.Ordinal))
            {
                var firstLine = ExtractShapeText(shape).Split('\n', 2)[0];
                if (firstLine.StartsWith(BranchMarkerPrefix, StringComparison.Ordinal))
                {
                    var branchName = firstLine[BranchMarkerPrefix.Length..].Trim();
                    if (branchName.Length > 0)
                    {
                        _labels[name] = TruncateLabel(firstLine);
                        _nodeSlide[name] = slidePart;
                        if (columnKey is not null)
                        {
                            _nodeColumn[name] = columnKey;
                        }

                        AddOrReplaceAnchor(new BranchAnchor(branchName, name, slidePart));
                    }
                }
            }
        }

        if (columnKey is not null)
        {
            GetOrCreateColumnState(columnKey).LastSlidePart = slidePart;
        }
    }

    private ColumnState GetOrCreateColumnState(string columnKey)
    {
        if (!_columns.TryGetValue(columnKey, out var column))
        {
            column = new ColumnState();
            _columns[columnKey] = column;
        }

        return column;
    }

    private static string? FindStepOrMarkerName(SlidePart slide)
    {
        var tree = slide.Slide?.CommonSlideData?.ShapeTree;
        if (tree is null)
        {
            return null;
        }

        return tree.Elements<P.Shape>()
            .Select(s => s.NonVisualShapeProperties?.NonVisualDrawingProperties?.Name?.Value)
            .LastOrDefault(n => n is not null && (n.StartsWith(StepNamePrefix, StringComparison.Ordinal) || n.StartsWith(BranchMarkerNamePrefix, StringComparison.Ordinal)));
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
        var eyebrow = columnKey == MainColumnKey ? MainEyebrow : $"{BranchEyebrowPrefix}{columnKey}";
        var nodeId = StepNamePrefix + Guid.NewGuid().ToString("N");

        var slidePart = CreateBlankSlide();
        var tree = GetTree(slidePart);
        tree.Append(BuildCaptionShape(nodeId, eyebrow, description));
        tree.Append(BuildImageShape(slidePart, screenshot));

        _labels[nodeId] = TruncateLabel(description);
        _nodeSlide[nodeId] = slidePart;
        GetOrCreateColumnState(columnKey).LastSlidePart = slidePart;

        Save();
    }

    public BranchActionResult MarkBranchAnchor(string branchName)
    {
        var columnKey = ColumnKey(_currentBranchName);
        if (!_columns.TryGetValue(columnKey, out var column) || column.LastSlidePart is null)
        {
            return new BranchActionResult(false, _branchAnchors.Count, null);
        }

        var markerName = BranchMarkerNamePrefix + Guid.NewGuid().ToString("N");
        var slidePart = column.LastSlidePart;
        GetTree(slidePart).Append(BuildMarkerShape(markerName, $"{BranchMarkerPrefix}{branchName}"));

        _labels[markerName] = TruncateLabel($"{BranchMarkerPrefix}{branchName}");
        _nodeSlide[markerName] = slidePart;
        AddOrReplaceAnchor(new BranchAnchor(branchName, markerName, slidePart));

        Save();

        return new BranchActionResult(true, _branchAnchors.Count, branchName);
    }

    /// <summary>
    /// Jumping to a branch never reuses an existing slide as a link target
    /// — it always creates a fresh junction slide, so every slide-to-slide
    /// relationship this writer ever creates has a unique target. That
    /// matters: <see cref="OpenXmlPartContainer.AddPart{T}(T)"/> always
    /// creates a *new* relationship even if one already exists to the same
    /// target, so re-linking to something already linked (e.g. from
    /// visiting the same branch twice) would otherwise leave duplicate,
    /// redundant relationships behind for PowerPoint to clean up itself.
    /// </summary>
    public BranchActionResult JumpToAnchor(string branchName)
    {
        var anchor = _branchAnchors.FirstOrDefault(a => a.Name == branchName);
        if (anchor is null)
        {
            return new BranchActionResult(false, _branchAnchors.Count, null);
        }

        var anchorLabel = _labels.GetValueOrDefault(anchor.NodeId, "(ohne Beschreibung)");
        var junction = CreateJunctionSlide($"Abzweigung: {branchName}", anchor.Slide, anchorLabel);
        AppendForwardReference(anchor, branchName, junction);

        _currentBranchName = branchName;
        GetOrCreateColumnState(branchName).LastSlidePart = junction;
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

    /// <summary>A small divider slide: title + a link back to wherever it was reached from.</summary>
    private SlidePart CreateJunctionSlide(string title, SlidePart backlinkTarget, string backlinkLabel)
    {
        var slidePart = CreateBlankSlide();
        var tree = GetTree(slidePart);
        tree.Append(BuildSectionHeading(title));
        tree.Append(BuildBackLink(slidePart, backlinkTarget, backlinkLabel));
        return slidePart;
    }

    private void AppendForwardReference(BranchAnchor anchor, string branchName, SlidePart targetSlide)
    {
        var markerOrStepShape = GetTree(anchor.Slide).Elements<P.Shape>()
            .FirstOrDefault(s => s.NonVisualShapeProperties?.NonVisualDrawingProperties?.Name?.Value == anchor.NodeId);
        if (markerOrStepShape?.TextBody is null)
        {
            return;
        }

        var relId = GetOrAddRelationshipId(anchor.Slide, targetSlide);
        var referenceText = $"→ siehe Abzweigung „{branchName}“";

        markerOrStepShape.TextBody.Append(new A.Paragraph(
            new A.Run(
                new A.RunProperties(new A.HyperlinkOnClick { Id = relId, Action = "ppaction://hlinksldjump" }) { Language = "de-DE", FontSize = 1200 },
                new A.Text(referenceText))));
    }

    private P.Shape BuildBackLink(SlidePart sourceSlide, SlidePart targetSlide, string label)
    {
        var relId = GetOrAddRelationshipId(sourceSlide, targetSlide);
        return new P.Shape(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = _nextShapeId++, Name = "backlink" },
                new P.NonVisualShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.ShapeProperties(
                new A.Transform2D(
                    new A.Offset { X = (long)(MarginPx * EmuPerPixel), Y = (long)((MarginPx + TitleHeightPx + 10) * EmuPerPixel) },
                    new A.Extents { Cx = (long)(ImageAreaWidthPx * EmuPerPixel), Cy = (long)(60 * EmuPerPixel) }),
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }),
            new P.TextBody(
                new A.BodyProperties { Wrap = A.TextWrappingValues.Square },
                new A.ListStyle(),
                new A.Paragraph(
                    new A.Run(
                        new A.RunProperties(new A.HyperlinkOnClick { Id = relId, Action = "ppaction://hlinksldjump" }) { Language = "de-DE", FontSize = 1400, Italic = true },
                        new A.Text($"↩ Ausgangspunkt: {label}")))));
    }

    /// <summary>
    /// Reuses an existing relationship between these two parts if one is
    /// already there, instead of always minting a new one — see the
    /// <see cref="JumpToAnchor"/> doc comment for why that matters. Cheap
    /// insurance: this writer's own design never actually re-links the same
    /// pair twice, but a future change reusing a slide as a target more
    /// than once would otherwise silently accumulate redundant relationships.
    /// </summary>
    private static string GetOrAddRelationshipId(OpenXmlPart source, OpenXmlPart target)
    {
        foreach (var idPartPair in source.Parts)
        {
            if (ReferenceEquals(idPartPair.OpenXmlPart, target))
            {
                return idPartPair.RelationshipId;
            }
        }

        source.AddPart(target);
        return source.GetIdOfPart(target);
    }

    private P.Shape BuildCaptionShape(string name, string eyebrow, string description) => new(
        new P.NonVisualShapeProperties(
            new P.NonVisualDrawingProperties { Id = _nextShapeId++, Name = name },
            new P.NonVisualShapeDrawingProperties(),
            new P.ApplicationNonVisualDrawingProperties()),
        new P.ShapeProperties(
            new A.Transform2D(
                new A.Offset { X = (long)(MarginPx * EmuPerPixel), Y = (long)(MarginPx * EmuPerPixel) },
                new A.Extents { Cx = (long)(ImageAreaWidthPx * EmuPerPixel), Cy = (long)(TitleHeightPx * EmuPerPixel) }),
            new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }),
        new P.TextBody(
            new A.BodyProperties { Wrap = A.TextWrappingValues.Square },
            new A.ListStyle(),
            new A.Paragraph(
                new A.Run(new A.RunProperties { Language = "de-DE", Bold = true, FontSize = 1200 }, new A.Text(eyebrow))),
            new A.Paragraph(
                new A.Run(new A.RunProperties { Language = "de-DE", Bold = true, FontSize = 2000 }, new A.Text(description)))));

    private static P.Shape BuildSectionHeading(string title) => new(
        new P.NonVisualShapeProperties(
            new P.NonVisualDrawingProperties { Id = 2, Name = "title" },
            new P.NonVisualShapeDrawingProperties(),
            new P.ApplicationNonVisualDrawingProperties()),
        new P.ShapeProperties(
            new A.Transform2D(
                new A.Offset { X = (long)(MarginPx * EmuPerPixel), Y = (long)(MarginPx * EmuPerPixel) },
                new A.Extents { Cx = (long)(ImageAreaWidthPx * EmuPerPixel), Cy = (long)(TitleHeightPx * EmuPerPixel) }),
            new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }),
        new P.TextBody(
            new A.BodyProperties { Wrap = A.TextWrappingValues.Square },
            new A.ListStyle(),
            new A.Paragraph(new A.Run(new A.RunProperties { Language = "de-DE", Bold = true, FontSize = 2400 }, new A.Text(title)))));

    private P.Shape BuildMarkerShape(string name, string text) => new(
        new P.NonVisualShapeProperties(
            new P.NonVisualDrawingProperties { Id = _nextShapeId++, Name = name },
            new P.NonVisualShapeDrawingProperties(),
            new P.ApplicationNonVisualDrawingProperties()),
        new P.ShapeProperties(
            new A.Transform2D(
                new A.Offset { X = (long)(MarginPx * EmuPerPixel), Y = (long)((SlideHeightPx - MarginPx - NoteHeightPx) * EmuPerPixel) },
                new A.Extents { Cx = (long)(ImageAreaWidthPx * EmuPerPixel), Cy = (long)(NoteHeightPx * EmuPerPixel) }),
            new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle },
            new A.NoFill()),
        new P.TextBody(
            new A.BodyProperties { Wrap = A.TextWrappingValues.Square },
            new A.ListStyle(),
            new A.Paragraph(
                new A.Run(
                    new A.RunProperties(new A.SolidFill(new A.RgbColorModelHex { Val = "7C3AED" })) { Language = "de-DE", Bold = true, Italic = true, FontSize = 1200 },
                    new A.Text(text)))));

    private P.Picture BuildImageShape(SlidePart slidePart, Bitmap screenshot)
    {
        var imagePart = slidePart.AddImagePart(ImagePartType.Png);
        using (var ms = new MemoryStream())
        {
            screenshot.Save(ms, ImageFormat.Png);
            ms.Position = 0;
            imagePart.FeedData(ms);
        }

        var scale = Math.Min(ImageAreaWidthPx / screenshot.Width, ImageAreaHeightPx / screenshot.Height);
        var imgWidthPx = screenshot.Width * scale;
        var imgHeightPx = screenshot.Height * scale;
        var x = (SlideWidthPx - imgWidthPx) / 2;
        var y = ImageTopPx + (ImageAreaHeightPx - imgHeightPx) / 2;

        var relId = slidePart.GetIdOfPart(imagePart);
        return new P.Picture(
            new P.NonVisualPictureProperties(
                new P.NonVisualDrawingProperties { Id = _nextShapeId++, Name = "screenshot.png" },
                new P.NonVisualPictureDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.BlipFill(new A.Blip { Embed = relId }, new A.Stretch(new A.FillRectangle())),
            new P.ShapeProperties(
                new A.Transform2D(
                    new A.Offset { X = (long)(x * EmuPerPixel), Y = (long)(y * EmuPerPixel) },
                    new A.Extents { Cx = (long)(imgWidthPx * EmuPerPixel), Cy = (long)(imgHeightPx * EmuPerPixel) }),
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }));
    }

    private static P.ShapeTree GetTree(SlidePart slidePart) => slidePart.Slide!.CommonSlideData!.ShapeTree!;

    private SlidePart CreateBlankSlide()
    {
        var presentationPart = _pptDoc!.PresentationPart!;
        var slidePart = presentationPart.AddNewPart<SlidePart>();
        slidePart.AddPart(_slideLayoutPart!);

        var tree = new P.ShapeTree(
            new P.NonVisualGroupShapeProperties(
                new P.NonVisualDrawingProperties { Id = 1, Name = "" },
                new P.NonVisualGroupShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.GroupShapeProperties());

        slidePart.Slide = new P.Slide(new P.CommonSlideData(tree), new P.ColorMapOverride(new A.MasterColorMapping()));

        var slideIdList = presentationPart.Presentation!.SlideIdList!;
        slideIdList.Append(new P.SlideId { Id = _nextSlideId++, RelationshipId = presentationPart.GetIdOfPart(slidePart) });

        return slidePart;
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
            Cx = (int)(SlideWidthPx * EmuPerPixel),
            Cy = (int)(SlideHeightPx * EmuPerPixel)
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
