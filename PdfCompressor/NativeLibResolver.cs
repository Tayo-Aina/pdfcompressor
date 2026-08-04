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

    /// <summary>Returns a full path to a usable gsdll64.dll.</summary>
    public static string Resolve()
    {
        // 1. Explicit override via environment variable.
        var fromEnv = Environment.GetEnvironmentVariable("PDFCOMPRESSOR_GS_DLL");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
        {
            RegisterDllDirectory(fromEnv);
            return Path.GetFullPath(fromEnv);
        }

        // 2. DLL next to the executable (handy in dev builds / for upgrades).
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

    private static string GetCachePath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var engineVersion = GetEmbeddedEngineVersion();
        return Path.Combine(root, "PdfCompressor", engineVersion, DllName);
    }

    private static string GetEmbeddedEngineVersion()
    {
        // Keyed on the assembly version so upgrading the app rotates the cache.
        var asmVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0.0";
        return asmVersion;
    }

    private static void ExtractEmbeddedEngine(string targetFile)
    {
        var dir = Path.GetDirectoryName(targetFile)!;
        Directory.CreateDirectory(dir);

        // Reuse an existing, same-size copy (fast path).
        using (var existing = File.Exists(targetFile) ? File.OpenRead(targetFile) : null)
        {
            var expected = GetEmbeddedEngineLength();
            if (existing is not null && existing.Length == expected)
            {
                return; // already materialised
            }
        }

        // Write the embedded bytes to a temp file first, then atomically move into place.
        var tmp = targetFile + ".tmp";
        using (var stream = GetEmbeddedEngineStream())
        {
            if (stream is null)
            {
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

    private static Stream? GetEmbeddedEngineStream()
    {
        const string resourceName = "PdfCompressor.gsdll64.dll";
        var asm = Assembly.GetExecutingAssembly();

        var direct = asm.GetManifestResourceStream(resourceName);
        if (direct is not null)
        {
            return direct;
        }

        // Fallback: scan manifest resource names (protects against name drift).
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (name.EndsWith(DllName, StringComparison.OrdinalIgnoreCase))
            {
                return asm.GetManifestResourceStream(name);
            }
        }

        return null;
    }

    private static long GetEmbeddedEngineLength()
    {
        using var s = GetEmbeddedEngineStream();
        return s?.Length ?? -1;
    }

    /// <summary>
    /// Adds the folder that contains the engine DLL to the process DLL search path,
    /// so both full-path LoadLibrary and plain "gsdll64.dll" DllImport loads succeed.
    /// </summary>
    private static void RegisterDllDirectory(string dllPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(dllPath));
        if (!string.IsNullOrEmpty(dir))
        {
            _ = SetDllDirectoryW(dir);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectoryW([MarshalAs(UnmanagedType.LPWStr)] string lpPathName);
}
