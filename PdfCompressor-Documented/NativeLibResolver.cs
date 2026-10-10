// =============================================================================
//  NativeLibResolver.cs
// =============================================================================
//  This file solves a tricky deployment problem:
//
//  PdfCompressor is published as a SINGLE .exe file (PublishSingleFile=true).
//  The compression engine is a NATIVE Windows DLL (gsdll64.dll, ~25 MB) which
//  cannot be inlined into the .NET assembly itself - .NET can only execute it
//  if it exists as a real file on disk that LoadLibrary() can open.
//
//  So we EMBED the DLL as a managed resource inside the exe at build time
//  (see the <EmbeddedResource> entry in PdfCompressor.csproj). At runtime this
//  class digs it back out and materializes it to disk so Ghostscript.NET can
//  load it. The user never sees any of this - from their point of view the app
//  is still just one file.
//
//  Resolution strategy, in priority order:
//    1. PDFCOMPRESSOR_GS_DLL environment variable (explicit override)
//    2. A gsdll64.dll sitting NEXT to the exe (handy in dev builds)
//    3. Extract the embedded copy into %LOCALAPPDATA%\PdfCompressor\<version>\
// =============================================================================

using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace PdfCompressor;

/// <summary>
/// Locates / materialises the native Ghostscript engine (gsdll64.dll) at runtime.
///
/// Strategy:
///   1. If the PDFCOMPRESSOR_GS_DLL environment variable points to a valid DLL, use it.
///   2. If a gsdll64.dll sits next to the exe (dev mode / side-by-side override), use it.
///   3. Otherwise extract the copy embedded in this assembly (single-file exe) into a
///      versioned cache under %LOCALAPPDATA%\PdfCompressor and use that.
/// Also calls SetDllDirectory so any relative "gsdll64.dll" DllImport loads resolve too.
/// </summary>
public static class NativeLibResolver
{
    private const string DllName = "gsdll64.dll";

    /// <summary>
    /// Returns a full path to a usable gsdll64.dll. This is called once by the
    /// CompressionEngine constructor. It never returns null - it throws if the
    /// DLL cannot be produced, so failures surface loudly instead of as a weird
    /// native-load error later.
    /// </summary>
    public static string Resolve()
    {
        // 1. Explicit override via environment variable.
        //    Useful for testing alternate engine versions without rebuilding.
        var fromEnv = Environment.GetEnvironmentVariable("PDFCOMPRESSOR_GS_DLL");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
        {
            RegisterDllDirectory(fromEnv);
            return Path.GetFullPath(fromEnv);
        }

        // 2. DLL next to the executable (handy in dev builds / for upgrades).
        //    If someone drops a newer gsdll64.dll beside the exe, this wins over
        //    the embedded copy - a cheap way to upgrade the engine.
        var exeDir = AppContext.BaseDirectory;
        var besideExe = Path.Combine(exeDir, DllName);
        if (File.Exists(besideExe))
        {
            RegisterDllDirectory(besideExe);
            return Path.GetFullPath(besideExe);
        }

        // 3. Extract the embedded engine into a versioned cache folder.
        var cacheFile = GetCachePath();
        ExtractEmbeddedEngine(cacheFile);
        RegisterDllDirectory(cacheFile);
        return cacheFile;
    }

    /// <summary>
    /// Computes where the extracted engine should live on disk.
    /// The version number in the path means: when the app is upgraded, the new
    /// version extracts to a FRESH folder instead of colliding with (or
    /// silently reusing) the old DLL - avoiding "old engine, new wrapper"
    /// mismatch bugs.
    /// </summary>
    private static string GetCachePath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var engineVersion = GetEmbeddedEngineVersion();
        return Path.Combine(root, "PdfCompressor", engineVersion, DllName);
    }

    /// <summary>
    /// The version of the embedded engine, derived from the ASSEMBLY version.
    /// (We don't try to query the DLL itself - reading the embedded version
    /// would require loading it, which is what we're trying to arrange.)
    /// </summary>
    private static string GetEmbeddedEngineVersion()
    {
        // Keyed on the assembly version so upgrading the app rotates the cache.
        var asmVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0.0";
        return asmVersion;
    }

    /// <summary>
    /// Ensures the embedded engine DLL exists at <paramref name="targetFile"/>,
    /// extracting it from the assembly resources if needed.
    /// </summary>
    private static void ExtractEmbeddedEngine(string targetFile)
    {
        var dir = Path.GetDirectoryName(targetFile)!;
        Directory.CreateDirectory(dir); // %LOCALAPPDATA%\PdfCompressor\<version>\ may not exist

        // Fast path: if a file already exists with the EXACT same length as the
        // embedded DLL, assume it is our extraction from a previous run and
        // reuse it. (Length is a cheap and reliable enough proxy here; a partial
        // or corrupt file would have a different size.)
        using (var existing = File.Exists(targetFile) ? File.OpenRead(targetFile) : null)
        {
            var expected = GetEmbeddedEngineLength();
            if (existing is not null && existing.Length == expected)
            {
                return; // already materialised
            }
        }

        // Slow path: write the embedded bytes out. We write to a ".tmp" file and
        // then ATOMICALLY move it into place. Why? If the app crashed halfway
        // through writing the real file, the next run would see a truncated DLL
        // and fail weirdly. Writing to a temp name and moving keeps the target
        // either fully-absent or fully-valid.
        var tmp = targetFile + ".tmp";
        using (var stream = GetEmbeddedEngineStream())
        {
            if (stream is null)
            {
                // No embedded resource found - the build is missing the DLL.
                throw new InvalidOperationException(
                    "The Ghostscript engine is not embedded in this build. " +
                    "Re-publish the exe, or set PDFCOMPRESSOR_GS_DLL to a valid gsdll64.dll path.");
            }

            using (var file = File.Create(tmp))
            {
                stream.CopyTo(file);
            }
        }

        File.Move(tmp, targetFile, overwrite: true);
    }

    /// <summary>
    /// Locates the embedded DLL inside this assembly's manifest resources.
    /// </summary>
    private static Stream? GetEmbeddedEngineStream()
    {
        // Resources are addressable by their logical name, which we set in the
        // csproj to "PdfCompressor.gsdll64.dll".
        const string resourceName = "PdfCompressor.gsdll64.dll";
        var asm = Assembly.GetExecutingAssembly();

        var direct = asm.GetManifestResourceStream(resourceName);
        if (direct is not null)
        {
            return direct;
        }

        // Fallback: scan all resource names looking for one ending in
        // "gsdll64.dll". This protects against the resource name drifting
        // (renaming / namespace changes) without breaking the build.
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (name.EndsWith(DllName, StringComparison.OrdinalIgnoreCase))
            {
                return asm.GetManifestResourceStream(name);
            }
        }

        return null;
    }

    /// <summary>Size of the embedded DLL, or -1 if it is missing.</summary>
    private static long GetEmbeddedEngineLength()
    {
        using var s = GetEmbeddedEngineStream();
        return s?.Length ?? -1;
    }

    /// <summary>
    /// Adds the folder that contains the engine DLL to the process DLL search path.
    ///
    /// Ghostscript.NET loads the DLL in two different ways depending on the code
    /// path: sometimes by full path (LoadLibrary), and sometimes by bare name
    /// ("gsdll64.dll" via DllImport). Windows resolves bare names by searching
    /// the process DLL directories. SetDllDirectoryW adds our folder to that
    /// search list, so BOTH load styles succeed no matter where the DLL ended up.
    /// </summary>
    private static void RegisterDllDirectory(string dllPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(dllPath));
        if (!string.IsNullOrEmpty(dir))
        {
            _ = SetDllDirectoryW(dir);
        }
    }

    // P/Invoke declaration for the Win32 API that adds a folder to the DLL
    // search path. CharSet.Unicode = use the "W" (wide char) variant semantics;
    // SetLastError = allow Marshal.GetLastWin32Error() if we ever need details.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectoryW([MarshalAs(UnmanagedType.LPWStr)] string lpPathName);
}
