using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using PstCore;

namespace OpenOutlook.Desktop;

/// <summary>
/// "View as: HTML | Rich Text | Plain Text | Headers" for archive messages. The bar tells which body formats the message really stores
/// (a missing one is greyed out), starts on the richest one (HTML, else Rich Text, else Plain Text, like the GTK client) and lets the
/// reader switch to the plain-text rendition or the internet headers.
/// </summary>
public partial class MainWindow
{
    private enum MessageFormat { Html, Rich, Plain, Headers }

    private MailMessage? _formatMessage;
    private MessageFormat _format;

    /// <summary>Shows an archive message in its richest format and arms the format bar.</summary>
    private void ShowPstMessageBody(MailMessage message)
    {
        _formatMessage = message;
        _format = message.HasHtml ? MessageFormat.Html : message.HasRtf ? MessageFormat.Rich : MessageFormat.Plain;
        FormatBar.IsVisible = true;
        FormatHtmlButton.IsEnabled = message.HasHtml;
        FormatRtfButton.IsEnabled = message.HasRtf;
        FormatTextButton.IsEnabled = true;
        FormatHeadersButton.IsEnabled = true;
        ApplyFormat();
    }

    private void HideFormatBar()
    {
        _formatMessage = null;
        FormatBar.IsVisible = false;
        BodyText.ClearValue(TextBox.FontFamilyProperty);
    }

    private void FormatClicked(object? sender, RoutedEventArgs e)
    {
        if (_formatMessage is null || sender is not Button { Tag: string tag }) return;
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
        foreach (var (button, format) in new[]
        {
            (FormatHtmlButton, MessageFormat.Html), (FormatRtfButton, MessageFormat.Rich),
            (FormatTextButton, MessageFormat.Plain), (FormatHeadersButton, MessageFormat.Headers)
        })
            button.FontWeight = format == _format ? FontWeight.Bold : FontWeight.Normal;
        BodyViewButton.IsVisible = false;                       // the bar replaces the old "View plain text" toggle for archive messages
    }
}
