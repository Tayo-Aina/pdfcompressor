using System.Windows;

namespace PdfCompressor;

/// <summary>Small modal dialog that asks the user for a folder name.</summary>
public partial class FolderNameDialog : Window
{
    public FolderNameDialog()
    {
        InitializeComponent();
        ThemeManager.ApplyWindowIcon(this);
        NameBox.Focus();
    }

    public string FolderName => NameBox.Text.Trim();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ThemeManager.ApplyWindowChrome(this);
    }

    private void CreateButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            return;
        }

        DialogResult = true;
    }
}
