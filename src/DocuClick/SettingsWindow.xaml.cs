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
        OutputPathBox.ItemsSource = _config.RecentOutputPaths;
        OutputPathBox.Text = _config.OutputPath;
        AttachmentsFolderBox.Text = _config.AttachmentsFolder;
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

        ZoomToCursorRadiusBox.Text = _config.ZoomToCursorRadius.ToString();

        _selectedHighlightColorHex = _config.HighlightColorHex;
        RefreshSwatchSelection();
        HighlightRadiusBox.Text = _config.HighlightRadius.ToString();
        HighlightThicknessBox.Text = _config.HighlightThickness.ToString();
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
    }

    private void RefreshSwatchSelection()
    {
        foreach (var child in HighlightColorSwatchPanel.Children)
        {
            if (child is not Button button || button.Tag is not string hex)
            {
                continue;
            }

            var isSelected = string.Equals(hex, _selectedHighlightColorHex, StringComparison.OrdinalIgnoreCase);
            button.BorderBrush = isSelected ? Brushes.Black : new SolidColorBrush(Color.FromRgb(0xC9, 0xC9, 0xD1));
            button.BorderThickness = new Thickness(isSelected ? 3 : 1);
        }
    }

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

    // --- Speicherort / save / cancel ------------------------------------

    private void OnBrowseOutputPathClicked(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            SelectedPath = string.IsNullOrWhiteSpace(OutputPathBox.Text) ? string.Empty : OutputPathBox.Text
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            OutputPathBox.Text = dialog.SelectedPath;
            // Reflected immediately (not just after "Speichern") so the
            // very same folder just picked already shows up in the
            // dropdown if the user reopens it before saving.
            _config.RememberRecentOutputPath(dialog.SelectedPath);
            OutputPathBox.ItemsSource = null;
            OutputPathBox.ItemsSource = _config.RecentOutputPaths;
            OutputPathBox.Text = dialog.SelectedPath;
        }
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        _config.OutputPath = OutputPathBox.Text.Trim();
        _config.AttachmentsFolder = string.IsNullOrWhiteSpace(AttachmentsFolderBox.Text)
            ? "Attachments"
            : AttachmentsFolderBox.Text.Trim();
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
        // Clamped to the same range the TopBar's own zoom-radius slider
        // enforces (see TopBarWindow's ZoomRadiusMin/Max) — this plain text
        // box has no such built-in limit, and an out-of-range value (0,
        // negative, ...) previously made every future capture fail with a
        // repeating error until manually corrected here again.
        _config.ZoomToCursorRadius = int.TryParse(ZoomToCursorRadiusBox.Text, out var zoomRadius)
            ? Math.Clamp(zoomRadius, 50, 600)
            : _config.ZoomToCursorRadius;

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
