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
        return AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}
