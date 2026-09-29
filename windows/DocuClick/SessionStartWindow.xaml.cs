using System.IO;
using System.Windows;
using DocuClick.Services;

namespace DocuClick;

/// <summary>
/// Shown every time a recording session is about to start, so the target
/// file is always either an explicitly chosen new folder + name or a
/// deliberately picked existing file — never a name so generic it silently
/// collides with (and resumes) an earlier session. There is no configured
/// "output root" any more (see AppConfig's own history) — a new session's
/// folder is a completely free choice made right here, and that same
/// folder is where its Attachments subfolder lives too, so a session is
/// always fully self-contained in whatever single folder the user picked.
/// </summary>
public partial class SessionStartWindow : Window
{
    // .docuclick inside an Obsidian vault (the plugin's diagram), else .html.
    private string TargetExtension => ObsidianVault.OutputExtensionFor(_selectedTargetFolder);

    // Absolute path, or "" until the user has actually browsed for one —
    // Starten refuses until this is set (see OnStartClicked). Defaults to
    // the most recently used folder (config.RecentOutputPaths) purely as a
    // starting point for Durchsuchen..., not a constraint on where the user
    // can end up.
    private string _selectedTargetFolder = "";

    // Absolute path to the picked existing file — null until browsed.
    private string? _selectedExistingFile;

    // Tracks whether the user has typed their own name, so the
    // folder-aware suggestion (see SetSuggestedFileName) only auto-updates
    // while they haven't overridden it.
    private bool _fileNameEditedByUser;
    private bool _suppressFileNameChangeTracking;

    /// <summary>Absolute path to the chosen/created target file once the dialog is confirmed.</summary>
    public string? SelectedFileName { get; private set; }

    public SessionStartWindow(AppConfig config)
    {
        InitializeComponent();

        _selectedTargetFolder = config.RecentOutputPaths.FirstOrDefault(Directory.Exists) ?? "";
        TargetFolderBox.Text = string.IsNullOrEmpty(_selectedTargetFolder) ? "" : _selectedTargetFolder;

        SetSuggestedFileName();
        UpdateTargetHint();

        NewFileNameBox.TextChanged += (_, _) =>
        {
            if (!_suppressFileNameChangeTracking)
            {
                _fileNameEditedByUser = true;
            }
        };

        ApplyModeToControls();
        NewFileNameBox.Focus();
        NewFileNameBox.SelectAll();
    }

    /// <summary>
    /// Suggests "&lt;Zielordner-Name&gt; yyyy-MM-dd (N)" (the chosen
    /// folder's own name, today's date, and a running number that skips
    /// names already taken in that folder) — never overwrites a name the
    /// user already typed themselves. "(N)" rather than "#N": this name
    /// also becomes the Attachments subfolder for every screenshot, and
    /// "#" is Obsidian's link-anchor delimiter — a literal "#" in a file or
    /// folder name breaks every embed that references it, since everything
    /// after it gets parsed as a heading/block reference instead of part
    /// of the path.
    /// </summary>
    private void SetSuggestedFileName()
    {
        if (_fileNameEditedByUser || string.IsNullOrEmpty(_selectedTargetFolder))
        {
            return;
        }

        var folderLabel = GetFolderLabel(_selectedTargetFolder);
        var datePart = DateTime.Now.ToString("yyyy-MM-dd");

        var existingNames = Directory.Exists(_selectedTargetFolder)
            ? Directory.GetFiles(_selectedTargetFolder, "*" + TargetExtension)
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

    private static string GetFolderLabel(string absoluteFolder)
    {
        var name = Path.GetFileName(absoluteFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? "Session" : name;
    }

    private void OnModeChanged(object sender, RoutedEventArgs e) => ApplyModeToControls();

    private void ApplyModeToControls()
    {
        // Guard: RadioButton's Checked event (IsChecked="True" in XAML) can
        // fire while InitializeComponent is still wiring named fields.
        if (NewFileNameBox is null || TargetFolderBox is null || ExistingFileBox is null)
        {
            return;
        }

        var isNewFile = NewFileRadio.IsChecked == true;
        NewFileNameBox.IsEnabled = isNewFile;
        TargetFolderBox.IsEnabled = isNewFile;
        ExistingFileBox.IsEnabled = !isNewFile;

        if (isNewFile)
        {
            NewFileNameBox.Focus();
        }
    }

    private void OnBrowseFolderClicked(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            SelectedPath = string.IsNullOrEmpty(_selectedTargetFolder) ? "" : _selectedTargetFolder,
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        _selectedTargetFolder = dialog.SelectedPath;
        TargetFolderBox.Text = _selectedTargetFolder;
        SetSuggestedFileName();
        UpdateTargetHint();
    }

    private void UpdateTargetHint() => TargetHintText.Text = ObsidianVault.TargetHint(_selectedTargetFolder);

    private void OnBrowseExistingFileClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Bestehenden Ablauf auswählen",
            Filter = "DocuClick-Ablauf (*.docuclick;*.html;*.canvas)|*.docuclick;*.html;*.canvas|Obsidian-Diagramme (*.docuclick)|*.docuclick|HTML-Abläufe (*.html)|*.html|Obsidian Canvas (*.canvas)|*.canvas|Alle Dateien (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _selectedExistingFile = dialog.FileName;
        ExistingFileBox.Text = dialog.FileName;
    }

    private void OnStartClicked(object sender, RoutedEventArgs e)
    {
        if (NewFileRadio.IsChecked == true)
        {
            if (string.IsNullOrEmpty(_selectedTargetFolder))
            {
                MessageBox.Show("Bitte einen Speicherort wählen.", "DocuClick", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var name = NewFileNameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show("Bitte einen Dateinamen eingeben.", "DocuClick", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var fileName = SanitizeFileNameSegment(Path.GetFileNameWithoutExtension(name)) + TargetExtension;
            SelectedFileName = Path.Combine(_selectedTargetFolder, fileName);
        }
        else
        {
            if (_selectedExistingFile is null)
            {
                MessageBox.Show("Bitte eine Datei auswählen.", "DocuClick", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SelectedFileName = _selectedExistingFile;
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
}
