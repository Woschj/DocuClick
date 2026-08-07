using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using DocuClick.Services;
using Microsoft.Web.WebView2.Core;

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
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Orientation = System.Windows.Controls.Orientation;

namespace DocuClick;

/// <summary>
/// Freely draggable, semi-transparent minimap of the current flow (Canvas/
/// draw.io modes). The diagram itself — every node, its
/// pan/zoom/click/drag interaction — renders inside an embedded
/// <see cref="Microsoft.Web.WebView2.Wpf.WebView2"/> running a small local
/// HTML/JS page (<c>WebAssets/</c>, Cytoscape.js) instead of hand-drawn WPF
/// shapes. That's a deliberate rewrite: repeated rounds of WPF-specific
/// bugs (window activation eating the first click, ScrollViewer marking
/// clicks "Handled" regardless of target, drag-threshold/routed-event
/// fights) kept resurfacing even after each individual fix — browser
/// pointer-event handling is the battle-tested tool for exactly this job.
///
/// Only the *rendering and gesture* layer moved into the WebView; every
/// decision about what a gesture *means* still lives here in C#, via the
/// same public event contract as before
/// (<see cref="NodeClicked"/>/<see cref="NewPathRequested"/>/
/// <see cref="ContinuePathRequested"/>/<see cref="PathsProvider"/>/
/// <see cref="RenameRequested"/>/<see cref="DeleteRequested"/>/
/// <see cref="ReparentRequested"/>) — App.xaml.cs and SessionManager needed
/// no changes for this rewrite. See WebAssets/flow.js for the other half of
/// the message protocol.
/// </summary>
public sealed class FlowPreviewOverlay : Window
{
    private const double PanelWidth = 260;
    private const double PanelHeight = 190;
    private const double Padding = 12;
    private const double HeaderHeight = 26;
    private const double CollapsedMinHeight = HeaderHeight + 16;

    // The resize grip is a native WPF element that must remain clickable
    // on top of the WebView2 — a hosted native child window's "airspace"
    // can't be painted over by WPF Z-order the way two ordinary WPF
    // elements can. Reserving this margin on the WebView2's own bounds
    // keeps the grip in a corner the WebView2 never occupies, instead of
    // relying on stacking order.
    private const double ResizeGripSize = 16;

    // Fixed node sizes and grid spacing — still computed here (not in
    // flow.js) and sent to the WebView as explicit per-node coordinates,
    // so there's exactly one implementation of "where does a node go" (see
    // BuildPreviewPayload) instead of duplicating this in two languages.
    // Deliberately NOT derived from the panel's current size — see the
    // original WPF-canvas-era doc comment this carries forward: a flow
    // smaller than the panel leaves the rest empty rather than stretching
    // to fill it, and one larger pans/zooms (native in the WebView now)
    // rather than shrinking nodes down to illegibility.
    private const double NodeWidth = 46;
    private const double NodeHeight = 20;
    private const double CurrentNodeWidth = 60;
    private const double CurrentNodeHeight = 28;
    private const double RowSpacing = 42;
    private const double ColumnSpacing = 110;

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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly Microsoft.Web.WebView2.Wpf.WebView2 _webView;
    private readonly Border _canvasHost;
    private readonly TextBlock _collapseIcon;
    private FlowPreview _lastPreview = new(new List<PreviewNode>(), new List<PreviewEdge>());
    private Point _resizeStartMouse;
    private Size _resizeStartSize;
    private bool _collapsed;
    private double _expandedHeight;
    private bool _webViewReady;

    public event Action<string>? NodeClicked;

    /// <summary>Fired when "+ Neuer Pfad" is chosen from a node's popup, after the user has named it — (originNodeId, pathName).</summary>
    public event Action<string, string>? NewPathRequested;

    /// <summary>Fired when an existing path is chosen from a node's popup — the path's own start-node id.</summary>
    public event Action<string>? ContinuePathRequested;

    /// <summary>Supplies the existing paths forking from a node, queried fresh right when its popup opens — set by App.xaml.cs to <see cref="SessionManager.ListPaths"/>.</summary>
    public Func<string, List<PathInfo>>? PathsProvider { get; set; }

    /// <summary>Fired after a node is renamed via double-click or its context menu — (nodeId, newLabel).</summary>
    public event Action<string, string>? RenameRequested;

    /// <summary>Fired after a node's deletion is confirmed (the overlay itself handles the cascade-delete confirmation dialog) via its context menu.</summary>
    public event Action<string>? DeleteRequested;

    /// <summary>Fired after a completed drag-and-drop re-parents a node onto a different one — (nodeId, newParentNodeId).</summary>
    public event Action<string, string>? ReparentRequested;

    /// <summary>Fired when the drag-to-connect gesture completes (source node dragged onto a valid target) — (fromNodeId, toNodeId). Additive: never removes an existing edge, so a node can end up with more than one parent (a real merge point), unlike <see cref="ReparentRequested"/>.</summary>
    public event Action<string, string>? ConnectRequested;

    /// <summary>Fired when a connector's right-click menu confirms "Verbindung löschen" — (fromNodeId, toNodeId). The undo counterpart to <see cref="ConnectRequested"/>.</summary>
    public event Action<string, string>? DisconnectRequested;

    /// <summary>Fired when the header's close (✕) icon is clicked — App.xaml.cs hides rather than destroys the window, so <see cref="UpdatePreview"/> keeps the state current for whenever the TopBar's reopen button brings it back.</summary>
    public event Action? CloseRequested;

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

        // Short, single-line title instead of a permanently-wrapped
        // instruction sentence — that sentence used up two lines of
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

        var closeIcon = CreateHeaderIcon("✕", "Schließen (über den Button in der Ablauf-Leiste wieder öffnen)");
        closeIcon.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            CloseRequested?.Invoke();
        };

        var headerIcons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        headerIcons.Children.Add(collapseToggle);
        headerIcons.Children.Add(closeIcon);

        var header = new DockPanel { Margin = new Thickness(Padding, 6, 8, 6) };
        DockPanel.SetDock(headerIcons, Dock.Right);
        header.Children.Add(headerIcons);
        header.Children.Add(titleText);
        DockPanel.SetDock(header, Dock.Top);

        // Drag the whole panel from the header — the WebView2 area can't
        // bubble MouseLeftButtonDown up to a WPF ancestor the way ordinary
        // WPF elements do (a hosted native child window's input doesn't
        // route through WPF's tree), so panel-dragging is now exclusively
        // a header gesture. That already matches what the info tooltip
        // above has always told users ("Panel per Kopfzeile ziehbar").
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (!e.Handled)
            {
                Activate();
                DragMove();
            }
        };

        _webView = new Microsoft.Web.WebView2.Wpf.WebView2
        {
            // Avoids a white flash before the (dark, translucent) page
            // finishes loading — this panel is meant to look like a HUD,
            // not a browser window.
            DefaultBackgroundColor = System.Drawing.Color.Transparent,
            // Leaves the bottom-right corner free for the resize grip
            // (see ResizeGripSize's own doc comment for why margin, not
            // z-order, is what actually keeps it clickable).
            Margin = new Thickness(0, 0, ResizeGripSize, ResizeGripSize)
        };
        _ = InitializeWebViewAsync();

        var canvasArea = new Grid { ClipToBounds = true };
        canvasArea.Children.Add(_webView);

        _canvasHost = new Border
        {
            Margin = new Thickness(Padding, 0, Padding, Padding),
            Child = canvasArea
        };

        var resizeGrip = CreateResizeGripVisual();
        resizeGrip.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            resizeGrip.CaptureMouse();
            // Window-relative, not PointToScreen: PointToScreen returns
            // physical-pixel coordinates while Width/Height/ActualWidth are
            // DIPs, so on any scaled display (125%/150%/... — the Windows
            // 11 default on most laptops) that mismatch turned a
            // near-zero mouse move into a huge logical Width/Height jump
            // the instant the grip was clicked. Both reads below come from
            // the same window-relative, DIP-space origin, so no conversion
            // — and no unit mismatch — is needed.
            _resizeStartMouse = e.GetPosition(this);
            _resizeStartSize = new Size(ActualWidth, ActualHeight);
        };
        resizeGrip.MouseMove += (_, e) =>
        {
            if (!resizeGrip.IsMouseCaptured)
            {
                return;
            }

            var current = e.GetPosition(this);
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

        var bounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        Loaded += (_, _) =>
        {
            // Left screen edge, same anchor CanvasStatusOverlay/
            // RecordingIndicatorOverlay use — this panel now covers what
            // those two used to show (a screenshot thumbnail and a record
            // indicator), which stopped being useful once this became a
            // full editing tool, so it deliberately takes over their spot
            // rather than sitting at the top-right by default.
            Left = bounds.Left + 8;
            Top = bounds.Top + TopBarWindow.BarHeight + 16;
        };

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.ExcludeFromScreenCapture(hwnd);
            HwndSource.FromHwnd(hwnd)?.AddHook(NativeMethods.DeliverActivatingClick);
        };

        // No MouseEnter-triggered pre-activation here, unlike TopBarWindow
        // — WebView2 is a hosted native child window with its own HWND,
        // and the cursor crossing in and out of its "airspace" (which
        // covers nearly this entire panel) fires WPF's MouseEnter/
        // MouseLeave repeatedly and spuriously. Calling Activate() on every
        // one of those, mid-interaction, is what caused the panel to
        // visibly jump/stutter while clicking around or resizing.
        // The few remaining native WPF click targets (resize grip, collapse
        // toggle, close icon) rely on NativeMethods.DeliverActivatingClick's
        // WM_MOUSEACTIVATE hook alone instead — sufficient here because,
        // unlike the old ScrollViewer-based canvas, none of them are a
        // ScrollViewer whose own internal click-to-focus handling defeats
        // that hook (see DeliverActivatingClick's doc comment for that
        // specific, no-longer-applicable failure mode). Content rendered
        // inside the WebView2 itself needs no WPF-level activation help at
        // all — Chromium's child HWND handles its own activation.
    }

    /// <summary>
    /// Points the WebView2 at the local <c>WebAssets/</c> page and wires up
    /// the message protocol described in <see cref="OnWebMessageReceived"/>.
    /// Uses a virtual hostname rather than a bare <c>file://</c> navigation
    /// — the recommended WebView2 approach, and needed for
    /// <c>fetch()</c>/relative-path loading to behave like a normal site
    /// instead of hitting local-file CORS restrictions.
    /// </summary>
    private async Task InitializeWebViewAsync()
    {
        try
        {
            // Explicit UserDataFolder, not WebView2's own default: that
            // default is derived from the *host process's* own exe path —
            // when launched via "dotnet DocuClick.dll" (as opposed to the
            // published DocuClick.exe directly), the host process is
            // dotnet.exe itself, sitting under Program Files, which a
            // normal user has no write access to — EnsureCoreWebView2Async
            // then fails outright with E_ACCESSDENIED before ever reaching
            // the page. %LOCALAPPDATA% is always writable by the current
            // user regardless of how the app was launched or installed.
            var userDataFolder = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DocuClick", "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
            await _webView.EnsureCoreWebView2Async(environment);
        }
        catch (Exception ex)
        {
            // Remaining possible cause: the WebView2 Runtime itself isn't
            // installed (present by default on Windows 11 and kept current
            // via Edge updates on Windows 10, but not guaranteed on every
            // machine). The Ablauf-Übersicht simply stays blank rather than
            // crashing the whole app over a HUD panel.
            LogService.Log($"WebView2 konnte nicht initialisiert werden: {ex.Message}");
            return;
        }

        var webAssetsDir = System.IO.Path.Combine(AppContext.BaseDirectory, "WebAssets");
        _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "docuclick.flowpreview", webAssetsDir, CoreWebView2HostResourceAccessKind.Allow);
        // The diagram has its own right-click context menu (Umbenennen/
        // Löschen, see flow.js) — the browser's default one would just be
        // visual noise on top of it.
        _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        _webView.NavigationCompleted += (_, _) =>
        {
            _webViewReady = true;
            PostPreview(_lastPreview);
        };
        _webView.CoreWebView2.Navigate("https://docuclick.flowpreview/index.html");
    }

    /// <summary>
    /// Routes a gesture reported from flow.js to the exact same public
    /// events/dialogs the old hand-drawn WPF canvas used — see the class
    /// doc comment for why the decision logic itself never moved. Only
    /// captures the raw message here and defers the actual handling (see
    /// <see cref="HandleWebMessage"/>) to a fresh dispatcher operation:
    /// several cases below show a modal dialog (BranchNameWindow) or
    /// MessageBox, and doing that synchronously from directly inside
    /// WebView2's own WebMessageReceived callback froze the whole window
    /// outright (confirmed in testing — clicking "Abzweigung setzen" hung
    /// the app immediately) rather than merely risking a re-entrancy edge
    /// case. Deferring decouples this from WebView2's own call stack/
    /// message loop entirely — the pre-WebView2 version of this file had
    /// the same remedy for an analogous problem (a Popup opened directly
    /// inside a mouse-event handler could treat its own triggering click
    /// as an immediate "outside click" and self-dismiss).
    /// </summary>
    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var json = e.WebMessageAsJson;
        Dispatcher.BeginInvoke(new Action(() => HandleWebMessage(json)));
    }

    private void HandleWebMessage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var type = root.GetProperty("type").GetString();

        switch (type)
        {
            case "nodeClick":
                NodeClicked?.Invoke(root.GetProperty("nodeId").GetString()!);
                break;

            case "requestPaths":
            {
                var nodeId = root.GetProperty("nodeId").GetString()!;
                var paths = PathsProvider?.Invoke(nodeId) ?? new List<PathInfo>();
                var payload = new
                {
                    type = "pathsResult",
                    nodeId,
                    paths = paths.Select(p => new { pathStartNodeId = p.PathStartNodeId, name = p.Name, stepCount = p.StepCount })
                };
                _webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, JsonOptions));
                break;
            }

            case "newPath":
            {
                var nodeId = root.GetProperty("nodeId").GetString()!;
                var nameWindow = new BranchNameWindow { Owner = this };
                NativeMethods.ModalDialogDepth++;
                try
                {
                    if (nameWindow.ShowDialog() == true && nameWindow.BranchName is { } name)
                    {
                        NewPathRequested?.Invoke(nodeId, name);
                    }
                }
                finally
                {
                    NativeMethods.ModalDialogDepth--;
                }
                break;
            }

            case "continuePath":
                ContinuePathRequested?.Invoke(root.GetProperty("pathStartNodeId").GetString()!);
                break;

            case "rename":
            {
                var nodeId = root.GetProperty("nodeId").GetString()!;
                var node = _lastPreview.Nodes.FirstOrDefault(n => n.Id == nodeId);
                var initialValue = node?.PathName ?? node?.Label ?? "";
                var nameWindow = new BranchNameWindow("DocuClick - Umbenennen", "Bezeichnung", initialValue) { Owner = this };
                NativeMethods.ModalDialogDepth++;
                try
                {
                    if (nameWindow.ShowDialog() == true && nameWindow.BranchName is { } newLabel)
                    {
                        RenameRequested?.Invoke(nodeId, newLabel);
                    }
                }
                finally
                {
                    NativeMethods.ModalDialogDepth--;
                }
                break;
            }

            case "delete":
                RequestDelete(root.GetProperty("nodeId").GetString()!);
                break;

            case "reparent":
                ReparentRequested?.Invoke(root.GetProperty("nodeId").GetString()!, root.GetProperty("newParentId").GetString()!);
                break;

            case "connect":
                ConnectRequested?.Invoke(root.GetProperty("fromId").GetString()!, root.GetProperty("toId").GetString()!);
                break;

            case "disconnect":
                DisconnectRequested?.Invoke(root.GetProperty("fromId").GetString()!, root.GetProperty("toId").GetString()!);
                break;
        }
    }

    /// <summary>
    /// A node with more than one outgoing edge (a decision point, or any
    /// node a path was forked from) deletes its whole downstream subtree
    /// along with it — see <see cref="IFlowWriter.DeleteNode"/> — so this
    /// confirms that with the user first, naming exactly how many further
    /// steps would go with it, before firing <see cref="DeleteRequested"/>.
    /// A node with 0 or 1 (non-path-start) child needs no confirmation: at
    /// most one step is ever lost, the one being deleted itself.
    /// </summary>
    private void RequestDelete(string nodeId)
    {
        var node = _lastPreview.Nodes.FirstOrDefault(n => n.Id == nodeId);
        if (node is null)
        {
            return;
        }

        var childCount = _lastPreview.Edges.Count(e => e.FromId == nodeId);
        if (childCount > 1 || (childCount == 1 && node.IsPathStart))
        {
            var subtreeSize = DescendantsOf(nodeId).Count;
            NativeMethods.ModalDialogDepth++;
            MessageBoxResult confirm;
            try
            {
                confirm = MessageBox.Show(
                    this,
                    $"„{node.Label}“ hat {childCount} abzweigende Fortsetzungen — beim Löschen werden auch alle {subtreeSize} nachfolgenden Knoten gelöscht. Fortfahren?",
                    "DocuClick", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            }
            finally
            {
                NativeMethods.ModalDialogDepth--;
            }
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }
        }

        DeleteRequested?.Invoke(nodeId);
    }

    private HashSet<string> DescendantsOf(string nodeId)
    {
        var forward = _lastPreview.Edges
            .GroupBy(e => e.FromId)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ToId).ToList());

        var result = new HashSet<string>();
        var queue = new Queue<string>();
        if (forward.TryGetValue(nodeId, out var direct))
        {
            foreach (var c in direct)
            {
                queue.Enqueue(c);
            }
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!result.Add(current))
            {
                continue;
            }

            if (forward.TryGetValue(current, out var children))
            {
                foreach (var c in children)
                {
                    queue.Enqueue(c);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Collapses the panel down to just its header row (hiding the WebView)
    /// so it can be gotten out of the way without closing it outright, and
    /// restores it back to its previous size afterwards. MinHeight is
    /// temporarily lowered too — otherwise the window-level MinHeight
    /// constraint (needed so the expanded minimap never shrinks to
    /// illegibility) would also floor the collapsed size.
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
    /// The classic three-diagonal-stripe "resize corner" glyph (as seen on
    /// textarea/status-bar resize handles), not a plain filled square — a
    /// square reads as decoration, not as a drag affordance, at this size.
    /// A transparent Rectangle sits behind the stripes purely for hit-
    /// testing: WPF only hit-tests a shape's actual fill/stroke geometry,
    /// and three 1px-wide diagonal lines alone would make the *drag start*
    /// nearly impossible to land on.
    /// </summary>
    private static Grid CreateResizeGripVisual()
    {
        const double size = 14;
        var stripeBrush = new SolidColorBrush(Color.FromArgb(160, 255, 255, 255));

        var grid = new Grid
        {
            Width = size,
            Height = size,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 2, 2),
            Cursor = Cursors.SizeNWSE,
            ToolTip = "Ziehen zum Verändern der Größe"
        };
        grid.Children.Add(new Rectangle { Width = size, Height = size, Fill = Brushes.Transparent });

        // Three parallel diagonal stripes of increasing length, stacked
        // toward the corner — short-medium-long from the tip inward.
        (double, double, double, double)[] stripes = { (10, 14, 14, 10), (6, 14, 14, 6), (2, 14, 14, 2) };
        foreach (var (x1, y1, x2, y2) in stripes)
        {
            grid.Children.Add(new Line
            {
                X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                Stroke = stripeBrush,
                StrokeThickness = 1.4,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
        }

        return grid;
    }

    /// <summary>Stores the latest preview and pushes it to the WebView (once it's ready to receive messages — see <see cref="InitializeWebViewAsync"/>).</summary>
    public void UpdatePreview(FlowPreview preview)
    {
        _lastPreview = preview;
        if (_webViewReady)
        {
            PostPreview(preview);
        }
    }

    private void PostPreview(FlowPreview preview)
    {
        var payload = BuildPreviewPayload(preview);
        _webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, JsonOptions));
    }

    /// <summary>
    /// Converts a <see cref="FlowPreview"/> into the plain JSON shape
    /// flow.js expects, computing each node's position from the flow's
    /// graph topology exactly as the old WPF canvas did — see the class
    /// doc comment and this method's row/column derivation for why that's
    /// schematic (BFS depth + path-first-seen-order), not the real output
    /// file's coordinates.
    /// </summary>
    private PreviewPayload BuildPreviewPayload(FlowPreview preview)
    {
        if (preview.Nodes.Count == 0)
        {
            return new PreviewPayload("preview", new List<NodePayload>(), new List<EdgePayload>());
        }

        var forward = preview.Edges
            .GroupBy(e => e.FromId)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ToId).ToList());
        var hasInbound = preview.Edges.Select(e => e.ToId).ToHashSet();

        // componentRootOf tracks which BFS root each node traces back to —
        // normally there's exactly one (the flow's first-ever node), but
        // DisconnectNodes can now sever a node from it, leaving one or more
        // extra roots (fully isolated, or a small orphaned sub-chain).
        // Used below so those don't all collapse onto column 0 and overlap
        // whatever else is there — see IFlowWriter.DisconnectNodes's doc
        // comment on why that's allowed rather than refused outright.
        var rowOf = new Dictionary<string, int>();
        var componentRootOf = new Dictionary<string, string>();
        var bfsQueue = new Queue<string>();
        foreach (var node in preview.Nodes)
        {
            if (!hasInbound.Contains(node.Id))
            {
                rowOf[node.Id] = 0;
                componentRootOf[node.Id] = node.Id;
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
                componentRootOf[child] = componentRootOf[id];
                bfsQueue.Enqueue(child);
            }
        }

        // Defensive: any node the BFS above never reached (shouldn't
        // happen for a tree-shaped flow, but a stray disconnected node
        // must not throw a KeyNotFoundException below) just lands at row 0,
        // its own singleton component.
        foreach (var node in preview.Nodes)
        {
            rowOf.TryAdd(node.Id, 0);
            componentRootOf.TryAdd(node.Id, node.Id);
        }

        var columnKeyToColumn = new Dictionary<string, int>();
        var columnOf = new Dictionary<string, int>();
        string? mainRootId = null;
        var nextColumn = 1;
        foreach (var node in preview.Nodes)
        {
            string columnKey;
            bool isMainRoot;
            if (node.PathId is { } pathId)
            {
                columnKey = "path:" + pathId;
                isMainRoot = false;
            }
            else
            {
                var root = componentRootOf[node.Id];
                mainRootId ??= root; // first non-path root seen keeps the original column-0 behavior
                isMainRoot = root == mainRootId;
                columnKey = "root:" + root;
            }

            if (isMainRoot)
            {
                columnOf[node.Id] = 0;
                continue;
            }

            if (!columnKeyToColumn.TryGetValue(columnKey, out var column))
            {
                column = nextColumn++;
                columnKeyToColumn[columnKey] = column;
            }

            columnOf[node.Id] = column;
        }

        var halfExtentX = CurrentNodeWidth / 2 + 8;
        var halfExtentY = CurrentNodeHeight / 2 + 8;

        (double X, double Y) GridPosition(string nodeId) => (
            halfExtentX + columnOf[nodeId] * ColumnSpacing + ColumnSpacing / 2,
            halfExtentY + rowOf[nodeId] * RowSpacing);

        var nodes = preview.Nodes.Select(n =>
        {
            var (x, y) = GridPosition(n.Id);
            var isMarker = n.IsDecisionPoint || n.IsPathStart;
            var width = n.IsCurrent ? CurrentNodeWidth : NodeWidth;
            var height = n.IsCurrent ? CurrentNodeHeight : NodeHeight;
            var hasChildren = forward.ContainsKey(n.Id);
            var permLabel = n.IsDecisionPoint
                ? "◆ Abzweigung"
                : n.IsPathStart && n.PathName is { } pathName
                    ? $"↳ {pathName}"
                    : n.IsCurrent
                        ? "● hier"
                        : "";

            return new NodePayload(
                n.Id, n.Label, permLabel, x, y, width, height, ColorToCss(GetNodeColor(n)),
                isMarker, n.IsDecisionPoint, n.IsPathStart, n.IsCurrent, hasChildren, n.PathName);
        }).ToList();

        var edges = preview.Edges.Select(e => new EdgePayload(e.FromId, e.ToId)).ToList();

        return new PreviewPayload("preview", nodes, edges);
    }

    private static string ColorToCss(Color c) => c.A == 255
        ? $"#{c.R:X2}{c.G:X2}{c.B:X2}"
        : $"rgba({c.R},{c.G},{c.B},{(c.A / 255.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)})";

    private static Color GetNodeColor(PreviewNode node)
    {
        if (node.IsCurrent)
        {
            return Color.FromRgb(0xE6, 0x39, 0x46);
        }

        // Decision points are always neutral gray, matching the actual
        // output file (DrawIoFlowWriter etc. use the same fixed color for
        // them) — they aren't part of any one path's color themselves,
        // regardless of which path happened to lead into them.
        if (node.IsDecisionPoint)
        {
            return Color.FromRgb(0x6B, 0x72, 0x80);
        }

        if (node.PathId is { } pathId)
        {
            return BranchPalette[StableHash(pathId) % BranchPalette.Length];
        }

        return Color.FromArgb(230, 0x4C, 0xAF, 0xE8);
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

    private sealed record NodePayload(
        string Id, string Label, string PermLabel, double X, double Y, double Width, double Height, string Color,
        bool IsMarker, bool IsDecisionPoint, bool IsPathStart, bool IsCurrent, bool HasChildren, string? PathName);

    private sealed record EdgePayload(string Source, string Target);

    private sealed record PreviewPayload(string Type, List<NodePayload> Nodes, List<EdgePayload> Edges);
}
