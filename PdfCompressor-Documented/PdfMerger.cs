// =============================================================================
//  PdfMerger.cs
// =============================================================================
//  MERGE: concatenates several PDFs into a single document.
//
//  HOW IT WORKS
//  ----------------------------------------------------------------------------
//  Merging PDFs properly (fonts, outlines, page trees...) is not something you
//  do by concatenating bytes - it needs a real PDF interpreter. We therefore
//  reuse the SAME Ghostscript engine the compressor uses (via Ghostscript.NET,
//  in-process, no separate installation): Ghostscript's "pdfwrite" device reads
//  every input file in the order given on the command line and writes one new
//  PDF containing all of their pages, in that order.
//
//  This mirrors CompressionEngine's structure on purpose:
//    - resolve the bundled native DLL in the constructor (NativeLibResolver),
//    - build the argument array as a List<string> (one element per switch, so
//      paths with spaces never need quoting),
//    - collect Ghostscript's stderr in a StderrCollector so failures can be
//      reported verbatim instead of as a generic "it failed".
//
//  The UI layer (MainWindow.MergeButton_Click) calls Merge() on a background
//  thread; the optional IProgress<int> hook reports per-page progress for
//  console-style use.
// =============================================================================

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
    /// <summary>Full path of the merged PDF that was written.</summary>
    public required string OutputPath { get; init; }

    /// <summary>How many input PDFs went in (2 or more).</summary>
    public int InputCount { get; init; }

    /// <summary>Sum of all input file sizes, for the "x → y" size report.</summary>
    public long OriginalSize { get; init; } // sum of all input files

    /// <summary>Size of the merged output file.</summary>
    public long MergedSize { get; init; }

    /// <summary>Percent the output is smaller than the inputs (0 when nothing shrank).</summary>
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
    /// <summary>Cached path of the bundled gsdll64.dll, resolved once in the constructor.</summary>
    private readonly string _nativeDllPath;

    public PdfMerger()
    {
        // Same lazy-but-once resolution as CompressionEngine: fail fast on the
        // FIRST merge attempt (not mid-batch) if the native engine is missing.
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

        // A "merge" with fewer than two documents is really a copy - refuse it
        // so the UI can show its "at least two PDFs" hint instead.
        if (inputPaths is null || inputPaths.Count < 2)
        {
            throw new ArgumentException("Merging requires at least two PDF files.", nameof(inputPaths));
        }

        var fullOutput = Path.GetFullPath(outputPath);
        var fullInputs = new List<string>(inputPaths.Count);
        long originalSize = 0;

        // ---- Validate every input up front ---------------------------------
        // Better to complain BEFORE starting Ghostscript than to have it fail
        // halfway through with a cryptic engine message.
        foreach (var path in inputPaths)
        {
            var full = Path.GetFullPath(path);
            if (!File.Exists(full))
            {
                throw new FileNotFoundException($"Input file not found: {full}");
            }
            // Never let an input be its own output - Ghostscript would truncate
            // the file it is supposed to read from.
            if (string.Equals(full, fullOutput, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The output path must differ from every input path.");
            }
            // Cheap sanity check: a real PDF starts with "%PDF-". This catches
            // a renamed .txt/.jpg that slipped past the UI filter.
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

            // Ghostscript reports page-by-page progress while pdfwrite copies
            // the inputs; surface it on the progress callback (and on the
            // console for the CLI-style harness, mirroring CompressionEngine).
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
            // The native DLL could not be loaded at all (missing/broken publish).
            throw new InvalidOperationException(
                "The Ghostscript engine could not be loaded. Re-publish the exe or set " +
                "PDFCOMPRESSOR_GS_DLL to a valid gsdll64.dll path.", ex);
        }
        catch (GhostscriptException ex)
        {
            // Engine ran but rejected the job (corrupt input, encryption...).
            // Append whatever it wrote to stderr so the user sees the real reason.
            var detail = stderr.ToString().Trim();
            var message = string.IsNullOrEmpty(detail) ? ex.Message : $"{ex.Message} :: {detail}";
            throw new InvalidOperationException($"Ghostscript failed: {message}", ex);
        }
        finally
        {
            // Erase the carriage-return progress line (console use only).
            if (!Console.IsOutputRedirected)
            {
                Console.Write("\r" + new string(' ', 48) + "\r");
            }
        }

        // Ghostscript sometimes exits "successfully" without writing anything
        // (e.g. all inputs rejected). Treat a missing/empty file as failure.
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

    /// <summary>
    /// True when the file's first five bytes are "%PDF-". Best-effort: any read
    /// problem simply counts as "not a PDF" and surfaces as a clear error.
    /// </summary>
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
    ///
    /// Argument order matters: -sOutputFile must be set BEFORE the input files,
    /// because everything after the switches is treated as an input document -
    /// and pdfwrite emits the inputs in the order it receives them, which is
    /// exactly the merge order the user arranged in the list.
    /// </summary>
    private static List<string> BuildArguments(
        IReadOnlyList<string> inputs, string output, MergeOptions options)
    {
        var args = new List<string>
        {
            "-q",            // quiet: no chatter on stdout (errors still hit stderr)
            "-dNOPAUSE",     // don't pause between pages
            "-dBATCH",       // exit when done instead of waiting for more input
            "-dSAFER",       // sandbox file access to the inputs/output
            "-sDEVICE=pdfwrite",         // the PDF-rewriting device (as opposed to png16m etc.)
            "-dCompatibilityLevel=1.5",
        };

        // Optional post-merge compression preset (not used by the UI today -
        // merging keeps the original quality - but supported for completeness).
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
        args.Add("-dAutoRotatePages=/None");   // never flip pages - some documents are landscape on purpose
        args.Add("-dFastWebView=true");        // linearize: fast web viewing

        if (!string.IsNullOrEmpty(options.Password))
        {
            args.Add($"-sPDFPassword={options.Password}");
        }

        // Output first, then every input in list order.
        args.Add($"-sOutputFile={output}");
        foreach (var input in inputs)
        {
            args.Add(input);
        }
        return args;
    }

    /// <summary>
    /// Collects Ghostscript's stderr so failures can be reported verbatim.
    /// Same pattern as CompressionEngine's collector: stdout is ignored (we run
    /// with -q), stderr is appended line by line and shown in exception messages.
    /// </summary>
    private sealed class StderrCollector : GhostscriptStdIO
    {
        private readonly StringBuilder _sb = new();

        /// <summary>true, false, true = capture stdin and stderr, discard stdout.</summary>
        public StderrCollector() : base(true, false, true) { }

        public override void StdIn(out string input, int count) => input = string.Empty;

        public override void StdOut(string output) { /* quiet mode: ignore */ }

        public override void StdError(string error) => _sb.AppendLine(error);

        public override string ToString() => _sb.ToString();
    }
}
