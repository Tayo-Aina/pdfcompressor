// =============================================================================
//  MainWindow.xaml.cs
// =============================================================================
//  This is the code-behind for the main window - i.e. the C# logic that powers
//  everything the user sees and clicks. The visual layout lives in the matching
//  MainWindow.xaml file; this file contains the BEHAVIOR.
//
//  THE WORKFLOW THIS FILE IMPLEMENTS
//  ----------------------------------------------------------------------------
//  The window has THREE modes, selected with the toggle buttons at the top
//  (AppMode). Whichever mode is active decides which files may be added, which
//  controls are visible, and what the action button does:
//
//    COMPRESS (original feature)
//      1. ADD      - user drops PDFs onto the drop zone, or clicks
//                    "Add PDF file(s)..." / "Add folder..." to browse.
//      2. COMPRESS - user picks a preset and clicks Compress. Each PDF is run
//                    through Ghostscript (CompressionEngine) on a background
//                    thread; the list shows live status per file.
//      3. SAVE     - the Save button stays disabled until at least one file was
//                    actually made smaller. Then:
//                       one file  -> a Save-As dialog
//                       many file -> folder picker + name-a-new-subfolder flow
//
//    IMAGES -> PDF (new feature)
//      Images (PNG/JPEG/BMP/GIF/TIFF/WebP/HEIC...) are added to the same list
//      and turned into ONE multi-page PDF by ImageToPdfConverter - one page per
//      image, in list order. The Save button then stores that single result.
//
//    MERGE (new feature)
//      Two or more PDFs are concatenated, in list order, by PdfMerger
//      (Ghostscript pdfwrite). "Move up"/"Move down" buttons - only visible in
//      this mode - let the user change the merge order before running it.
//
//  WHY async/await + Task.Run?
//    Ghostscript (and image decoding) can take seconds. Heavy work runs on a
//    background thread; await hands control back to the UI so the window stays
//    responsive. async void is the standard WPF event-handler pattern; the
//    _busy flag guards against re-entry.
//
//  See CompressionEngine.cs / ImageToPdfConverter.cs / PdfMerger.cs for the
//  actual document-processing logic.
// =============================================================================

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives; // ToggleButton (the mode selector)
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace PdfCompressor;

/// <summary>One row in the file list.</summary>
public sealed class FileItem : INotifyPropertyChanged
{
    private string _status = "Pending";
    private Brush _statusBrush = Brushes.Gray;

    public FileItem(string path)
    {
        Path = path;
    }

    /// <summary>Full path of the input file (image in Images mode, PDF otherwise).</summary>
    public string Path { get; }

    /// <summary>Just the file name, for display (e.g. "invoice.pdf").</summary>
    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>Human-friendly size of the original, e.g. "12.3 MB".</summary>
    public string Size => FormatSize(new FileInfo(Path).Length);

    /// <summary>
    /// Full path to the compressed result, if compression succeeded AND made the
    /// file smaller. null otherwise. This is what the Save step copies from.
    /// (Images/Merge modes don't use this - they produce ONE file, tracked by
    /// MainWindow._singleResultPath instead.)
    /// </summary>
    public string? ResultPath { get; set; }

    /// <summary>Status text shown on the row (Pending / Compressing… / Done — … / Failed: …).</summary>
    public string Status
    {
        get => _status;
        set
        {
            if (_status != value)
            {
                _status = value;
                // Tell WPF "Status changed" so any binding to Status re-evaluates.
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
            }
        }
    }

    /// <summary>Brush used to color the status text (theme-aware).</summary>
    public Brush StatusBrush
    {
        get => _statusBrush;
        set
        {
            if (!Equals(_statusBrush, value))
            {
                _statusBrush = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusBrush)));
            }
        }
    }

    /// <summary>The event WPF subscribes to for change notifications.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Converts a byte count into something readable ("512 B", "1.9 MB", ...).
    /// It walks the size down through 1024-based units until it fits.
    /// </summary>
    internal static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{size:0.0} {units[unit]}";
    }
}

/// <summary>
/// The three things the app can do with the files in the list. The active mode
/// is shown as a checked toggle button in the window's top row and drives file
/// filtering, visible controls, status texts and the action button.
/// </summary>
public enum AppMode
{
    /// <summary>Shrink PDFs with Ghostscript (original feature).</summary>
    Compress,

    /// <summary>Turn image files into one multi-page PDF (ImageToPdfConverter).</summary>
    ImagesToPdf,

    /// <summary>Concatenate several PDFs into one (PdfMerger).</summary>
    Merge
}

/// <summary>Main window: drag &amp; drop / browse input, convert/compress/merge, then choose where to save.</summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// Every image format the file pickers and drag &amp; drop accept in
    /// Images -> PDF mode. Windows/WPF decodes all of these through WIC - on
    /// Windows 11 that even includes WebP and HEIC via the "unknown" bitmap
    /// decoder, so we can happily advertise them in the drop-zone text.
    /// </summary>
    private static readonly string[] ImageExtensions =
    {
        ".png", ".jpg", ".jpeg", ".jfif", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif"
    };

    /// <summary>
    /// The rows shown in the ListBox. ObservableCollection notifies the ListBox
    /// when items are added/removed so the UI stays in sync with this list.
    /// The ORDER of this list is also the page order in Images mode and the
    /// document order in Merge mode.
    /// </summary>
    private readonly ObservableCollection<FileItem> _files = new();

    /// <summary>
    /// Scratch folder where results are written BEFORE the user chooses a final
    /// destination (compressed PDFs, converted PDFs, merged PDFs). Using %TEMP%
    /// keeps intermediate files out of the user's directories; the folder is
    /// deleted on window close.
    /// </summary>
    private readonly string _resultDir;

    /// <summary>true while a job is in progress (guards against double-clicks).</summary>
    private bool _busy;

    /// <summary>The active mode (compress / images→pdf / merge), mirrored from the toggles.</summary>
    private AppMode _mode = AppMode.Compress;

    /// <summary>
    /// Set by the Images -> PDF / Merge modes: both produce exactly ONE output
    /// PDF, so instead of per-row ResultPath entries the whole batch funnels
    /// into this single path. null while compressing (or when nothing ran yet).
    /// </summary>
    private string? _singleResultPath;

    public MainWindow()
    {
        InitializeComponent(); // REQUIRED - builds the UI from MainWindow.xaml

        // NOTE (the hard-won lesson): ThemeButton's Click is wired ONLY in the
        // XAML (Click="ThemeButton_Click"). We used to ALSO subscribe here in
        // code, which made every click toggle twice - dark->light->dark in one
        // click - looking exactly like the button did nothing at all. One
        // subscription only, thanks.
        UpdateThemeButtonLabel();

        FileList.ItemsSource = _files;

        // Apply the initial (compress mode) texts/visibility. This must happen
        // BEFORE any mode-toggle event matters: the very first Checked event
        // fires while InitializeComponent parses the XAML - at that point the
        // sibling toggles don't exist yet, so ModeButton_Checked bails out and
        // relies on this call instead.
        ApplyModeUi();

        // Create the temp scratch folder up front so jobs can rely on it.
        _resultDir = Path.Combine(Path.GetTempPath(), "PdfCompressorResults");
        Directory.CreateDirectory(_resultDir);

        // If the exe was launched with file/folder arguments (e.g. dragged onto
        // the icon, or opened from "Open with..."), preload them into the list.
        // AddFile filters for the CURRENT mode, so only matching files land here.
        foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
        {
            if (Directory.Exists(arg))
            {
                AddDirectory(arg);
            }
            else if (File.Exists(arg))
            {
                AddFile(arg);
            }
        }
    }

    /// <summary>
    /// Adds one file to the list if the current mode accepts it and it is not
    /// already there. Wrong-type files are silently ignored (the drop zone
    /// accepts anything; we filter here - .pdf for compress/merge, an image
    /// extension for Images -> PDF).
    /// </summary>
    private void AddFile(string path)
    {
        if (!IsAcceptedFile(path, _mode))
        {
            return;
        }

        // De-duplicate: dragging the same folder twice should not double the list.
        if (_files.Any(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _files.Add(new FileItem(path));
        StatusText.Text = $"{_files.Count} {FileNoun()} added. {ActionNoun()} when ready.";
    }

    /// <summary>
    /// Scans a folder for files the current mode accepts (top level only - not
    /// recursive, on purpose: dropping a big folder tree shouldn't pull in
    /// hundreds of files by surprise) and adds each one.
    /// </summary>
    private void AddDirectory(string dir)
    {
        // Enumerate everything, then let the mode filter decide - one code path
        // for "is this a pdf / is this an image" (IsAcceptedFile).
        var accepted = Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly)
                                .Where(p => IsAcceptedFile(p, _mode))
                                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                                .ToList();
        foreach (var file in accepted)
        {
            AddFile(file);
        }

        if (accepted.Count == 0)
        {
            StatusText.Text = _mode == AppMode.ImagesToPdf
                ? "No supported image files found in that folder."
                : "No PDF files found in that folder.";
        }
    }

    /// <summary>
    /// Whether <paramref name="path"/> may enter the list for the given mode:
    /// images in Images -> PDF mode, PDFs in Compress and Merge modes.
    /// </summary>
    private static bool IsAcceptedFile(string path, AppMode mode)
    {
        if (mode == AppMode.ImagesToPdf)
        {
            var ext = System.IO.Path.GetExtension(path);
            return ImageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
        }
        return path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Singular/plural noun for status texts ("1 image" / "3 PDFs").</summary>
    private string FileNoun() => _mode == AppMode.ImagesToPdf
        ? (_files.Count == 1 ? "image" : "images")
        : (_files.Count == 1 ? "PDF" : "PDFs");

    /// <summary>Which button the status text should point the user at next.</summary>
    private string ActionNoun() => _mode switch
    {
        AppMode.ImagesToPdf => "Click Create PDF",
        AppMode.Merge => "Click Merge",
        _ => "Click Compress"
    };

    // ---- Mode switching ----

    /// <summary>
    /// Fired by every mode toggle (Checked). Recomputes which mode was picked,
    /// then enforces radio-button behaviour by unchecking the other two.
    /// </summary>
    private void ModeButton_Checked(object sender, RoutedEventArgs e)
    {
        // The first Checked event fires while InitializeComponent is still parsing the XAML,
        // before the sibling toggles have been assigned - the constructor calls ApplyModeUi()
        // afterwards, so it is safe to ignore events at that point.
        if (CompressModeButton is null || ImagesModeButton is null || MergeModeButton is null)
        {
            return;
        }

        if (sender is not ToggleButton { IsChecked: true } button)
        {
            return;
        }

        var mode = button == ImagesModeButton ? AppMode.ImagesToPdf
                 : button == MergeModeButton ? AppMode.Merge
                 : AppMode.Compress;

        // Manual radio behaviour: only one mode toggle may stay checked.
        // (ToggleButton has no GroupName - that is a RadioButton concept - so we
        // do it ourselves here.)
        CompressModeButton.IsChecked = button == CompressModeButton;
        ImagesModeButton.IsChecked = button == ImagesModeButton;
        MergeModeButton.IsChecked = button == MergeModeButton;

        SetMode(mode);
    }

    /// <summary>
    /// A mode toggle can never end up unchecked - clicking the active one keeps
    /// it on. Without this, a second click would blank the selector while the
    /// old mode stayed silently active.
    /// </summary>
    private void ModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton button && button.IsChecked != true)
        {
            button.IsChecked = true;
        }
    }

    /// <summary>Switches the app to <paramref name="mode"/> and refreshes the UI.</summary>
    private void SetMode(AppMode mode)
    {
        if (_busy || _mode == mode)
        {
            return; // a job is running (buttons are disabled anyway) or nothing changed
        }

        _mode = mode;

        // Different modes accept different files, so the previous list is not usable - start fresh.
        ResetList();
        ApplyModeUi();
    }

    /// <summary>
    /// Shows/hides everything that depends on the active mode: drop-zone texts,
    /// the add-files button label, the Move up/down buttons (merge only), the
    /// per-mode option panel (preset vs. page size) and the action button
    /// (Compress / Create PDF / Merge), plus the starting status text.
    /// </summary>
    private void ApplyModeUi()
    {
        var isCompress = _mode == AppMode.Compress;
        var isImages = _mode == AppMode.ImagesToPdf;
        var isMerge = _mode == AppMode.Merge;

        DropText.Text = _mode switch
        {
            AppMode.ImagesToPdf => "Drag & drop images here (PNG, JPEG, BMP, GIF, TIFF, WebP, HEIC…)",
            AppMode.Merge => "Drag & drop PDF files to merge",
            _ => "Drag & drop PDF files here"
        };

        DropHint.Text = _mode switch
        {
            AppMode.ImagesToPdf => "one page per image, in list order · click the zone to browse",
            AppMode.Merge => "the list order is the merge order · click the zone to browse",
            _ => "or click the zone to browse · you can also add a whole folder below"
        };

        AddFilesButton.Content = isImages ? "Add image(s)…" : "Add PDF file(s)…";

        // Reordering only makes sense for merging - hide the buttons elsewhere.
        MoveUpButton.Visibility = isMerge ? Visibility.Visible : Visibility.Collapsed;
        MoveDownButton.Visibility = isMerge ? Visibility.Visible : Visibility.Collapsed;

        // Per-mode option panel and action button (only one of each visible).
        PresetPanel.Visibility = isCompress ? Visibility.Visible : Visibility.Collapsed;
        PageSizePanel.Visibility = isImages ? Visibility.Visible : Visibility.Collapsed;
        CompressButton.Visibility = isCompress ? Visibility.Visible : Visibility.Collapsed;
        CreatePdfButton.Visibility = isImages ? Visibility.Visible : Visibility.Collapsed;
        MergeButton.Visibility = isMerge ? Visibility.Visible : Visibility.Collapsed;

        if (StatusText is not null)
        {
            StatusText.Text = _mode switch
            {
                AppMode.ImagesToPdf => "Drop images or add files to begin.",
                AppMode.Merge => "Drop two or more PDFs to merge them.",
                _ => "Drop PDFs or add files to begin."
            };
        }
    }

    /// <summary>
    /// Empties the list and forgets any pending single-file result. Called when
    /// switching modes (the old list may contain files the new mode rejects)
    /// and by the Clear button.
    /// </summary>
    private void ResetList()
    {
        _files.Clear();
        _singleResultPath = null;
        Progress.Value = 0;
        Progress.IsIndeterminate = false;
        SaveButton.IsEnabled = false;
    }

    /// <summary>
    /// Disables every action that would interfere with a running job: the three
    /// action buttons, the browse/clear buttons, the reorder buttons and the
    /// mode toggles (switching modes mid-run would pull the list out from under
    /// the worker). When the job ends, Save is re-enabled iff there is actually
    /// something to save (a single converted/merged file or shrunk compress
    /// results).
    /// </summary>
    private void SetBusy(bool busy)
    {
        CompressButton.IsEnabled = !busy;
        CreatePdfButton.IsEnabled = !busy;
        MergeButton.IsEnabled = !busy;
        AddFilesButton.IsEnabled = !busy;
        AddFolderButton.IsEnabled = !busy;
        ClearButton.IsEnabled = !busy;
        MoveUpButton.IsEnabled = !busy;
        MoveDownButton.IsEnabled = !busy;
        CompressModeButton.IsEnabled = !busy;
        ImagesModeButton.IsEnabled = !busy;
        MergeModeButton.IsEnabled = !busy;

        if (!busy)
        {
            SaveButton.IsEnabled = _singleResultPath is not null ||
                                   _files.Any(f => f.ResultPath is not null);
        }
        else
        {
            SaveButton.IsEnabled = false;
        }
    }

    // ---- Drop zone handlers ----

    /// <summary>
    /// Fires continuously while the user drags something over the zone.
    /// We set the cursor feedback: Copy (with a "+") for files/folders, None
    /// (the "no" cursor) for anything else like dragged text.
    /// </summary>
    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// Fires when the user RELEASES the drag over the zone. The dropped data
    /// is a string[] of paths (files AND folders mixed together). Each path is
    /// routed to the right handler based on whether it is a directory; AddFile/
    /// AddDirectory then filter by the active mode, so dropping a mixed folder
    /// in Images mode only picks up images.
    /// </summary>
    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return;
        }

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                AddDirectory(path);
            }
            else if (File.Exists(path))
            {
                AddFile(path);
            }
        }
    }

    /// <summary>Clicking the drop zone opens the file picker (a natural UX shortcut).</summary>
    private void DropZone_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        AddFilesButton_Click(sender, e);
    }

    // ---- Theme toggle ----

    /// <summary>
    /// Wired in XAML: Click="ThemeButton_Click". Flips the theme and updates
    /// the button label to say what clicking it AGAIN would do.
    /// </summary>
    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        ThemeManager.Toggle();          // swap all brushes + save the choice
        UpdateThemeButtonLabel();       // refresh "☾ Dark" / "☀ Light" text
    }

    /// <summary>
    /// The button always announces the OTHER theme:
    ///   currently dark -> shows "☀ Light" (click to go light)
    ///   currently light -> shows "☾ Dark"  (click to go dark)
    /// </summary>
    private void UpdateThemeButtonLabel()
    {
        ThemeButton.Content = ThemeManager.IsDark ? "☀ Light" : "☾ Dark";
    }

    /// <summary>
    /// Reads a named brush out of the application resources (where the current
    /// theme's palette lives). Used for the per-row status colors, which need
    /// to follow the theme. Falls back to gray if the key is somehow missing.
    /// </summary>
    private static Brush GetBrush(string key)
    {
        var brush = Application.Current.Resources[key] as Brush;
        return brush ?? new SolidColorBrush(Colors.Gray);
    }

    // ---- Browse buttons ----

    /// <summary>
    /// Opens a multi-select file picker. Title and filter depend on the active
    /// mode: image formats for Images -> PDF, PDFs for Compress/Merge (the merge
    /// dialog even spells out that selection order = merge order).
    /// </summary>
    private void AddFilesButton_Click(object sender, RoutedEventArgs e)
    {
        var (title, filter) = _mode switch
        {
            AppMode.ImagesToPdf => (
                "Select image files to convert to PDF",
                "Image files (*.png;*.jpg;*.jpeg;*.jfif;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.heic;*.heif)" +
                "|*.png;*.jpg;*.jpeg;*.jfif;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.heic;*.heif" +
                "|All files (*.*)|*.*"),
            AppMode.Merge => (
                "Select PDF files to merge (selection order = merge order)",
                "PDF files (*.pdf)|*.pdf|All files (*.*)|*.*"),
            _ => (
                "Select PDF file(s) to compress",
                "PDF files (*.pdf)|*.pdf|All files (*.*)|*.*")
        };

        var dlg = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            Multiselect = true,
            CheckFileExists = true
        };

        if (dlg.ShowDialog(this) == true)
        {
            foreach (var file in dlg.FileNames)
            {
                AddFile(file);
            }
        }
    }

    /// <summary>Opens a folder picker and adds every file the current mode accepts.</summary>
    private void AddFolderButton_Click(object sender, RoutedEventArgs e)
    {
        // OpenFolderDialog (Microsoft.Win32) is the modern Win11 folder picker.
        var dlg = new OpenFolderDialog
        {
            Title = _mode == AppMode.ImagesToPdf
                ? "Select a folder containing images"
                : "Select a folder containing PDFs",
            Multiselect = false
        };

        if (dlg.ShowDialog(this) == true)
        {
            AddDirectory(dlg.FolderName);
        }
    }

    /// <summary>Empties the list and resets the progress/status area.</summary>
    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        ResetList();
        StatusText.Text = _mode switch
        {
            AppMode.ImagesToPdf => "Drop images or add files to begin.",
            AppMode.Merge => "Drop two or more PDFs to merge them.",
            _ => "Drop PDFs or add files to begin."
        };
    }

    // ---- Compress ----

    /// <summary>
    /// The main action of Compress mode. Iterates the file list, compresses
    /// each PDF on a background thread, and reports per-file results.
    ///
    /// WHY async/await + Task.Run?
    ///   Ghostscript can take many seconds per file. If we ran it on the UI
    ///   thread, the window would freeze (and Windows would show the dreaded
    ///   "Not Responding"). Task.Run moves the heavy work off the UI thread;
    ///   await returns control to the UI thread between files so the progress
    ///   bar and status text keep updating.
    ///
    /// WHY async void?
    ///   Event handlers in WPF cannot return Task, so async void is the
    ///   standard pattern. The _busy flag protects against re-entry.
    /// </summary>
    private async void CompressButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return; // already running - ignore the click
        }

        if (_files.Count == 0)
        {
            StatusText.Text = "Add at least one PDF first.";
            return;
        }

        _busy = true;
        SetBusy(true);        // freeze every action button (incl. mode toggles)
        _singleResultPath = null; // old single-file results are stale now
        Progress.IsIndeterminate = false;

        // Map the combo box selection to the engine's preset enum.
        var preset = PresetBox.SelectedIndex switch
        {
            0 => CompressionPreset.Screen,
            2 => CompressionPreset.Printer,
            _ => CompressionPreset.Ebook // index 1 (and anything weird) = ebook
        };

        try
        {
            // One engine instance for the whole batch. (It resolves the native
            // DLL in its constructor; doing that once beats doing it per file.)
            var engine = new CompressionEngine();
            var total = _files.Count;
            var ready = 0; // count of files that came out SMALLER

            for (var i = 0; i < total; i++)
            {
                var item = _files[i];

                // Mark this row as in progress (blue text).
                item.Status = "Compressing…";
                item.StatusBrush = GetBrush("Brush.StatusBlue");

                // Progress bar: give a rough per-file position. (Starts at 5 so
                // the bar is visibly alive even for a single file.)
                Progress.Value = total == 1 ? 5 : i * 100.0 / total;
                StatusText.Text = $"Compressing {i + 1} of {total}: {item.Name}…";

                try
                {
                    // Output goes to our temp scratch folder with a predictable
                    // name: "<original-name>_compressed.pdf".
                    var output = Path.Combine(
                        _resultDir,
                        Path.GetFileNameWithoutExtension(item.Path) + "_compressed.pdf");

                    // Run the engine OFF the UI thread; await hands control back
                    // to the UI while it works.
                    var result = await Task.Run(() =>
                        engine.Compress(item.Path, output, new CompressionOptions { Preset = preset }));

                    if (result.IsSmaller)
                    {
                        // Win! Remember where the result lives for the Save step.
                        item.ResultPath = result.OutputPath;
                        item.Status = $"Done — {FileItem.FormatSize(result.OriginalSize)} → {FileItem.FormatSize(result.CompressedSize)} (−{result.SavingsPercent:0.0}%)";
                        item.StatusBrush = GetBrush("Brush.StatusGreen");
                        ready++;
                    }
                    else
                    {
                        // Ghostscript succeeded but the output wasn't smaller -
                        // the PDF was already optimized. Don't offer it for
                        // saving, and clean up the pointless temp output.
                        TryDelete(result.OutputPath);
                        item.Status = "No gain (already optimized)";
                        item.StatusBrush = GetBrush("Brush.StatusGray");
                    }
                }
                catch (Exception ex)
                {
                    // Per-file failure (corrupt PDF, password-protected, engine
                    // error...). Keep going with the next file - one bad file
                    // should not sink the whole batch.
                    item.Status = "Failed: " + ex.Message;
                    item.StatusBrush = GetBrush("Brush.StatusRed");
                }
            }

            Progress.Value = 100;
            SaveButton.IsEnabled = ready > 0; // (SetBusy(false) re-checks this too)
            StatusText.Text = ready > 0
                ? $"Compression complete — {ready} file(s) ready. Click Save to choose where to store them."
                : "Compression finished — nothing to save (all files were already optimized or failed).";
        }
        finally
        {
            // Runs whether the loop finished or threw. Always re-enable the UI;
            // SetBusy(false) lights Save iff at least one file shrank.
            _busy = false;
            SetBusy(false);
        }
    }

    // ---- Images -> PDF ----

    /// <summary>
    /// Action button of Images -> PDF mode. Converts every image in the list
    /// (in list order - each becomes one page) into a single PDF via
    /// ImageToPdfConverter, running on a background thread.
    ///
    /// Progress: the converter reports completed pages through an IProgress<int>,
    /// which Progress&lt;T&gt; marshals back onto the UI thread automatically
    /// (it captured the SynchronizationContext when constructed here).
    ///
    /// On success every row flips to "Page N ✓" and _singleResultPath points at
    /// the result so Save can store it. Any failure (unreadable image, disk
    /// error) aborts the whole run with a message in the status bar - the
    /// converter names the offending file.
    /// </summary>
    private async void CreatePdfButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        if (_files.Count == 0)
        {
            StatusText.Text = "Add at least one image first.";
            return;
        }

        _busy = true;
        SetBusy(true);
        Progress.IsIndeterminate = false;
        _singleResultPath = null;

        // Map the page-size combo to the converter's page-size enum
        // (index 0 = fit A4, 1 = fit Letter, 2 = actual pixel size @ 96 dpi).
        var options = new ImageToPdfOptions
        {
            PageSize = PageSizeBox.SelectedIndex switch
            {
                1 => ImagePdfPageSize.FitLetter,
                2 => ImagePdfPageSize.ActualPixels,
                _ => ImagePdfPageSize.FitA4
            }
        };

        foreach (var item in _files)
        {
            item.Status = "Converting…";
            item.StatusBrush = GetBrush("Brush.StatusBlue");
        }

        // One output PDF per run, predictably named with a timestamp so two
        // runs never collide in the scratch folder.
        var output = Path.Combine(_resultDir, $"images_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");

        try
        {
            var paths = _files.Select(f => f.Path).ToList();
            var total = paths.Count;
            var progress = new Progress<int>(done =>
            {
                Progress.Value = done * 100.0 / total;
                StatusText.Text = $"Converted {done} of {total} image(s)…";
            });

            var engine = new ImageToPdfConverter();

            // Background thread: WPF's BitmapDecoder works fine off the UI
            // thread (we decode with BitmapCacheOption.OnLoad, so no dispatcher
            // pumping is needed while the file is open).
            var result = await Task.Run(() => engine.Convert(paths, output, options, progress));

            _singleResultPath = result.OutputPath;

            // Mark every row with its page number - list order == page order.
            var page = 1;
            foreach (var item in _files)
            {
                item.Status = $"Page {page++} ✓";
                item.StatusBrush = GetBrush("Brush.StatusGreen");
            }

            Progress.Value = 100;
            SaveButton.IsEnabled = true;
            StatusText.Text =
                $"PDF created — {result.PageCount} page(s), {FileItem.FormatSize(result.OutputSize)}. Click Save…";
        }
        catch (Exception ex)
        {
            // The converter wraps per-image failures in ImageToPdfException with
            // the file name, so this message tells the user exactly what broke.
            foreach (var item in _files)
            {
                item.Status = "Not converted";
                item.StatusBrush = GetBrush("Brush.StatusGray");
            }
            StatusText.Text = "Conversion failed: " + ex.Message;
            Progress.Value = 0;
        }
        finally
        {
            _busy = false;
            SetBusy(false);
        }
    }

    // ---- Merge ----

    /// <summary>
    /// Action button of Merge mode. Concatenates at least two PDFs - in LIST
    /// ORDER, which the user can adjust with Move up/Move down - into one file
    /// via PdfMerger (Ghostscript pdfwrite), on a background thread.
    ///
    /// Ghostscript reports no fine-grained progress for merging, so the
    /// progress bar runs indeterminate (barber-pole) while it works.
    /// </summary>
    private async void MergeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        if (_files.Count < 2)
        {
            StatusText.Text = "Add at least two PDFs to merge.";
            return;
        }

        _busy = true;
        SetBusy(true);
        Progress.IsIndeterminate = true; // no per-page progress: barber-pole instead
        _singleResultPath = null;

        foreach (var item in _files)
        {
            item.Status = "Merging…";
            item.StatusBrush = GetBrush("Brush.StatusBlue");
        }

        var output = Path.Combine(_resultDir, $"merged_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");

        try
        {
            var paths = _files.Select(f => f.Path).ToList();
            var engine = new PdfMerger();
            var result = await Task.Run(() => engine.Merge(paths, output));

            _singleResultPath = result.OutputPath;

            foreach (var item in _files)
            {
                item.Status = "Merged ✓";
                item.StatusBrush = GetBrush("Brush.StatusGreen");
            }

            SaveButton.IsEnabled = true;
            StatusText.Text =
                $"Merged {result.InputCount} PDFs — {FileItem.FormatSize(result.OriginalSize)} → " +
                $"{FileItem.FormatSize(result.MergedSize)}. Click Save…";
        }
        catch (Exception ex)
        {
            foreach (var item in _files)
            {
                item.Status = "Not merged";
                item.StatusBrush = GetBrush("Brush.StatusGray");
            }
            StatusText.Text = "Merge failed: " + ex.Message;
        }
        finally
        {
            _busy = false;
            Progress.IsIndeterminate = false;
            SetBusy(false);
        }
    }

    // ---- Reordering (merge mode) ----

    /// <summary>
    /// Moves the selected row one position up. ObservableCollection.Move keeps
    /// the ListBox animated and - crucially for merging - changes the document
    /// order that PdfMerger will produce.
    /// </summary>
    private void MoveUpButton_Click(object sender, RoutedEventArgs e)
    {
        var index = FileList.SelectedIndex;
        if (index > 0)
        {
            _files.Move(index, index - 1);
            FileList.SelectedIndex = index - 1; // follow the row so the user can click repeatedly
        }
    }

    /// <summary>Moves the selected row one position down (see MoveUpButton_Click).</summary>
    private void MoveDownButton_Click(object sender, RoutedEventArgs e)
    {
        var index = FileList.SelectedIndex;
        if (index >= 0 && index < _files.Count - 1)
        {
            _files.Move(index, index + 1);
            FileList.SelectedIndex = index + 1;
        }
    }

    // ---- Save ----

    /// <summary>
    /// Copies results out of the temp scratch folder to a user-chosen
    /// destination.
    ///   - Images -> PDF / Merge: ONE file exists (_singleResultPath) -> a
    ///     single Save As dialog. (Handled first, before anything else.)
    ///   - Compress:
    ///       one file  -> standard Save As dialog (name + location).
    ///       many files -> folder picker, then a prompt to NAME a new subfolder
    ///                    which is created inside the chosen directory. All the
    ///                    compressed files land inside that subfolder.
    /// </summary>
    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        // Images -> PDF and Merge produce one file — just ask where to put it.
        if (_singleResultPath is not null)
        {
            var title = _mode == AppMode.Merge ? "Save merged PDF" : "Save PDF created from images";
            var dlg = new SaveFileDialog
            {
                Title = title,
                Filter = "PDF files (*.pdf)|*.pdf",
                FileName = System.IO.Path.GetFileName(_singleResultPath), // pre-fill the name
                AddExtension = true,
                DefaultExt = ".pdf"
            };

            if (dlg.ShowDialog(this) == true)
            {
                try
                {
                    // Copy (not move): the temp copy still lives in scratch, so
                    // the user can save the same result twice if they want.
                    File.Copy(_singleResultPath, dlg.FileName, overwrite: true);
                    StatusText.Text = $"Saved to: {dlg.FileName}";
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"Could not save the file:\n{ex.Message}", "PdfCompressor",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            return;
        }

        // ---- Compress results: one path per successful file ----
        var ready = _files.Where(f => f.ResultPath is not null).ToList();
        if (ready.Count == 0)
        {
            return;
        }

        if (ready.Count == 1)
        {
            // ---- Single file: classic Save As ----
            var item = ready[0];
            var dlg = new SaveFileDialog
            {
                Title = "Save compressed PDF",
                Filter = "PDF files (*.pdf)|*.pdf",
                FileName = System.IO.Path.GetFileName(item.ResultPath!), // pre-fill the name
                AddExtension = true,
                DefaultExt = ".pdf"
            };

            if (dlg.ShowDialog(this) == true)
            {
                try
                {
                    // Copy (not move): the temp copy still lives in scratch so
                    // the user can save the same result twice if they want.
                    File.Copy(item.ResultPath!, dlg.FileName, overwrite: true);
                    StatusText.Text = $"Saved to: {dlg.FileName}";
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"Could not save the file:\n{ex.Message}", "PdfCompressor",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
        else
        {
            // ---- Multiple files: pick a parent folder, then NAME a new one ----
            var dlg = new OpenFolderDialog
            {
                Title = "Choose where to create the output folder",
                Multiselect = false
            };

            if (dlg.ShowDialog(this) == true)
            {
                // Ask the user to name a NEW folder (validation loop inside).
                var folderName = PromptForFolderName(dlg.FolderName);
                if (folderName is null)
                {
                    return; // user cancelled the naming dialog - abort silently
                }

                var targetDir = Path.Combine(dlg.FolderName, folderName);
                Directory.CreateDirectory(targetDir); // make the new folder

                var saved = 0;
                foreach (var item in ready)
                {
                    try
                    {
                        File.Copy(
                            item.ResultPath!,
                            Path.Combine(targetDir, Path.GetFileName(item.ResultPath!)),
                            overwrite: true);
                        saved++;
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, $"Could not save {item.Name}:\n{ex.Message}", "PdfCompressor",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }

                StatusText.Text = saved > 0
                    ? $"Saved {saved} file(s) to: {targetDir}"
                    : "No files were saved.";
            }
        }
    }

    /// <summary>
    /// Asks the user to name a new folder that will be created inside
    /// <paramref name="parentDir"/>. Returns the validated name, or null if cancelled.
    ///
    /// Validation loop:
    ///   - empty name                    -> warn, ask again
    ///   - Windows-illegal characters    -> warn, ask again
    ///   - name already taken            -> warn, ask again
    ///
    /// IMPORTANT IMPLEMENTATION DETAIL: a fresh FolderNameDialog is created on
    /// EVERY loop iteration. WPF throws if you call ShowDialog() on a window
    /// that has already been shown and closed, so reusing one dialog instance
    /// would crash on the second invalid attempt.
    /// </summary>
    private string? PromptForFolderName(string parentDir)
    {
        while (true)
        {
            var dialog = new FolderNameDialog();
            if (dialog.ShowDialog() != true)
            {
                return null; // cancelled
            }

            var name = dialog.FolderName.Trim();

            if (name.Length == 0)
            {
                MessageBox.Show(this, "Folder name can't be empty.", "PdfCompressor",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                continue;
            }

            // Windows reserves these characters in folder names:
            //   \ / : * ? " < > |
            var invalid = Path.GetInvalidFileNameChars();
            if (name.IndexOfAny(invalid) >= 0)
            {
                MessageBox.Show(this,
                    "Folder name contains characters that aren't allowed in Windows folders.",
                    "PdfCompressor", MessageBoxButton.OK, MessageBoxImage.Warning);
                continue;
            }

            // Refuse to silently reuse an existing file/folder name - the user
            // asked for a NEW folder, so a collision means "pick something else".
            var target = Path.Combine(parentDir, name);
            if (Directory.Exists(target) || File.Exists(target))
            {
                MessageBox.Show(this,
                    $"A file or folder named \"{name}\" already exists in that location. Pick another name.",
                    "PdfCompressor", MessageBoxButton.OK, MessageBoxImage.Warning);
                continue;
            }

            return name; // all checks passed
        }
    }

    // ---- Cleanup ----

    /// <summary>
    /// Runs when the window closes. Deletes the temp scratch folder and every
    /// intermediate inside it (compressed, converted and merged PDFs alike).
    /// Best-effort: if a file is locked (e.g. open in a viewer), we skip
    /// cleanup rather than crash the exit.
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        try
        {
            if (Directory.Exists(_resultDir))
            {
                Directory.Delete(_resultDir, recursive: true);
            }
        }
        catch
        {
            // best effort — temp files will be cleaned by the OS eventually
        }

        base.OnClosed(e);
    }

    /// <summary>Deletes a file, ignoring any failure (used for temp outputs).</summary>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best effort
        }
    }
}
