using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using DocuClick.Services;

// UseWindowsForms implicitly brings System.Drawing/Windows.Forms into every
// file too; combined with the WPF namespaces above, several names (Color,
// Brushes, Cursors, ...) exist in both and become ambiguous. This file is
// WPF-only UI, so alias to those.
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;
using Rectangle = System.Windows.Shapes.Rectangle;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using TextAlignment = System.Windows.TextAlignment;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Orientation = System.Windows.Controls.Orientation;

namespace DocuClick;

/// <summary>
/// Freely draggable, semi-transparent minimap of the current flow (Canvas/
/// draw.io/Excalidraw modes): every node as a small square laid out in a
/// row/column grid derived from the flow's graph structure (see
/// <see cref="UpdatePreview"/>), connector lines between them, and the node
/// the next click will connect from highlighted — so it's always visible at
/// a glance where "you are" in a long, branching flow without opening the
/// actual file.
///
/// Also doubles as a navigation tool: clicking any node jumps the recording
/// cursor there (<see cref="NodeClicked"/>), the same way "Branch
/// auswählen" does for named branches, but for any node in the flow — see
/// <see cref="SessionManager.JumpToNode"/>.
/// </summary>
public sealed class FlowPreviewOverlay : Window
{
    private const double PanelWidth = 260;
    private const double PanelHeight = 190;
    private const double Padding = 12;
    private const double HeaderHeight = 26;
    private const double CollapsedMinHeight = HeaderHeight + 16;

    // Node dimensions at the default (un-resized) panel size — wider than
    // tall (real content nodes are cards, not squares) and, per node size,
    // scaled up together with the window in UpdatePreview so enlarging the
    // minimap via the resize grip actually gives bigger, easier-to-click
    // targets instead of only spreading them further apart.
    private const double NodeWidth = 22;
    private const double NodeHeight = 14;
    private const double CurrentNodeWidth = 32;
    private const double CurrentNodeHeight = 20;
    private const double MinSizeScale = 0.8;
    private const double MaxSizeScale = 2.5;

    // Floors for the grid layout in UpdatePreview: below these, rows/columns
    // stop shrinking and the canvas grows past the visible panel instead —
    // scrolling to see more beats every node shrinking into an unreadable,
    // unclickable smear once a flow has enough steps or branches.
    private const double MinRowSpacing = 24;
    private const double MinColumnSpacing = 60;

    // Same accent palette as DrawIoFlowWriter's branch colors, reused here
    // so a branch's minimap dot and its actual card color line up in
    // draw.io mode. A stable (non-randomized) hash of the branch name
    // picks the color deterministically, so it never flickers between
    // redraws or picks up .NET's per-process string-hash randomization.
    private static readonly Color[] BranchPalette =
    {
        Color.FromRgb(0xD9, 0x77, 0x06), Color.FromRgb(0x05, 0x96, 0x69),
        Color.FromRgb(0xDB, 0x27, 0x76), Color.FromRgb(0x7C, 0x3A, 0xED),
        Color.FromRgb(0xDC, 0x26, 0x26), Color.FromRgb(0x08, 0x91, 0xB2)
    };

    private readonly Canvas _canvas;
    private readonly TextBlock _emptyHint;
    private readonly Border _canvasHost;
    private readonly TextBlock _collapseIcon;
    private FlowPreview _lastPreview = new(new List<PreviewNode>(), new List<PreviewEdge>());
    private Point _resizeStartMouse;
    private Size _resizeStartSize;
    private bool _collapsed;
    private double _expandedHeight;

    public event Action<string>? NodeClicked;

    public FlowPreviewOverlay()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        // Borderless windows get no OS edge/corner resize handling for
        // free (that comes from WindowStyle chrome, which is off here) —
        // resizing is done manually via the grip below instead.
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = false;
        SizeToContent = SizeToContent.Manual;
        Width = PanelWidth + Padding * 2;
        Height = PanelHeight + HeaderHeight + Padding;
        MinWidth = 160;
        MinHeight = 110;

        // Short, single-line title instead of the previous permanently-
        // wrapped instruction sentence — that sentence used up two lines of
        // vertical space on every redraw even though it only needs to be
        // read once. The full instructions now live in the info icon's
        // tooltip, and the panel can be collapsed to just this header row
        // via the toggle icon when the user doesn't currently need it.
        var titleText = new TextBlock
        {
            Text = "Ablauf-Übersicht",
            Foreground = Brushes.White,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Opacity = 0.9,
            VerticalAlignment = VerticalAlignment.Center
        };

        var infoIcon = CreateHeaderIcon("i", "Panel per Kopfzeile ziehbar, per Ecke unten rechts größenverstellbar. Diagramm im Panel per Ziehen verschieben. Knoten anklicken, um dorthin zu springen.");

        _collapseIcon = new TextBlock
        {
            Text = "–", // en dash, doubles as a minimal "collapse" glyph; becomes "+" when collapsed
            Foreground = Brushes.White,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var collapseToggle = WrapHeaderIcon(_collapseIcon, "Ein-/Ausklappen");
        collapseToggle.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            ToggleCollapsed();
        };

        var headerIcons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        headerIcons.Children.Add(infoIcon);
        headerIcons.Children.Add(collapseToggle);

        var header = new DockPanel { Margin = new Thickness(Padding, 6, 8, 6) };
        DockPanel.SetDock(headerIcons, Dock.Right);
        header.Children.Add(headerIcons);
        header.Children.Add(titleText);
        DockPanel.SetDock(header, Dock.Top);

        // Transparent (not null/unset) Background: WPF only hit-tests a
        // panel's empty space when it actually has a Background brush —
        // without this, clicks that don't land on a node square never
        // reach any handler at all (not even bubbling to the ScrollViewer
        // below), which is why drag-panning silently did nothing while the
        // mouse wheel — routed differently — still worked.
        _canvas = new Canvas { Background = Brushes.Transparent };

        _emptyHint = new TextBlock
        {
            Text = "Noch keine Klicks aufgezeichnet.",
            Foreground = Brushes.White,
            Opacity = 0.6,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        // The canvas itself is now content-sized (see UpdatePreview) rather
        // than stretched to fill the panel, so a flow with more steps/
        // branches than comfortably fit can grow past the visible area —
        // reachable by click-and-drag panning (below) instead of scrollbars,
        // which read as clutter on a small floating HUD panel. Bars stay
        // Hidden (not Disabled): panning still needs programmatic scrolling
        // (ScrollToHorizontalOffset/VerticalOffset) to actually work, and
        // BringIntoView's auto-scroll-to-current-node relies on it too —
        // Disabled would turn off scrolling entirely, not just the chrome.
        var scrollViewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Cursor = Cursors.SizeAll,
            Content = _canvas
        };

        var panning = false;
        var panMouseStart = default(Point);
        var panOffsetStart = default(Point);
        scrollViewer.MouseLeftButtonDown += (_, e) =>
        {
            // Only reaches here for clicks that didn't land on a node
            // square — those mark the event Handled in their own handler
            // first, and a standard (non-"handled events too") subscription
            // like this one never sees an already-handled event.
            e.Handled = true; // don't also start a window-drag via the border handler below
            panning = true;
            scrollViewer.CaptureMouse();
            panMouseStart = e.GetPosition(scrollViewer);
            panOffsetStart = new Point(scrollViewer.HorizontalOffset, scrollViewer.VerticalOffset);
        };
        scrollViewer.MouseMove += (_, e) =>
        {
            if (!panning)
            {
                return;
            }

            var current = e.GetPosition(scrollViewer);
            scrollViewer.ScrollToHorizontalOffset(panOffsetStart.X - (current.X - panMouseStart.X));
            scrollViewer.ScrollToVerticalOffset(panOffsetStart.Y - (current.Y - panMouseStart.Y));
        };
        scrollViewer.MouseLeftButtonUp += (_, _) =>
        {
            panning = false;
            scrollViewer.ReleaseMouseCapture();
        };

        var canvasArea = new Grid { ClipToBounds = true };
        canvasArea.Children.Add(scrollViewer);
        canvasArea.Children.Add(_emptyHint);

        _canvasHost = new Border
        {
            Margin = new Thickness(Padding, 0, Padding, Padding),
            Child = canvasArea
        };

        // Reacts to the *viewport* growing/shrinking (dragging the resize
        // grip), not the canvas's own content size — the canvas is
        // content-sized now, so listening to its own SizeChanged would
        // mean "redo the layout because the layout changed its size",
        // which only wastes a redundant pass instead of responding to
        // anything the user did.
        _canvasHost.SizeChanged += (_, _) => UpdatePreview(_lastPreview);

        var resizeGrip = new Rectangle
        {
            Width = 14,
            Height = 14,
            Fill = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
            Cursor = Cursors.SizeNWSE,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 2, 2),
            ToolTip = "Ziehen zum Verändern der Größe"
        };
        resizeGrip.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true; // don't also start a window-drag via the border handler below
            resizeGrip.CaptureMouse();
            _resizeStartMouse = PointToScreen(e.GetPosition(this));
            _resizeStartSize = new Size(ActualWidth, ActualHeight);
        };
        resizeGrip.MouseMove += (_, e) =>
        {
            if (!resizeGrip.IsMouseCaptured)
            {
                return;
            }

            var current = PointToScreen(e.GetPosition(this));
            Width = Math.Max(MinWidth, _resizeStartSize.Width + (current.X - _resizeStartMouse.X));
            Height = Math.Max(MinHeight, _resizeStartSize.Height + (current.Y - _resizeStartMouse.Y));
        };
        resizeGrip.MouseLeftButtonUp += (_, _) => resizeGrip.ReleaseMouseCapture();
        canvasArea.Children.Add(resizeGrip);

        var content = new DockPanel();
        content.Children.Add(header);
        content.Children.Add(_canvasHost);

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(170, 25, 25, 28)),
            CornerRadius = new CornerRadius(8),
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Child = content
        };
        Content = border;

        // Drag the whole panel from anywhere except a node square/the
        // resize grip — those mark the event Handled before it bubbles here.
        border.MouseLeftButtonDown += (_, e) =>
        {
            if (!e.Handled)
            {
                Activate();
                DragMove();
            }
        };

        var bounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        Loaded += (_, _) =>
        {
            Left = bounds.Right - ActualWidth - 16;
            Top = bounds.Top + RecordingIndicatorOverlay.TopBarClearance + 16;
        };

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.ExcludeFromScreenCapture(hwnd);
        };

        UpdatePreview(_lastPreview);
    }

    /// <summary>
    /// Collapses the panel down to just its header row (hiding the node
    /// canvas/resize grip) so it can be gotten out of the way without
    /// closing it outright, and restores it back to its previous size
    /// afterwards. MinHeight is temporarily lowered too — otherwise the
    /// window-level MinHeight constraint (needed so the expanded minimap
    /// never shrinks to illegibility) would also floor the collapsed size.
    /// </summary>
    private void ToggleCollapsed()
    {
        _collapsed = !_collapsed;
        _canvasHost.Visibility = _collapsed ? Visibility.Collapsed : Visibility.Visible;
        _collapseIcon.Text = _collapsed ? "+" : "–";

        if (_collapsed)
        {
            _expandedHeight = ActualHeight > 0 ? ActualHeight : Height;
            MinHeight = CollapsedMinHeight;
            Height = CollapsedMinHeight;
        }
        else
        {
            MinHeight = 110;
            Height = _expandedHeight;
        }
    }

    /// <summary>Small circular header affordance (info/collapse icons) built from a symbol string — see <see cref="WrapHeaderIcon"/> for the shared visual.</summary>
    private static Border CreateHeaderIcon(string symbol, string tooltip)
    {
        var text = new TextBlock
        {
            Text = symbol,
            Foreground = Brushes.White,
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        return WrapHeaderIcon(text, tooltip);
    }

    /// <summary>
    /// Wraps header content in a small translucent circle with a hover
    /// highlight, matching the frosted-glass affordance style used by
    /// <see cref="TopBarWindow"/>'s buttons — kept as plain Border/TextBlock
    /// (not a real Button) since this window has no XAML/styles of its own
    /// and a real Button would need the same from-scratch ControlTemplate
    /// treatment TopBarWindow uses just for two tiny icons.
    /// </summary>
    private static Border WrapHeaderIcon(FrameworkElement content, string tooltip)
    {
        var icon = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)),
            Margin = new Thickness(4, 0, 0, 0),
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
            Child = content
        };
        icon.MouseEnter += (_, _) => icon.Background = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
        icon.MouseLeave += (_, _) => icon.Background = new SolidColorBrush(Color.FromArgb(45, 255, 255, 255));
        return icon;
    }

    /// <summary>
    /// Redraws the minimap from scratch using a layout derived purely from
    /// the flow's graph topology — NOT the real x/y coordinates the active
    /// output writer (Canvas/draw.io/Excalidraw) uses for its own file.
    /// Those real coordinates reflect actual card sizes and spacing in that
    /// file, which vary wildly and are dominated by whichever two nodes
    /// happen to be furthest apart; normalizing directly against them (the
    /// previous approach) meant one distant outlier could compress every
    /// other node into an unreadable sliver. Instead: every node's row is
    /// its depth from the flow's start (one step down per edge) and its
    /// column is which branch it belongs to (0 = main flow, 1.. = each
    /// named branch in first-seen order) — spacing is then derived purely
    /// from row/column counts, so it fills the panel evenly regardless of
    /// how the real file happens to be laid out. Rows/columns below a
    /// legible minimum stop shrinking and the canvas scrolls instead (see
    /// <see cref="MinRowSpacing"/>/<see cref="MinColumnSpacing"/>).
    /// </summary>
    public void UpdatePreview(FlowPreview preview)
    {
        _lastPreview = preview;
        _canvas.Children.Clear();

        if (preview.Nodes.Count == 0)
        {
            _emptyHint.Visibility = Visibility.Visible;
            _canvas.Width = 0;
            _canvas.Height = 0;
            return;
        }

        _emptyHint.Visibility = Visibility.Collapsed;

        // The *viewport* size (falls back to the initial content size
        // before the first layout pass has run, when it's still 0).
        var viewportWidth = _canvasHost.ActualWidth > 0 ? _canvasHost.ActualWidth : PanelWidth;
        var viewportHeight = _canvasHost.ActualHeight > 0 ? _canvasHost.ActualHeight : PanelHeight;

        // Row = depth from the flow's start (root nodes with no inbound
        // edge get row 0), computed via BFS over the edge graph rather than
        // trusting list order — a node's row is always exactly one more
        // than whatever node points into it.
        var forward = preview.Edges
            .GroupBy(e => e.FromId)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ToId).ToList());
        var hasInbound = preview.Edges.Select(e => e.ToId).ToHashSet();

        var rowOf = new Dictionary<string, int>();
        var bfsQueue = new Queue<string>();
        foreach (var node in preview.Nodes)
        {
            if (!hasInbound.Contains(node.Id))
            {
                rowOf[node.Id] = 0;
                bfsQueue.Enqueue(node.Id);
            }
        }

        while (bfsQueue.Count > 0)
        {
            var id = bfsQueue.Dequeue();
            if (!forward.TryGetValue(id, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                if (rowOf.ContainsKey(child))
                {
                    continue;
                }

                rowOf[child] = rowOf[id] + 1;
                bfsQueue.Enqueue(child);
            }
        }

        // Defensive: any node the BFS above never reached (shouldn't
        // happen for a tree-shaped flow, but a stray disconnected node
        // must not throw a KeyNotFoundException below) just lands at row 0.
        foreach (var node in preview.Nodes)
        {
            rowOf.TryAdd(node.Id, 0);
        }

        // Column = which branch (0 = main flow, otherwise the branch's
        // first-seen order among the nodes) — BranchName is already tagged
        // onto every node by FlowPreviewBranching.TagBranches before this
        // preview reaches the overlay.
        var columnOfBranch = new Dictionary<string, int>();
        var columnOf = new Dictionary<string, int>();
        foreach (var node in preview.Nodes)
        {
            if (node.BranchName is { } branch)
            {
                if (!columnOfBranch.TryGetValue(branch, out var column))
                {
                    column = columnOfBranch.Count + 1;
                    columnOfBranch[branch] = column;
                }

                columnOf[node.Id] = column;
            }
            else
            {
                columnOf[node.Id] = 0;
            }
        }

        var columnCount = columnOfBranch.Count + 1;
        var maxRow = rowOf.Values.Max();

        // Slot sizes never shrink below a legible floor — once the panel
        // is too small to fit every row/column at that floor, the canvas
        // grows past the viewport and the ScrollViewer around it takes
        // over instead of squeezing nodes into an unreadable smear.
        var rowSpacing = Math.Max(MinRowSpacing, viewportHeight / Math.Max(1, maxRow));
        var columnSpacing = Math.Max(MinColumnSpacing, viewportWidth / columnCount);

        // Node size still grows with the window (bigger panel → bigger,
        // easier-to-click nodes), but is now also capped by whichever slot
        // dimension (row or column) is currently tightest, using the
        // bigger current-node box as the worst case so a regular node
        // (which is smaller) is guaranteed to fit too.
        var windowSizeScale = Math.Clamp(Math.Min(viewportWidth / PanelWidth, viewportHeight / PanelHeight), MinSizeScale, MaxSizeScale);
        var slotFitScale = Math.Min(rowSpacing / CurrentNodeHeight, columnSpacing / CurrentNodeWidth) * 0.7;
        var sizeScale = Math.Clamp(Math.Min(windowSizeScale, slotFitScale), 0.5, MaxSizeScale);

        var nodeWidth = NodeWidth * sizeScale;
        var nodeHeight = NodeHeight * sizeScale;
        var currentNodeWidth = CurrentNodeWidth * sizeScale;
        var currentNodeHeight = CurrentNodeHeight * sizeScale;

        var halfExtentX = Math.Max(currentNodeWidth, nodeWidth) / 2 + 6;
        var halfExtentY = Math.Max(currentNodeHeight, nodeHeight) / 2 + 6;

        var contentWidth = Math.Max(viewportWidth, columnCount * columnSpacing + halfExtentX * 2);
        var contentHeight = Math.Max(viewportHeight, maxRow * rowSpacing + halfExtentY * 2);
        _canvas.Width = contentWidth;
        _canvas.Height = contentHeight;

        (double X, double Y) GridPosition(string nodeId) => (
            halfExtentX + columnOf[nodeId] * columnSpacing + columnSpacing / 2,
            halfExtentY + rowOf[nodeId] * rowSpacing);

        var centers = preview.Nodes.ToDictionary(n => n.Id, n => GridPosition(n.Id));

        var edgeBrush = new SolidColorBrush(Color.FromArgb(130, 255, 255, 255));
        foreach (var edge in preview.Edges)
        {
            if (!centers.TryGetValue(edge.FromId, out var from) || !centers.TryGetValue(edge.ToId, out var to))
            {
                continue;
            }

            _canvas.Children.Add(new Line
            {
                X1 = from.X,
                Y1 = from.Y,
                X2 = to.X,
                Y2 = to.Y,
                Stroke = edgeBrush,
                StrokeThickness = 1
            });

            // A midpoint arrowhead instead of one at either endpoint: at
            // either end it would sit right on top of (or under) a node
            // square — the midpoint always has clear space around it and
            // still unambiguously shows which way the flow runs without
            // having to trace the color/branch of each node.
            var dx = to.X - from.X;
            var dy = to.Y - from.Y;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1)
            {
                continue;
            }

            var ux = dx / length;
            var uy = dy / length;
            var midX = (from.X + to.X) / 2;
            var midY = (from.Y + to.Y) / 2;
            var arrowLength = Math.Clamp(6 * sizeScale, 4, 10);
            var arrowHalfWidth = arrowLength * 0.4;
            var tip = new Point(midX + ux * arrowLength / 2, midY + uy * arrowLength / 2);
            var baseCenter = new Point(midX - ux * arrowLength / 2, midY - uy * arrowLength / 2);
            var perpX = -uy;
            var perpY = ux;

            var arrowHead = new Polygon
            {
                Fill = edgeBrush,
                Points = new PointCollection
                {
                    tip,
                    new Point(baseCenter.X + perpX * arrowHalfWidth, baseCenter.Y + perpY * arrowHalfWidth),
                    new Point(baseCenter.X - perpX * arrowHalfWidth, baseCenter.Y - perpY * arrowHalfWidth)
                }
            };
            _canvas.Children.Add(arrowHead);
        }

        Rectangle? currentSquare = null;
        foreach (var node in preview.Nodes)
        {
            var center = centers[node.Id];
            var width = node.IsCurrent ? currentNodeWidth : nodeWidth;
            var height = node.IsCurrent ? currentNodeHeight : nodeHeight;

            // Branch-marker nodes render as pill/oval shapes (RadiusX/Y =
            // half the dimension) instead of the sharper-cornered rectangle
            // used for regular nodes, so the waypoint itself stays visually
            // distinct even when its color matches every other node
            // further down that same branch.
            var radius = node.IsBranchMarker ? Math.Min(width, height) / 2 : 4;

            var square = new Rectangle
            {
                Width = width,
                Height = height,
                RadiusX = radius,
                RadiusY = radius,
                Fill = new SolidColorBrush(GetNodeColor(node)),
                Stroke = Brushes.White,
                StrokeThickness = node.IsCurrent ? 2 : 1,
                Cursor = Cursors.Hand,
                ToolTip = node.BranchName is { } b ? $"{node.Label} · Branch: {b}" : node.Label
            };
            Canvas.SetLeft(square, center.X - width / 2);
            Canvas.SetTop(square, center.Y - height / 2);

            var nodeId = node.Id;
            square.MouseLeftButtonDown += (_, e) =>
            {
                // Marks Handled so the panel-drag handler on the outer
                // border (which bubbles up from here) doesn't also fire.
                e.Handled = true;
                NodeClicked?.Invoke(nodeId);
            };

            _canvas.Children.Add(square);

            if (node.IsCurrent)
            {
                currentSquare = square;
            }

            // Hovering shows the full label for any node, but branch
            // markers and "you are here" are what people actually need to
            // spot at a glance — so those two get a permanent text label
            // instead of requiring a hover, unlike the many identical-
            // looking regular nodes in between.
            if (node.IsBranchMarker || node.IsCurrent)
            {
                var labelText = node.IsBranchMarker && node.BranchName is { } branchName
                    ? $"↳ {branchName}"
                    : "● hier";

                var label = new TextBlock
                {
                    Text = labelText,
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                    Background = new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)),
                    Padding = new Thickness(3, 1, 3, 1),
                    IsHitTestVisible = false
                };

                // Measuring gives DesiredSize without needing a layout pass,
                // so a node sitting in the rightmost column (rightmost
                // node's label used to just run off the edge and get
                // clipped, unreadable — see the "Ablauf-Übersicht" bug
                // report) flips its label to the left instead of overflowing.
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var labelWidth = label.DesiredSize.Width;
                var fitsOnRight = center.X + width / 2 + 4 + labelWidth <= contentWidth;
                Canvas.SetLeft(label, fitsOnRight ? center.X + width / 2 + 4 : center.X - width / 2 - 4 - labelWidth);
                Canvas.SetTop(label, center.Y - 8);
                _canvas.Children.Add(label);
            }
        }

        // Auto-scrolls the ScrollViewer so "you are here" is always in
        // view after a redraw, without the user having to manually scroll
        // down to find it in a long flow — BringIntoView walks up to the
        // nearest ScrollViewer ancestor on its own. Deferred to Loaded
        // priority: the square was only just added, so it needs one layout
        // pass before its position is something BringIntoView can act on.
        if (currentSquare is { } toReveal)
        {
            Dispatcher.BeginInvoke(new Action(() => toReveal.BringIntoView()), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private static Color GetNodeColor(PreviewNode node)
    {
        if (node.IsCurrent)
        {
            return Color.FromRgb(0xE6, 0x39, 0x46);
        }

        if (node.BranchName is { } branch)
        {
            return BranchPalette[StableHash(branch) % BranchPalette.Length];
        }

        return node.IsBranchMarker
            ? Color.FromRgb(0x7C, 0x3A, 0xED)
            : Color.FromArgb(230, 0x4C, 0xAF, 0xE8);
    }

    private static int StableHash(string value)
    {
        unchecked
        {
            var hash = 17;
            foreach (var c in value)
            {
                hash = hash * 31 + c;
            }

            return hash & 0x7FFFFFFF;
        }
    }
}
