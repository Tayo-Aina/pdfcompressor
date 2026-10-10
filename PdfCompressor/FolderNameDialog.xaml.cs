using System.Windows;

namespace PdfCompressor;

/// <summary>Small modal dialog that asks the user for a folder name.</summary>
public partial class FolderNameDialog : Window
{
    public FolderNameDialog()
    {
        InitializeComponent();
        NameBox.Focus();
    }

    public string FolderName => NameBox.Text.Trim();

    private void CreateButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            return;
        }

        DialogResult = true;
    }
}
