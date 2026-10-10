using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using PstCore;

namespace OpenOutlook.Desktop;

/// <summary>
/// "View as" menu for archive messages. Unavailable stored formats are disabled; the message starts in its richest format.
/// </summary>
public partial class MainWindow
{
    private enum MessageFormat { Html, Rich, Plain, Headers }

    private MailMessage? _formatMessage;
    private MessageFormat _format;

    /// <summary>Shows an archive message in its richest format and arms the format menu.</summary>
    private void ShowPstMessageBody(MailMessage message)
    {
        _formatMessage = message;
        _format = message.HasHtml ? MessageFormat.Html : message.HasRtf ? MessageFormat.Rich : MessageFormat.Plain;
        FormatBar.IsVisible = true;
        ApplyFormat();
    }

    private void HideFormatBar()
    {
        _formatMessage = null;
        FormatBar.IsVisible = false;
        BodyText.ClearValue(TextBox.FontFamilyProperty);
    }

    private void FormatMenuClicked(object? sender, RoutedEventArgs e)
    {
        if (_formatMessage is not { } message) return;
        var flyout = new MenuFlyout();
        Add("HTML", "html", MessageFormat.Html, message.HasHtml);
        Add("Rich Text", "rtf", MessageFormat.Rich, message.HasRtf);
        Add("Plain Text", "text", MessageFormat.Plain, true);
        Add("Headers", "headers", MessageFormat.Headers, true);
        flyout.ShowAt(FormatBar);

        void Add(string label, string tag, MessageFormat format, bool enabled)
        {
            var item = new MenuItem
            {
                Header = format == _format ? "✓ " + label : label,
                Tag = tag,
                IsEnabled = enabled
            };
            item.Click += FormatClicked;
            flyout.Items.Add(item);
        }
    }

    private void FormatClicked(object? sender, RoutedEventArgs e)
    {
        if (_formatMessage is null || sender is not Control { Tag: string tag }) return;
        var wanted = tag switch { "html" => MessageFormat.Html, "rtf" => MessageFormat.Rich, "headers" => MessageFormat.Headers, _ => MessageFormat.Plain };
        if (wanted == _format) return;
        _format = wanted;
        ApplyFormat();
    }

    private void ApplyFormat()
    {
        if (_formatMessage is not { } message) return;
        string? html = null;
        string plain = message.BodyText;
        switch (_format)
        {
            case MessageFormat.Html: html = message.BodyHtml; break;
            case MessageFormat.Rich: html = message.BodyRtfHtml; break;
            case MessageFormat.Headers: plain = PstMessageHeader.HeadersText(message); break;
        }
        // The plain rendition of an HTML-only message is the text the engine converts from its HTML (BodyText), never empty.
        SetMessageBody(string.IsNullOrWhiteSpace(html) ? null : html, plain);
        if (_format == MessageFormat.Headers) BodyText.FontFamily = new FontFamily("Consolas, Menlo, DejaVu Sans Mono, monospace");
        else BodyText.ClearValue(TextBox.FontFamilyProperty);
        FormatBar.Content = _format switch
        {
            MessageFormat.Html => "View as: HTML ▾",
            MessageFormat.Rich => "View as: Rich Text ▾",
            MessageFormat.Headers => "View as: Headers ▾",
            _ => "View as: Plain Text ▾"
        };
        BodyViewButton.IsVisible = false; // The format menu replaces the rich/plain toggle for archive messages.
    }
}
