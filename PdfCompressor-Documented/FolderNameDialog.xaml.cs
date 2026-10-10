// =============================================================================
//  FolderNameDialog.xaml.cs
// =============================================================================
//  This is the tiny modal dialog that asks the user to NAME a new folder.
//
//  Why does this dialog exist at all?
//    When the user compresses several PDFs at once and clicks "Save...", we ask
//    them to pick a directory in Explorer and then to type a name for a NEW
//    empty folder that will be created inside it. All compressed files go into
//    that named folder instead of being dumped loose into the chosen directory.
//
//  The validation loop (empty name, illegal characters, name already in use) is
//  NOT in this dialog - it lives in MainWindow.PromptForFolderName(). This file
//  is deliberately dumb: it only collects a string and reports success/failure.
//  Keeping the dialog dumb and the validation in the caller makes both pieces
//  easier to reason about and test.
// =============================================================================

using System.Windows;

namespace PdfCompressor;

/// <summary>
/// A simple one-field input dialog.
///
/// WPF dialog conventions used here:
///   - ShowDialog() from the caller makes this a MODAL window (blocks the caller).
///   - Setting DialogResult = true closes the window and makes ShowDialog()
///     return true (meaning "user confirmed").
///   - A Button with IsCancel="True" (see the XAML) automatically closes the
///     dialog with DialogResult = false when clicked or when Esc is pressed.
/// </summary>
public partial class FolderNameDialog : Window
{
    /// <summary>
    /// Constructor. InitializeComponent() is REQUIRED - it parses the matching
    /// FolderNameDialog.xaml file and builds the visual tree described there.
    /// If you forget this call, every named control (like NameBox) stays null
    /// and the window is completely blank.
    /// </summary>
    public FolderNameDialog()
    {
        InitializeComponent();

        // Small UX touch: put the keyboard cursor straight into the text box so
        // the user can just start typing without clicking first.
        NameBox.Focus();
    }

    /// <summary>
    /// The name the user typed, with surrounding whitespace trimmed.
    /// (Callers must still validate it - see MainWindow.PromptForFolderName.)
    /// </summary>
    public string FolderName => NameBox.Text.Trim();

    /// <summary>
    /// Wired to the "Create" button via Click="CreateButton_Click" in the XAML.
    /// </summary>
    private void CreateButton_Click(object sender, RoutedEventArgs e)
    {
        // If the box is empty or only whitespace, do nothing. The dialog stays
        // open and the user can keep typing. (The empty-name warning message is
        // shown by the caller's validation loop.)
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            return;
        }

        // Close the dialog and signal "confirmed" to the caller.
        DialogResult = true;
    }
}
