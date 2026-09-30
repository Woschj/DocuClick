using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using DocuClick.Mac.Platform;
using DocuClick.Services;

namespace DocuClick.Mac.UI;

/// <summary>Settings — the same sections as on Windows (v1.13), plus the macOS capture options and permissions.</summary>
internal sealed class SettingsWindow : DialogWindow<bool>
{
    private enum HotkeyTarget { None, StartStop, BranchMark, ZoomToCursor }

    private static readonly (string Tag, string Text)[] SkipModifiers =
    {
        ("None", "Keine (immer aufzeichnen)"), ("Alt", "⌥ Option"), ("Shift", "⇧ Umschalt"), ("Command", "⌘ Befehl"), ("Control", "⌃ Control")
    };

    private static readonly string[] Swatches = { "#E63946", "#F77F00", "#FFD60A", "#22C55E", "#1D4ED8", "#7C3AED" };

    private readonly AppConfig _config;
    private readonly CheckBox _useAccessibility = new() { Content = "Elementnamen (Bedienungshilfen) für die Beschreibungstexte verwenden" };
    private readonly CheckBox _clickSound = new() { Content = "Signalton bei jedem aufgezeichneten Klick" };
    private readonly CheckBox _captureEnter = new() { Content = "Auch bei Enter-Taste aufzeichnen (aktives Fenster + fokussiertes Element)" };
    private readonly CheckBox _captureRightClick = new() { Content = "Auch bei Rechtsklick aufzeichnen" };
    private readonly ComboBox _skipModifier = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _beforeClick = new() { Content = "Bild von direkt VOR dem Klick verwenden (zeigt noch nicht die Reaktion der App, z. B. aufgeklappte Menüs)" };
    private readonly CheckBox _downscale = new() { Content = "Retina-Screenshots auf normale Größe verkleinern (deutlich kleinere Abläufe)" };
    private static readonly (string Tag, string Text)[] ScreenshotFormats =
    {
        ("WebP", "WebP – klein, gute Qualität (empfohlen)"),
        ("Jpeg", "JPEG – klein, auch für ältere Programme"),
        ("Png", "PNG – verlustfrei, sehr groß"),
    };
    private readonly ComboBox _screenshotFormat = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _autoInstallPlugin = new() { Content = "Obsidian-Plugin beim Aufnehmen in einen Vault automatisch installieren und aktualisieren" };
    private readonly CheckBox _templateOverview = new() { Content = "Neue Ablauf-Übersicht testen (gleicher Editor wie Browser und Obsidian, mit Schwärzen und Drucken; wirkt beim nächsten Start)" };
    private readonly Dictionary<HotkeyTarget, TextBox> _hotkeyBoxes = new();
    private readonly Dictionary<HotkeyTarget, (string Modifiers, string Key)> _hotkeys = new();
    private readonly WrapPanel _swatchPanel = new();
    private readonly NumericUpDown _highlightRadius = new() { Minimum = 1, Maximum = 500, FormatString = "0" };
    private readonly NumericUpDown _highlightThickness = new() { Minimum = 1, Maximum = 50, FormatString = "0" };
    private readonly Ellipse _preview = new() { Width = 80, Height = 80 };
    private string _selectedColor;
    private HotkeyTarget _capturing = HotkeyTarget.None;

    public event Action? PermissionsRequested;

    public SettingsWindow(AppConfig config)
    {
        _config = config;
        _selectedColor = config.HighlightColorHex;
        Title = "DocuClick – Einstellungen";
        Width = 620;
        Height = 760;

        _useAccessibility.IsChecked = config.UseUiAutomation;
        _clickSound.IsChecked = config.EnableClickSound;
        _captureEnter.IsChecked = config.CaptureOnEnter;
        _captureRightClick.IsChecked = config.CaptureOnRightClick;
        _skipModifier.ItemsSource = SkipModifiers.Select(m => m.Text).ToList();
        _skipModifier.SelectedIndex = Math.Max(0, Array.FindIndex(SkipModifiers, m => m.Tag == config.SkipRecordingModifier));
        _beforeClick.IsChecked = config.CaptureTiming == "BeforeClick";
        _downscale.IsChecked = config.DownscaleHiDpiScreenshots;
        _screenshotFormat.ItemsSource = ScreenshotFormats.Select(f => f.Text).ToList();
        _screenshotFormat.SelectedIndex = Math.Max(0, Array.FindIndex(ScreenshotFormats, f => string.Equals(f.Tag, config.ScreenshotFormat, StringComparison.OrdinalIgnoreCase)));
        _autoInstallPlugin.IsChecked = config.AutoInstallObsidianPlugin;
        _templateOverview.IsChecked = config.UseTemplateOverview;

        _hotkeys[HotkeyTarget.StartStop] = (config.StartStopModifiers, config.StartStopKey);
        _hotkeys[HotkeyTarget.BranchMark] = (config.BranchMarkModifiers, config.BranchMarkKey);
        _hotkeys[HotkeyTarget.ZoomToCursor] = (config.ZoomToCursorModifiers, config.ZoomToCursorKey);

        foreach (var hex in Swatches)
        {
            var swatch = new Button
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Margin = new Thickness(0, 0, 8, 0), Tag = hex,
                Background = new SolidColorBrush(Color.Parse(hex))
            };
            swatch.Click += (_, _) =>
            {
                _selectedColor = hex;
                RefreshHighlight();
            };
            _swatchPanel.Children.Add(swatch);
        }

        _highlightRadius.Value = config.HighlightRadius;
        _highlightThickness.Value = config.HighlightThickness;
        _highlightRadius.ValueChanged += (_, _) => RefreshHighlight();
        _highlightThickness.ValueChanged += (_, _) => RefreshHighlight();

        var permissions = Ui.Button("Berechtigungen prüfen …");
        permissions.Click += (_, _) => PermissionsRequested?.Invoke();

        var previewBox = new Border
        {
            Width = 110, Height = 110, CornerRadius = new CornerRadius(8), ClipToBounds = true,
            Background = new SolidColorBrush(Color.Parse("#F3F3F6")), BorderBrush = Ui.CardBorder, BorderThickness = new Thickness(1),
            Child = _preview
        };
        _preview.HorizontalAlignment = HorizontalAlignment.Center;
        _preview.VerticalAlignment = VerticalAlignment.Center;

        var content = new StackPanel
        {
            Children =
            {
                Ui.Card(Ui.Section("Aufnahme-Verhalten"), _useAccessibility, _clickSound, _captureEnter, _captureRightClick,
                    Ui.Label("Klicks überspringen bei gedrückter Taste"), _skipModifier,
                    _beforeClick, _downscale,
                    Ui.Label("Screenshot-Format"), _screenshotFormat, _autoInstallPlugin, _templateOverview,
                    Ui.Hint("Die Zieldatei wird bei jedem Start einer neuen Session abgefragt. Passwortfelder werden nie aufgezeichnet.")),
                Ui.Card(Ui.Section("Tastenkürzel"),
                    HotkeyRow(HotkeyTarget.StartStop, "Aufnahme starten/stoppen"),
                    HotkeyRow(HotkeyTarget.BranchMark, "Abzweigung setzen"),
                    Ui.Hint("Markiert den aktuellen Knoten als Abzweigungspunkt und fragt direkt nach dem Namen des ersten Pfads. Weitere Pfade: in der Ablauf-Übersicht auf den Abzweigungspunkt klicken."),
                    HotkeyRow(HotkeyTarget.ZoomToCursor, "Zoom-auf-Cursor umschalten"),
                    Ui.Hint("Auf „Ändern“ klicken und die Tastenkombination drücken (Esc bricht ab). Mindestens eine der Tasten ⌃ ⌥ ⌘ verwenden.")),
                Ui.Card(Ui.Section("Highlighter"), Ui.Label("Farbe"), _swatchPanel,
                    new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("*,12,*,16,Auto"),
                        Children =
                        {
                            new StackPanel { Spacing = 4, Children = { Ui.Label("Radius (Punkte)"), _highlightRadius } },
                            At(new StackPanel { Spacing = 4, Children = { Ui.Label("Strichstärke (Punkte)"), _highlightThickness } }, 2),
                            At(previewBox, 4)
                        }
                    }),
                Ui.Card(Ui.Section("Berechtigungen"),
                    Ui.Hint("DocuClick braucht Bedienungshilfen, Eingabeüberwachung und Bildschirmaufnahme."), permissions)
            }
        };

        var cancel = Ui.Button("Abbrechen");
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Close();
        var save = Ui.Button("Speichern", primary: true);
        save.Click += (_, _) => Save();

        var root = new DockPanel { Margin = new Thickness(20) };
        var buttons = Ui.ButtonRow(cancel, save);
        buttons.Margin = new Thickness(0, 12, 0, 0);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer { Content = content });
        Content = root;

        KeyDown += OnKeyDownWhileCapturing;
        RefreshHighlight();
        RefreshHotkeys();
    }

    private static Control At(Control control, int column)
    {
        Grid.SetColumn(control, column);
        return control;
    }

    private Control HotkeyRow(HotkeyTarget target, string label)
    {
        var box = new TextBox { IsReadOnly = true };
        _hotkeyBoxes[target] = box;
        var change = Ui.Button("Ändern");
        change.Click += (_, _) =>
        {
            _capturing = target;
            box.Text = "Tasten drücken … (Esc = Abbrechen)";
            Focus();
        };
        return new StackPanel { Spacing = 4, Children = { Ui.Label(label), Ui.PathRow(box, change) } };
    }

    private void OnKeyDownWhileCapturing(object? sender, KeyEventArgs e)
    {
        if (_capturing == HotkeyTarget.None)
        {
            return;
        }

        e.Handled = true;
        if (e.Key == Key.Escape)
        {
            _capturing = HotkeyTarget.None;
            RefreshHotkeys();
            return;
        }

        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return; // wait for the key that completes the combination
        }

        var parts = new List<string>();
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) parts.Add("Control");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Command");

        _hotkeys[_capturing] = (string.Join("+", parts), e.Key.ToString());
        _capturing = HotkeyTarget.None;
        RefreshHotkeys();
    }

    private void RefreshHotkeys()
    {
        foreach (var (target, box) in _hotkeyBoxes)
        {
            var (modifiers, key) = _hotkeys[target];
            box.Text = MacHotkeys.Format(modifiers, key);
        }
    }

    /// <summary>Swatch selection plus a true-to-look preview of the click circle (same alpha as HighlightRenderer), scaled to fit.</summary>
    private void RefreshHighlight()
    {
        foreach (var swatch in _swatchPanel.Children.OfType<Button>())
        {
            var selected = string.Equals((string?)swatch.Tag, _selectedColor, StringComparison.OrdinalIgnoreCase);
            swatch.BorderBrush = selected ? Brushes.Black : Brushes.Transparent;
            swatch.BorderThickness = new Thickness(3);
        }

        var radius = (double)(_highlightRadius.Value ?? 24);
        var thickness = (double)(_highlightThickness.Value ?? 4);
        var color = Color.Parse(_selectedColor);
        _preview.Fill = new SolidColorBrush(Color.FromArgb(60, color.R, color.G, color.B));
        _preview.Stroke = new SolidColorBrush(color);
        _preview.StrokeThickness = Math.Clamp(thickness * 80 / (2 * Math.Max(1, radius)), 1.5, 22);
    }

    private void Save()
    {
        _config.UseUiAutomation = _useAccessibility.IsChecked == true;
        _config.EnableClickSound = _clickSound.IsChecked == true;
        _config.CaptureOnEnter = _captureEnter.IsChecked == true;
        _config.CaptureOnRightClick = _captureRightClick.IsChecked == true;
        _config.SkipRecordingModifier = SkipModifiers[Math.Max(0, _skipModifier.SelectedIndex)].Tag;
        _config.CaptureTiming = _beforeClick.IsChecked == true ? "BeforeClick" : "AfterClick";
        _config.DownscaleHiDpiScreenshots = _downscale.IsChecked == true;
        _config.ScreenshotFormat = ScreenshotFormats[Math.Max(0, _screenshotFormat.SelectedIndex)].Tag;
        _config.AutoInstallObsidianPlugin = _autoInstallPlugin.IsChecked == true;
        _config.UseTemplateOverview = _templateOverview.IsChecked == true;
        (_config.StartStopModifiers, _config.StartStopKey) = _hotkeys[HotkeyTarget.StartStop];
        (_config.BranchMarkModifiers, _config.BranchMarkKey) = _hotkeys[HotkeyTarget.BranchMark];
        (_config.ZoomToCursorModifiers, _config.ZoomToCursorKey) = _hotkeys[HotkeyTarget.ZoomToCursor];
        _config.HighlightColorHex = _selectedColor;
        _config.HighlightRadius = (int)(_highlightRadius.Value ?? _config.HighlightRadius);
        _config.HighlightThickness = (int)(_highlightThickness.Value ?? _config.HighlightThickness);

        ConfigService.Save(_config);
        Complete(true);
    }
}
