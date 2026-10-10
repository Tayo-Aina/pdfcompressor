// =============================================================================
//  CompressionEngine.cs
// =============================================================================
//  This is the HEART of the app - the code that actually makes PDFs smaller.
//
//  WHAT IS GHOSTSCRIPT?
//  ----------------------------------------------------------------------------
//  Ghostscript is a mature open-source interpreter for PostScript and PDF. It
//  ships with a "pdfwrite" device that can READ a PDF and WRITE a new, re-
//  compressed one. This re-encoding pass is what shrinks the file: images are
//  downsampled (fewer pixels) and re-compressed (lower JPEG quality), duplicate
//  images are de-duplicated, and fonts are subsetted.
//
//  HOW WE CALL IT
//  ----------------------------------------------------------------------------
//  Two options exist:
//    1. Shell out to gswin64c.exe (a separate process) - clunky, needs an
//       installed Ghostscript, error handling is string-matching misery.
//    2. Use Ghostscript.NET - a managed wrapper that loads gsdll64.dll INTO our
//       own process and drives it through C# objects. Faster, no install needed,
//       exceptions instead of stderr scraping. WE USE THIS ONE.
//
//  The native DLL is found / extracted by NativeLibResolver (see that file) and
//  the actual Ghostscript command-line arguments are built in BuildArguments().
// =============================================================================

using System.Collections.Generic;
using System.IO;
using System.Text;
using Ghostscript.NET;
using Ghostscript.NET.Processor;

namespace PdfCompressor;

/// <summary>
/// User-selectable compression presets, mirroring Ghostscript's -dPDFSETTINGS.
/// Each preset trades off file size against visual quality by changing the
/// resolution at which embedded images are downsampled:
///   Screen  -> 72 dpi  (smallest files, lowest quality)
///   Ebook   -> 150 dpi (balanced - the recommended default)
///   Printer -> 300 dpi (near-original quality, modest savings)
/// </summary>
public enum CompressionPreset
{
    /// <summary>Aggressive: downsampled to 72 dpi. Smallest files, lowest quality.</summary>
    Screen,

    /// <summary>Balanced: downsampled to 150 dpi. Good default for most documents.</summary>
    Ebook,

    /// <summary>Light: downsampled to 300 dpi. Near-original quality, modest savings.</summary>
    Printer
}

/// <summary>
/// Options that control a single compression run. This is the "request" object
/// passed into CompressionEngine.Compress(). All fields are optional - the
/// defaults (Ebook preset, no overrides) are sensible for 99% of users.
/// </summary>
public sealed class CompressionOptions
{
    /// <summary>Which quality/size trade-off to use.</summary>
    public CompressionPreset Preset { get; set; } = CompressionPreset.Ebook;

    /// <summary>Optional explicit image resolution override (dpi); 0 = use the preset default.</summary>
    public int Dpi { get; set; }

    /// <summary>Optional JPEG quality 1-100; 0 = use the preset default.</summary>
    public int JpegQuality { get; set; }

    /// <summary>Password for encrypted PDFs; null when not needed.</summary>
    public string? Password { get; set; }
}

/// <summary>
/// Outcome of a compression run. Immutable (init-only properties) so callers
/// can safely pass it around and cache it - e.g. the UI stores these in each
/// file row so the Save step knows where the finished file lives.
/// </summary>
public sealed class CompressionResult
{
    public required string InputPath { get; init; }
    public required string OutputPath { get; init; }
    public long OriginalSize { get; init; }
    public long CompressedSize { get; init; }
    public string? Error { get; init; }

    /// <summary>true when the engine completed without an error.</summary>
    public bool Succeeded => Error is null;

    /// <summary>
    /// The whole point of the app: did we actually end up with a smaller file?
    /// The UI uses this to decide whether to offer the result for saving.
    /// </summary>
    public bool IsSmaller => CompressedSize < OriginalSize;

    /// <summary>Percentage of bytes saved, e.g. 23.4 means "23.4% smaller".</summary>
    public double SavingsPercent => OriginalSize <= 0 ? 0 : (1.0 - (double)CompressedSize / OriginalSize) * 100.0;
}

/// <summary>
/// Drives the Ghostscript pdfwrite device through the Ghostscript.NET managed wrapper
/// (in-process — no external Ghostscript installation required).
/// </summary>
public sealed class CompressionEngine
{
    /// <summary>Full path to the native gsdll64.dll we are going to load.</summary>
    private readonly string _nativeDllPath;

    /// <summary>
    /// Constructor. Resolving the native DLL is the one thing that can genuinely
    /// fail up front (missing/not embedded), so we do it here - an engine that
    /// cannot possibly work should fail fast at construction, not midway through
    /// a user's compress job.
    /// </summary>
    public CompressionEngine()
    {
        _nativeDllPath = NativeLibResolver.Resolve();
    }

    /// <summary>
    /// Compresses <paramref name="inputPath"/> into <paramref name="outputPath"/>.
    /// Throws on invalid input or engine failure; returns a result otherwise.
    ///
    /// NOTE: this is SYNCHRONOUS and can take a while on big PDFs. The UI never
    /// calls it on the UI thread - it wraps the call in Task.Run (see
    /// MainWindow.CompressButton_Click) so the window stays responsive.
    /// </summary>
    public CompressionResult Compress(string inputPath, string outputPath, CompressionOptions options)
    {
        // ---- Guard clause 1: basic argument sanity ----
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        options ??= new CompressionOptions(); // null options = all defaults

        // Normalize to full paths so every subsequent check and the final
        // result object deal with unambiguous, comparable paths.
        var fullInput = Path.GetFullPath(inputPath);
        var fullOutput = Path.GetFullPath(outputPath);

        // ---- Guard clause 2: input must exist ----
        if (!File.Exists(fullInput))
        {
            throw new FileNotFoundException($"Input file not found: {fullInput}");
        }

        // ---- Guard clause 3: never let input and output be the same file.
        // Ghostscript happily overwrites the input if asked - which would
        // destroy the user's original if something went wrong. We forbid it.
        if (string.Equals(fullInput, fullOutput, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Input and output paths must be different.");
        }

        // ---- Guard clause 4: empty file check (0-byte files crash Ghostscript) ----
        var originalSize = new FileInfo(fullInput).Length;
        if (originalSize == 0)
        {
            throw new InvalidDataException("The input file is empty (0 bytes).");
        }

        // ---- Guard clause 5: is it actually a PDF?
        // Every PDF starts with the 5 bytes "%PDF-". Checking the header is a
        // cheap, reliable way to give the user a friendly error instead of a
        // cryptic Ghostscript complaint for a random .txt renamed to .pdf.
        if (!LooksLikePdf(fullInput))
        {
            throw new InvalidDataException("The input file does not look like a PDF (missing %PDF- header).");
        }

        // ---- Build the Ghostscript command line and prepare stderr capture ----
        var args = BuildArguments(fullInput, fullOutput, options);
        var stderr = new StderrCollector(); // Ghostscript writes errors to stderr; we capture them

        try
        {
            // GhostscriptVersionInfo wraps the native DLL path.
            var version = new GhostscriptVersionInfo(_nativeDllPath);

            // GhostscriptProcessor is the workhorse from Ghostscript.NET. It is
            // IDisposable - the using guarantees the native library is released.
            using var processor = new GhostscriptProcessor(version);

            // Progress reporting hook: Ghostscript.NET raises Processing events
            // with the current page number. In console mode we render a simple
            // \r progress line; in the GUI this isn't used (the UI has its own
            // per-file progress bar).
            processor.Processing += (_, e) =>
            {
                if (e.TotalPages > 0 && !Console.IsOutputRedirected)
                {
                    var pct = (int)(e.CurrentPage * 100L / e.TotalPages);
                    Console.Write($"\rCompressing... page {e.CurrentPage}/{e.TotalPages} ({pct,3}%)");
                }
            };

            // Run! The first argument array is the full Ghostscript command
            // (device, settings, output file, input file); the second is the
            // object that receives stdout/stderr.
            processor.Process(args.ToArray(), stderr);
        }
        catch (GhostscriptLibraryNotInstalledException ex)
        {
            // Ghostscript.NET throws this if it cannot find/load the native DLL.
            throw new InvalidOperationException(
                "The Ghostscript engine could not be loaded. Re-publish the exe or set " +
                "PDFCOMPRESSOR_GS_DLL to a valid gsdll64.dll path.", ex);
        }
        catch (GhostscriptException ex)
        {
            // Any other engine-level failure. Ghostscript's own error text (on
            // stderr) is far more useful than the wrapper's generic message, so
            // we append it when we captured any.
            var detail = stderr.ToString().Trim();
            var message = string.IsNullOrEmpty(detail)
                ? ex.Message
                : $"{ex.Message} :: {detail}";
            throw new InvalidOperationException($"Ghostscript failed: {message}", ex);
        }
        finally
        {
            // Wipe the console progress line we may have written earlier.
            if (!Console.IsOutputRedirected)
            {
                Console.Write("\r" + new string(' ', 24) + "\r");
            }
        }

        // ---- Post-condition check: did Ghostscript actually produce output?
        // A silent no-op (e.g. malformed internal content) would otherwise look
        // like success. Fail loudly instead of handing back a missing file.
        if (!File.Exists(fullOutput) || new FileInfo(fullOutput).Length == 0)
        {
            var detail = stderr.ToString().Trim();
            throw new InvalidOperationException(
                $"Ghostscript produced no output. {(string.IsNullOrEmpty(detail) ? "" : "Engine said: " + detail)}");
        }

        // ---- All good - package the outcome. ----
        return new CompressionResult
        {
            InputPath = fullInput,
            OutputPath = fullOutput,
            OriginalSize = originalSize,
            CompressedSize = new FileInfo(fullOutput).Length,
        };
    }

    /// <summary>
    /// Cheap PDF sniff: reads the first 5 bytes and checks for the "%PDF-"
    /// magic header. Any IO problem is treated as "not a PDF" (return false).
    /// </summary>
    private static bool LooksLikePdf(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> header = stackalloc byte[5]; // stackalloc: no heap allocation for 5 bytes
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
    /// Builds the Ghostscript argument array. Each switch is a separate element so paths with
    /// spaces / non-ASCII characters never need shell quoting.
    ///
    /// IMPORTANT DESIGN NOTE: we are NOT invoking a shell. Ghostscript.NET passes
    /// this array straight to the native API, so we never need to worry about
    /// quoting, escaping, or injection - a file named `foo; rm -rf /`.pdf is
    /// perfectly safe. This is one of the big advantages of the in-process
    /// wrapper over shelling out.
    /// </summary>
    private static List<string> BuildArguments(string input, string output, CompressionOptions options)
    {
        // Every Ghostscript invocation starts with a common base set.
        var args = new List<string>
        {
            "-q",                       // quiet: suppress banner and most chatter
            "-dNOPAUSE",                // don't stop and wait for a keypress between pages
            "-dBATCH",                  // exit when done (paired with NOPAUSE for batch runs)
            "-dSAFER",                  // sandbox mode: deny file-system access to the PDF's
                                        // embedded code (defends against malicious documents)
            "-sDEVICE=pdfwrite",        // the re-encoder we use (PDF -> PDF)
            "-dCompatibilityLevel=1.5", // output targets PDF 1.5 (widely compatible)
        };

        // Map our friendly preset enum to Ghostscript's -dPDFSETTINGS selector.
        var presetName = options.Preset switch
        {
            CompressionPreset.Screen => "screen",
            CompressionPreset.Printer => "printer",
            _ => "ebook"
        };
        args.Add($"-dPDFSETTINGS=/{presetName}");

        // Optional explicit overrides (only applied when the user asked for them).
        // These override whatever the preset chose for image resolution.
        if (options.Dpi > 0)
        {
            args.Add("-dDownsampleColorImages=true");
            args.Add("-dDownsampleGrayImages=true");
            args.Add("-dDownsampleMonoImages=true");
            args.Add($"-dColorImageResolution={options.Dpi}");
            args.Add($"-dGrayImageResolution={options.Dpi}");
            args.Add($"-dMonoImageResolution={options.Dpi}");
        }

        if (options.JpegQuality is > 0 and <= 100)
        {
            args.Add($"-dJPEGQ={options.JpegQuality}");
        }

        // Quality-preserving flags: these keep the document looking as close to
        // the original as the preset allows.
        args.Add("-dColorImageDownsampleType=/Bicubic"); // bicubic = smoother downsampling
        args.Add("-dGrayImageDownsampleType=/Bicubic");
        args.Add("-dMonoImageDownsampleType=/Subsample");
        args.Add("-dEmbedAllFonts=true");      // keep fonts in the file (portable)
        args.Add("-dSubsetFonts=true");        // only embed the glyphs actually used (saves a lot)
        args.Add("-dDetectDuplicateImages=true"); // reuse identical images once (big win for scans)
        args.Add("-dAutoRotatePages=/None");   // don't rotate pages (keeps layout identical)
        args.Add("-dFastWebView=true");        // linearize for web/streaming

        if (!string.IsNullOrEmpty(options.Password))
        {
            args.Add($"-sPDFPassword={options.Password}"); // unlock encrypted PDFs
        }

        args.Add($"-sOutputFile={output}"); // where to write (a new file - input is never touched)
        args.Add(input);                    // the source file is the final argument
        return args;
    }

    /// <summary>
    /// Collects Ghostscript's stderr so failures can be reported verbatim.
    ///
    /// GhostscriptStdIO is the abstract class Ghostscript.NET uses to pump the
    /// engine's stdin/stdout/stderr through. We redirect:
    ///   stdin  -> always empty   (we never feed it input)
    ///   stdout -> ignored        (quiet mode; nothing useful there)
    ///   stderr -> captured       (the error text we surface to the user)
    /// </summary>
    private sealed class StderrCollector : GhostscriptStdIO
    {
        private readonly System.Text.StringBuilder _sb = new();

        // base(true, false, true) = handle stdin(true) and stderr(true), ignore
        // stdout(false). Matching our three overrides below.
        public StderrCollector() : base(true, false, true) { }

        public override void StdIn(out string input, int count) => input = string.Empty;

        public override void StdOut(string output) { /* quiet mode: ignore */ }

        public override void StdError(string error) => _sb.AppendLine(error);

        public override string ToString() => _sb.ToString();
    }
}
