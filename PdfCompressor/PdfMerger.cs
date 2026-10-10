using System.Collections.Generic;
using System.IO;
using System.Text;
using Ghostscript.NET;
using Ghostscript.NET.Processor;

namespace PdfCompressor;

/// <summary>Options for a merge run.</summary>
public sealed class MergeOptions
{
    /// <summary>Password used when any of the inputs is encrypted; null when not needed.</summary>
    public string? Password { get; set; }

    /// <summary>
    /// When set, the merged result is additionally re-compressed with this preset
    /// (uses Ghostscript's -dPDFSETTINGS). Null = keep the original quality untouched.
    /// </summary>
    public CompressionPreset? Preset { get; set; }
}

/// <summary>Outcome of a merge run.</summary>
public sealed class MergeResult
{
    public required string OutputPath { get; init; }
    public int InputCount { get; init; }
    public long OriginalSize { get; init; } // sum of all input files
    public long MergedSize { get; init; }

    public double SavingsPercent =>
        OriginalSize <= 0 ? 0 : (1.0 - (double)MergedSize / OriginalSize) * 100.0;
}

/// <summary>
/// Concatenates several PDFs into one, again driving Ghostscript's pdfwrite device
/// in-process through Ghostscript.NET (same engine the compressor uses — no external
/// installation required). Page order follows the order of <paramref name="inputPaths"/>.
/// </summary>
public sealed class PdfMerger
{
    private readonly string _nativeDllPath;

    public PdfMerger()
    {
        _nativeDllPath = NativeLibResolver.Resolve();
    }

    /// <summary>
    /// Merges at least two PDFs into <paramref name="outputPath"/>.
    /// Throws on invalid input or engine failure; returns a result otherwise.
    /// </summary>
    public MergeResult Merge(
        IReadOnlyList<string> inputPaths,
        string outputPath,
        MergeOptions? options = null,
        IProgress<int>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        options ??= new MergeOptions();

        if (inputPaths is null || inputPaths.Count < 2)
        {
            throw new ArgumentException("Merging requires at least two PDF files.", nameof(inputPaths));
        }

        var fullOutput = Path.GetFullPath(outputPath);
        var fullInputs = new List<string>(inputPaths.Count);
        long originalSize = 0;

        foreach (var path in inputPaths)
        {
            var full = Path.GetFullPath(path);
            if (!File.Exists(full))
            {
                throw new FileNotFoundException($"Input file not found: {full}");
            }
            if (string.Equals(full, fullOutput, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The output path must differ from every input path.");
            }
            if (!LooksLikePdf(full))
            {
                throw new InvalidDataException(
                    $"\"{Path.GetFileName(full)}\" does not look like a PDF (missing %PDF- header).");
            }

            originalSize += new FileInfo(full).Length;
            fullInputs.Add(full);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
        var args = BuildArguments(fullInputs, fullOutput, options);
        var stderr = new StderrCollector();

        try
        {
            var version = new GhostscriptVersionInfo(_nativeDllPath);
            using var processor = new GhostscriptProcessor(version);

            processor.Processing += (_, e) =>
            {
                if (e.TotalPages > 0 && !Console.IsOutputRedirected)
                {
                    var pct = (int)(e.CurrentPage * 100L / e.TotalPages);
                    Console.Write($"\rMerging... page {e.CurrentPage}/{e.TotalPages} ({pct,3}%)");
                    progress?.Report(e.CurrentPage);
                }
            };

            processor.Process(args.ToArray(), stderr);
        }
        catch (GhostscriptLibraryNotInstalledException ex)
        {
            throw new InvalidOperationException(
                "The Ghostscript engine could not be loaded. Re-publish the exe or set " +
                "PDFCOMPRESSOR_GS_DLL to a valid gsdll64.dll path.", ex);
        }
        catch (GhostscriptException ex)
        {
            var detail = stderr.ToString().Trim();
            var message = string.IsNullOrEmpty(detail) ? ex.Message : $"{ex.Message} :: {detail}";
            throw new InvalidOperationException($"Ghostscript failed: {message}", ex);
        }
        finally
        {
            if (!Console.IsOutputRedirected)
            {
                Console.Write("\r" + new string(' ', 48) + "\r");
            }
        }

        if (!File.Exists(fullOutput) || new FileInfo(fullOutput).Length == 0)
        {
            var detail = stderr.ToString().Trim();
            throw new InvalidOperationException(
                $"Ghostscript produced no output. {(string.IsNullOrEmpty(detail) ? "" : "Engine said: " + detail)}");
        }

        return new MergeResult
        {
            OutputPath = fullOutput,
            InputCount = fullInputs.Count,
            OriginalSize = originalSize,
            MergedSize = new FileInfo(fullOutput).Length,
        };
    }

    private static bool LooksLikePdf(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> header = stackalloc byte[5];
            var read = fs.Read(header);
            return read >= 5 &&
                   header[0] == (byte)'%' && header[1] == (byte)'P' &&
                   header[2] == (byte)'D' && header[3] == (byte)'F' && header[4] == (byte)'-';
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Builds the Ghostscript argument array: pdfwrite with every input file in order.
    /// Each switch is a separate element so paths with spaces never need shell quoting.
    /// </summary>
    private static List<string> BuildArguments(
        IReadOnlyList<string> inputs, string output, MergeOptions options)
    {
        var args = new List<string>
        {
            "-q",
            "-dNOPAUSE",
            "-dBATCH",
            "-dSAFER",
            "-sDEVICE=pdfwrite",
            "-dCompatibilityLevel=1.5",
        };

        if (options.Preset is { } preset)
        {
            var presetName = preset switch
            {
                CompressionPreset.Screen => "screen",
                CompressionPreset.Printer => "printer",
                _ => "ebook"
            };
            args.Add($"-dPDFSETTINGS=/{presetName}");
        }

        // Quality-preserving flags (mirrors the compression engine so a merged file
        // behaves exactly like a compressed one).
        args.Add("-dEmbedAllFonts=true");
        args.Add("-dSubsetFonts=true");
        args.Add("-dDetectDuplicateImages=true");
        args.Add("-dAutoRotatePages=/None");
        args.Add("-dFastWebView=true");

        if (!string.IsNullOrEmpty(options.Password))
        {
            args.Add($"-sPDFPassword={options.Password}");
        }

        args.Add($"-sOutputFile={output}");
        foreach (var input in inputs)
        {
            args.Add(input);
        }
        return args;
    }

    /// <summary>Collects Ghostscript's stderr so failures can be reported verbatim.</summary>
    private sealed class StderrCollector : GhostscriptStdIO
    {
        private readonly StringBuilder _sb = new();

        public StderrCollector() : base(true, false, true) { }

        public override void StdIn(out string input, int count) => input = string.Empty;

        public override void StdOut(string output) { /* quiet mode: ignore */ }

        public override void StdError(string error) => _sb.AppendLine(error);

        public override string ToString() => _sb.ToString();
    }
}
