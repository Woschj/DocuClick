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
    private const double NodeSize = 14;
    private const double CurrentNodeSize = 20;

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
    private FlowPreview _lastPreview = new(new List<PreviewNode>(), new List<PreviewEdge>());
    private Point _resizeStartMouse;
    private Size _resizeStartSize;

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
        Height = PanelHeight + 34 + Padding;
        MinWidth = 160;
        MinHeight = 110;

        var header = new TextBlock
        {
            Text = "Ablauf-Übersicht — ziehbar/größenverstellbar, Knoten anklicken zum Springen",
            Foreground = Brushes.White,
            FontSize = 10,
            Opacity = 0.8,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(Padding, 6, Padding, 4)
        };
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

        var canvasHost = new Border
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
        content.Children.Add(canvasHost);

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

        var drawableWidth = canvasWidth - CurrentNodeSize;
        var drawableHeight = canvasHeight - CurrentNodeSize;
        var scale = Math.Min(drawableWidth / spanX, drawableHeight / spanY);

        (double X, double Y) ToCanvas(double x, double y) => (
            CurrentNodeSize / 2 + (x - minX) * scale,
            CurrentNodeSize / 2 + (y - minY) * scale);

        var centers = preview.Nodes.ToDictionary(n => n.Id, n => ToCanvas(n.X + n.Width / 2, n.Y + n.Height / 2));

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
                Stroke = new SolidColorBrush(Color.FromArgb(130, 255, 255, 255)),
                StrokeThickness = 1
            });
        }

        foreach (var node in preview.Nodes)
        {
            var center = centers[node.Id];
            var size = node.IsCurrent ? CurrentNodeSize : NodeSize;

            // Branch-marker nodes render as circles (RadiusX/Y = half the
            // size) instead of squares, so the waypoint itself stays
            // visually distinct even when its color matches every other
            // node further down that same branch.
            var radius = node.IsBranchMarker ? size / 2 : 3;

            var square = new Rectangle
            {
                Width = size,
                Height = size,
                RadiusX = radius,
                RadiusY = radius,
                Fill = new SolidColorBrush(GetNodeColor(node)),
                Stroke = Brushes.White,
                StrokeThickness = node.IsCurrent ? 2 : 1,
                Cursor = Cursors.Hand,
                ToolTip = node.BranchName is { } b ? $"{node.Label} · Branch: {b}" : node.Label
            };
            Canvas.SetLeft(square, center.X - size / 2);
            Canvas.SetTop(square, center.Y - size / 2);

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
                Canvas.SetLeft(label, center.X + size / 2 + 4);
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
