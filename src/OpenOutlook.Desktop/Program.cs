using Avalonia;

namespace OpenOutlook.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Embedded WebKitGTK can paint a blank native child surface with GPU compositing,
        // especially under X11 software displays. Set this before WebKit is initialized.
        if (OperatingSystem.IsLinux() &&
            Environment.GetEnvironmentVariable("WEBKIT_DISABLE_COMPOSITING_MODE") is null)
            Environment.SetEnvironmentVariable("WEBKIT_DISABLE_COMPOSITING_MODE", "1");
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
