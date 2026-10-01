using System.Text;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;

namespace OpenOutlook.Desktop;

/// <summary>
/// A small window shown when an exception escapes a handler, instead of the application vanishing.
///
/// Before this existed, any exception thrown inside an <c>async void</c> event handler killed the
/// process with no message: if it happened while composing, the draft was simply gone and nothing
/// explained why. The notice keeps the window alive so unread mail and any in-progress compose
/// survive, states what failed, and offers the details on the clipboard for reporting.
/// </summary>
public static class CrashNotice
{
    private static Window? _current;

    /// <summary>
    /// Show the notice, or refresh it if one is already open. A failing operation that repeats -- a
    /// folder that throws every time it is selected -- would otherwise stack a new window per click.
    /// </summary>
    public static void Show(Exception exception)
    {
        var details = Describe(exception);

        if (_current is { IsVisible: true } existing)
        {
            if (existing.Tag is TextBlock previous) previous.Text = DetailsText(details, _repeat++);
            existing.Activate();
            return;
        }

        var text = new TextBlock
        {
            Text = DetailsText(details, 0),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(16),
            MaxWidth = 640,
        };
        _repeat = 1;

        var copy = new Button { Content = "Copy details", Margin = new Avalonia.Thickness(0, 0, 8, 0) };
        var close = new Button { Content = "Close" };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Avalonia.Thickness(16, 0, 16, 16),
        };
        buttons.Children.Add(copy);
        buttons.Children.Add(close);

        var body = new StackPanel();
        body.Children.Add(text);
        body.Children.Add(buttons);

        var window = new Window
        {
            Title = "Unexpected error",
            Content = body,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = Brushes.White,
            Tag = text,
        };

        copy.Click += async (_, _) =>
        {
            try
            {
                var clipboard = window.Clipboard;
                if (clipboard is null) return;
                await clipboard.SetTextAsync(details);
                copy.Content = "Copied";
            }
            catch (Exception) { copy.Content = "Copy failed - see the log"; }
        };
        close.Click += (_, _) => window.Close();
        window.Closed += (_, _) => { if (ReferenceEquals(_current, window)) _current = null; };

        _current = window;
        window.Show();
    }

    private static int _repeat;

    private static string DetailsText(string details, int repeat) =>
        "OpenOutlook hit an unexpected error and kept running. " +
        "Your message list and any draft you were writing are still here." +
        (repeat > 0 ? $"\n\nThis is the {Ordinal(repeat + 1)} error in this window." : "") +
        "\n\n" + details;

    private static string Ordinal(int count) => count switch
    {
        2 => "second", 3 => "third", 4 => "fourth", 5 => "fifth", _ => $"{count}th",
    };

    /// <summary>Text for the notice and the clipboard: type, message and a bounded stack trace.</summary>
    public static string Describe(Exception exception)
    {
        var builder = new StringBuilder();
        builder.Append(exception.GetType().FullName).Append(": ").Append(exception.Message);
        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
            builder.Append("\ncaused by ").Append(inner.GetType().FullName).Append(": ").Append(inner.Message);
        if (!string.IsNullOrEmpty(exception.StackTrace))
        {
            var frames = exception.StackTrace.Split('\n')
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Take(12)
                .ToArray();
            builder.Append('\n').Append(string.Join('\n', frames));
            var omitted = exception.StackTrace.Split('\n').Length - frames.Length;
            if (omitted > 0) builder.Append($"\n... {omitted} more frames in the log");
        }
        return builder.ToString();
    }
}
