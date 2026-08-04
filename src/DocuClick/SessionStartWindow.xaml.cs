using System.IO;
using System.Windows;
using DocuClick.Services;

namespace DocuClick;

/// <summary>
/// Shown every time a recording session is about to start, so the target
/// file is always either an explicitly typed new name or a deliberately
/// chosen existing file — never an auto-generated "Screenshots yyyy-MM-dd"
/// default. Also asks which (sub)folder relative to the vault path the
/// file belongs in, so captures can be filed straight into a vault's
/// existing structure instead of always landing at its root.
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

        var vaultPath = config.VaultPath;
        _extension = SessionManager.ExtensionForOutputMode(config.OutputMode);

        var existingFiles = new List<string>();
        var existingFolders = new List<string>();
        if (!string.IsNullOrWhiteSpace(vaultPath) && Directory.Exists(vaultPath))
        {
            existingFiles = Directory.GetFiles(vaultPath, "*" + _extension, SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(vaultPath, f))
                .OrderByDescending(f => File.GetLastWriteTimeUtc(Path.Combine(vaultPath, f)))
                .ToList();

            existingFolders = Directory.GetDirectories(vaultPath, "*", SearchOption.AllDirectories)
                .Select(d => Path.GetRelativePath(vaultPath, d))
                // Hide Obsidian's own config folder and anything nested under it/other dot-folders.
                .Where(d => !d.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(segment => segment.StartsWith('.')))
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        ExistingFilesListBox.ItemsSource = existingFiles;
        if (existingFiles.Count == 0)
        {
            ExistingFileRadio.IsEnabled = false;
        }

        TargetFolderBox.ItemsSource = existingFolders;

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
        if (NewFileNameBox is null || ExistingFilesListBox is null || TargetFolderBox is null)
        {
            return;
        }

        var isNewFile = NewFileRadio.IsChecked == true;
        NewFileNameBox.IsEnabled = isNewFile;
        TargetFolderBox.IsEnabled = isNewFile;
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

            var fileName = SanitizeFileNameSegment(Path.GetFileNameWithoutExtension(name)) + _extension;
            var folder = SanitizeRelativeFolder(TargetFolderBox.Text ?? "");

            SelectedFileName = string.IsNullOrEmpty(folder) ? fileName : Path.Combine(folder, fileName);
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

    private static string SanitizeFileNameSegment(string name)
    {
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalidChar, '_');
        }

        return name;
    }

    /// <summary>
    /// Splits on both slash styles, sanitizes each segment, and drops "."
    /// / ".." so a typed folder path can never escape the vault root.
    /// </summary>
    private static string SanitizeRelativeFolder(string input)
    {
        var segments = input
            .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s != "." && s != "..")
            .Select(SanitizeFileNameSegment);

        return Path.Combine(segments.ToArray());
    }
}
