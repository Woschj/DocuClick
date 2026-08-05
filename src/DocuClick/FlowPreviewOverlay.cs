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

    private readonly Canvas _canvas;
    private readonly TextBlock _emptyHint;

    public event Action<string>? NodeClicked;

    public FlowPreviewOverlay()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;

        var header = new TextBlock
        {
            Text = "Ablauf-Übersicht — ziehbar, Knoten anklicken zum Springen",
            Foreground = Brushes.White,
            FontSize = 10,
            Opacity = 0.8,
            TextWrapping = TextWrapping.Wrap,
            Width = PanelWidth,
            Margin = new Thickness(Padding, 6, Padding, 4)
        };

        _canvas = new Canvas { Width = PanelWidth, Height = PanelHeight };

        _emptyHint = new TextBlock
        {
            Text = "Noch keine Klicks aufgezeichnet.",
            Foreground = Brushes.White,
            Opacity = 0.6,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Width = PanelWidth,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var canvasArea = new Grid { Width = PanelWidth, Height = PanelHeight, ClipToBounds = true };
        canvasArea.Children.Add(_canvas);
        canvasArea.Children.Add(_emptyHint);

        var canvasHost = new Border
        {
            Width = PanelWidth,
            Height = PanelHeight,
            Margin = new Thickness(Padding, 0, Padding, Padding),
            Child = canvasArea
        };

        var content = new StackPanel();
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

        // Drag the whole panel from anywhere except a node square — those
        // mark the event Handled in UpdatePreview before it bubbles here.
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

        UpdatePreview(new FlowPreview(new List<PreviewNode>(), new List<PreviewEdge>()));
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
        _canvas.Children.Clear();

        if (preview.Nodes.Count == 0)
        {
            _emptyHint.Visibility = Visibility.Visible;
            return;
        }

        _emptyHint.Visibility = Visibility.Collapsed;

        var minX = preview.Nodes.Min(n => n.X);
        var minY = preview.Nodes.Min(n => n.Y);
        var maxX = preview.Nodes.Max(n => n.X + n.Width);
        var maxY = preview.Nodes.Max(n => n.Y + n.Height);
        var spanX = Math.Max(maxX - minX, 1);
        var spanY = Math.Max(maxY - minY, 1);

        var drawableWidth = PanelWidth - CurrentNodeSize;
        var drawableHeight = PanelHeight - CurrentNodeSize;
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

            var square = new Rectangle
            {
                Width = size,
                Height = size,
                RadiusX = 3,
                RadiusY = 3,
                Fill = node.IsCurrent
                    ? new SolidColorBrush(Color.FromRgb(0xE6, 0x39, 0x46))
                    : node.IsBranchMarker
                        ? new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED))
                        : new SolidColorBrush(Color.FromArgb(230, 0x4C, 0xAF, 0xE8)),
                Stroke = Brushes.White,
                StrokeThickness = node.IsCurrent ? 2 : 1,
                Cursor = Cursors.Hand,
                ToolTip = node.Label
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
        }
    }
}
