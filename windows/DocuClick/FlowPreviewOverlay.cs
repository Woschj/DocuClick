using System.Windows;
using System.Windows.Automation;
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
/// The Ablauf-Übersicht: a freely draggable, semi-transparent window with the
/// shared editor template (WebAssets/viewer.template.html — the same editor
/// as the .html file in a browser, the Obsidian plugin and the macOS app) in
/// a <see cref="Microsoft.Web.WebView2.Wpf.WebView2"/>. <see cref="PageHost"/>
/// handles its messages: the page edits and saves the whole document, newly
/// recorded clicks reach it as messages, and it adds the recording actions
/// ("Hier weiter aufnehmen", "Neuer Pfad ab hier").
/// </summary>
public sealed class FlowPreviewOverlay : Window, IFlowEditorHost
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

    // One fixed size for the whole editing window.
    private const double PanelWidth = 1000;
    private const double PanelHeight = 720;
    private double _panelWidth = PanelWidth;
    private double _panelHeight = PanelHeight;

    /// <summary>Absolute folder of the loaded session — screenshots are resolved relative to it.</summary>
    public string? CurrentSessionFolder { get; set; }

    /// <summary>The page's counterpart in the app (messages, saves, recorded clicks).</summary>
    public EditorPageHost PageHost { get; }

    // The page is written here and served as https://docuclick.editor/,
    // screenshots come from https://docuclick.session/ (the vault root, or the
    // session folder outside a vault).
    private static readonly string EditorPageFolder = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DocuClick", "EditorPage");
    private string? _sessionImageRoot;
    private int _pageVersion;

    /// <summary>(session folder, file node value) → the screenshot's URL for the template page; null outside the mapped folder.</summary>
    public string? ImageUrl(string folder, string file)
    {
        var root = ObsidianVault.FindRoot(folder) ?? folder;
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, file));
        var relative = System.IO.Path.GetRelativePath(root, full);
        if (relative.StartsWith("..", StringComparison.Ordinal) || System.IO.Path.IsPathRooted(relative))
        {
            return null;
        }

        return "https://docuclick.session/" + string.Join("/", relative.Split('\\', '/').Select(Uri.EscapeDataString));
    }

    private readonly Microsoft.Web.WebView2.Wpf.WebView2 _webView;
    private readonly Border _canvasHost;
    private readonly TextBlock _collapseIcon;
    private Point _resizeStartMouse;
    private Size _resizeStartSize;
    private bool _collapsed;
    private double _expandedHeight;
    private bool _webViewReady;

    /// <summary>Fired when the header's close (✕) icon is clicked — App.xaml.cs hides rather than destroys the window, so <see cref="UpdatePreview"/> keeps the state current for whenever the TopBar's reopen button brings it back.</summary>
    public event Action? CloseRequested;

    public FlowPreviewOverlay(SessionManager session)
    {
        PageHost = new EditorPageHost(session, this, ImageUrl);
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

        // Centered — this is a real editing window the user looks straight
        // at, not an unobtrusive corner overlay. Clamp dimensions so it
        // never overflows the monitor work area (e.g. on laptops or scaled displays).
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(Width, Math.Max(MinWidth, workArea.Width - 32));
        Height = Math.Min(Height, Math.Max(MinHeight, workArea.Height - 32));
        Left = workArea.Left + (workArea.Width - Width) / 2;
        Top = workArea.Top + (workArea.Height - Height) / 2;

        // Short, single-line title instead of a permanently-wrapped
        // instruction sentence — that sentence used up two lines of
        // vertical space on every redraw even though it only needs to be
        // read once. The full instructions now live in the info icon's
        // tooltip, and the panel can be collapsed to just this header row
        // via the toggle icon when the user doesn't currently need it.
        var titleText = new TextBlock
        {
            Text = "DocuClick · Ablauf-Übersicht",
            Foreground = Brushes.White,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Opacity = 0.95,
            VerticalAlignment = VerticalAlignment.Center
        };

        var fitIcon = CreateHeaderIcon("⛶", "Ansicht zentrieren / einpassen (auch per Doppelklick auf die leere Fläche)");
        AutomationProperties.SetAutomationId(fitIcon, "FlowPreview.FitViewButton");
        fitIcon.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (_webViewReady && _webView?.CoreWebView2 is not null)
            {
                _ = _webView.CoreWebView2.ExecuteScriptAsync("typeof ensureViewFit === 'function' && ensureViewFit()");
            }
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
        AutomationProperties.SetAutomationId(collapseToggle, "FlowPreview.CollapseButton");
        collapseToggle.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            ToggleCollapsed();
        };

        var closeIcon = CreateHeaderIcon("✕", "Schließen (über den Button in der Ablauf-Leiste wieder öffnen)");
        AutomationProperties.SetAutomationId(closeIcon, "FlowPreview.CloseButton");
        closeIcon.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            CloseRequested?.Invoke();
        };

        var headerIcons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        headerIcons.Children.Add(fitIcon);
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
            Background = new SolidColorBrush(Color.FromArgb(220, 11, 15, 25)),
            CornerRadius = new CornerRadius(10),
            BorderBrush = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Child = content
        };
        Content = border;

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
    /// Sets up the WebView2 and loads the editor page (see <see cref="LoadTemplatePage"/>).
    /// Uses virtual hostnames rather than bare <c>file://</c> navigation — the
    /// recommended WebView2 approach, so the page may read the screenshots'
    /// pixels (redaction) instead of hitting local-file restrictions.
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

        // The editor has its own right-click context menu — the browser's
        // default one would just be visual noise on top of it.
        _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        // No DevTools on this HUD panel — it only ever navigates to our own
        // bundled WebAssets, so there's nothing to debug via F12 in normal
        // use, and leaving it enabled is needless extra surface for anyone
        // with mouse/keyboard access to the running app.
        _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
        _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        System.IO.Directory.CreateDirectory(EditorPageFolder);
        _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "docuclick.editor", EditorPageFolder, CoreWebView2HostResourceAccessKind.Allow);
        _webView.NavigationCompleted += (_, _) => _webViewReady = true;
        LoadTemplatePage();
    }

    /// <summary>
    /// (Re)builds the page for the loaded flow — at start and
    /// when another file is loaded; later clicks reach the running page as
    /// messages (EditorPageHost.OnSessionChanged), so zoom and undo stay.
    /// </summary>
    private void LoadTemplatePage()
    {
        if (_webView.CoreWebView2 is null)
        {
            return;
        }

        var folder = CurrentSessionFolder;
        var root = folder is null ? null : ObsidianVault.FindRoot(folder) ?? folder;
        if (root is not null && root != _sessionImageRoot)
        {
            if (_sessionImageRoot is not null)
            {
                _webView.CoreWebView2.ClearVirtualHostNameToFolderMapping("docuclick.session");
            }

            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "docuclick.session", root, CoreWebView2HostResourceAccessKind.Allow);
            _sessionImageRoot = root;
        }

        var html = PageHost.BuildPage()
            ?? "<!doctype html><meta charset=\"utf-8\"><body style=\"background:#0b0f19;color:#94a3b8;font:13px system-ui;padding:24px\">Noch kein Ablauf geladen.</body>";
        System.IO.File.WriteAllText(System.IO.Path.Combine(EditorPageFolder, "index.html"), html);
        _webViewReady = false;
        _webView.CoreWebView2.Navigate($"https://docuclick.editor/index.html?v={++_pageVersion}");
    }

    /// <summary>
    /// Hands a message from the page to <see cref="PageHost"/>. Only captures
    /// the raw message here and defers the actual handling to a fresh
    /// dispatcher operation: "Neuer Pfad ab hier" shows a modal dialog
    /// (BranchNameWindow), and doing that synchronously from directly inside
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
        Dispatcher.BeginInvoke(new Action(() => _ = HandleWebMessageAsync(json)));
    }

    private async Task HandleWebMessageAsync(string json)
    {
        try
        {
            await PageHost.HandleMessageAsync(json);
        }
        catch (Exception ex)
        {
            LogService.Log($"Ablauf-Übersicht: Nachricht konnte nicht verarbeitet werden: {ex}");
        }
    }

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
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(35, 255, 255, 255)),
            Margin = new Thickness(4, 0, 0, 0),
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
            Child = content
        };
        icon.MouseEnter += (_, _) => icon.Background = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255));
        icon.MouseLeave += (_, _) => icon.Background = new SolidColorBrush(Color.FromArgb(35, 255, 255, 255));
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


    /// <summary>The session changed (click recorded, jump, new path, file switch): bring the page up to date.</summary>
    public void UpdatePreview()
    {
        if (_webView.CoreWebView2 is null)
        {
            return; // the first page is built once the WebView2 is ready
        }

        if (PageHost.NeedsNewPage)
        {
            LoadTemplatePage();
        }
        else
        {
            PageHost.OnSessionChanged();
        }
    }

    // --- IFlowEditorHost ------------------------------------------------------
    // ModalDialogDepth tells the TopBar/overlay activation hook that a
    // modal dialog is open (see NativeMethods.DeliverActivatingClick).

    public Task<string?> PromptTextAsync(string? title = null, string? label = null, string? initialValue = null)
    {
        var nameWindow = new BranchNameWindow(title, label, initialValue) { Owner = this };
        NativeMethods.ModalDialogDepth++;
        try
        {
            return Task.FromResult(nameWindow.ShowDialog() == true ? nameWindow.BranchName : null);
        }
        finally
        {
            NativeMethods.ModalDialogDepth--;
        }
    }

    public void PostToWeb(string json)
    {
        if (_webViewReady)
        {
            _webView.CoreWebView2.PostWebMessageAsJson(json);
        }
    }
}
