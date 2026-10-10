// =============================================================================
//  ThemeManager.cs
// =============================================================================
//  This class is the brain behind the app's dark / light mode toggle.
//
//  HOW THEMING WORKS IN THIS APP (read this first!)
//  ----------------------------------------------------------------------------
//  Every color the UI uses is stored as a named brush in App.xaml under the
//  "Application.Resources" dictionary, e.g. "Brush.WindowBackground". The XAML
//  does NOT hardcode colors; it references them like this:
//
//      Background="{DynamicResource Brush.WindowBackground}"
//
//  The word "DynamicResource" is the key to the whole system:
//    - StaticResource  = resolve the brush ONCE when the window loads and keep
//                        using that exact brush object forever.
//    - DynamicResource = re-look-up the brush every time the resource dictionary
//                        changes. If a new brush is stored under the same key,
//                        every control using it re-colors itself automatically.
//
//  So to switch themes at runtime we simply REPLACE every brush in the resource
//  dictionary with a differently-colored one, and DynamicResource does the rest.
//
//  We learned this the hard way: the first version used StaticResource and the
//  toggle did nothing visible, because controls kept pointing at the original
//  brush objects.
//
//  ThemeManager also PERSISTS the user's choice to a text file so the app opens
//  in the right theme next time. On the very first run (no file yet) it reads
//  the Windows setting instead, so the app politely matches the OS.
// =============================================================================

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
    /// <summary>
    /// Tracks which theme is currently applied. Stored as a static because the
    /// theme is an application-wide (not per-window) concept.
    /// Defaults to dark so the window doesn't flash white before Load() runs.
    /// </summary>
    public static bool IsDark { get; private set; } = true;

    // -------------------------------------------------------------------------
    // Settings file location. We use %APPDATA% (Environment.SpecialFolder.
    // ApplicationData) because that is the correct per-user place for app
    // settings on Windows - NOT the exe's folder, which may be read-only
    // (e.g. under Program Files) or inside a zip on the user's desktop.
    // -------------------------------------------------------------------------
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PdfCompressor");

    private static readonly string SettingsFile = Path.Combine(SettingsDir, "theme.txt");

    /// <summary>
    /// Applies the saved theme (or the OS theme on first run) and returns whether dark is active.
    /// Called once from App.OnStartup.
    /// </summary>
    public static bool Load()
    {
        // Decide which theme we should start with. Default to dark.
        var dark = true;

        if (File.Exists(SettingsFile))
        {
            // The user has toggled the theme before - read their choice.
            var saved = File.ReadAllText(SettingsFile).Trim().ToLowerInvariant();
            dark = saved switch
            {
                "light" => false,
                "dark" => true,
                // Corrupt / unexpected file content: fall back to dark instead
                // of crashing or showing a broken-looking UI.
                _ => true
            };
        }
        else
        {
            // First run ever: mirror the Windows app mode when available.
            // Registry key:
            //   HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize
            //   AppsUseLightTheme = 0  -> Windows is in dark mode
            //   AppsUseLightTheme = 1  -> Windows is in light mode
            // Reading the registry is wrapped in try/catch because a locked-down
            // or unusual profile can throw; a theme that follows Windows is a
            // nice-to-have, not worth crashing over.
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

    /// <summary>
    /// Flips to the opposite theme, saves the choice and returns the new dark state.
    /// Called from the theme button in MainWindow.
    /// </summary>
    public static bool Toggle()
    {
        var dark = !IsDark;
        Apply(dark);
        Save(dark);
        return dark;
    }

    /// <summary>
    /// The actual theme switch: replaces every brush in the application's
    /// resource dictionary with the brushes for the requested theme.
    /// Because the XAML uses DynamicResource, all live controls re-render.
    /// </summary>
    /// <param name="dark">true = apply the dark palette, false = light palette.</param>
    public static void Apply(bool dark)
    {
        IsDark = dark;

        // Pick the palette for the requested theme.
        var brushes = dark
            ? CreateDarkBrushes()
            : CreateLightBrushes();

        // Application.Current.Resources is the SAME dictionary that App.xaml
        // defines, so writing to it is how we update the theme at runtime.
        var resources = Application.Current.Resources;
        foreach (var (key, brush) in brushes)
        {
            // Setting resources[key] replaces the existing brush. Any control
            // with a DynamicResource to this key immediately gets the new color.
            resources[key] = brush;
        }
    }

    /// <summary>
    /// Persists the choice as a single line ("dark" or "light") in
    /// %APPDATA%\PdfCompressor\theme.txt. Best-effort: a read-only profile
    /// should never crash the app just because we could not save a preference.
    /// </summary>
    private static void Save(bool dark)
    {
        try
        {
            Directory.CreateDirectory(SettingsDir); // file may not exist yet
            File.WriteAllText(SettingsFile, dark ? "dark" : "light");
        }
        catch
        {
            // best effort — a read-only / weird profile should not break the app
        }
    }

    // -------------------------------------------------------------------------
    // Palettes
    // -------------------------------------------------------------------------
    // Each palette is a dictionary from brush KEY (as referenced in XAML) to a
    // SolidColorBrush with the theme-appropriate color. The two dictionaries
    // MUST contain the same keys, otherwise a control could reference a key that
    // only exists in one theme and turn invisible/black.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Dark palette - muted near-black surfaces with light text.
    /// Colors are 8-digit hex: #AARRGGBB (AA = alpha, always FF = opaque).
    /// </summary>
    private static Dictionary<string, SolidColorBrush> CreateDarkBrushes() => new()
    {
        // Window chrome / page background
        ["Brush.WindowBackground"] = Brush("#1E1E1E"),
        // Generic raised surfaces (e.g. dropdown popup)
        ["Brush.SurfaceBackground"] = Brush("#242428"),
        // Input controls (text boxes, combo boxes)
        ["Brush.ControlBackground"] = Brush("#2B2B30"),
        // Borders around controls
        ["Brush.ControlBorder"] = Brush("#3F3F46"),
        // The file list background
        ["Brush.ListBackground"] = Brush("#232327"),
        // Progress bar track (the empty part)
        ["Brush.ProgressBackground"] = Brush("#33333A"),
        // The drag & drop zone (slightly blue-tinted to hint "action")
        ["Brush.DropZoneBackground"] = Brush("#16212E"),

        // Main text / titles
        ["Brush.TextPrimary"] = Brush("#E8EAED"),
        // Secondary / helper text
        ["Brush.TextSecondary"] = Brush("#9AA0A6"),
        // Disabled buttons and controls
        ["Brush.TextDisabled"] = Brush("#6B7280"),

        // Brand/accent color (progress bar fill, drop zone border)
        ["Brush.Accent"] = Brush("#3B82F6"),

        // Buttons: normal / hover / pressed / disabled
        ["Brush.ButtonBackground"] = Brush("#33333A"),
        ["Brush.ButtonHover"] = Brush("#3E3E46"),
        ["Brush.ButtonPressed"] = Brush("#2A2A30"),
        ["Brush.ButtonDisabled"] = Brush("#26262B"),

        // Per-file status colors in the list. These are BRIGHT variants because
        // the dark background demands more luminance for readability.
        ["Brush.StatusBlue"] = Brush("#60A5FA"),   // compressing
        ["Brush.StatusGreen"] = Brush("#4ADE80"),  // done
        ["Brush.StatusRed"] = Brush("#F87171"),    // failed
        ["Brush.StatusGray"] = Brush("#9CA3AF"),   // no gain
    };

    /// <summary>
    /// Light palette - white/light-gray surfaces with dark text.
    /// The same keys as the dark palette, just different values.
    /// </summary>
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

        // Status colors are DARKER variants here so they read well on white.
        ["Brush.StatusBlue"] = Brush("#2563EB"),
        ["Brush.StatusGreen"] = Brush("#16A34A"),
        ["Brush.StatusRed"] = Brush("#DC2626"),
        ["Brush.StatusGray"] = Brush("#9CA3AF"),
    };

    /// <summary>
    /// Helper: converts a hex color string like "#1E1E1E" into a frozen
    /// SolidColorBrush. Freezing a brush makes it immutable and lets WPF share
    /// it across threads/controls efficiently. (Frozen brushes cannot be
    /// modified, but we never need to modify them - we replace them whole.)
    /// </summary>
    private static SolidColorBrush Brush(string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
