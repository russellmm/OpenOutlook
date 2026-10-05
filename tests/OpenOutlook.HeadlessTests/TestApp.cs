using Avalonia;
using Avalonia.Headless;
using OpenOutlook.Desktop;

[assembly: AvaloniaTestApplication(typeof(OpenOutlook.HeadlessTests.TestAppBuilder))]

namespace OpenOutlook.HeadlessTests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        // Settings stores read XDG_CONFIG_HOME first: keep every run away from the owner's real settings.
        var scratch = Path.Combine(Path.GetTempPath(), "oo-headless-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", scratch);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(scratch, "data"));   // the account registry lives here
        Environment.SetEnvironmentVariable("OPENOUTLOOK_NO_MIRROR", "1");                       // no background mailbox copies in tests
        // The real app contains exceptions that surface on the UI thread (App.OnFrameworkInitializationCompleted); the native WebView2 control cannot start
        // inside the headless host and would otherwise fail whichever test happens to be running.
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) => { if (e.Exception.StackTrace?.Contains("NativeWebView") == true) e.Handled = true; };
        return AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}
