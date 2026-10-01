using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace OpenOutlook.Desktop;

public sealed partial class App : Application
{
    /// <summary>Set once the lifetime is shutting down, so no error window is raised during exit.</summary>
    internal static bool ShuttingDown { get; private set; }

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

        // An exception that surfaces after an `await` inside an event handler is re-raised on the UI
        // thread's synchronization context, and by default it aborts the process -- no message, and any
        // draft being written goes with it. Avalonia 11.2 has no Application.UnhandledException event,
        // but Dispatcher.UIThread.UnhandledException does cover this path: reporting there and setting
        // Handled = true keeps the window alive, as measured against Avalonia 11.2.3 (an escaping async
        // void exception aborts with SIGABRT unhooked, and a second escape is caught just as well after
        // the first). Exceptions thrown synchronously inside an event are already swallowed by Avalonia's
        // own input routing, so this hook plus the recorders above cover what reaches the loop.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            AppLog.Error("ui", e.Exception, "unhandled exception on the UI thread");
            // Cancellation is a normal outcome of these awaits (a folder switch abandoning a read, an
            // app closing), not a fault worth interrupting the user over. The log still has it.
            if (!ShuttingDown && e.Exception is not OperationCanceledException)
            {
                // A failure inside the notice must not escape this handler: that would abort the process
                // for good, which is the exact outcome the handler exists to prevent.
                try { CrashNotice.Show(e.Exception); }
                catch (Exception noticeFailure) { AppLog.Error("notice", noticeFailure, "crash notice failed"); }
            }
            e.Handled = true;
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
            desktop.Exit += (_, _) =>
            {
                ShuttingDown = true;
                AppLog.Note("lifetime", "application exit");
            };
            if (Environment.GetEnvironmentVariable("OPENOUTLOOK_TEST_THROW_UNHANDLED") == "1")
                ScheduleTestEscape();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Throws from an async void continuation a moment after startup, to check from the outside that the
    /// hook above really contains such an escape. Opt-in through OPENOUTLOOK_TEST_THROW_UNHANDLED=1; see
    /// scripts/headless-smoke.sh (OO_SMOKE_CRASH_GUARD=1), which asserts the process is still alive and
    /// that the notice appeared. Without a seam like this, the guard could only ever be assumed.
    /// </summary>
    private void ScheduleTestEscape()
    {
        DispatcherTimer.RunOnce(() => ThrowAfterAwaiting(), TimeSpan.FromMilliseconds(1500));

        static async void ThrowAfterAwaiting()
        {
            await Task.Delay(100);
            throw new InvalidOperationException("test escape from an async void continuation");
        }
    }
}
