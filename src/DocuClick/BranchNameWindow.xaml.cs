using System.Windows;

namespace DocuClick;

/// <summary>
/// Small always-on-top text prompt — used both for naming a new path when
/// it's started from a decision point, and (via the optional constructor
/// parameters) for renaming an existing node's label in the Ablauf-Übersicht.
/// Same dialog either way: a single required text field, "Setzen"/"Abbrechen".
/// </summary>
public partial class BranchNameWindow : Window
{
    public string? BranchName { get; private set; }

    public BranchNameWindow(string? title = null, string? label = null, string? initialValue = null)
    {
        InitializeComponent();
        if (title is not null)
        {
            Title = title;
        }

        if (label is not null)
        {
            LabelText.Text = label;
        }

        Loaded += (_, _) =>
        {
            if (initialValue is not null)
            {
                BranchNameBox.Text = initialValue;
            }

            BranchNameBox.Focus();
            BranchNameBox.SelectAll();
        };
    }

    private void OnSetClicked(object sender, RoutedEventArgs e)
    {
        var name = BranchNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("Bitte einen Namen eingeben.", "DocuClick", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        BranchName = name;
        DialogResult = true;
        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
