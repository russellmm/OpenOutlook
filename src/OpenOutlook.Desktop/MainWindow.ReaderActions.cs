using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenOutlook.Desktop;

public partial class MainWindow
{
    private bool _savingPrintablePdf;

    private void ReaderMoreClicked(object? sender, RoutedEventArgs e)
    {
        var hasHtml = _bodyHtml is not null;
        var canOpenBrowser = hasHtml || !string.IsNullOrWhiteSpace(_formatMessage?.BodyHtml);
        var flyout = new MenuFlyout();

        if (hasHtml)
        {
            Add(_preferSnapshotForMessage ? "Use interactive reader" : "Use snapshot reader", ReaderModeClicked);
            Add(_showOriginalHtml ? "Return to safe layout" : "Show original HTML (trusted mail)", ToggleOriginalClicked);
            if (!_embeddedHtmlActive)
                Add("Open interactive view", OpenInteractiveReaderClicked);
        }

        if (canOpenBrowser || PopOutMessageButton.IsVisible)
        {
            if (flyout.Items.Count > 0) flyout.Items.Add(new Separator());
            if (canOpenBrowser)
                Add("Open in browser", OpenInBrowserClicked);
            if (PopOutMessageButton.IsVisible)
                Add("Save printable PDF", SavePrintablePdfClicked, !_savingPrintablePdf);
        }

        if (hasHtml)
        {
            flyout.Items.Add(new Separator());
            Add("Zoom in", ZoomInClicked);
            Add("Zoom out", ZoomOutClicked);
            Add("Fit to pane", ZoomFitClicked);
        }

        if (flyout.Items.Count > 0) flyout.ShowAt(ReaderMoreButton);

        void Add(string label, EventHandler<RoutedEventArgs> handler, bool enabled = true)
        {
            var item = new MenuItem { Header = label, IsEnabled = enabled };
            item.Click += handler;
            flyout.Items.Add(item);
        }
    }
}
