using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PdfCompressor;

/// <summary>
/// Applies a light or dark theme to the whole app by swapping the brushes in
/// Application.Resources. The user's choice is saved to
/// %APPDATA%\PdfCompressor\theme.txt and restored on next launch.
/// On first run (no saved setting) it follows the Windows app mode.
/// Also keeps the window icon and the native title bar in sync with the theme.
/// </summary>
public static class ThemeManager
{
    public static bool IsDark { get; private set; } = true;

    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PdfCompressor");

    private static readonly string SettingsFile = Path.Combine(SettingsDir, "theme.txt");

    private static BitmapImage? _darkIcon;
    private static BitmapImage? _lightIcon;

    /// <summary>Applies the saved theme (or the OS theme on first run) and returns whether dark is active.</summary>
    public static bool Load()
    {
        var dark = true;

        if (File.Exists(SettingsFile))
        {
            var saved = File.ReadAllText(SettingsFile).Trim().ToLowerInvariant();
            dark = saved switch
            {
                "light" => false,
                "dark" => true,
                _ => true
            };
        }
        else
        {
            // First run: mirror the Windows app mode when available.
            try
            {
                const string key = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
                var value = Microsoft.Win32.Registry.GetValue(key, "AppsUseLightTheme", 1);
                if (value is int v)
                {
                    dark = v == 0;
                }
            }
            catch
            {
                // fall back to dark
            }
        }

        Apply(dark);
        return dark;
    }

    /// <summary>Toggles between themes, saves the choice and returns the new dark state.</summary>
    public static bool Toggle()
    {
        var dark = !IsDark;
        Apply(dark);
        Save(dark);
        return dark;
    }

    public static void Apply(bool dark)
    {
        IsDark = dark;

        var brushes = dark
            ? CreateDarkBrushes()
            : CreateLightBrushes();

        var resources = Application.Current.Resources;
        foreach (var (key, brush) in brushes)
        {
            resources[key] = brush;
        }
    }

    // ---- Window icon / title bar ----

    /// <summary>Matches the window icon (taskbar + title bar) to the active theme.</summary>
    public static void ApplyWindowIcon(Window window)
    {
        try
        {
            window.Icon = IsDark
                ? _darkIcon ??= LoadIcon("app-dark.ico")
                : _lightIcon ??= LoadIcon("app-light.ico");
        }
        catch
        {
            // an icon is decoration — never fail over it
        }
    }

    /// <summary>Paints the native title bar dark or light to match the theme (Windows 10 1809+).</summary>
    public static void ApplyWindowChrome(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var dark = IsDark ? 1 : 0;
            // DWMWA_USE_IMMERSIVE_DARK_MODE is 20 on current builds, 19 on early ones.
            if (DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
            }
        }
        catch
        {
            // older Windows or a non-DWM session — keep the default title bar
        }
    }

    private static BitmapImage LoadIcon(string fileName)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri($"pack://application:,,,/Assets/{fileName}", UriKind.Absolute);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static void Save(bool dark)
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            File.WriteAllText(SettingsFile, dark ? "dark" : "light");
        }
        catch
        {
            // best effort — a read-only / weird profile should not break the app
        }
    }

    // ---- Palettes (flat colours, no gradients) ----

    private static Dictionary<string, SolidColorBrush> CreateDarkBrushes() => new()
    {
        ["Brush.WindowBackground"] = Brush("#17191D"),
        ["Brush.SurfaceBackground"] = Brush("#1D2025"),
        ["Brush.ControlBackground"] = Brush("#23262C"),
        ["Brush.ControlBorder"] = Brush("#31353D"),
        ["Brush.ListBackground"] = Brush("#1D2025"),
        ["Brush.ProgressBackground"] = Brush("#262A31"),
        ["Brush.DropZoneBackground"] = Brush("#1A1F27"),
        ["Brush.DropZoneBorder"] = Brush("#2E4159"),

        ["Brush.TextPrimary"] = Brush("#E6E8EC"),
        ["Brush.TextSecondary"] = Brush("#8C93A0"),
        ["Brush.TextDisabled"] = Brush("#5A6170"),

        ["Brush.Accent"] = Brush("#4C8DF6"),
        ["Brush.AccentHover"] = Brush("#3F7EE8"),
        ["Brush.AccentPressed"] = Brush("#3468C9"),
        ["Brush.OnAccent"] = Brush("#FFFFFF"),

        ["Brush.ButtonBackground"] = Brush("#23262C"),
        ["Brush.ButtonHover"] = Brush("#2B2F37"),
        ["Brush.ButtonPressed"] = Brush("#1E2127"),
        ["Brush.ButtonDisabled"] = Brush("#1C1F24"),

        ["Brush.SegmentBackground"] = Brush("#121418"),
        ["Brush.SegmentSelected"] = Brush("#2A2E37"),
        ["Brush.SelectedBackground"] = Brush("#1F2C42"),
        ["Brush.ScrollThumb"] = Brush("#363B45"),

        ["Brush.StatusBlue"] = Brush("#6FA8FF"),
        ["Brush.StatusGreen"] = Brush("#4CC38A"),
        ["Brush.StatusRed"] = Brush("#F07171"),
        ["Brush.StatusGray"] = Brush("#8C93A0"),
    };

    private static Dictionary<string, SolidColorBrush> CreateLightBrushes() => new()
    {
        ["Brush.WindowBackground"] = Brush("#F5F6F8"),
        ["Brush.SurfaceBackground"] = Brush("#FFFFFF"),
        ["Brush.ControlBackground"] = Brush("#FFFFFF"),
        ["Brush.ControlBorder"] = Brush("#D9DDE4"),
        ["Brush.ListBackground"] = Brush("#FFFFFF"),
        ["Brush.ProgressBackground"] = Brush("#E7EAEF"),
        ["Brush.DropZoneBackground"] = Brush("#F3F7FF"),
        ["Brush.DropZoneBorder"] = Brush("#C3D7F5"),

        ["Brush.TextPrimary"] = Brush("#1B1F27"),
        ["Brush.TextSecondary"] = Brush("#5E6673"),
        ["Brush.TextDisabled"] = Brush("#9AA3B0"),

        ["Brush.Accent"] = Brush("#2F6FEB"),
        ["Brush.AccentHover"] = Brush("#2861D2"),
        ["Brush.AccentPressed"] = Brush("#2153B4"),
        ["Brush.OnAccent"] = Brush("#FFFFFF"),

        ["Brush.ButtonBackground"] = Brush("#FFFFFF"),
        ["Brush.ButtonHover"] = Brush("#F1F3F7"),
        ["Brush.ButtonPressed"] = Brush("#E5E9F0"),
        ["Brush.ButtonDisabled"] = Brush("#F1F3F6"),

        ["Brush.SegmentBackground"] = Brush("#EAEDF2"),
        ["Brush.SegmentSelected"] = Brush("#FFFFFF"),
        ["Brush.SelectedBackground"] = Brush("#EAF1FE"),
        ["Brush.ScrollThumb"] = Brush("#C6CDD8"),

        ["Brush.StatusBlue"] = Brush("#2F6FEB"),
        ["Brush.StatusGreen"] = Brush("#17A05E"),
        ["Brush.StatusRed"] = Brush("#DC3D3D"),
        ["Brush.StatusGray"] = Brush("#7A8494"),
    };

    private static SolidColorBrush Brush(string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
