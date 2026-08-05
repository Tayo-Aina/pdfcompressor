using System.Windows;

namespace PdfCompressor;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ThemeManager.Load();
    }
}
