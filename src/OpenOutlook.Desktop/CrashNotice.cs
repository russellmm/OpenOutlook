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
    public static void Show(Exception exception)
    {
        var details = Describe(exception);

        var text = new TextBlock
        {
            Text = "OpenOutlook hit an unexpected error and kept running. " +
                   "Your message list and any draft you were writing are still here.\n\n" + details,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(16),
            MaxWidth = 640,
        };

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

        window.Show();
    }

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
