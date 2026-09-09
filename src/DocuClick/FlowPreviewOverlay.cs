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
/// Freely draggable, semi-transparent minimap of the current flow (Canvas
/// mode — the only live-recording branching mode). The diagram itself —
/// every node, its pan/zoom/click/drag interaction — renders inside an embedded
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
/// <see cref="ConnectRequested"/>) — App.xaml.cs and SessionManager needed
/// no changes for this rewrite. See WebAssets/flow.js for the other half of
/// the message protocol.
/// </summary>
public sealed class FlowPreviewOverlay : Window
{
    private const double PanelPadding = 12;
    private const double HeaderHeight = 26;
    private const double CollapsedMinHeight = HeaderHeight + 16;

    // The resize grip is a native WPF element that must remain clickable
    // on top of the WebView2 — a hosted native child window's "airspace"
    // can't be painted over by WPF Z-order the way two ordinary WPF
    // elements can. Reserving this margin on the WebView2's own bounds
    // keeps the grip in a corner the WebView2 never occupies, instead of
    // relying on stacking order.
    private const double ResizeGripSize = 16;

    // Two size presets, switched via SetLargeMode: a small, unobtrusive
    // minimap while a recording is actually running (mustn't cover the
    // screen being recorded — see the "compact" defaults, unchanged from
    // before this existed) vs. a much bigger editing window for "Ablauf
    // öffnen" on an existing file with no recording active, where the
    // point is to actually *look* at the flow — same-size cards as the
    // exported/live HTML's own Cytoscape rendering (220x170), so a
    // screenshot thumbnail is as recognizable here as it is in the
    // browser instead of shrunk down to a barely-there smudge.
    private const double CompactPanelWidth = 420;
    private const double CompactPanelHeight = 320;
    private const double CompactNodeWidth = 72;
    private const double CompactNodeHeight = 54;
    private const double CompactCurrentNodeWidth = 92;
    private const double CompactCurrentNodeHeight = 70;
    private const double CompactRowSpacing = 68;
    private const double CompactColumnSpacing = 130;

    private const double LargePanelWidth = 1000;
    private const double LargePanelHeight = 720;
    private const double LargeNodeWidth = 200;
    private const double LargeNodeHeight = 150;
    private const double LargeCurrentNodeWidth = 220;
    private const double LargeCurrentNodeHeight = 170;
    private const double LargeRowSpacing = 190;
    private const double LargeColumnSpacing = 260;

    // Fixed node sizes and grid spacing for whichever preset is currently
    // active — still computed here (not in flow.js) and sent to the
    // WebView as explicit per-node coordinates, so there's exactly one
    // implementation of "where does a node go" (see BuildPreviewPayload)
    // instead of duplicating this in two languages. Deliberately NOT
    // derived from the panel's current size — see the original WPF-
    // canvas-era doc comment this carries forward: a flow smaller than the
    // panel leaves the rest empty rather than stretching to fill it, and
    // one larger pans/zooms (native in the WebView now) rather than
    // shrinking nodes down to illegibility.
    private double _panelWidth = CompactPanelWidth;
    private double _panelHeight = CompactPanelHeight;
    private double _nodeWidth = CompactNodeWidth;
    private double _nodeHeight = CompactNodeHeight;
    private double _currentNodeWidth = CompactCurrentNodeWidth;
    private double _currentNodeHeight = CompactCurrentNodeHeight;
    private double _rowSpacing = CompactRowSpacing;
    private double _columnSpacing = CompactColumnSpacing;
    private bool _largeMode;

    // Same accent palette as DrawIoConverter's branch colors, reused here
    // so a branch's minimap dot and its actual card color line up in a
    // draw.io export. A stable (non-randomized) hash of the branch name
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

    private readonly AppConfig _config;
    private readonly Microsoft.Web.WebView2.Wpf.WebView2 _webView;
    private readonly Border _canvasHost;
    private readonly TextBlock _collapseIcon;
    private FlowPreview _lastPreview = new(new List<PreviewNode>(), new List<PreviewEdge>());
    private Point _resizeStartMouse;
    private Size _resizeStartSize;
    private bool _collapsed;
    private double _expandedHeight;
    private bool _webViewReady;
    private string? _mappedOutputPath;

    // Keyed by output-relative path, populated lazily — a screenshot file is
    // never modified after AttachmentSaver first writes it, so there is
    // nothing to invalidate this against; without it, BuildImageDataUri was
    // re-reading and re-base64-encoding *every* image-bearing node's
    // screenshot on *every single* large-mode preview push (each move/
    // rename/connect while editing), not just the one node that actually
    // changed — confirmed as a real, avoidable per-push cost.
    private readonly Dictionary<string, string> _imageDataUriCache = new();

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

    /// <summary>Fired when the drag-to-connect gesture completes (source node dragged onto a valid target) — (fromNodeId, toNodeId). Additive: never removes an existing edge, so a node can end up with more than one parent (a real merge point).</summary>
    public event Action<string, string>? ConnectRequested;

    /// <summary>Fired when a connector's right-click menu confirms "Verbindung löschen" — (fromNodeId, toNodeId). The undo counterpart to <see cref="ConnectRequested"/>.</summary>
    public event Action<string, string>? DisconnectRequested;

    /// <summary>Fired when a large-mode drag-to-move gesture completes — (nodeId, x, y), the node's new position in its own real coordinate space. Never fires from the compact live-recording minimap, which has no drag-to-move gesture at all (see flow.js's mousedown handler).</summary>
    public event Action<string, double, double>? MoveRequested;

    /// <summary>Fired after the "+ Neuer Knoten hier" background context menu's naming dialog is confirmed — (label, x, y), where the user right-clicked. Large mode only, same reasoning as <see cref="MoveRequested"/>.</summary>
    public event Action<string, double, double>? AddNodeRequested;

    /// <summary>Fired when the header's close (✕) icon is clicked — App.xaml.cs hides rather than destroys the window, so <see cref="UpdatePreview"/> keeps the state current for whenever the TopBar's reopen button brings it back.</summary>
    public event Action? CloseRequested;

    public FlowPreviewOverlay(AppConfig config)
    {
        _config = config;
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
        Width = _panelWidth + PanelPadding * 2;
        Height = _panelHeight + HeaderHeight + PanelPadding;
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

        var header = new DockPanel { Margin = new Thickness(PanelPadding, 6, 8, 6) };
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
            Margin = new Thickness(PanelPadding, 0, PanelPadding, PanelPadding),
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
            // SetLargeMode already centered the window if it was called
            // (with large: true) before this first Show() — don't stomp
            // that back to the compact HUD's top-left anchor.
            if (_largeMode)
            {
                return;
            }

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
            EnsureOutputMapping();
            PostPreview(_lastPreview);
        };
        _webView.CoreWebView2.Navigate("https://docuclick.flowpreview/index.html");
    }

    /// <summary>
    /// Exposes the output folder as its own virtual host so node payloads
    /// can carry a plain image URL (letting the browser load/cache
    /// screenshots itself) instead of embedding every screenshot's bytes as
    /// base64 in *every* preview push — that would bloat the WebView
    /// message on every single click, the same per-click cost problem that
    /// made the old live draw.io writer too slow to keep as a recording
    /// mode (see DrawIoConverter's own doc comment). Re-checked on every
    /// push (cheap: one string compare) rather than once at startup, since
    /// the output path can change later in Settings while this window is
    /// still alive.
    /// </summary>
    private void EnsureOutputMapping()
    {
        if (!_webViewReady)
        {
            return;
        }

        var outputPath = _config.OutputPath;
        if (string.IsNullOrWhiteSpace(outputPath) || !System.IO.Directory.Exists(outputPath)
            || string.Equals(_mappedOutputPath, outputPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "docuclick.output", outputPath, CoreWebView2HostResourceAccessKind.Allow);
        _mappedOutputPath = outputPath;
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

            case "connect":
                ConnectRequested?.Invoke(root.GetProperty("fromId").GetString()!, root.GetProperty("toId").GetString()!);
                break;

            case "disconnect":
                DisconnectRequested?.Invoke(root.GetProperty("fromId").GetString()!, root.GetProperty("toId").GetString()!);
                break;

            case "move":
                MoveRequested?.Invoke(
                    root.GetProperty("nodeId").GetString()!,
                    root.GetProperty("x").GetDouble(),
                    root.GetProperty("y").GetDouble());
                break;

            case "addNode":
            {
                var x = root.GetProperty("x").GetDouble();
                var y = root.GetProperty("y").GetDouble();
                var nameWindow = new BranchNameWindow("DocuClick - Neuer Knoten", "Bezeichnung", "") { Owner = this };
                NativeMethods.ModalDialogDepth++;
                try
                {
                    if (nameWindow.ShowDialog() == true && nameWindow.BranchName is { } label)
                    {
                        AddNodeRequested?.Invoke(label, x, y);
                    }
                }
                finally
                {
                    NativeMethods.ModalDialogDepth--;
                }
                break;
            }

            // Diagnostic only (see flow.js's rebuildImageOverlays): a
            // thumbnail's <img> failed to load. Logged rather than shown to
            // the user — this HUD has no DevTools access in practice, so
            // LogService.Log's file is the only way to see the exact URL
            // and pin down why (bad output-folder mapping, wrong escaping, the file
            // genuinely missing, ...).
            case "imageLoadError":
                LogService.Log($"Ablauf-Übersicht: Bild konnte nicht geladen werden: {root.GetProperty("url").GetString()}");
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

    /// <summary>
    /// Switches between the compact live-recording minimap and the much
    /// bigger "Ablauf öffnen" editing window — see the size presets' own
    /// doc comment above. Called on every preview push with whether a
    /// recording is currently running (App.xaml.cs already tracks this via
    /// <see cref="SessionManager.IsRunning"/>) rather than once at
    /// construction, since the very same overlay instance is reused across
    /// however many recording/editing sessions happen while the app stays
    /// open. A no-op when the mode hasn't actually changed, so it never
    /// fights a manual resize-grip adjustment on every single click.
    /// </summary>
    public void SetLargeMode(bool large)
    {
        if (large == _largeMode)
        {
            return;
        }

        _largeMode = large;
        _panelWidth = large ? LargePanelWidth : CompactPanelWidth;
        _panelHeight = large ? LargePanelHeight : CompactPanelHeight;
        _nodeWidth = large ? LargeNodeWidth : CompactNodeWidth;
        _nodeHeight = large ? LargeNodeHeight : CompactNodeHeight;
        _currentNodeWidth = large ? LargeCurrentNodeWidth : CompactCurrentNodeWidth;
        _currentNodeHeight = large ? LargeCurrentNodeHeight : CompactCurrentNodeHeight;
        _rowSpacing = large ? LargeRowSpacing : CompactRowSpacing;
        _columnSpacing = large ? LargeColumnSpacing : CompactColumnSpacing;

        var newHeight = _panelHeight + HeaderHeight + PanelPadding;
        if (_collapsed)
        {
            // Collapsed only shows the header row right now — just remember
            // the new target height for whenever it's expanded again.
            _expandedHeight = newHeight;
        }
        else
        {
            Width = _panelWidth + PanelPadding * 2;
            Height = newHeight;
        }

        var bounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        if (large)
        {
            // Centered, not the compact HUD's top-left anchor — this is now
            // a real editing window the user looks straight at, not an
            // unobtrusive corner overlay.
            Left = bounds.Left + (bounds.Width - Width) / 2;
            Top = bounds.Top + (bounds.Height - Height) / 2;
        }
        else
        {
            Left = bounds.Left + 8;
            Top = bounds.Top + TopBarWindow.BarHeight + 16;
        }
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
        EnsureOutputMapping();
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
            return new PreviewPayload("preview", _largeMode, new List<NodePayload>(), new List<EdgePayload>());
        }

        // Row/column grid slot, from graph topology alone (structural edges
        // only — a manual cross-connect must draw as a single extra line
        // and never shift where a node "really" belongs; see
        // FlowPreviewBranching.ComputeGridLayout's own doc comment for the
        // bug this used to cause). Shared with CanvasFlowWriter's
        // end-of-session relayout so the live minimap and the persisted
        // .canvas file's actual node positions can never drift into two
        // different "clean" arrangements.
        var slotOf = FlowPreviewBranching.ComputeGridLayout(preview);
        var forward = preview.Edges
            .Where(e => !e.Manual)
            .GroupBy(e => e.FromId)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ToId).ToList());

        var halfExtentX = _currentNodeWidth / 2 + 8;
        var halfExtentY = _currentNodeHeight / 2 + 8;

        (double X, double Y) GridPosition(string nodeId)
        {
            var (row, column) = slotOf[nodeId];
            return (halfExtentX + column * _columnSpacing + _columnSpacing / 2, halfExtentY + row * _rowSpacing);
        }

        var nodes = preview.Nodes.Select(n =>
        {
            // Large (editing) mode uses the node's own real, persisted
            // coordinates instead of the schematic grid slot — the whole
            // point of drag-to-move (see MoveNode) is overriding the
            // auto-layout, which only means anything if what's actually on
            // screen is what's on disk. Compact mode keeps the schematic
            // layout: it's a tiny live-recording minimap with no dragging,
            // where a clean fixed grid reads better than the real
            // (branch-column-spaced, much larger) coordinates would at
            // that scale.
            var (x, y) = _largeMode ? (n.X, n.Y) : GridPosition(n.Id);
            var isMarker = n.IsDecisionPoint || n.IsPathStart;
            var width = n.IsCurrent ? _currentNodeWidth : _nodeWidth;
            var height = n.IsCurrent ? _currentNodeHeight : _nodeHeight;
            var hasChildren = forward.ContainsKey(n.Id);
            var permLabel = n.IsDecisionPoint
                ? "◆ Abzweigung"
                : n.IsPathStart && n.PathName is { } pathName
                    ? $"↳ {pathName}"
                    : n.IsCurrent
                        ? "● hier"
                        : "";

            // Compact mode only ever shows permLabel (a short marker tag,
            // blank for an ordinary node — the full description is a hover
            // tooltip instead, see flow.js) exactly as before this existed.
            // Large mode shows the actual description right on the card,
            // matching the exported/live HTML's own always-visible label —
            // the whole point of this mode being to look like that view.
            var displayLabel = !_largeMode
                ? permLabel
                : isMarker ? permLabel : n.IsCurrent ? $"● {n.Label}" : n.Label;

            // Large mode embeds the screenshot as a data: URI instead of a
            // docuclick.output URL — confirmed via a real run (see
            // LogService's "Bild konnte nicht geladen werden" entries) that
            // a plain <img> pointed at that virtual host still fails to
            // load the file, even though it exists at exactly that path;
            // a data: URI needs no cross-origin/virtual-host resolution of
            // any kind, so it sidesteps whatever that turns out to be.
            // Compact mode keeps the URL approach: it re-sends the whole
            // preview on *every single click* during a live recording, and
            // base64-embedding every screenshot on every one of those is
            // the exact per-click cost this design already avoided once
            // (see BuildImageUrl's own doc comment) — acceptable for large
            // mode's one-time load, not for that.
            var imageUrl = _largeMode ? BuildImageDataUri(n.ImagePath) : BuildImageUrl(n.ImagePath);

            return new NodePayload(
                n.Id, n.Label, permLabel, displayLabel, x, y, width, height, ColorToCss(GetNodeColor(n)),
                isMarker, n.IsDecisionPoint, n.IsPathStart, n.IsCurrent, hasChildren, n.PathName,
                imageUrl);
        }).ToList();

        var edges = preview.Edges.Select(e => new EdgePayload(e.FromId, e.ToId, e.Manual)).ToList();

        return new PreviewPayload("preview", _largeMode, nodes, edges);
    }

    /// <summary>
    /// Turns an output-relative screenshot path (as stored on the Canvas
    /// file's sibling "file" node, e.g. "Attachments/Session/073934_321.png")
    /// into a URL under the <see cref="EnsureOutputMapping"/> virtual host —
    /// each path segment is escaped separately (not the whole string, which
    /// would also escape the "/" separators the mapping needs intact), since
    /// session/folder names routinely contain spaces or parentheses.
    /// </summary>
    private static string? BuildImageUrl(string? outputRelativePath) => outputRelativePath is null
        ? null
        : "https://docuclick.output/" + string.Join("/", outputRelativePath.Split('/').Select(Uri.EscapeDataString));

    /// <summary>
    /// See the large-mode branch in <see cref="BuildPreviewPayload"/> for why
    /// this exists alongside <see cref="BuildImageUrl"/> instead of replacing
    /// it outright. Cached by path — see <see cref="_imageDataUriCache"/>'s
    /// own doc comment.
    /// </summary>
    private string? BuildImageDataUri(string? outputRelativePath)
    {
        if (outputRelativePath is null)
        {
            return null;
        }

        if (_imageDataUriCache.TryGetValue(outputRelativePath, out var cached))
        {
            return cached;
        }

        var fullPath = System.IO.Path.Combine(_config.OutputPath, outputRelativePath);
        try
        {
            var bytes = System.IO.File.ReadAllBytes(fullPath);
            var result = $"data:image/png;base64,{Convert.ToBase64String(bytes)}";
            _imageDataUriCache[outputRelativePath] = result;
            return result;
        }
        catch (Exception ex)
        {
            // Deliberately not cached — a transient failure (e.g. a
            // momentary antivirus lock right after the file was written)
            // should get another chance on the next push, not be
            // permanently remembered as broken for this window's lifetime.
            LogService.Log($"Ablauf-Übersicht: Screenshot konnte nicht eingebettet werden ({fullPath}): {ex.Message}");
            return null;
        }
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

        // Decision points are always neutral gray, matching a draw.io
        // export (DrawIoConverter uses the same fixed color for them) —
        // they aren't part of any one path's color themselves,
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
        string Id, string Label, string PermLabel, string DisplayLabel, double X, double Y, double Width, double Height, string Color,
        bool IsMarker, bool IsDecisionPoint, bool IsPathStart, bool IsCurrent, bool HasChildren, string? PathName,
        string? ImageUrl);

    private sealed record EdgePayload(string Source, string Target, bool Manual);

    private sealed record PreviewPayload(string Type, bool Large, List<NodePayload> Nodes, List<EdgePayload> Edges);
}
