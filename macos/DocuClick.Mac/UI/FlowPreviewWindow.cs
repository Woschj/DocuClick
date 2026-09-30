using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DocuClick.Mac.Platform;
using DocuClick.Services;

namespace DocuClick.Mac.UI;

/// <summary>
/// The Ablauf-Übersicht on macOS: the shared editor template (the same editor
/// as the .html file in a browser, the Obsidian plugin and the Windows app),
/// hosted in Avalonia's NativeWebView (WebKit), with <see cref="EditorPageHost"/>
/// handling its messages. The page and its screenshots come from the app's
/// local service (<see cref="PageUrl"/>), since WebKit cannot read the
/// session folder from a page loaded as text. Looks like the Windows
/// panel too: borderless and translucent (dark glass), with its own header
/// (einpassen, einklappen, schließen), draggable by the header and resizable
/// from the bottom-right grip. Focusable (unlike the top bar): it's a full
/// editor with inline text editing.
/// </summary>
internal sealed class FlowPreviewWindow : Window, IFlowEditorHost
{
    private const double HeaderHeight = 32;
    private const double GripSize = 20;

    private readonly NativeWebView _webView;
    private readonly Queue<string> _pending = new();
    private readonly Border _body;
    private readonly TextBlock _collapseGlyph = new() { Text = "–" };
    private bool _pageReady;
    private bool _collapsed;
    private double _expandedHeight;

    /// <summary>The page's counterpart in the app; set before the window opens.</summary>
    public EditorPageHost? PageHost { get; set; }

    /// <summary>URL of the editor page for a given version (a new version forces a reload); null if the local service isn't running.</summary>
    public Func<int, string>? PageUrl { get; set; }

    private int _pageVersion;

    /// <summary>The header's "Schließen": the app hides the window instead of destroying it.</summary>
    public event Action? CloseRequested;

    public FlowPreviewWindow()
    {
        Title = "DocuClick · Ablauf-Übersicht";
        Width = 1000;
        Height = 720;
        MinWidth = 420;
        MinHeight = HeaderHeight + 2;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = Brushes.Transparent;
        CanResize = true;

        _webView = new NativeWebView { Background = Brushes.Transparent, Margin = new Thickness(0, 0, GripSize, GripSize) };
        _webView.WebMessageReceived += (_, e) =>
        {
            if (e.Body is { Length: > 0 } json)
            {
                Dispatcher.UIThread.Post(() => _ = HandleAsync(json));
            }
        };
        _webView.NavigationCompleted += (_, _) =>
        {
            MakeWebViewTransparent();
            _pageReady = true;
            while (_pending.Count > 0)
            {
                Deliver(_pending.Dequeue());
            }
        };

        var fit = HeaderIcon("⛶", "Ansicht zentrieren / einpassen (auch per Doppelklick auf die leere Fläche)");
        fit.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            _ = _webView.InvokeScript("typeof ensureViewFit === 'function' && ensureViewFit()");
        };
        var collapse = HeaderIcon(_collapseGlyph, "Ein-/Ausklappen");
        collapse.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            ToggleCollapsed();
        };
        var close = HeaderIcon("✕", "Schließen (über „Ablauf“ in der Leiste oben wieder öffnen)");
        close.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            CloseRequested?.Invoke();
        };

        var header = new DockPanel { Height = HeaderHeight, Margin = new Thickness(12, 0, 8, 0), Background = Brushes.Transparent };
        var icons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Children = { fit, collapse, close } };
        DockPanel.SetDock(icons, Dock.Right);
        header.Children.Add(icons);
        header.Children.Add(new TextBlock
        {
            Text = "DocuClick · Ablauf-Übersicht", Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeight.SemiBold,
            Opacity = 0.95, VerticalAlignment = VerticalAlignment.Center
        });
        header.PointerPressed += (_, e) =>
        {
            if (!e.Handled && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginMoveDrag(e);
            }
        };

        var grip = ResizeGrip();
        var area = new Grid { Children = { _webView, grip } };
        _body = new Border { Margin = new Thickness(10, 0, 10, 10), Child = area };

        var layout = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);
        layout.Children.Add(_body);

        // Same dark glass as the Windows panel (ARGB 220, 11, 15, 25).
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(220, 11, 15, 25)),
            CornerRadius = new CornerRadius(10),
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Child = layout
        };

        // Hide instead of destroying: the web view and its state stay ready
        // for the top bar's "Ablauf" button.
        Closing += (_, e) =>
        {
            e.Cancel = true;
            CloseRequested?.Invoke();
        };

        // Load the page once; later Show() calls just reveal it again.
        var navigated = false;
        Opened += (_, _) =>
        {
            // Resizable from every edge and corner like any Mac window,
            // despite having no title bar.
            if (NativeWindow.Handle(this) is var window && window != 0)
            {
                Native.dc_window_make_resizable(window);
            }

            if (!navigated)
            {
                navigated = true;
                if (PageHost is not null && PageUrl is not null)
                {
                    LoadTemplatePage();
                }
                else
                {
                    _webView.NavigateToString(UnavailablePage);
                }
            }
        };
    }

    /// <summary>(Re)loads the editor page — at start and when another file is loaded.</summary>
    private void LoadTemplatePage()
    {
        _pageReady = false;
        _pending.Clear();
        _webView.Navigate(new Uri(PageUrl!(++_pageVersion)));
    }

    // Without the local service (port taken by another program) there is no page to show.
    private const string UnavailablePage = "<!doctype html><meta charset=\"utf-8\"><body style=\"background:transparent;color:#cbd5e1;font:13px -apple-system,system-ui;padding:24px\">"
        + "Die Ablauf-Übersicht ist gerade nicht verfügbar: DocuClicks lokaler Dienst (Port 47811) konnte nicht starten – vermutlich belegt ihn ein anderes Programm. "
        + "Die Aufnahme läuft trotzdem; den Ablauf im Browser oder in Obsidian öffnen, oder DocuClick neu starten.</body>";

    private void MakeWebViewTransparent()
    {
        if (_webView.TryGetPlatformHandle()?.Handle is { } handle && handle != 0)
        {
            Native.dc_webview_make_transparent(handle);
        }
        else if (NativeWindow.Handle(this) is var window && window != 0)
        {
            Native.dc_webview_make_transparent(window);
        }
    }

    private void ToggleCollapsed()
    {
        _collapsed = !_collapsed;
        _body.IsVisible = !_collapsed;
        _collapseGlyph.Text = _collapsed ? "+" : "–";
        if (_collapsed)
        {
            _expandedHeight = Height;
            Height = HeaderHeight + 2;
        }
        else
        {
            Height = _expandedHeight;
        }
    }

    private static Border HeaderIcon(string symbol, string tooltip) => HeaderIcon(new TextBlock { Text = symbol }, tooltip);

    private static Border HeaderIcon(TextBlock glyph, string tooltip)
    {
        glyph.Foreground = Brushes.White;
        glyph.FontSize = 11;
        glyph.FontWeight = FontWeight.Bold;
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.VerticalAlignment = VerticalAlignment.Center;
        var icon = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11),
            Background = new SolidColorBrush(Color.FromArgb(35, 255, 255, 255)),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = glyph
        };
        ToolTip.SetTip(icon, tooltip);
        icon.PointerEntered += (_, _) => icon.Background = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255));
        icon.PointerExited += (_, _) => icon.Background = new SolidColorBrush(Color.FromArgb(35, 255, 255, 255));
        return icon;
    }

    /// <summary>
    /// Visible bottom-right grip (three diagonal stripes, like the Windows
    /// panel) in addition to the native edge/corner resizing. Kept outside
    /// the web view's area — the native web view would swallow its clicks.
    /// </summary>
    private Control ResizeGrip()
    {
        var stripes = new Canvas { Width = GripSize, Height = GripSize };
        foreach (var offset in new[] { 4.0, 9.0, 14.0 })
        {
            stripes.Children.Add(new Avalonia.Controls.Shapes.Line
            {
                StartPoint = new Point(offset, GripSize - 2),
                EndPoint = new Point(GripSize - 2, offset),
                Stroke = new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)),
                StrokeThickness = 1.6,
                StrokeLineCap = PenLineCap.Round
            });
        }

        var grip = new Border
        {
            Width = GripSize, Height = GripSize, Background = Brushes.Transparent, Child = stripes,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Cursor = new Cursor(StandardCursorType.BottomRightCorner)
        };
        ToolTip.SetTip(grip, "Ziehen zum Verändern der Größe");
        var resizing = false;
        var start = default(Point);
        var startSize = default(Size);
        grip.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            resizing = true;
            start = e.GetPosition(this);
            startSize = new Size(Width, Height);
            e.Pointer.Capture(grip);
        };
        grip.PointerMoved += (_, e) =>
        {
            if (resizing)
            {
                var current = e.GetPosition(this);
                Width = Math.Max(MinWidth, startSize.Width + current.X - start.X);
                Height = Math.Max(200, startSize.Height + current.Y - start.Y);
            }
        };
        grip.PointerReleased += (_, e) =>
        {
            resizing = false;
            e.Pointer.Capture(null);
        };
        return grip;
    }

    private async Task HandleAsync(string json)
    {
        try
        {
            if (PageHost is not null)
            {
                await PageHost.HandleMessageAsync(json);
            }
        }
        catch (Exception ex)
        {
            LogService.Log($"Ablauf-Übersicht: Nachricht konnte nicht verarbeitet werden: {ex}");
        }
    }

    /// <summary>The session changed (click recorded, jump, new path, file switch): bring the page up to date.</summary>
    public void UpdatePreview()
    {
        if (PageHost is null || PageUrl is null)
        {
            return;
        }

        if (PageHost.NeedsNewPage)
        {
            if (IsVisible || _pageVersion > 0)
            {
                LoadTemplatePage();
            }
        }
        else
        {
            PageHost.OnSessionChanged();
        }
    }

    // --- IFlowEditorHost --------------------------------------------------------

    public void PostToWeb(string json)
    {
        if (!_pageReady)
        {
            _pending.Enqueue(json);
            return;
        }

        Deliver(json);
    }

    private void Deliver(string json) => _ = _webView.InvokeScript($"window.docuclickHostMessage && window.docuclickHostMessage({json});");

    public async Task<string?> PromptTextAsync(string? title = null, string? label = null, string? initialValue = null) =>
        await new BranchNameWindow(title, label, initialValue).ShowAndWaitAsync();
}
