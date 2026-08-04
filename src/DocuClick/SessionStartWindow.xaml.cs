using System.IO;
using System.Windows;
using DocuClick.Services;

namespace DocuClick;

/// <summary>
/// Shown every time a recording session is about to start, so the target
/// file is always either an explicitly typed new name or a deliberately
/// chosen existing file — never an auto-generated "Screenshots yyyy-MM-dd"
/// default.
/// </summary>
public partial class SessionStartWindow : Window
{
    private readonly string _extension;

    public string? SelectedFileName { get; private set; }

    /// <param name="preselectExistingFile">
    /// If set and present among the existing files (e.g. a pending "Ablauf
    /// fortsetzen ab Punkt..." resume anchor lives in this file), the
    /// "bestehende Datei" option is preselected with it instead of
    /// defaulting to "neue Datei".
    /// </param>
    public SessionStartWindow(AppConfig config, string? preselectExistingFile = null)
    {
        InitializeComponent();

        _extension = SessionManager.ExtensionForOutputMode(config.OutputMode);

        var existingFiles = new List<string>();
        if (!string.IsNullOrWhiteSpace(config.VaultPath) && Directory.Exists(config.VaultPath))
        {
            existingFiles = Directory.GetFiles(config.VaultPath, "*" + _extension)
                .Select(f => Path.GetFileName(f) ?? f)
                .OrderByDescending(f => File.GetLastWriteTimeUtc(Path.Combine(config.VaultPath, f)))
                .ToList();
        }

        ExistingFilesListBox.ItemsSource = existingFiles;
        if (existingFiles.Count == 0)
        {
            ExistingFileRadio.IsEnabled = false;
        }

        if (preselectExistingFile is not null && existingFiles.Contains(preselectExistingFile))
        {
            ExistingFileRadio.IsChecked = true;
            ExistingFilesListBox.SelectedItem = preselectExistingFile;
        }
        else
        {
            NewFileNameBox.Text = $"Screenshots {DateTime.Now:yyyy-MM-dd}";
        }

        ApplyModeToControls();
        NewFileNameBox.Focus();
        NewFileNameBox.SelectAll();
    }

    private void OnModeChanged(object sender, RoutedEventArgs e) => ApplyModeToControls();

    private void ApplyModeToControls()
    {
        // Guard: RadioButton's Checked event (IsChecked="True" in XAML) can
        // fire while InitializeComponent is still wiring named fields.
        if (NewFileNameBox is null || ExistingFilesListBox is null)
        {
            return;
        }

        NewFileNameBox.IsEnabled = NewFileRadio.IsChecked == true;
        ExistingFilesListBox.IsEnabled = ExistingFileRadio.IsChecked == true;
    }

    private void OnStartClicked(object sender, RoutedEventArgs e)
    {
        if (NewFileRadio.IsChecked == true)
        {
            var name = NewFileNameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show("Bitte einen Dateinamen eingeben.", "DocuClick", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            foreach (var invalidChar in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(invalidChar, '_');
            }

            SelectedFileName = Path.GetFileNameWithoutExtension(name) + _extension;
        }
        else
        {
            if (ExistingFilesListBox.SelectedItem is not string selected)
            {
                MessageBox.Show("Bitte eine Datei auswählen.", "DocuClick", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SelectedFileName = selected;
        }

        DialogResult = true;
        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
