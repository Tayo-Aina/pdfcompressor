using System.IO;
using System.Windows;
using System.Windows.Media;

namespace PdfCompressor;

/// <summary>
/// Applies a light or dark theme to the whole app by swapping the brushes in
/// Application.Resources. The user's choice is saved to
/// %APPDATA%\PdfCompressor\theme.txt and restored on next launch.
/// On first run (no saved setting) it follows the Windows app mode.
/// </summary>
public static class ThemeManager
{
    public static bool IsDark { get; private set; } = true;

    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PdfCompressor");

    private static readonly string SettingsFile = Path.Combine(SettingsDir, "theme.txt");

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

    // ---- Palettes ----

    private static Dictionary<string, SolidColorBrush> CreateDarkBrushes() => new()
    {
        ["Brush.WindowBackground"] = Brush("#1E1E1E"),
        ["Brush.SurfaceBackground"] = Brush("#242428"),
        ["Brush.ControlBackground"] = Brush("#2B2B30"),
        ["Brush.ControlBorder"] = Brush("#3F3F46"),
        ["Brush.ListBackground"] = Brush("#232327"),
        ["Brush.ProgressBackground"] = Brush("#33333A"),
        ["Brush.DropZoneBackground"] = Brush("#16212E"),

        ["Brush.TextPrimary"] = Brush("#E8EAED"),
        ["Brush.TextSecondary"] = Brush("#9AA0A6"),
        ["Brush.TextDisabled"] = Brush("#6B7280"),

        ["Brush.Accent"] = Brush("#3B82F6"),

        ["Brush.ButtonBackground"] = Brush("#33333A"),
        ["Brush.ButtonHover"] = Brush("#3E3E46"),
        ["Brush.ButtonPressed"] = Brush("#2A2A30"),
        ["Brush.ButtonDisabled"] = Brush("#26262B"),

        // Status colors (readable on dark)
        ["Brush.StatusBlue"] = Brush("#60A5FA"),
        ["Brush.StatusGreen"] = Brush("#4ADE80"),
        ["Brush.StatusRed"] = Brush("#F87171"),
        ["Brush.StatusGray"] = Brush("#9CA3AF"),
    };

    private static Dictionary<string, SolidColorBrush> CreateLightBrushes() => new()
    {
        ["Brush.WindowBackground"] = Brush("#FAFAFB"),
        ["Brush.SurfaceBackground"] = Brush("#F3F4F6"),
        ["Brush.ControlBackground"] = Brush("#FFFFFF"),
        ["Brush.ControlBorder"] = Brush("#D1D5DB"),
        ["Brush.ListBackground"] = Brush("#FFFFFF"),
        ["Brush.ProgressBackground"] = Brush("#E5E7EB"),
        ["Brush.DropZoneBackground"] = Brush("#EFF6FF"),

        ["Brush.TextPrimary"] = Brush("#1F2937"),
        ["Brush.TextSecondary"] = Brush("#6B7280"),
        ["Brush.TextDisabled"] = Brush("#9CA3AF"),

        ["Brush.Accent"] = Brush("#2563EB"),

        ["Brush.ButtonBackground"] = Brush("#F3F4F6"),
        ["Brush.ButtonHover"] = Brush("#E5E7EB"),
        ["Brush.ButtonPressed"] = Brush("#D1D5DB"),
        ["Brush.ButtonDisabled"] = Brush("#F1F1F3"),

        // Status colors (readable on light)
        ["Brush.StatusBlue"] = Brush("#2563EB"),
        ["Brush.StatusGreen"] = Brush("#16A34A"),
        ["Brush.StatusRed"] = Brush("#DC2626"),
        ["Brush.StatusGray"] = Brush("#9CA3AF"),
    };

    private static SolidColorBrush Brush(string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
