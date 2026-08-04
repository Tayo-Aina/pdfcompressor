using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace PdfCompressor;

/// <summary>One row in the file list.</summary>
public sealed class FileItem : INotifyPropertyChanged
{
    private string _status = "Pending";
    private string _statusBrush = "#6B7280";

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

    public string StatusBrush
    {
        get => _statusBrush;
        set
        {
            if (_statusBrush != value)
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

/// <summary>Main window: drag &amp; drop / browse input, compress, then choose where to save.</summary>
public partial class MainWindow : Window
{
    private readonly ObservableCollection<FileItem> _files = new();
    private readonly string _resultDir;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        FileList.ItemsSource = _files;

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
        if (!path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_files.Any(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _files.Add(new FileItem(path));
        StatusText.Text = $"{_files.Count} file(s) added. Click Compress when ready.";
    }

    private void AddDirectory(string dir)
    {
        var pdfs = Directory.EnumerateFiles(dir, "*.pdf", SearchOption.TopDirectoryOnly)
                            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                            .ToList();
        foreach (var pdf in pdfs)
        {
            AddFile(pdf);
        }

        if (pdfs.Count == 0)
        {
            StatusText.Text = "No PDF files found in that folder.";
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

    // ---- Browse buttons ----

    private void AddFilesButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select PDF file(s) to compress",
            Filter = "PDF files (*.pdf)|*.pdf|All files (*.*)|*.*",
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
            Title = "Select a folder containing PDFs",
            Multiselect = false
        };

        if (dlg.ShowDialog(this) == true)
        {
            AddDirectory(dlg.FolderName);
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _files.Clear();
        SaveButton.IsEnabled = false;
        Progress.Value = 0;
        StatusText.Text = "Drop PDFs or add files to begin.";
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
        CompressButton.IsEnabled = false;
        SaveButton.IsEnabled = false;

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
                item.StatusBrush = "#2563EB";
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
                        item.StatusBrush = "#16A34A";
                        ready++;
                    }
                    else
                    {
                        TryDelete(result.OutputPath);
                        item.Status = "No gain (already optimized)";
                        item.StatusBrush = "#9CA3AF";
                    }
                }
                catch (Exception ex)
                {
                    item.Status = "Failed: " + ex.Message;
                    item.StatusBrush = "#DC2626";
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
            CompressButton.IsEnabled = true;
        }
    }

    // ---- Save ----

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
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
                Title = "Choose a folder to save the compressed PDFs",
                Multiselect = false
            };

            if (dlg.ShowDialog(this) == true)
            {
                var saved = 0;
                foreach (var item in ready)
                {
                    try
                    {
                        File.Copy(
                            item.ResultPath!,
                            System.IO.Path.Combine(dlg.FolderName, System.IO.Path.GetFileName(item.ResultPath!)),
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
                    ? $"Saved {saved} file(s) to: {dlg.FolderName}"
                    : "No files were saved.";
            }
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
