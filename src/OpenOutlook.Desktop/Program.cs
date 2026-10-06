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
        // Single-instance guard: two windows over one archive invite lost edits (each holds its own
        // snapshot), and the owner asked for it outright. The lock is an open exclusive file handle,
        // so a crash releases it automatically. Smoke tests isolate per HOME or opt out via OO_SMOKE_PST.
        if (Environment.GetEnvironmentVariable("OO_ALLOW_MULTI") != "1" &&
            Environment.GetEnvironmentVariable("OO_SMOKE_PST") is null)
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenOutlook");
                Directory.CreateDirectory(dir);
                InstanceLock = new FileStream(Path.Combine(dir, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) { SecondInstance = true; }
            catch (UnauthorizedAccessException) { SecondInstance = true; }
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    internal static FileStream? InstanceLock;
    internal static bool SecondInstance;

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace().With(LinuxOptions());

    /// <summary>
    /// Linux: menus, drop-downs and tooltips are drawn inside the window instead of as extra X windows. Under WSLg (and some other compositors) those extra windows
    /// end up behind the main window, so a menu opened in OpenOutlook was painted over by the window itself.
    /// </summary>
    private static X11PlatformOptions LinuxOptions() => new() { OverlayPopups = true };
}
