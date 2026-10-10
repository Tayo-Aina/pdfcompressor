// =============================================================================
//  App.xaml.cs
// =============================================================================
//  This file backs the App.xaml markup. In a WPF application the "App" class
//  represents the APPLICATION ITSELF (the process-level object), as opposed to
//  MainWindow which is just one window inside it.
//
//  What happens at startup, in order:
//    1. WPF loads App.xaml (the <Application> root element).
//    2. OnStartup() fires -> we call ThemeManager.Load() so the window opens
//       with the user's saved theme (dark or light) already applied.
//    3. WPF sees StartupUri="MainWindow.xaml" in App.xaml and opens the main
//       window automatically.
//
//  The reason the theme must be loaded HERE and not in MainWindow's constructor
//  is that App-level resources (the brushes in App.xaml) are read by the window
//  as it is being constructed. If we waited until after the window existed, the
//  window would flash the default (dark) theme first.
// =============================================================================

using System.Windows;

namespace PdfCompressor;

/// <summary>
/// The WPF application entry point.
///
/// We override OnStartup instead of using a Main() method because WPF generates
/// the entry point for us when OutputType is WinExe + UseWPF is true. Overriding
/// OnStartup is the idiomatic place to run "before the UI shows" logic.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Called once, right after the Application object is initialized and its
    /// resources (App.xaml) are loaded, but BEFORE the StartupUri window opens.
    /// This makes it the perfect spot to apply the saved color theme.
    /// </summary>
    /// <param name="e">Startup event args (contains command-line args if needed).</param>
    protected override void OnStartup(StartupEventArgs e)
    {
        // IMPORTANT: always call the base implementation first, otherwise the
        // rest of WPF's startup pipeline (including opening StartupUri) may not
        // behave correctly.
        base.OnStartup(e);

        // Read %APPDATA%\PdfCompressor\theme.txt (or the Windows theme on the
        // very first run) and swap every brush in App.xaml to match. Because the
        // XAML uses DynamicResource everywhere, this instantly recolors the UI.
        ThemeManager.Load();
    }
}
