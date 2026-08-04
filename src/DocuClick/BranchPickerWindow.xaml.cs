using System.Windows;

namespace DocuClick;

/// <summary>Lists all currently named branch anchors so the user can pick exactly one to jump to.</summary>
public partial class BranchPickerWindow : Window
{
    public string? SelectedBranchName { get; private set; }

    public BranchPickerWindow(List<string> branchNames)
    {
        InitializeComponent();
        BranchListBox.ItemsSource = branchNames;
        if (branchNames.Count > 0)
        {
            BranchListBox.SelectedIndex = branchNames.Count - 1; // most recently marked first-guess
        }
    }

    private void OnSelectClicked(object sender, RoutedEventArgs e)
    {
        SelectedBranchName = BranchListBox.SelectedItem as string;
        if (SelectedBranchName is null)
        {
            MessageBox.Show("Bitte einen Branch auswählen.", "DocuClick", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
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
