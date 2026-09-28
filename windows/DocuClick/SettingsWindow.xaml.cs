using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DocuClick.Services;

// UseWPF + UseWindowsForms together implicitly bring System.Windows.Forms
// into every file; combined with the WPF namespaces above, Button/TextBox/
// KeyEventArgs exist in both and become ambiguous. Alias them to the WPF
// versions here rather than qualifying every call site.
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;

namespace DocuClick;

public partial class SettingsWindow : Window
{
    private enum HotkeyTarget { None, StartStop, BranchMark, ZoomToCursor }

    private readonly AppConfig _config;

    private string _selectedHighlightColorHex = "#E63946";

    private string _startStopModifiers = "";
    private string _startStopKey = "R";
    private string _branchMarkModifiers = "";
    private string _branchMarkKey = "F9";
    private string _zoomToCursorModifiers = "";
    private string _zoomToCursorKey = "F11";

    private HotkeyTarget _capturingTarget = HotkeyTarget.None;

    public event Action? SettingsSaved;

    public SettingsWindow(AppConfig config)
    {
        InitializeComponent();
        MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 40);
        Height = Math.Min(820, MaxHeight);
        _config = config;
        LoadIntoForm();
    }

    private void LoadIntoForm()
    {
        UseUiAutomationBox.IsChecked = _config.UseUiAutomation;
        EnableClickSoundBox.IsChecked = _config.EnableClickSound;
        CaptureOnEnterBox.IsChecked = _config.CaptureOnEnter;
        CaptureOnRightClickBox.IsChecked = _config.CaptureOnRightClick;
        SelectSkipModifier(_config.SkipRecordingModifier);

        _startStopModifiers = _config.StartStopModifiers;
        _startStopKey = _config.StartStopKey;
        _branchMarkModifiers = _config.BranchMarkModifiers;
        _branchMarkKey = _config.BranchMarkKey;
        _zoomToCursorModifiers = _config.ZoomToCursorModifiers;
        _zoomToCursorKey = _config.ZoomToCursorKey;
        RefreshHotkeyDisplays();

        _selectedHighlightColorHex = _config.HighlightColorHex;
        RefreshSwatchSelection();
        HighlightRadiusBox.Text = _config.HighlightRadius.ToString();
        HighlightThicknessBox.Text = _config.HighlightThickness.ToString();
        RefreshHighlightPreview();
    }

    private void SelectSkipModifier(string modifier)
    {
        foreach (ComboBoxItem item in SkipModifierBox.Items)
        {
            if ((string)item.Tag == modifier)
            {
                SkipModifierBox.SelectedItem = item;
                return;
            }
        }

        SkipModifierBox.SelectedIndex = 0;
    }

    // --- Color swatches -----------------------------------------------

    private void OnHighlightColorSwatchClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button clicked || clicked.Tag is not string hex)
        {
            return;
        }

        _selectedHighlightColorHex = hex;
        RefreshSwatchSelection();
        RefreshHighlightPreview();
    }

    private void RefreshSwatchSelection()
    {
        // Each swatch is a Button wrapped in its own always-same-size
        // "ColorSwatchFrame" Border (see Theme.xaml's own comment on why) —
        // selection toggles only that outer frame's BorderBrush, never the
        // swatch button's own border, so there's no dynamic thickness for
        // WPF's rounded-corner rendering to get wrong.
        foreach (var child in HighlightColorSwatchPanel.Children)
        {
            if (child is not Border frame || frame.Child is not Button button || button.Tag is not string hex)
            {
                continue;
            }

            var isSelected = string.Equals(hex, _selectedHighlightColorHex, StringComparison.OrdinalIgnoreCase);
            frame.BorderBrush = isSelected ? Brushes.Black : Brushes.Transparent;
        }
    }

    private void OnHighlightPreviewInputChanged(object sender, TextChangedEventArgs e) => RefreshHighlightPreview();

    /// <summary>
    /// Mirrors HighlightRenderer.DrawClickCircle's exact look (semi-transparent
    /// fill at alpha 60/255, solid stroke) so this is a true preview, not just
    /// an approximation — scaled to always fill the preview box regardless of
    /// the actual radius, since only the *relative* thickness-to-radius look
    /// matters for "what would this look like", not the literal pixel size.
    /// </summary>
    private void RefreshHighlightPreview()
    {
        const double displayDiameter = 80; // fits inside the 110x110 box with margin regardless of stroke width
        const double minDisplayThickness = 1.5;
        const double maxDisplayThickness = 22; // keeps the rendered stroke (half of which draws outward) from overflowing the ClipToBounds box

        var radius = ParseHighlightValue(HighlightRadiusBox.Text, _config.HighlightRadius, 1, 500);
        var thickness = ParseHighlightValue(HighlightThicknessBox.Text, _config.HighlightThickness, 1, 50);

        var scale = displayDiameter / (2.0 * radius);
        var displayThickness = Math.Clamp(thickness * scale, minDisplayThickness, maxDisplayThickness);

        var color = (Color)System.Windows.Media.ColorConverter.ConvertFromString(_selectedHighlightColorHex);
        HighlightPreviewEllipse.Width = displayDiameter;
        HighlightPreviewEllipse.Height = displayDiameter;
        HighlightPreviewEllipse.Fill = new SolidColorBrush(Color.FromArgb(60, color.R, color.G, color.B));
        HighlightPreviewEllipse.Stroke = new SolidColorBrush(color);
        HighlightPreviewEllipse.StrokeThickness = displayThickness;
    }

    private static int ParseHighlightValue(string text, int fallback, int min, int max) =>
        int.TryParse(text, out var value) ? Math.Clamp(value, min, max) : fallback;

    // --- Hotkey capture -------------------------------------------------

    private void OnRecordStartStopClicked(object sender, RoutedEventArgs e) => BeginCapture(HotkeyTarget.StartStop, StartStopDisplayBox);

    private void OnRecordBranchMarkClicked(object sender, RoutedEventArgs e) => BeginCapture(HotkeyTarget.BranchMark, BranchMarkDisplayBox);

    private void OnRecordZoomToCursorClicked(object sender, RoutedEventArgs e) => BeginCapture(HotkeyTarget.ZoomToCursor, ZoomToCursorDisplayBox);

    private void BeginCapture(HotkeyTarget target, TextBox displayBox)
    {
        if (_capturingTarget != HotkeyTarget.None)
        {
            return;
        }

        _capturingTarget = target;
        displayBox.Text = "Taste(n) drücken... (Esc = Abbrechen)";
        PreviewKeyDown += OnCapturingPreviewKeyDown;
    }

    private void OnCapturingPreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            EndCapture();
            return;
        }

        if (IsPureModifierKey(key))
        {
            return; // wait for the actual key that completes the combo
        }

        var modifiersText = FormatModifiers(Keyboard.Modifiers);
        var keyText = key.ToString();

        switch (_capturingTarget)
        {
            case HotkeyTarget.StartStop:
                _startStopModifiers = modifiersText;
                _startStopKey = keyText;
                break;
            case HotkeyTarget.BranchMark:
                _branchMarkModifiers = modifiersText;
                _branchMarkKey = keyText;
                break;
            case HotkeyTarget.ZoomToCursor:
                _zoomToCursorModifiers = modifiersText;
                _zoomToCursorKey = keyText;
                break;
        }

        EndCapture();
    }

    private void EndCapture()
    {
        PreviewKeyDown -= OnCapturingPreviewKeyDown;
        _capturingTarget = HotkeyTarget.None;
        RefreshHotkeyDisplays();
    }

    private void RefreshHotkeyDisplays()
    {
        StartStopDisplayBox.Text = FormatHotkey(_startStopModifiers, _startStopKey);
        BranchMarkDisplayBox.Text = FormatHotkey(_branchMarkModifiers, _branchMarkKey);
        ZoomToCursorDisplayBox.Text = FormatHotkey(_zoomToCursorModifiers, _zoomToCursorKey);
    }

    private static string FormatHotkey(string modifiers, string key) =>
        string.IsNullOrEmpty(modifiers) ? key : $"{modifiers.Replace("+", " + ")} + {key}";

    private static bool IsPureModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or
        Key.LWin or Key.RWin;

    private static string FormatModifiers(ModifierKeys modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Control");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Windows");
        return string.Join("+", parts);
    }

    // --- Save / cancel ---------------------------------------------------

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        _config.UseUiAutomation = UseUiAutomationBox.IsChecked == true;
        _config.EnableClickSound = EnableClickSoundBox.IsChecked == true;
        _config.CaptureOnEnter = CaptureOnEnterBox.IsChecked == true;
        _config.CaptureOnRightClick = CaptureOnRightClickBox.IsChecked == true;

        _config.SkipRecordingModifier = SkipModifierBox.SelectedItem is ComboBoxItem selected
            ? (string)selected.Tag
            : "None";

        _config.StartStopModifiers = _startStopModifiers;
        _config.StartStopKey = _startStopKey;
        _config.BranchMarkModifiers = _branchMarkModifiers;
        _config.BranchMarkKey = _branchMarkKey;
        _config.ZoomToCursorModifiers = _zoomToCursorModifiers;
        _config.ZoomToCursorKey = _zoomToCursorKey;
        // ZoomToCursorRadius itself is no longer editable here — the
        // TopBar's own zoom-radius slider already reads/clamps/persists it
        // directly (see App.xaml.cs's ZoomRadiusCommitted wiring), so a
        // second, duplicate control here could only ever go stale against it.

        _config.HighlightColorHex = _selectedHighlightColorHex;
        // A non-positive radius/thickness makes GDI+'s ellipse/pen drawing
        // throw, failing every subsequent capture the same way.
        _config.HighlightRadius = int.TryParse(HighlightRadiusBox.Text, out var radius)
            ? Math.Clamp(radius, 1, 500)
            : _config.HighlightRadius;
        _config.HighlightThickness = int.TryParse(HighlightThicknessBox.Text, out var thickness)
            ? Math.Clamp(thickness, 1, 50)
            : _config.HighlightThickness;

        ConfigService.Save(_config);
        SettingsSaved?.Invoke();
        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e) => Close();
}
