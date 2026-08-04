using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DocuClick.Services;

namespace DocuClick;

public partial class SettingsWindow : Window
{
    private enum HotkeyTarget { None, StartStop, BranchMark, BranchJump }

    private readonly AppConfig _config;

    private string _selectedHighlightColorHex = "#E63946";

    private string _startStopModifiers = "";
    private string _startStopKey = "R";
    private string _branchMarkModifiers = "";
    private string _branchMarkKey = "F9";
    private string _branchJumpModifiers = "";
    private string _branchJumpKey = "F10";

    private HotkeyTarget _capturingTarget = HotkeyTarget.None;

    public event Action? SettingsSaved;

    public SettingsWindow(AppConfig config)
    {
        InitializeComponent();
        _config = config;
        LoadIntoForm();
    }

    private void LoadIntoForm()
    {
        VaultPathBox.Text = _config.VaultPath;
        AttachmentsFolderBox.Text = _config.AttachmentsFolder;
        UseUiAutomationBox.IsChecked = _config.UseUiAutomation;
        EnableClickSoundBox.IsChecked = _config.EnableClickSound;
        CaptureOnEnterBox.IsChecked = _config.CaptureOnEnter;
        SelectSkipModifier(_config.SkipRecordingModifier);
        NewNotePerSessionBox.IsChecked = _config.NewNotePerSession;
        FixedNoteNameBox.Text = _config.FixedNoteName;
        FixedNoteNameBox.IsEnabled = !_config.NewNotePerSession;

        UseCanvasBox.IsChecked = _config.UseCanvas;

        _startStopModifiers = _config.StartStopModifiers;
        _startStopKey = _config.StartStopKey;
        _branchMarkModifiers = _config.BranchMarkModifiers;
        _branchMarkKey = _config.BranchMarkKey;
        _branchJumpModifiers = _config.BranchJumpModifiers;
        _branchJumpKey = _config.BranchJumpKey;
        RefreshHotkeyDisplays();

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

    private void OnNewNotePerSessionChanged(object sender, RoutedEventArgs e)
    {
        FixedNoteNameBox.IsEnabled = NewNotePerSessionBox.IsChecked != true;
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

    private void OnRecordBranchJumpClicked(object sender, RoutedEventArgs e) => BeginCapture(HotkeyTarget.BranchJump, BranchJumpDisplayBox);

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
            case HotkeyTarget.BranchJump:
                _branchJumpModifiers = modifiersText;
                _branchJumpKey = keyText;
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
        BranchJumpDisplayBox.Text = FormatHotkey(_branchJumpModifiers, _branchJumpKey);
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

    // --- Vault / save / cancel ------------------------------------------

    private void OnBrowseVaultClicked(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            SelectedPath = string.IsNullOrWhiteSpace(VaultPathBox.Text) ? string.Empty : VaultPathBox.Text
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            VaultPathBox.Text = dialog.SelectedPath;
        }
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        _config.VaultPath = VaultPathBox.Text.Trim();
        _config.AttachmentsFolder = string.IsNullOrWhiteSpace(AttachmentsFolderBox.Text)
            ? "Attachments"
            : AttachmentsFolderBox.Text.Trim();
        _config.UseUiAutomation = UseUiAutomationBox.IsChecked == true;
        _config.EnableClickSound = EnableClickSoundBox.IsChecked == true;
        _config.CaptureOnEnter = CaptureOnEnterBox.IsChecked == true;

        _config.SkipRecordingModifier = SkipModifierBox.SelectedItem is ComboBoxItem selected
            ? (string)selected.Tag
            : "None";
        _config.NewNotePerSession = NewNotePerSessionBox.IsChecked == true;
        _config.FixedNoteName = FixedNoteNameBox.Text.Trim();

        _config.UseCanvas = UseCanvasBox.IsChecked == true;

        _config.StartStopModifiers = _startStopModifiers;
        _config.StartStopKey = _startStopKey;
        _config.BranchMarkModifiers = _branchMarkModifiers;
        _config.BranchMarkKey = _branchMarkKey;
        _config.BranchJumpModifiers = _branchJumpModifiers;
        _config.BranchJumpKey = _branchJumpKey;

        _config.HighlightColorHex = _selectedHighlightColorHex;
        _config.HighlightRadius = int.TryParse(HighlightRadiusBox.Text, out var radius) ? radius : _config.HighlightRadius;
        _config.HighlightThickness = int.TryParse(HighlightThicknessBox.Text, out var thickness) ? thickness : _config.HighlightThickness;

        ConfigService.Save(_config);
        SettingsSaved?.Invoke();
        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e) => Close();
}
