using System.Windows;

namespace DocuClick;

/// <summary>Small always-on-top prompt for naming a new path when it's started from a decision point in the Ablauf-Übersicht.</summary>
public partial class BranchNameWindow : Window
{
    public string? BranchName { get; private set; }

    public BranchNameWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            BranchNameBox.Focus();
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
