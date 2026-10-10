using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
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

    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path);
    public string Size => FormatSize(new FileInfo(Path).Length);
    public string? ResultPath { get; set; }

    public string Status
    {
        get => _status;
        set
        {
            if (_status != value)
            {
                _status = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
            }
        }
    }

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

    public event PropertyChangedEventHandler? PropertyChanged;

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

/// <summary>The three things the app can do with the files in the list.</summary>
public enum AppMode
{
    /// <summary>Shrink PDFs with Ghostscript (original feature).</summary>
    Compress,

    /// <summary>Turn image files into one multi-page PDF.</summary>
    ImagesToPdf,

    /// <summary>Concatenate several PDFs into one.</summary>
    Merge
}

/// <summary>Main window: drag &amp; drop / browse input, compress, then choose where to save.</summary>
public partial class MainWindow : Window
{
    /// <summary>Every image format the file pickers and drag &amp; drop will accept in Images → PDF mode.</summary>
    private static readonly string[] ImageExtensions =
    {
        ".png", ".jpg", ".jpeg", ".jfif", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif"
    };

    private readonly ObservableCollection<FileItem> _files = new();
    private readonly string _resultDir;
    private bool _busy;
    private AppMode _mode = AppMode.Compress;

    /// <summary>Set by the Images → PDF / Merge modes: the single PDF waiting to be saved.</summary>
    private string? _singleResultPath;

    public MainWindow()
    {
        InitializeComponent();
        UpdateThemeButtonLabel();
        FileList.ItemsSource = _files;
        ApplyModeUi();

        _resultDir = Path.Combine(Path.GetTempPath(), "PdfCompressorResults");
        Directory.CreateDirectory(_resultDir);

        // If the exe was launched with file/folder arguments (e.g. dragged onto the icon), preload them.
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

    private void AddFile(string path)
    {
        if (!IsAcceptedFile(path, _mode))
        {
            return;
        }

        if (_files.Any(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _files.Add(new FileItem(path));
        StatusText.Text = $"{_files.Count} {FileNoun()} added. {ActionNoun()} when ready.";
    }

    private void AddDirectory(string dir)
    {
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

    /// <summary>Whether <paramref name="path"/> may enter the list for the given mode.</summary>
    private static bool IsAcceptedFile(string path, AppMode mode)
    {
        if (mode == AppMode.ImagesToPdf)
        {
            var ext = System.IO.Path.GetExtension(path);
            return ImageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
        }
        return path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
    }

    private string FileNoun() => _mode == AppMode.ImagesToPdf
        ? (_files.Count == 1 ? "image" : "images")
        : (_files.Count == 1 ? "PDF" : "PDFs");

    private string ActionNoun() => _mode switch
    {
        AppMode.ImagesToPdf => "Click Create PDF",
        AppMode.Merge => "Click Merge",
        _ => "Click Compress"
    };

    // ---- Mode switching ----

    private void ModeButton_Checked(object sender, RoutedEventArgs e)
    {
        // The first Checked event fires while InitializeComponent is still parsing the XAML,
        // before the sibling toggles have been assigned — the constructor calls ApplyModeUi()
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
        CompressModeButton.IsChecked = button == CompressModeButton;
        ImagesModeButton.IsChecked = button == ImagesModeButton;
        MergeModeButton.IsChecked = button == MergeModeButton;

        SetMode(mode);
    }

    /// <summary>A mode toggle can never end up unchecked — clicking the active one keeps it on.</summary>
    private void ModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton button && button.IsChecked != true)
        {
            button.IsChecked = true;
        }
    }

    private void SetMode(AppMode mode)
    {
        if (_busy || _mode == mode)
        {
            return;
        }

        _mode = mode;

        // Different modes accept different files, so the previous list is not usable — start fresh.
        ResetList();
        ApplyModeUi();
    }

    /// <summary>Shows/hides everything that depends on the active mode.</summary>
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
        MoveUpButton.Visibility = isMerge ? Visibility.Visible : Visibility.Collapsed;
        MoveDownButton.Visibility = isMerge ? Visibility.Visible : Visibility.Collapsed;

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

    private void ResetList()
    {
        _files.Clear();
        _singleResultPath = null;
        Progress.Value = 0;
        Progress.IsIndeterminate = false;
        SaveButton.IsEnabled = false;
    }

    /// <summary>Disables every action that would interfere with a running job.</summary>
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

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

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

    private void DropZone_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        AddFilesButton_Click(sender, e);
    }

    // ---- Theme toggle ----

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        ThemeManager.Toggle();
        UpdateThemeButtonLabel();
    }

    private void UpdateThemeButtonLabel()
    {
        ThemeButton.Content = ThemeManager.IsDark ? "☀ Light" : "☾ Dark";
    }

    /// <summary>Looks up a brush from Application resources, or falls back to a safe gray.</summary>
    private static Brush GetBrush(string key)
    {
        var brush = Application.Current.Resources[key] as Brush;
        return brush ?? new SolidColorBrush(Colors.Gray);
    }

    // ---- Browse buttons ----

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

    private void AddFolderButton_Click(object sender, RoutedEventArgs e)
    {
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

    private async void CompressButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        if (_files.Count == 0)
        {
            StatusText.Text = "Add at least one PDF first.";
            return;
        }

        _busy = true;
        SetBusy(true);
        _singleResultPath = null;
        Progress.IsIndeterminate = false;

        var preset = PresetBox.SelectedIndex switch
        {
            0 => CompressionPreset.Screen,
            2 => CompressionPreset.Printer,
            _ => CompressionPreset.Ebook
        };

        try
        {
            var engine = new CompressionEngine();
            var total = _files.Count;
            var ready = 0;

            for (var i = 0; i < total; i++)
            {
                var item = _files[i];
                item.Status = "Compressing…";
                item.StatusBrush = GetBrush("Brush.StatusBlue");
                Progress.Value = total == 1 ? 5 : i * 100.0 / total;
                StatusText.Text = $"Compressing {i + 1} of {total}: {item.Name}…";

                try
                {
                    var output = Path.Combine(
                        _resultDir,
                        Path.GetFileNameWithoutExtension(item.Path) + "_compressed.pdf");

                    var result = await Task.Run(() =>
                        engine.Compress(item.Path, output, new CompressionOptions { Preset = preset }));

                    if (result.IsSmaller)
                    {
                        item.ResultPath = result.OutputPath;
                        item.Status = $"Done — {FileItem.FormatSize(result.OriginalSize)} → {FileItem.FormatSize(result.CompressedSize)} (−{result.SavingsPercent:0.0}%)";
                        item.StatusBrush = GetBrush("Brush.StatusGreen");
                        ready++;
                    }
                    else
                    {
                        TryDelete(result.OutputPath);
                        item.Status = "No gain (already optimized)";
                        item.StatusBrush = GetBrush("Brush.StatusGray");
                    }
                }
                catch (Exception ex)
                {
                    item.Status = "Failed: " + ex.Message;
                    item.StatusBrush = GetBrush("Brush.StatusRed");
                }
            }

            Progress.Value = 100;
            SaveButton.IsEnabled = ready > 0;
            StatusText.Text = ready > 0
                ? $"Compression complete — {ready} file(s) ready. Click Save to choose where to store them."
                : "Compression finished — nothing to save (all files were already optimized or failed).";
        }
        finally
        {
            _busy = false;
            SetBusy(false);
        }
    }

    // ---- Images → PDF ----

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
            var result = await Task.Run(() => engine.Convert(paths, output, options, progress));

            _singleResultPath = result.OutputPath;

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
        Progress.IsIndeterminate = true;
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

    private void MoveUpButton_Click(object sender, RoutedEventArgs e)
    {
        var index = FileList.SelectedIndex;
        if (index > 0)
        {
            _files.Move(index, index - 1);
            FileList.SelectedIndex = index - 1;
        }
    }

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

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        // Images → PDF and Merge produce one file — just ask where to put it.
        if (_singleResultPath is not null)
        {
            var title = _mode == AppMode.Merge ? "Save merged PDF" : "Save PDF created from images";
            var dlg = new SaveFileDialog
            {
                Title = title,
                Filter = "PDF files (*.pdf)|*.pdf",
                FileName = System.IO.Path.GetFileName(_singleResultPath),
                AddExtension = true,
                DefaultExt = ".pdf"
            };

            if (dlg.ShowDialog(this) == true)
            {
                try
                {
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

        var ready = _files.Where(f => f.ResultPath is not null).ToList();
        if (ready.Count == 0)
        {
            return;
        }

        if (ready.Count == 1)
        {
            var item = ready[0];
            var dlg = new SaveFileDialog
            {
                Title = "Save compressed PDF",
                Filter = "PDF files (*.pdf)|*.pdf",
                FileName = System.IO.Path.GetFileName(item.ResultPath!),
                AddExtension = true,
                DefaultExt = ".pdf"
            };

            if (dlg.ShowDialog(this) == true)
            {
                try
                {
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
            var dlg = new OpenFolderDialog
            {
                Title = "Choose where to create the output folder",
                Multiselect = false
            };

            if (dlg.ShowDialog(this) == true)
            {
                var folderName = PromptForFolderName(dlg.FolderName);
                if (folderName is null)
                {
                    return; // cancelled
                }

                var targetDir = Path.Combine(dlg.FolderName, folderName);
                Directory.CreateDirectory(targetDir);

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
    /// </summary>
    private string? PromptForFolderName(string parentDir)
    {
        while (true)
        {
            var dialog = new FolderNameDialog();
            if (dialog.ShowDialog() != true)
            {
                return null;
            }

            var name = dialog.FolderName.Trim();

            if (name.Length == 0)
            {
                MessageBox.Show(this, "Folder name can't be empty.", "PdfCompressor",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                continue;
            }

            var invalid = Path.GetInvalidFileNameChars();
            if (name.IndexOfAny(invalid) >= 0)
            {
                MessageBox.Show(this,
                    "Folder name contains characters that aren't allowed in Windows folders.",
                    "PdfCompressor", MessageBoxButton.OK, MessageBoxImage.Warning);
                continue;
            }

            var target = Path.Combine(parentDir, name);
            if (Directory.Exists(target) || File.Exists(target))
            {
                MessageBox.Show(this,
                    $"A file or folder named \"{name}\" already exists in that location. Pick another name.",
                    "PdfCompressor", MessageBoxButton.OK, MessageBoxImage.Warning);
                continue;
            }

            return name;
        }
    }

    // ---- Cleanup ----

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
