using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace OpenOutlook.Desktop;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Handlers are attached before the main window is constructed: a failure during startup is
        // exactly the case where a log line and a visible message matter most.
        AppLog.Start();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Error("domain", e.ExceptionObject as Exception, $"terminating={e.IsTerminating}");

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // Fire-and-forget tasks used to fail silently. Record them; marking observed keeps the
            // process alive, which is the right trade for a mail client whose reader panes run many
            // independent background loads.
            AppLog.Error("task", e.Exception, "unobserved task exception");
            e.SetObserved();
        };

        // Avalonia 11.2 exposes no Application.UnhandledException event, so there is no framework
        // hook to catch an exception that escapes an event handler on the UI thread: it reaches the
        // platform loop and takes the process with it. The handlers above still record it. Containing
        // those escapes -- logging them, telling the user and keeping the window alive -- has to
        // happen at each handler; see SafeUi for the wrapper that does it.

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
            desktop.Exit += (_, _) => AppLog.Note("lifetime", "application exit");
        }

        base.OnFrameworkInitializationCompleted();
    }
}
