using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Desktop;

/// <summary>Edits the complete mailbox draft, including recipients, body and file attachments.</summary>
public sealed class ComposeWindow : Window
{
    private readonly GraphMailWriter _writer;
    private readonly Func<Task<string>> _token;
    private readonly TextBox _to = new() { Watermark = "name@example.com" };
    private readonly TextBox _cc = new() { Watermark = "Optional" };
    private readonly TextBox _bcc = new() { Watermark = "Optional" };
    private readonly TextBox _subject = new();
    private readonly TextBlock _bodyLabel = new() { Text = "Message" };
    private readonly TextBox _body = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 200 };
    private readonly CheckBox _html = new() { Content = "HTML body" };
    private readonly ListBox _attachments = new() { MinHeight = 65, MaxHeight = 120 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _save = new() { Content = "Save draft" };
    private readonly Button _send = new() { Content = "Send" };
    private readonly Button _discard = new() { Content = "Discard changes" };
    private readonly Button _add = new() { Content = "Attach files" };
    private readonly Button _remove = new() { Content = "Remove selected" };
    private readonly Button _visual = new() { Content = "Visual editor" };
    private readonly Button _source = new() { Content = "HTML source", IsVisible = false };
    private readonly List<AttachmentItem> _files = [];
    private string? _draftId;
    private bool _busy;
    private bool _loadFailed;
    private bool _loadingMode;
    private bool _loadingDraft;
    private bool _dirty;
    private bool _closingAfterSave;
    private bool _sent;
    private bool _closingPromptOpen;
    private bool _sourceMode;
    private string _htmlBody = "";
    private GraphMailWriter.DraftContent? _savedContent;

    private sealed record AttachmentItem(string Name, string? Path, string? Id, long Size)
    {
        public override string ToString() => $"{Name} ({Size / 1024.0:0.#} KB)" + (Path is null ? "" : " · pending");
    }

    public ComposeWindow(string accountAddress, GraphMailWriter writer, Func<Task<string>> token,
        string? draftId = null)
    {
        _writer = writer;
        _token = token;
        _draftId = draftId;
        Title = "Compose — OpenOutlook";
        Width = 850;
        Height = 760;
        MinWidth = 500;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { _save, _send, _discard } };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { _html, _visual, _source, _add, _remove } };
        _save.Click += async (_, _) => { await SaveAsync(false); };
        _send.Click += async (_, _) => { await SaveAsync(true); };
        _discard.Click += (_, _) => { _closingAfterSave = true; Close(); };
        _add.Click += async (_, _) => await AddAttachmentsAsync();
        _remove.Click += async (_, _) => await RemoveAttachmentAsync();
        _visual.Click += (_, _) => OpenVisualEditor();
        _source.Click += (_, _) => ToggleSource();
        _html.IsCheckedChanged += (_, _) =>
        {
            if (!_loadingMode && _html.IsChecked == true)
            {
                _htmlBody = "<p>" + System.Net.WebUtility.HtmlEncode(_body.Text ?? "")
                    .Replace("\r\n", "\n").Replace("\n", "<br>") + "</p>";
                _body.Text = PreviewText(_htmlBody);
            }
            else if (!_loadingMode && _html.IsChecked != true)
            {
                if (_sourceMode) _htmlBody = _body.Text ?? "";
                _sourceMode = false;
                _body.Text = PreviewText(_htmlBody);
            }
            UpdateBodyMode();
            if (!_loadingDraft) _dirty = true;
        };
        foreach (var box in new[] { _to, _cc, _bcc, _subject, _body })
            box.TextChanged += (_, _) => { if (!_loadingDraft) _dirty = true; };
        Closing += ComposeClosing;

        var layout = new Grid { Margin = new Thickness(16),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,*,Auto,Auto,Auto,Auto") };
        Add(new TextBlock { Text = "From: " + accountAddress }, 0);
        Add(new TextBlock { Text = "To" }, 1);
        Add(_to, 2);
        Add(new TextBlock { Text = "Cc" }, 3);
        Add(_cc, 4);
        Add(new TextBlock { Text = "Bcc" }, 5);
        Add(_bcc, 6);
        Add(new TextBlock { Text = "Subject" }, 7);
        Add(_subject, 8);
        var bodyArea = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(_body, 1);
        _bodyLabel.Margin = new Thickness(0, 0, 0, 6);
        bodyArea.Children.Add(_bodyLabel);
        bodyArea.Children.Add(_body);
        Add(bodyArea, 9);
        Add(tools, 10);
        Add(_attachments, 11);
        Add(buttons, 12);
        Add(_status, 13);
        Content = layout;
        UpdateBodyMode();
        RefreshAttachments();
        if (_draftId is not null) Opened += async (_, _) => await LoadDraftAsync();
        return;

        void Add(Control control, int row)
        {
            Grid.SetRow(control, row);
            control.Margin = new Thickness(0, 0, 0, 6);
            layout.Children.Add(control);
        }
    }

    private void UpdateBodyMode()
    {
        var html = _html.IsChecked == true;
        _bodyLabel.Text = html ? (_sourceMode ? "HTML source" : "Message preview — use Visual editor to format") : "Message";
        _body.Watermark = html ? (_sourceMode ? "HTML source" : "Formatted message preview; use Visual editor to change it") : "Write your message";
        _body.IsReadOnly = html && !_sourceMode;
        _visual.IsEnabled = !_busy && html;
        _source.IsVisible = html;
        _source.IsEnabled = !_busy && html;
        _source.Content = _sourceMode ? "Close source" : "HTML source";
    }

    private async Task LoadDraftAsync()
    {
        SetBusy(true);
        _status.Text = "Loading draft…";
        try
        {
            _loadingDraft = true;
            var token = await _token();
            var draft = await _writer.GetDraftAsync(token, _draftId!);
            _to.Text = draft.To;
            _cc.Text = draft.Cc;
            _bcc.Text = draft.Bcc;
            _subject.Text = draft.Subject;
            _loadingMode = true;
            _html.IsChecked = draft.ContentType.Equals("html", StringComparison.OrdinalIgnoreCase);
            _loadingMode = false;
            _htmlBody = _html.IsChecked == true ? draft.Body : "";
            _body.Text = _html.IsChecked == true ? PreviewText(_htmlBody) : draft.Body;
            UpdateBodyMode();
            if (draft.HasAttachments)
            {
                var attachments = await _writer.ListDraftAttachmentsAsync(token, _draftId!);
                _files.AddRange(attachments.Select(item => new AttachmentItem(item.Name, null, item.Id, item.SizeBytes)));
            }
            RefreshAttachments();
            _status.Text = "Edit this draft, then save or send it.";
        }
        catch (Exception ex)
        {
            _loadFailed = true;
            _status.Text = "Could not open this draft: " + SafeError(ex);
        }
        finally
        {
            _loadingDraft = false;
            if (!_loadFailed) _savedContent = CurrentContent();
            _dirty = false;
            SetBusy(false);
        }
    }

    private async Task AddAttachmentsAsync()
    {
        try
        {
            var selected = await SafePick.FilesAsync(this, new FilePickerOpenOptions
            { Title = "Attach files", AllowMultiple = true }, failure => _status.Text = failure);
            foreach (var item in selected)
            {
                var path = item.TryGetLocalPath();
                if (path is null) continue;
                var length = new FileInfo(path).Length;
                if (length > GraphMailWriter.MaximumComposeAttachmentBytes)
                {
                    _status.Text = $"{item.Name} exceeds the 150 MB attachment limit.";
                    continue;
                }
                _files.Add(new AttachmentItem(item.Name, path, null, length));
                _dirty = true;
            }
            RefreshAttachments();
        }
        catch (Exception ex) { _status.Text = "Could not attach a file: " + SafeError(ex); }
    }

    private async Task RemoveAttachmentAsync()
    {
        if (_busy || _attachments.SelectedItem is not AttachmentItem selected) return;
        if (selected.Id is null)
        {
            _files.Remove(selected);
            _dirty = true;
            RefreshAttachments();
            return;
        }
        SetBusy(true);
        try
        {
            await _writer.RemoveAttachmentAsync(await _token(), _draftId!, selected.Id);
            _files.Remove(selected);
            RefreshAttachments();
            _status.Text = "Attachment removed from the draft.";
        }
        catch (Exception ex) { _status.Text = "Could not remove attachment: " + SafeError(ex); }
        finally { SetBusy(false); }
    }

    private async Task<bool> SaveAsync(bool send)
    {
        if (_busy || _loadFailed) return false;
        if (send && string.IsNullOrWhiteSpace(_to.Text))
        {
            _status.Text = "Enter at least one recipient before sending.";
            return false;
        }
        SetBusy(true);
        try
        {
            _status.Text = "Saving draft…";
            var token = await _token();
            var content = CurrentContent();
            if (_draftId is null)
                _draftId = await _writer.CreateDraftAsync(token, content);
            else
                await _writer.UpdateDraftAsync(token, _draftId, content);
            var uploaded = false;
            foreach (var pending in _files.Where(item => item.Path is not null).ToArray())
            {
                _status.Text = "Uploading " + pending.Name + "…";
                await _writer.AddFileAttachmentAsync(token, _draftId, pending.Path!);
                uploaded = true;
                _files.Remove(pending);
                RefreshAttachments();
            }
            if (uploaded)
            {
                var remote = await _writer.ListDraftAttachmentsAsync(token, _draftId);
                _files.RemoveAll(item => item.Path is null);
                _files.AddRange(remote.Select(item => new AttachmentItem(item.Name, null, item.Id, item.SizeBytes)));
                RefreshAttachments();
            }
            _dirty = false;
            _savedContent = content;
            if (send)
            {
                _status.Text = "Sending…";
                await _writer.SendDraftAsync(token, _draftId);
                _sent = true;
                Close();
            }
            else _status.Text = "Draft saved in your Microsoft mailbox.";
            return true;
        }
        catch (Exception ex)
        {
            _status.Text = "Mail action failed: " + SafeError(ex) +
                (_draftId is null ? "" : " The message is saved in Drafts; check it before retrying Send.");
            return false;
        }
        finally { SetBusy(false); }
    }

    private async void ComposeClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_sent || _closingAfterSave) return;
        if (_closingPromptOpen) { e.Cancel = true; return; }
        if (_busy)
        {
            e.Cancel = true;
            _status.Text = "Wait for the current draft operation to finish before closing.";
            return;
        }
        if (!HasUnsavedChanges()) return;
        e.Cancel = true;
        if (await SaveAsync(false))
        {
            _closingAfterSave = true;
            Close();
        }
        else
            await OfferDiscardAfterFailedSaveAsync();
    }

    private async Task OfferDiscardAfterFailedSaveAsync()
    {
        _closingPromptOpen = true;
        try
        {
            var dialog = new Window { Title = "Draft could not be saved — OpenOutlook", Width = 470,
                SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var keep = new Button { Content = "Keep editing" };
            var discard = new Button { Content = "Discard and close" };
            keep.Click += (_, _) => dialog.Close(false);
            discard.Click += (_, _) => dialog.Close(true);
            dialog.Content = new StackPanel { Margin = new Thickness(18), Spacing = 14, Children =
            {
                new TextBlock { Text = "OpenOutlook could not save this draft. Keep it open to retry, or discard your unsaved changes and close.",
                    TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
                    Children = { keep, discard } }
            } };
            if (await dialog.ShowDialog<bool>(this))
            {
                _closingAfterSave = true;
                Close();
            }
        }
        finally { _closingPromptOpen = false; }
    }

    private void RefreshAttachments() => _attachments.ItemsSource = _files.ToArray();

    private GraphMailWriter.DraftContent CurrentContent() =>
        new(_to.Text ?? "", _cc.Text ?? "", _bcc.Text ?? "", _subject.Text ?? "",
            _html.IsChecked == true ? (_sourceMode ? _body.Text ?? "" : _htmlBody) : _body.Text ?? "",
            _html.IsChecked == true ? "HTML" : "Text");

    private bool HasUnsavedChanges()
    {
        if (_dirty || _files.Any(item => item.Path is not null)) return true;
        var current = CurrentContent();
        if (_savedContent is not null) return current != _savedContent;
        return !string.IsNullOrWhiteSpace(current.To) || !string.IsNullOrWhiteSpace(current.Cc) ||
            !string.IsNullOrWhiteSpace(current.Bcc) || !string.IsNullOrWhiteSpace(current.Subject) ||
            !string.IsNullOrWhiteSpace(current.Body);
    }

    private void OpenVisualEditor()
    {
        if (_html.IsChecked != true) return;
        if (_sourceMode) _htmlBody = _body.Text ?? "";
        RichComposeEditor.Open(this, _htmlBody, edited =>
            {
                _htmlBody = edited;
                _body.Text = _sourceMode ? edited : PreviewText(edited);
                _dirty = true;
            },
            error => _status.Text = error);
    }

    private void ToggleSource()
    {
        if (_html.IsChecked != true) return;
        if (_sourceMode)
        {
            _htmlBody = _body.Text ?? "";
            _sourceMode = false;
            _body.Text = PreviewText(_htmlBody);
        }
        else
        {
            _sourceMode = true;
            _body.Text = _htmlBody;
        }
        _dirty = true;
        UpdateBodyMode();
    }

    private static string PreviewText(string html)
    {
        try { return string.Concat(SafeHtmlPreview.Parse(html).Select(run => run.Text)); }
        catch (Exception) { return "(Formatted message; use Visual editor or HTML source to inspect it.)"; }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        foreach (var control in new Control[] { _to, _cc, _bcc, _subject, _body, _html, _save, _send, _discard, _add, _remove })
            control.IsEnabled = !busy && !_loadFailed;
        UpdateBodyMode();
    }

    private static string SafeError(Exception ex) => ex switch
    {
        GraphMailException error => error.Message,
        ArgumentException error => error.Message,
        IOException error => error.Message,
        _ => "Check the connection and account permissions, then retry."
    };
}
