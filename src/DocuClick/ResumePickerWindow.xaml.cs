using System.Windows;
using DocuClick.Services;

namespace DocuClick;

public partial class ResumePickerWindow : Window
{
    public ResumableNode? SelectedNode { get; private set; }

    public ResumePickerWindow(List<ResumableNode> nodes)
    {
        InitializeComponent();
        NodeListBox.ItemsSource = nodes;
        if (nodes.Count > 0)
        {
            NodeListBox.SelectedIndex = nodes.Count - 1; // most recently added node first-guess
        }
    }

    private void OnSelectClicked(object sender, RoutedEventArgs e)
    {
        SelectedNode = NodeListBox.SelectedItem as ResumableNode;
        if (SelectedNode is null)
        {
            MessageBox.Show("Bitte einen Knoten auswählen.", "DocuClick", MessageBoxButton.OK, MessageBoxImage.Warning);
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
