using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PstCore;

namespace OpenOutlook.Desktop;

/// <summary>Right-click > Properties on a message: the message's details plus its raw internet headers (what the sender's server and every relay put on it).</summary>
public sealed partial class MainWindow
{
    private MenuItem MessagePropertiesMenuItem()
    {
        var properties = new MenuItem { Header = "Properties" };
        properties.Click += async (_, _) => await ShowMessagePropertiesAsync();
        return properties;
    }

    private async Task ShowMessagePropertiesAsync()
    {
        if (CurrentTransferOrigin() is not { } origin || OriginCount(origin) == 0) { StatusText.Text = "Select a message first."; return; }
        await CreateMessagePropertiesSheet(origin).ShowDialog(this);
    }

    /// <summary>The header block of a raw message (RFC 822 / MIME bytes): everything before the first blank line.</summary>
    internal static string HeaderBlockOf(byte[] raw)
    {
        var text = Encoding.UTF8.GetString(raw);
        var end = Regex.Match(text, "\r?\n\r?\n");
        var block = end.Success ? text[..end.Index] : text;
        return block.Replace("\r\n", "\n").TrimEnd();
    }

    /// <summary>The value of a header (unfolded), or "" when it is not there.</summary>
    internal static string HeaderValue(string headers, string name)
    {
        var m = Regex.Match(headers, "^" + Regex.Escape(name) + @":[ \t]*(.*(?:\n[ \t]+.*)*)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        return m.Success ? Regex.Replace(m.Groups[1].Value, @"\s*\n\s*", " ").Trim() : "";
    }

    /// <summary>Builds the sheet (not shown) for the first selected message. The header text and the recipient / attachment lists arrive when it opens.</summary>
    internal Window CreateMessagePropertiesSheet(object originObject)
    {
        var rows = new List<(string Label, string Value)>();
        string subject;
        Func<CancellationToken, Task<(string Headers, string? Recipients, string? Attachments)>> load;
        string Date(DateTimeOffset? t) => t is { } v ? v.LocalDateTime.ToString("ddd d MMM yyyy HH:mm:ss") : "";
        string Importance(int i) => i switch { 0 => "Low", 2 => "High", _ => "Normal" };

        switch (originObject)
        {
            case PstOrigin p:
            {
                var s = p.Messages[0];
                subject = s.Subject;
                var store = _stores[p.Path];
                rows.Add(("Subject", s.Subject));
                rows.Add(("From", s.From));
                rows.Add(("To", s.To));
                rows.Add(("Recipients", "loading…"));
                rows.Add(("Sent", s.Sent == DateTime.MinValue ? "" : s.Sent.ToString("ddd d MMM yyyy HH:mm:ss")));
                rows.Add(("Received", s.Received == DateTime.MinValue ? "" : s.Received.ToString("ddd d MMM yyyy HH:mm:ss")));
                rows.Add(("Size", PstMessageHeader.FormatSize(s.Size) + $" ({s.Size:N0} bytes)"));
                rows.Add(("Importance", Importance(s.Importance)));
                rows.Add(("Status", (s.IsRead ? "Read" : "Unread") + (s.Flagged ? ", flagged" : "")));
                rows.Add(("Attachments", s.HasAttachment ? "loading…" : "none"));
                rows.Add(("Message class", s.MessageClass));
                rows.Add(("Internet message ID", "loading…"));
                rows.Add(("Folder", p.Folder.Name));
                rows.Add(("Data file", p.Path));
                rows.Add(("Message ID", "0x" + s.Nid.ToString("X")));
                load = ct => Task.Run(() =>
                {
                    var m = store.OpenMessage(s);
                    string Names(RecipientKind k) => string.Join("; ", m.Recipients.Where(r => r.Kind == k).Select(r => r.ToString()));
                    var recips = string.Join("\n", new[] { ("To", Names(RecipientKind.To)), ("Cc", Names(RecipientKind.Cc)), ("Bcc", Names(RecipientKind.Bcc)) }
                        .Where(x => x.Item2.Length > 0).Select(x => x.Item1 + ": " + x.Item2));
                    var attachments = string.Join("\n", m.Attachments.Where(a => !a.IsHidden).Select(a => a.Size > 0 ? $"{a.FileName} ({PstMessageHeader.FormatSize(a.Size)})" : a.FileName));
                    return (PstMessageHeader.HeadersText(m), recips.Length == 0 ? "(none)" : recips, attachments);
                }, ct);
                break;
            }
            case MicrosoftOrigin ms:
            {
                var s = ms.Messages[0];
                subject = s.Subject;
                rows.Add(("Subject", s.Subject));
                rows.Add(("From", s.From));
                rows.Add(("To", s.To));
                rows.Add(("Received", Date(s.Received)));
                rows.Add(("Size", s.SizeBytes is { } b ? PstMessageHeader.FormatSize(b) + $" ({b:N0} bytes)" : ""));
                rows.Add(("Importance", Importance(s.Importance)));
                rows.Add(("Status", (s.IsRead ? "Read" : "Unread") + (s.IsFlagged ? ", flagged" : "") + (s.IsDraft ? ", draft" : "")));
                rows.Add(("Attachments", s.HasAttachments ? "yes" : "none"));
                rows.Add(("Internet message ID", "loading…"));
                rows.Add(("Account", ms.Folder.Account.DisplayAddress));
                rows.Add(("Folder", ms.Folder.Name));
                rows.Add(("Message ID", s.Id));
                var (source, items) = MakeTransferSource(ms);
                load = async ct => (HeaderBlockOf(await source.ReadMimeAsync(items[0], ct)), null, null);
                break;
            }
            case GmailOrigin g:
            {
                var s = g.Messages[0];
                subject = s.Subject;
                rows.Add(("Subject", s.Subject));
                rows.Add(("From", s.From));
                rows.Add(("To", s.To));
                rows.Add(("Received", Date(s.Received)));
                rows.Add(("Size", s.SizeBytes is { } b ? PstMessageHeader.FormatSize(b) + $" ({b:N0} bytes)" : ""));
                rows.Add(("Status", (s.IsRead ? "Read" : "Unread") + (s.IsFlagged ? ", starred" : "") + (s.IsDraft ? ", draft" : "")));
                rows.Add(("Attachments", s.HasAttachments ? "yes" : "none"));
                rows.Add(("Internet message ID", "loading…"));
                rows.Add(("Account", g.Folder.Account.DisplayAddress));
                rows.Add(("Label", g.Folder.Name));
                rows.Add(("Message ID", s.Id));
                var (source, items) = MakeTransferSource(g);
                load = async ct => (HeaderBlockOf(await source.ReadMimeAsync(items[0], ct)), null, null);
                break;
            }
            default: throw new InvalidOperationException("Unknown message source.");
        }

        var values = new Dictionary<string, SelectableTextBlock>();
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var labelBrush = Themed.Brush("OlTextSecondary", Brushes.Gray);
        foreach (var (label, value) in rows)
        {
            var row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var l = new TextBlock { Text = label, Foreground = labelBrush, Margin = new Thickness(0, 3, 16, 3), VerticalAlignment = VerticalAlignment.Top };
            var v = new SelectableTextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) };
            Grid.SetRow(l, row); Grid.SetRow(v, row); Grid.SetColumn(v, 1);
            grid.Children.Add(l); grid.Children.Add(v);
            values[label] = v;
        }

        var headers = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Text = "Loading the internet headers…",
            FontFamily = new FontFamily("Consolas, Menlo, DejaVu Sans Mono, monospace"), FontSize = 12, MinHeight = 200, MaxHeight = 320
        };
        var copy = new Button { Content = "Copy headers", IsEnabled = false };
        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true, MinWidth = 80 };
        var dialog = new Window
        {
            Title = "Message Properties", Width = 760, Height = 700, MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(dialog)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(headers.Text ?? "");
        };
        close.Click += (_, _) => dialog.Close();
        var panel = new DockPanel { Margin = new Thickness(18) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0), Children = { copy, close } };
        DockPanel.SetDock(buttons, Dock.Bottom);
        var top = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = string.IsNullOrWhiteSpace(subject) ? "(no subject)" : subject, FontSize = 18, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                new ScrollViewer { Content = grid, MaxHeight = 300, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto },
                new TextBlock { Text = "Internet headers (as received)", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) }
            }
        };
        DockPanel.SetDock(top, Dock.Top);
        panel.Children.Add(buttons);
        panel.Children.Add(top);
        panel.Children.Add(headers);
        dialog.Content = panel;

        var cancel = new CancellationTokenSource();
        dialog.Closed += (_, _) => { cancel.Cancel(); cancel.Dispose(); };
        dialog.Opened += async (_, _) =>
        {
            close.Focus();
            try
            {
                var (text, recipients, attachments) = await load(cancel.Token);
                headers.Text = text;
                copy.IsEnabled = true;
                if (values.TryGetValue("Recipients", out var r) && recipients is not null) r.Text = recipients;
                if (values.TryGetValue("Attachments", out var a) && attachments is not null) a.Text = attachments.Length == 0 ? "none" : attachments;
                var id = HeaderValue(text, "Message-ID");
                values["Internet message ID"].Text = id.Length == 0 ? "(none)" : id;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AppLog.Error("message-properties", ex, "reading the message headers failed");
                headers.Text = "The headers could not be read: " + ex.Message;
                if (values.TryGetValue("Internet message ID", out var id)) id.Text = "";
                if (values.TryGetValue("Recipients", out var r)) r.Text = "";
            }
        };
        return dialog;
    }
}
