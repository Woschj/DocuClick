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
/// draw.io/Excalidraw modes): every node as a small square positioned to
/// scale, connector lines between them, and the node the next click will
/// connect from highlighted — so it's always visible at a glance where "you
/// are" in a long, branching flow without opening the actual file.
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

        var infoIcon = CreateHeaderIcon("i", "Ziehbar/größenverstellbar (Ecke unten rechts). Knoten anklicken, um dorthin zu springen.");

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

        _canvas = new Canvas();

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

        var canvasArea = new Grid { ClipToBounds = true };
        canvasArea.Children.Add(_canvas);
        canvasArea.Children.Add(_emptyHint);

        // Node positions are normalized against the canvas's actual
        // current size, so growing the window via the resize grip spreads
        // the same nodes across more space instead of leaving them
        // clustered in a corner.
        _canvas.SizeChanged += (_, _) => UpdatePreview(_lastPreview);

        _canvasHost = new Border
        {
            Margin = new Thickness(Padding, 0, Padding, Padding),
            Child = canvasArea
        };

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
    /// Redraws the minimap from scratch: all node positions are normalized
    /// into the fixed panel size (nodes vary a lot in real size/spacing
    /// across output modes, so a to-scale rendering would make most of them
    /// vanish to sub-pixel size in a long flow — uniform squares connected
    /// by lines stay legible and clickable regardless of flow length).
    /// </summary>
    public void UpdatePreview(FlowPreview preview)
    {
        _lastPreview = preview;
        _canvas.Children.Clear();

        if (preview.Nodes.Count == 0)
        {
            _emptyHint.Visibility = Visibility.Visible;
            return;
        }

        _emptyHint.Visibility = Visibility.Collapsed;

        // Falls back to the initial content size before the first layout
        // pass has run (ActualWidth/Height are still 0 at that point).
        var canvasWidth = _canvas.ActualWidth > 0 ? _canvas.ActualWidth : PanelWidth;
        var canvasHeight = _canvas.ActualHeight > 0 ? _canvas.ActualHeight : PanelHeight;

        var minX = preview.Nodes.Min(n => n.X);
        var minY = preview.Nodes.Min(n => n.Y);
        var maxX = preview.Nodes.Max(n => n.X + n.Width);
        var maxY = preview.Nodes.Max(n => n.Y + n.Height);
        var spanX = Math.Max(maxX - minX, 1);
        var spanY = Math.Max(maxY - minY, 1);

        // Grows node size together with the panel (not just the spacing
        // between them) — clamped so a tiny window doesn't shrink nodes to
        // illegibility and a huge one doesn't blow them up absurdly. This is
        // only a starting point for the layout margin below; the actual
        // rendered size gets capped further down so nodes can never overlap.
        var tentativeSizeScale = Math.Clamp(Math.Min(canvasWidth / PanelWidth, canvasHeight / PanelHeight), MinSizeScale, MaxSizeScale);
        var tentativeHalfExtent = Math.Max(CurrentNodeWidth, CurrentNodeHeight) * tentativeSizeScale / 2;

        var drawableWidth = canvasWidth - tentativeHalfExtent * 2;
        var drawableHeight = canvasHeight - tentativeHalfExtent * 2;

        // Separate X/Y scale factors instead of one shared "min of both"
        // factor: with many branch columns, horizontal span grows a lot
        // (each branch is its own column), which used to force the
        // *vertical* spacing down too even though the sequential main flow
        // had plenty of vertical room — branching sideways no longer
        // squeezes the unrelated vertical layout.
        var scaleX = drawableWidth / spanX;
        var scaleY = drawableHeight / spanY;

        (double X, double Y) ToCanvas(double x, double y) => (
            tentativeHalfExtent + (x - minX) * scaleX,
            tentativeHalfExtent + (y - minY) * scaleY);

        var centers = preview.Nodes.ToDictionary(n => n.Id, n => ToCanvas(n.X + n.Width / 2, n.Y + n.Height / 2));

        // Nodes must never collide, however tight the flow's real layout
        // is — cap the rendered size (never the position) so every pair of
        // nodes fits within its own on-screen distance. Per-pair (not one
        // global worst case using the biggest possible node size): most
        // nodes are the small regular kind, so a tight regular/regular pair
        // shouldn't be constrained as if it were the much bigger
        // current-node box — that was massively over-shrinking everything
        // for the sake of the one node that's actually bigger.
        double HalfDiagonal(PreviewNode n)
        {
            var w = n.IsCurrent ? CurrentNodeWidth : NodeWidth;
            var h = n.IsCurrent ? CurrentNodeHeight : NodeHeight;
            return Math.Sqrt(w * w + h * h) / 2;
        }

        const double CollisionMargin = 0.85; // leave a visible gap, not just "not touching"
        var nodeList = preview.Nodes;
        var collisionSafeScale = double.PositiveInfinity;
        for (var i = 0; i < nodeList.Count; i++)
        {
            var centerI = centers[nodeList[i].Id];
            var halfDiagonalI = HalfDiagonal(nodeList[i]);
            for (var j = i + 1; j < nodeList.Count; j++)
            {
                var centerJ = centers[nodeList[j].Id];
                var dx = centerI.X - centerJ.X;
                var dy = centerI.Y - centerJ.Y;
                var distance = Math.Sqrt(dx * dx + dy * dy);
                var sumHalfDiagonals = halfDiagonalI + HalfDiagonal(nodeList[j]);
                if (sumHalfDiagonals <= 0)
                {
                    continue;
                }

                var pairSafeScale = distance * CollisionMargin / sumHalfDiagonals;
                if (pairSafeScale < collisionSafeScale)
                {
                    collisionSafeScale = pairSafeScale;
                }
            }
        }

        if (double.IsPositiveInfinity(collisionSafeScale))
        {
            collisionSafeScale = tentativeSizeScale; // nothing to collide with (0 or 1 node)
        }

        var sizeScale = Math.Clamp(Math.Min(tentativeSizeScale, collisionSafeScale), 0.4, MaxSizeScale);
        var nodeWidth = NodeWidth * sizeScale;
        var nodeHeight = NodeHeight * sizeScale;
        var currentNodeWidth = CurrentNodeWidth * sizeScale;
        var currentNodeHeight = CurrentNodeHeight * sizeScale;

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
            // square, especially once nodes shrink under the collision-
            // avoidance scaling above — the midpoint always has clear space
            // around it and still unambiguously shows which way the flow
            // runs without having to trace the color/branch of each node.
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
                Canvas.SetLeft(label, center.X + width / 2 + 4);
                Canvas.SetTop(label, center.Y - 8);
                _canvas.Children.Add(label);
            }
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
