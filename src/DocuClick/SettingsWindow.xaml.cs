using System.Windows;
using DocuClick.Services;

namespace DocuClick;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;

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
        StartStopModifiersBox.Text = _config.StartStopModifiers;
        StartStopKeyBox.Text = _config.StartStopKey;
        SelectSkipModifier(_config.SkipRecordingModifier);
        NewNotePerSessionBox.IsChecked = _config.NewNotePerSession;
        FixedNoteNameBox.Text = _config.FixedNoteName;
        FixedNoteNameBox.IsEnabled = !_config.NewNotePerSession;

        UseCanvasBox.IsChecked = _config.UseCanvas;
        BranchMarkModifiersBox.Text = _config.BranchMarkModifiers;
        BranchMarkKeyBox.Text = _config.BranchMarkKey;
        BranchJumpModifiersBox.Text = _config.BranchJumpModifiers;
        BranchJumpKeyBox.Text = _config.BranchJumpKey;

        HighlightColorBox.Text = _config.HighlightColorHex;
        HighlightRadiusBox.Text = _config.HighlightRadius.ToString();
        HighlightThicknessBox.Text = _config.HighlightThickness.ToString();
    }

    private void SelectSkipModifier(string modifier)
    {
        foreach (System.Windows.Controls.ComboBoxItem item in SkipModifierBox.Items)
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

    private void OnUseCanvasChanged(object sender, RoutedEventArgs e)
    {
        // Hotkeys only make sense once there is something to branch, but
        // leaving the fields editable regardless keeps the form simple.
    }

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
        _config.StartStopModifiers = string.IsNullOrWhiteSpace(StartStopModifiersBox.Text) ? "Control+Alt" : StartStopModifiersBox.Text.Trim();
        _config.StartStopKey = string.IsNullOrWhiteSpace(StartStopKeyBox.Text) ? "R" : StartStopKeyBox.Text.Trim();
        _config.SkipRecordingModifier = SkipModifierBox.SelectedItem is System.Windows.Controls.ComboBoxItem selected
            ? (string)selected.Tag
            : "None";
        _config.NewNotePerSession = NewNotePerSessionBox.IsChecked == true;
        _config.FixedNoteName = FixedNoteNameBox.Text.Trim();

        _config.UseCanvas = UseCanvasBox.IsChecked == true;
        _config.BranchMarkModifiers = string.IsNullOrWhiteSpace(BranchMarkModifiersBox.Text) ? "Control+Alt" : BranchMarkModifiersBox.Text.Trim();
        _config.BranchMarkKey = string.IsNullOrWhiteSpace(BranchMarkKeyBox.Text) ? "B" : BranchMarkKeyBox.Text.Trim();
        _config.BranchJumpModifiers = string.IsNullOrWhiteSpace(BranchJumpModifiersBox.Text) ? "Control+Alt" : BranchJumpModifiersBox.Text.Trim();
        _config.BranchJumpKey = string.IsNullOrWhiteSpace(BranchJumpKeyBox.Text) ? "J" : BranchJumpKeyBox.Text.Trim();

        _config.HighlightColorHex = string.IsNullOrWhiteSpace(HighlightColorBox.Text)
            ? "#E63946"
            : HighlightColorBox.Text.Trim();
        _config.HighlightRadius = int.TryParse(HighlightRadiusBox.Text, out var radius) ? radius : _config.HighlightRadius;
        _config.HighlightThickness = int.TryParse(HighlightThicknessBox.Text, out var thickness) ? thickness : _config.HighlightThickness;

        ConfigService.Save(_config);
        SettingsSaved?.Invoke();
        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e) => Close();
}
