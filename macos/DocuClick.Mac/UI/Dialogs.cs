using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DocuClick.Services;

namespace DocuClick.Mac.UI;

/// <summary>Name/label prompt — path names, renames, new nodes (the Windows BranchNameWindow).</summary>
internal sealed class BranchNameWindow : DialogWindow<string>
{
    public BranchNameWindow(string? title = null, string? label = null, string? initialValue = null)
    {
        Title = title ?? "DocuClick - Pfad benennen";
        Width = 380;
        SizeToContent = SizeToContent.Height;

        var nameBox = new TextBox { Text = initialValue ?? "", PlaceholderText = title is null ? "z. B. „Login-Fehler“" : null };
        var error = Ui.Error();

        var cancel = Ui.Button("Abbrechen");
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Close();
        var ok = Ui.Button(title is null ? "Setzen" : "OK", primary: true);
        ok.IsDefault = true;
        ok.Click += (_, _) =>
        {
            var name = nameBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                Ui.ShowError(error, "Bitte einen Namen eingeben.");
                return;
            }

            Complete(name);
        };

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 8,
            Children = { Ui.Label(label ?? "Pfad-Name"), nameBox, error, Ui.ButtonRow(cancel, ok) }
        };
        Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            nameBox.Focus();
            nameBox.SelectAll();
        }, DispatcherPriority.Input);
    }
}

/// <summary>Yes/No question (cascade delete, overwrite).</summary>
internal sealed class ConfirmWindow : DialogWindow<bool>
{
    private ConfirmWindow(string message)
    {
        Title = "DocuClick";
        Width = 440;
        SizeToContent = SizeToContent.Height;

        var no = Ui.Button("Nein");
        no.IsCancel = true;
        no.Click += (_, _) => Complete(false);
        var yes = Ui.Button("Ja", primary: true);
        yes.Click += (_, _) => Complete(true);

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Ui.TextPrimary },
                Ui.ButtonRow(no, yes)
            }
        };
    }

    public static async Task<bool> AskAsync(string message) => await new ConfirmWindow(message).ShowAndWaitAsync();
}

/// <summary>
/// Asked every time a new session starts: a new file (freely chosen folder +
/// name) or an existing Ablauf to continue — the macOS counterpart of the
/// Windows SessionStartWindow (v1.13: no global output folder).
/// </summary>
internal sealed class SessionStartWindow : DialogWindow<string>
{
    private readonly string _extension = SessionManager.OutputExtension;
    private readonly RadioButton _newFileRadio;
    private readonly TextBox _folderBox = new() { IsReadOnly = true, PlaceholderText = "Speicherort wählen …" };
    private readonly TextBox _nameBox = new();
    private readonly TextBox _existingBox = new() { IsReadOnly = true, PlaceholderText = "Ablauf-Datei wählen …" };
    private readonly Button _browseFolder = Ui.Button("Auswählen …");
    private readonly Button _browseFile = Ui.Button("Auswählen …");
    private readonly TextBlock _error = Ui.Error();
    private string _folder;
    private string? _existingFile;
    private bool _nameEditedByUser;
    private bool _suppressNameTracking;

    public SessionStartWindow(AppConfig config)
    {
        Title = "DocuClick – Session starten";
        Width = 520;
        SizeToContent = SizeToContent.Height;

        _folder = config.RecentOutputPaths.FirstOrDefault(Directory.Exists) ?? "";
        _folderBox.Text = _folder;

        _newFileRadio = new RadioButton { Content = "Neue Datei anlegen", GroupName = "Mode", IsChecked = true };
        var existingRadio = new RadioButton { Content = "Bestehende Datei fortsetzen", GroupName = "Mode" };
        _newFileRadio.IsCheckedChanged += (_, _) => ApplyMode();

        _nameBox.TextChanged += (_, _) =>
        {
            if (!_suppressNameTracking)
            {
                _nameEditedByUser = true;
            }
        };
        _browseFolder.Click += async (_, _) => await BrowseFolderAsync();
        _browseFile.Click += async (_, _) => await BrowseFileAsync();

        var cancel = Ui.Button("Abbrechen");
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Close();
        var start = Ui.Button("Starten", primary: true);
        start.IsDefault = true;
        start.Click += (_, _) => OnStart();

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 10,
            Children =
            {
                Ui.Hint("Jede Aufnahme-Session braucht eine Zieldatei — neue Datei anlegen oder eine bestehende fortsetzen."),
                Ui.Card(
                    _newFileRadio,
                    Indent(Ui.Label("Speicherort")), Indent(Ui.PathRow(_folderBox, _browseFolder)),
                    Indent(Ui.Label("Dateiname")), Indent(_nameBox),
                    Indent(Ui.Hint("Die .html-Datei und ihr Attachments-Unterordner werden direkt in diesem Ordner angelegt.")),
                    existingRadio,
                    Indent(Ui.Label("Datei")), Indent(Ui.PathRow(_existingBox, _browseFile))),
                _error,
                Ui.ButtonRow(cancel, start)
            }
        };

        SuggestFileName();
        ApplyMode();
        Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            _nameBox.Focus();
            _nameBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    private static Control Indent(Control control)
    {
        control.Margin = new Thickness(24, 0, 0, 0);
        return control;
    }

    private void ApplyMode()
    {
        var isNew = _newFileRadio.IsChecked == true;
        _nameBox.IsEnabled = _folderBox.IsEnabled = _browseFolder.IsEnabled = isNew;
        _existingBox.IsEnabled = _browseFile.IsEnabled = !isNew;
    }

    /// <summary>"&lt;Ordnername&gt; yyyy-MM-dd (N)" — never overwrites a name the user typed. "(N)" not "#N": "#" breaks links.</summary>
    private void SuggestFileName()
    {
        if (_nameEditedByUser || string.IsNullOrEmpty(_folder))
        {
            return;
        }

        var label = Path.GetFileName(_folder.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : "Session";
        var taken = Directory.Exists(_folder)
            ? Directory.GetFiles(_folder, "*" + _extension).Select(f => Path.GetFileNameWithoutExtension(f)!).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var date = DateTime.Now.ToString("yyyy-MM-dd");
        var n = 1;
        string candidate;
        do
        {
            candidate = $"{label} {date} ({n++})";
        } while (taken.Contains(candidate));

        _suppressNameTracking = true;
        _nameBox.Text = candidate;
        _suppressNameTracking = false;
    }

    private async Task BrowseFolderAsync()
    {
        var start = Directory.Exists(_folder) ? await StorageProvider.TryGetFolderFromPathAsync(_folder) : null;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Speicherort für den Ablauf wählen",
            SuggestedStartLocation = start
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path)
        {
            _folder = path;
            _folderBox.Text = path;
            SuggestFileName();
        }
    }

    private async Task BrowseFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Bestehenden Ablauf auswählen",
            AllowMultiple = false,
            FileTypeFilter = new[] { Ui.AblaufFileType, FilePickerFileTypes.All }
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
        {
            _existingFile = path;
            _existingBox.Text = path;
        }
    }

    private void OnStart()
    {
        if (_newFileRadio.IsChecked == true)
        {
            if (string.IsNullOrEmpty(_folder))
            {
                Ui.ShowError(_error, "Bitte einen Speicherort wählen.");
                return;
            }

            var name = _nameBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                Ui.ShowError(_error, "Bitte einen Dateinamen eingeben.");
                return;
            }

            var fileName = Path.GetFileNameWithoutExtension(name);
            foreach (var invalid in Path.GetInvalidFileNameChars().Append(':'))
            {
                fileName = fileName.Replace(invalid, '_');
            }

            Complete(Path.Combine(_folder, fileName + _extension));
        }
        else if (_existingFile is not null)
        {
            Complete(_existingFile);
        }
        else
        {
            Ui.ShowError(_error, "Bitte eine Datei auswählen.");
        }
    }
}
