using System.IO;
using System.Windows;
using System.Windows.Controls;
using DocuClick.Services;

namespace DocuClick;

/// <summary>
/// Shown every time a recording session is about to start, so the target
/// file is always either an explicitly typed new name or a deliberately
/// chosen existing file — never a name so generic it silently collides
/// with (and resumes) an earlier session. Also asks which (sub)folder
/// relative to the vault path the file belongs in, so captures can be
/// filed straight into a vault's existing structure instead of always
/// landing at its root.
/// </summary>
public partial class SessionStartWindow : Window
{
    private readonly string _vaultPath;
    private readonly string _extension;

    // Tracks whether the user has typed their own name, so the
    // folder-aware suggestion (see SuggestFileName) only auto-updates
    // while they haven't overridden it.
    private bool _fileNameEditedByUser;
    private bool _suppressFileNameChangeTracking;

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

        _vaultPath = config.VaultPath;
        _extension = SessionManager.ExtensionForOutputMode(config.OutputMode);

        var existingFiles = new List<string>();
        var existingFolders = new List<string>();
        if (!string.IsNullOrWhiteSpace(_vaultPath) && Directory.Exists(_vaultPath))
        {
            existingFiles = Directory.GetFiles(_vaultPath, "*" + _extension, SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(_vaultPath, f))
                .OrderByDescending(f => File.GetLastWriteTimeUtc(Path.Combine(_vaultPath, f)))
                .ToList();

            existingFolders = Directory.GetDirectories(_vaultPath, "*", SearchOption.AllDirectories)
                .Select(d => Path.GetRelativePath(_vaultPath, d))
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
            SetSuggestedFileName();
        }

        NewFileNameBox.TextChanged += (_, _) =>
        {
            if (!_suppressFileNameChangeTracking)
            {
                _fileNameEditedByUser = true;
            }
        };
        TargetFolderBox.SelectionChanged += (_, _) => SetSuggestedFileName();
        TargetFolderBox.LostFocus += (_, _) => SetSuggestedFileName();

        ApplyModeToControls();
        NewFileNameBox.Focus();
        NewFileNameBox.SelectAll();
    }

    /// <summary>
    /// Suggests "&lt;Zielordner-Name&gt; yyyy-MM-dd (N)" (folder name of
    /// wherever the file is about to be created, today's date, and a
    /// running number that skips names already taken in that folder) —
    /// never overwrites a name the user already typed themselves.
    /// "(N)" rather than "#N": this name also becomes the Attachments
    /// subfolder for every screenshot in Canvas mode, and
    /// "#" is Obsidian's link-anchor delimiter — a literal "#" in a file
    /// or folder name breaks every embed that references it, since
    /// everything after it gets parsed as a heading/block reference
    /// instead of part of the path.
    /// </summary>
    private void SetSuggestedFileName()
    {
        if (_fileNameEditedByUser)
        {
            return;
        }

        var folder = SanitizeRelativeFolder(TargetFolderBox.Text ?? "");
        var folderLabel = GetFolderLabel(folder);
        var datePart = DateTime.Now.ToString("yyyy-MM-dd");
        var targetDir = string.IsNullOrEmpty(folder) ? _vaultPath : Path.Combine(_vaultPath, folder);

        var existingNames = Directory.Exists(targetDir)
            ? Directory.GetFiles(targetDir, "*" + _extension)
                .Select(f => Path.GetFileNameWithoutExtension(f)!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var n = 1;
        string candidate;
        do
        {
            candidate = $"{folderLabel} {datePart} ({n})";
            n++;
        } while (existingNames.Contains(candidate));

        _suppressFileNameChangeTracking = true;
        NewFileNameBox.Text = candidate;
        _suppressFileNameChangeTracking = false;
    }

    private string GetFolderLabel(string relativeFolder)
    {
        if (string.IsNullOrWhiteSpace(relativeFolder))
        {
            var vaultName = Path.GetFileName(_vaultPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.IsNullOrWhiteSpace(vaultName) ? "Session" : vaultName;
        }

        var lastSegment = relativeFolder
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .LastOrDefault(s => !string.IsNullOrWhiteSpace(s));

        return string.IsNullOrWhiteSpace(lastSegment) ? "Session" : lastSegment;
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
