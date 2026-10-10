using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PdfCompressor;

/// <summary>How the generated PDF pages are sized relative to each image.</summary>
public enum ImagePdfPageSize
{
    /// <summary>Image is scaled down (never up) to fit an A4 page; wide images get a landscape page.</summary>
    FitA4,

    /// <summary>Same as FitA4 but with US Letter dimensions.</summary>
    FitLetter,

    /// <summary>Page size equals the image pixel size at the configured DPI — no scaling at all.</summary>
    ActualPixels
}

/// <summary>Options for a picture → PDF conversion run.</summary>
public sealed class ImageToPdfOptions
{
    public ImagePdfPageSize PageSize { get; set; } = ImagePdfPageSize.FitA4;

    /// <summary>Only used by <see cref="ImagePdfPageSize.ActualPixels"/>; 96 = standard monitor dpi.</summary>
    public int Dpi { get; set; } = 96;
}

/// <summary>Outcome of a picture → PDF conversion run.</summary>
public sealed class ImageToPdfResult
{
    public required string OutputPath { get; init; }

    /// <summary>Number of pages written (one per input image).</summary>
    public int PageCount { get; init; }

    public long OutputSize { get; init; }
    public long TotalInputSize { get; init; }
}

/// <summary>
/// Converts images (PNG, JPEG, BMP, GIF, TIFF, WebP, HEIC … — anything Windows/WPF can decode)
/// into a PDF with one page per image.
///
/// Strategy per image:
///   • Regular JPEGs without rotation metadata are embedded byte-for-byte (DCTDecode) — no
///     re-encoding, therefore no quality loss and no extra CPU cost.
///   • Everything else is decoded to raw pixels and stored losslessly with FlateDecode
///     (zlib). Transparency is preserved through a PDF soft mask (SMask).
///   • JPEGs carrying an EXIF orientation tag are decoded, rotated/mirrored to upright,
///     then re-encoded once at JPEG quality 95 so phone photos stay small *and* upright.
/// The class writes a complete, self-contained PDF file itself — no external tools involved.
/// </summary>
public sealed class ImageToPdfConverter
{
    /// <summary>
    /// Converts every image in <paramref name="imagePaths"/> (kept in the given order) into a
    /// single PDF at <paramref name="outputPath"/>. Throws on invalid input or the first
    /// unreadable image; <paramref name="progress"/> reports completed pages (1-based count).
    /// </summary>
    public ImageToPdfResult Convert(
        IReadOnlyList<string> imagePaths,
        string outputPath,
        ImageToPdfOptions? options = null,
        IProgress<int>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (imagePaths is null || imagePaths.Count == 0)
        {
            throw new ArgumentException("At least one image is required.", nameof(imagePaths));
        }

        options ??= new ImageToPdfOptions();
        var fullOutput = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);

        var pages = new List<PageData>(imagePaths.Count);
        long totalInput = 0;

        for (var i = 0; i < imagePaths.Count; i++)
        {
            var path = imagePaths[i];
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Image not found: {path}");
            }

            try
            {
                pages.Add(LoadPage(path, options));
            }
            catch (Exception ex) when (ex is not ImageToPdfException)
            {
                throw new ImageToPdfException(
                    $"Could not read image \"{Path.GetFileName(path)}\": {ex.Message}", ex);
            }

            totalInput += new FileInfo(path).Length;
            progress?.Report(i + 1);
        }

        WritePdf(pages, fullOutput, DateTime.Now);

        return new ImageToPdfResult
        {
            OutputPath = fullOutput,
            PageCount = pages.Count,
            OutputSize = new FileInfo(fullOutput).Length,
            TotalInputSize = totalInput,
        };
    }

    // ---- Per-image loading -------------------------------------------------

    /// <summary>Page layout numbers, computed once per image.</summary>
    private sealed record Layout(double PageWidth, double PageHeight,
                                 double DrawWidth, double DrawHeight,
                                 double OffsetX, double OffsetY);

    /// <summary>Everything needed to write one page: layout numbers plus the compressed image streams.</summary>
    private sealed class PageData
    {
        public required int PixelWidth { get; init; }
        public required int PixelHeight { get; init; }
        public required Layout Layout { get; init; }

        /// <summary>Compressed bytes of the image XObject stream.</summary>
        public required byte[] ImageStream { get; init; }

        /// <summary>"FlateDecode" or "DCTDecode" (raw JPEG passthrough).</summary>
        public required string Filter { get; init; }

        /// <summary>"/DeviceRGB" or "/DeviceGray".</summary>
        public required string ColorSpace { get; init; }

        /// <summary>Compressed grayscale alpha channel for transparency, or null when fully opaque.</summary>
        public byte[]? AlphaStream { get; init; }
    }

    private static PageData LoadPage(string path, ImageToPdfOptions options)
    {
        var fileBytes = File.ReadAllBytes(path);

        // Fast, lossless path: plain JPEG, already upright → embed the file bytes as-is.
        if (IsJpeg(fileBytes))
        {
            var orientation = ReadJpegOrientation(fileBytes);
            if (orientation == 1 &&
                TryReadJpegHeader(fileBytes, out var jw, out var jh, out var components) &&
                components is 1 or 3)
            {
                var layout = ComputeLayout(jw, jh, options);
                return new PageData
                {
                    PixelWidth = jw,
                    PixelHeight = jh,
                    ImageStream = fileBytes,
                    Filter = "DCTDecode",
                    ColorSpace = components == 1 ? "/DeviceGray" : "/DeviceRGB",
                    Layout = layout,
                };
            }

            // JPEG that needs rotation: decode → straighten → re-encode once at high quality.
            return LoadDecoded(path, fileBytes, orientation, options, reencodeAsJpeg: true);
        }

        // Everything else: decode and store losslessly.
        return LoadDecoded(path, fileBytes, orientation: 1, options, reencodeAsJpeg: false);
    }

    private static PageData LoadDecoded(
        string path, byte[] fileBytes, int orientation, ImageToPdfOptions options, bool reencodeAsJpeg)
    {
        BitmapFrame frame;
        using (var ms = new MemoryStream(fileBytes, writable: false))
        {
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
            {
                throw new InvalidDataException("the file contains no image frames");
            }
            frame = decoder.Frames[0]; // animated GIF / multi-page TIFF: first frame only
        }

        var width = frame.PixelWidth;
        var height = frame.PixelHeight;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException("the image has zero width or height");
        }

        // Normalise everything to 32-bit BGRA for uniform processing.
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var bgra = new byte[checked(width * height * 4)];
        converted.CopyPixels(bgra, width * 4, 0);

        // Apply EXIF orientation so photos taken with a phone come out upright.
        if (orientation is >= 2 and <= 8)
        {
            bgra = RemapOrientation(bgra, width, height, orientation, out width, out height);
        }

        // An upright-but-was-rotated JPEG: re-encode at high quality instead of a huge
        // lossless dump — phone photos are large, and JPEG-in-PDF stays compact.
        if (reencodeAsJpeg)
        {
            var wb = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            wb.WritePixels(new Int32Rect(0, 0, width, height), bgra, width * 4, 0);

            var encoder = new JpegBitmapEncoder { QualityLevel = 95 };
            encoder.Frames.Add(BitmapFrame.Create(wb));
            using var jpegStream = new MemoryStream();
            encoder.Save(jpegStream);

            return new PageData
            {
                PixelWidth = width,
                PixelHeight = height,
                ImageStream = jpegStream.ToArray(),
                Filter = "DCTDecode",
                ColorSpace = "/DeviceRGB",
                Layout = ComputeLayout(width, height, options),
            };
        }

        // Split BGRA into an RGB plane (always) and an alpha plane (only when needed).
        var pixelCount = width * height;
        var rgb = new byte[pixelCount * 3];
        byte[]? alpha = null;
        var hasAlpha = false;

        for (int i = 0, s = 0, d = 0; i < pixelCount; i++, s += 4, d += 3)
        {
            var a = bgra[s + 3];
            rgb[d] = bgra[s + 2];     // R
            rgb[d + 1] = bgra[s + 1]; // G
            rgb[d + 2] = bgra[s];     // B
            if (a != 255)
            {
                hasAlpha = true;
            }
        }

        if (hasAlpha)
        {
            alpha = new byte[pixelCount];
            for (int i = 0, s = 3; i < pixelCount; i++, s += 4)
            {
                alpha[i] = bgra[s];
            }
        }

        return new PageData
        {
            PixelWidth = width,
            PixelHeight = height,
            ImageStream = ZlibCompress(rgb),
            Filter = "FlateDecode",
            ColorSpace = "/DeviceRGB",
            AlphaStream = alpha is null ? null : ZlibCompress(alpha),
            Layout = ComputeLayout(width, height, options),
        };
    }

    /// <summary>Reorders pixels for EXIF orientations 2–8 (1 = upright, handled by the caller).</summary>
    private static byte[] RemapOrientation(
        byte[] src, int w, int h, int orientation, out int outW, out int outH)
    {
        var swap = orientation >= 5; // orientations 5–8 are quarter-turns: dimensions swap
        outW = swap ? h : w;
        outH = swap ? w : h;

        var dst = new byte[checked(outW * outH * 4)];

        for (var y = 0; y < outH; y++)
        {
            for (var x = 0; x < outW; x++)
            {
                int sx, sy;
                switch (orientation)
                {
                    case 2: sx = w - 1 - x; sy = y; break;            // mirror horizontal
                    case 3: sx = w - 1 - x; sy = h - 1 - y; break;    // rotate 180°
                    case 4: sx = x; sy = h - 1 - y; break;            // mirror vertical
                    case 5: sx = y; sy = x; break;                    // transpose
                    case 6: sx = y; sy = h - 1 - x; break;            // rotate 90° CW
                    case 7: sx = w - 1 - y; sy = h - 1 - x; break;    // transverse
                    default: sx = w - 1 - y; sy = x; break;           // 8: rotate 90° CCW
                }

                var si = (sy * w + sx) * 4;
                var di = (y * outW + x) * 4;
                dst[di] = src[si];
                dst[di + 1] = src[si + 1];
                dst[di + 2] = src[si + 2];
                dst[di + 3] = src[si + 3];
            }
        }

        return dst;
    }

    // ---- Page layout -------------------------------------------------------

    private static Layout ComputeLayout(int pixelW, int pixelH, ImageToPdfOptions options)
    {
        if (options.PageSize == ImagePdfPageSize.ActualPixels)
        {
            var dpi = options.Dpi > 0 ? options.Dpi : 96;
            var scale = 72.0 / dpi; // 1 image pixel = `scale` PDF points
            var w = pixelW * scale;
            var h = pixelH * scale;
            return new Layout(w, h, w, h, 0, 0);
        }

        double pageW, pageH;
        if (options.PageSize == ImagePdfPageSize.FitLetter)
        {
            pageW = 612;   // 8.5 in
            pageH = 792;   // 11 in
        }
        else
        {
            pageW = 595.28; // A4, 210 mm
            pageH = 841.89; // A4, 297 mm
        }

        if (pixelW > pixelH)
        {
            (pageW, pageH) = (pageH, pageW); // wide image → landscape page
        }

        // Scale to fit ("contain") and centre; never upscale.
        var scaleF = Math.Min(pageW / pixelW, pageH / pixelH);
        var drawW = pixelW * scaleF;
        var drawH = pixelH * scaleF;
        return new Layout(pageW, pageH, drawW, drawH, (pageW - drawW) / 2, (pageH - drawH) / 2);
    }

    // ---- PDF writing -------------------------------------------------------

    private static void WritePdf(IReadOnlyList<PageData> pages, string outputPath, DateTime now)
    {
        var pageCount = pages.Count;

        // Object numbering: 1 = Catalog, 2 = Pages, then per page (page, contents, image[, smask]),
        // and finally the Info dictionary.
        var pageObjNums = new int[pageCount];
        var cursor = 3;
        for (var i = 0; i < pageCount; i++)
        {
            pageObjNums[i] = cursor;
            cursor += pages[i].AlphaStream is null ? 3 : 4;
        }
        var infoObjNum = cursor;
        var totalObjects = cursor; // objects are numbered 1..totalObjects

        var bodies = new byte[totalObjects + 1][];

        bodies[1] = Ascii("<< /Type /Catalog /Pages 2 0 R >>");

        var kids = new StringBuilder();
        foreach (var n in pageObjNums)
        {
            kids.Append(n).Append(" 0 R ");
        }
        bodies[2] = Ascii($"<< /Type /Pages /Kids [ {kids} ] /Count {pageCount} >>");

        for (var i = 0; i < pageCount; i++)
        {
            var p = pages[i];
            var pageObj = pageObjNums[i];
            var contentObj = pageObj + 1;
            var imageObj = pageObj + 2;
            var smaskObj = p.AlphaStream is null ? 0 : pageObj + 3;

            bodies[pageObj] = Ascii(
                $"<< /Type /Page /Parent 2 0 R " +
                $" /MediaBox [0 0 {N(p.Layout.PageWidth)} {N(p.Layout.PageHeight)}]" +
                $" /Resources << /XObject << /Im0 {imageObj} 0 R >> >>" +
                $" /Contents {contentObj} 0 R >>");

            // Draw the image: fit-rectangle matrix (cm) followed by the XObject invocation.
            var content = "q\n" +
                $"{N(p.Layout.DrawWidth)} 0 0 {N(p.Layout.DrawHeight)} {N(p.Layout.OffsetX)} {N(p.Layout.OffsetY)} cm\n" +
                "/Im0 Do\n" +
                "Q\n";
            var contentBytes = ZlibCompress(Ascii(content));
            bodies[contentObj] = StreamBody(
                $"<< /Length {contentBytes.Length} /Filter /FlateDecode >>", contentBytes);

            var smaskRef = smaskObj == 0 ? "" : $" /SMask {smaskObj} 0 R";
            bodies[imageObj] = StreamBody(
                $"<< /Type /XObject /Subtype /Image" +
                $" /Width {p.PixelWidth} /Height {p.PixelHeight}" +
                $" /ColorSpace {p.ColorSpace} /BitsPerComponent 8{smaskRef}" +
                $" /Filter /{p.Filter} /Length {p.ImageStream.Length} >>",
                p.ImageStream);

            if (smaskObj != 0 && p.AlphaStream is not null)
            {
                bodies[smaskObj] = StreamBody(
                    $"<< /Type /XObject /Subtype /Image" +
                    $" /Width {p.PixelWidth} /Height {p.PixelHeight}" +
                    $" /ColorSpace /DeviceGray /BitsPerComponent 8" +
                    $" /Filter /FlateDecode /Length {p.AlphaStream.Length} >>",
                    p.AlphaStream);
            }
        }

        bodies[infoObjNum] = Ascii(
            $"<< /Producer (PdfCompressor) /Creator (PdfCompressor)" +
            $" /CreationDate (D:{now:yyyyMMddHHmmssZ}) >>");

        // ---- Serialise: header, objects, cross-reference table, trailer ----
        using var fs = File.Create(outputPath);

        void WriteAscii(string s)
        {
            var b = Ascii(s);
            fs.Write(b, 0, b.Length);
        }

        // File header: the ASCII signature, then the conventional second-line
        // "binary comment": '%' + four bytes >= 128, which tells the parser the
        // file contains binary data. The '%' matters - WITHOUT it line 2 is an
        // invalid token and strict viewers (Edge's PDF viewer) refuse the file.
        WriteAscii("%PDF-1.4\n%");
        fs.WriteByte(0xE2); fs.WriteByte(0xE3); fs.WriteByte(0xCF); fs.WriteByte(0xD3);
        fs.WriteByte((byte)'\n');

        var offsets = new long[totalObjects + 1];
        for (var n = 1; n <= totalObjects; n++)
        {
            offsets[n] = fs.Position;
            WriteAscii($"{n} 0 obj\n");
            var body = bodies[n];
            fs.Write(body, 0, body.Length);
            WriteAscii("\nendobj\n");
        }

        var xrefPos = fs.Position;
        WriteAscii($"xref\n0 {totalObjects + 1}\n");
        WriteAscii("0000000000 65535 f \r\n");
        for (var n = 1; n <= totalObjects; n++)
        {
            WriteAscii(offsets[n].ToString("0000000000", CultureInfo.InvariantCulture) + " 00000 n \r\n");
        }

        WriteAscii(
            $"trailer\n<< /Size {totalObjects + 1} /Root 1 0 R /Info {infoObjNum} 0 R >>\n" +
            $"startxref\n{xrefPos}\n%%EOF\n");
    }

    /// <summary>Builds "N 0 obj &lt;&lt;dict&gt;&gt; stream … endstream" as raw bytes.</summary>
    private static byte[] StreamBody(string dictionary, byte[] streamBytes)
    {
        var head = Ascii($"{dictionary}\nstream\n");
        var tail = Ascii("\nendstream");
        var body = new byte[head.Length + streamBytes.Length + tail.Length];
        Buffer.BlockCopy(head, 0, body, 0, head.Length);
        Buffer.BlockCopy(streamBytes, 0, body, head.Length, streamBytes.Length);
        Buffer.BlockCopy(tail, 0, body, head.Length + streamBytes.Length, tail.Length);
        return body;
    }

    // ---- Small helpers -----------------------------------------------------

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    /// <summary>Formats a PDF real number with invariant culture.</summary>
    private static string N(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>PDF FlateDecode expects a zlib stream (RFC 1950) — that's exactly ZLibStream.</summary>
    private static byte[] ZlibCompress(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(raw, 0, raw.Length);
        }
        return ms.ToArray();
    }

    private static bool IsJpeg(byte[] b) =>
        b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;

    /// <summary>
    /// Reads width/height/component-count from a JPEG's SOF marker so the file can be
    /// embedded without decoding it at all.
    /// </summary>
    private static bool TryReadJpegHeader(byte[] b, out int width, out int height, out int components)
    {
        width = height = components = 0;
        foreach (var (marker, pos) in EnumerateJpegMarkers(b))
        {
            if (IsStartOfFrame(marker))
            {
                // Payload layout: precision(1) height(2) width(2) components(1)
                if (pos + 6 > b.Length)
                {
                    return false;
                }
                height = (b[pos + 1] << 8) | b[pos + 2];
                width = (b[pos + 3] << 8) | b[pos + 4];
                components = b[pos + 5];
                return width > 0 && height > 0;
            }
            if (marker == 0xDA) // start of scan reached without an SOF
            {
                return false;
            }
        }
        return false;
    }

    /// <summary>
    /// Returns the EXIF orientation tag (1–8) of a JPEG, or 1 when absent/unknown.
    /// Only JPEGs can be uprighted this way; other formats are assumed to be stored upright.
    /// </summary>
    private static int ReadJpegOrientation(byte[] b)
    {
        foreach (var (marker, pos) in EnumerateJpegMarkers(b))
        {
            if (marker == 0xDA) // scan data starts here — metadata must appear before it
            {
                return 1;
            }

            if (marker != 0xE1) // APP1
            {
                continue;
            }

            // payload must start with "Exif\0\0" then a TIFF header
            if (pos + 20 > b.Length ||
                b[pos] != 'E' || b[pos + 1] != 'x' || b[pos + 2] != 'i' || b[pos + 3] != 'f' ||
                b[pos + 4] != 0 || b[pos + 5] != 0)
            {
                continue;
            }

            var orient = ParseTiffOrientation(b, pos + 6, b.Length);
            return orient is >= 1 and <= 8 ? orient : 1;
        }
        return 1;
    }

    /// <summary>Walks JPEG markers; yields (markerByte, payloadStartIndex) pairs.</summary>
    private static IEnumerable<(byte Marker, int Pos)> EnumerateJpegMarkers(byte[] b)
    {
        var i = 2; // skip SOI
        while (i + 3 < b.Length)
        {
            if (b[i] != 0xFF)
            {
                yield break; // structure broken — stop guessing
            }

            var marker = b[i + 1];
            if (marker == 0xFF)
            {
                i++; // fill byte
                continue;
            }
            if (marker == 0x01 || marker is >= 0xD0 and <= 0xD7)
            {
                i += 2; // standalone markers (RSTn, TEM) have no payload
                continue;
            }
            if (marker == 0xD9)
            {
                yield break; // end of image
            }

            yield return (marker, i + 4); // payload starts after marker + length field

            var len = (b[i + 2] << 8) | b[i + 3];
            if (len < 2)
            {
                yield break;
            }
            i += 2 + len;
        }
    }

    private static bool IsStartOfFrame(byte marker) =>
        marker is (>= 0xC0 and <= 0xC3) or (>= 0xC5 and <= 0xC7)
               or (>= 0xC9 and <= 0xCB) or (>= 0xCD and <= 0xCF);

    /// <summary>Parses the TIFF structure inside an EXIF APP1 payload looking for tag 0x0112.</summary>
    private static int ParseTiffOrientation(byte[] b, int tiffStart, int limit)
    {
        if (tiffStart + 8 > limit)
        {
            return 1;
        }

        bool little;
        if (b[tiffStart] == 'I' && b[tiffStart + 1] == 'I')
        {
            little = true;
        }
        else if (b[tiffStart] == 'M' && b[tiffStart + 1] == 'M')
        {
            little = false;
        }
        else
        {
            return 1;
        }

        int U16(int off) => little ? b[off] | (b[off + 1] << 8) : (b[off] << 8) | b[off + 1];
        int U32(int off) => little
            ? b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24)
            : (b[off] << 24) | (b[off + 1] << 16) | (b[off + 2] << 8) | b[off + 3];

        if (U16(tiffStart + 2) != 42)
        {
            return 1;
        }

        var ifd = tiffStart + U32(tiffStart + 4);
        if (ifd + 2 > limit)
        {
            return 1;
        }

        var entryCount = U16(ifd);
        for (var e = 0; e < entryCount; e++)
        {
            var entry = ifd + 2 + e * 12;
            if (entry + 12 > limit)
            {
                return 1;
            }
            if (U16(entry) == 0x0112) // Orientation
            {
                var type = U16(entry + 2);
                var count = U32(entry + 4);
                if (type == 3 && count == 1) // SHORT, single value
                {
                    return little ? b[entry + 8] | (b[entry + 9] << 8) : (b[entry + 8] << 8) | b[entry + 9];
                }
                return 1;
            }
        }

        return 1;
    }
}

/// <summary>Thrown when a specific input image cannot be read or converted.</summary>
public sealed class ImageToPdfException : Exception
{
    public ImageToPdfException(string message, Exception? inner = null) : base(message, inner) { }
}
