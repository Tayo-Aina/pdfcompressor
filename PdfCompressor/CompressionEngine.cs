using System.Collections.Generic;
using System.IO;
using System.Text;
using Ghostscript.NET;
using Ghostscript.NET.Processor;

namespace PdfCompressor;

/// <summary>User-selectable compression presets, mirroring Ghostscript's -dPDFSETTINGS.</summary>
public enum CompressionPreset
{
    /// <summary>Aggressive: downsampled to 72 dpi. Smallest files, lowest quality.</summary>
    Screen,

    /// <summary>Balanced: downsampled to 150 dpi. Good default for most documents.</summary>
    Ebook,

    /// <summary>Light: downsampled to 300 dpi. Near-original quality, modest savings.</summary>
    Printer
}

/// <summary>Options that control a single compression run.</summary>
public sealed class CompressionOptions
{
    public CompressionPreset Preset { get; set; } = CompressionPreset.Ebook;

    /// <summary>Optional explicit image resolution override (dpi); 0 = use the preset default.</summary>
    public int Dpi { get; set; }

    /// <summary>Optional JPEG quality 1-100; 0 = use the preset default.</summary>
    public int JpegQuality { get; set; }

    /// <summary>Password for encrypted PDFs; null when not needed.</summary>
    public string? Password { get; set; }
}

/// <summary>Outcome of a compression run.</summary>
public sealed class CompressionResult
{
    public required string InputPath { get; init; }
    public required string OutputPath { get; init; }
    public long OriginalSize { get; init; }
    public long CompressedSize { get; init; }
    public string? Error { get; init; }

    public bool Succeeded => Error is null;
    public bool IsSmaller => CompressedSize < OriginalSize;
    public double SavingsPercent => OriginalSize <= 0 ? 0 : (1.0 - (double)CompressedSize / OriginalSize) * 100.0;
}

/// <summary>
/// Drives the Ghostscript pdfwrite device through the Ghostscript.NET managed wrapper
/// (in-process — no external Ghostscript installation required).
/// </summary>
public sealed class CompressionEngine
{
    private readonly string _nativeDllPath;

    public CompressionEngine()
    {
        _nativeDllPath = NativeLibResolver.Resolve();
    }

    /// <summary>
    /// Compresses <paramref name="inputPath"/> into <paramref name="outputPath"/>.
    /// Throws on invalid input or engine failure; returns a result otherwise.
    /// </summary>
    public CompressionResult Compress(string inputPath, string outputPath, CompressionOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        options ??= new CompressionOptions();

        var fullInput = Path.GetFullPath(inputPath);
        var fullOutput = Path.GetFullPath(outputPath);

        if (!File.Exists(fullInput))
        {
            throw new FileNotFoundException($"Input file not found: {fullInput}");
        }

        if (string.Equals(fullInput, fullOutput, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Input and output paths must be different.");
        }

        var originalSize = new FileInfo(fullInput).Length;
        if (originalSize == 0)
        {
            throw new InvalidDataException("The input file is empty (0 bytes).");
        }

        if (!LooksLikePdf(fullInput))
        {
            throw new InvalidDataException("The input file does not look like a PDF (missing %PDF- header).");
        }

        var args = BuildArguments(fullInput, fullOutput, options);
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
                    Console.Write($"\rCompressing... page {e.CurrentPage}/{e.TotalPages} ({pct,3}%)");
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
            var message = string.IsNullOrEmpty(detail)
                ? ex.Message
                : $"{ex.Message} :: {detail}";
            throw new InvalidOperationException($"Ghostscript failed: {message}", ex);
        }
        finally
        {
            if (!Console.IsOutputRedirected)
            {
                Console.Write("\r" + new string(' ', 24) + "\r");
            }
        }

        if (!File.Exists(fullOutput) || new FileInfo(fullOutput).Length == 0)
        {
            var detail = stderr.ToString().Trim();
            throw new InvalidOperationException(
                $"Ghostscript produced no output. {(string.IsNullOrEmpty(detail) ? "" : "Engine said: " + detail)}");
        }

        return new CompressionResult
        {
            InputPath = fullInput,
            OutputPath = fullOutput,
            OriginalSize = originalSize,
            CompressedSize = new FileInfo(fullOutput).Length,
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
    /// Builds the Ghostscript argument array. Each switch is a separate element so paths with
    /// spaces / non-ASCII characters never need shell quoting.
    /// </summary>
    private static List<string> BuildArguments(string input, string output, CompressionOptions options)
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

        // Preset selector.
        var presetName = options.Preset switch
        {
            CompressionPreset.Screen => "screen",
            CompressionPreset.Printer => "printer",
            _ => "ebook"
        };
        args.Add($"-dPDFSETTINGS=/{presetName}");

        // Optional explicit overrides (only applied when the user asked for them).
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

        // Quality-preserving flags.
        args.Add("-dColorImageDownsampleType=/Bicubic");
        args.Add("-dGrayImageDownsampleType=/Bicubic");
        args.Add("-dMonoImageDownsampleType=/Subsample");
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
        args.Add(input);
        return args;
    }

    /// <summary>Collects Ghostscript's stderr so failures can be reported verbatim.</summary>
    private sealed class StderrCollector : GhostscriptStdIO
    {
        private readonly System.Text.StringBuilder _sb = new();

        public StderrCollector() : base(true, false, true) { }

        public override void StdIn(out string input, int count) => input = string.Empty;

        public override void StdOut(string output) { /* quiet mode: ignore */ }

        public override void StdError(string error) => _sb.AppendLine(error);

        public override string ToString() => _sb.ToString();
    }
}
