using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OpenOutlook.Auth;
using OpenOutlook.Providers.Google;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Desktop;

/// <summary>
/// Moving and copying messages between the folders of one online mailbox (Gmail labels or Microsoft folders): by dragging rows onto a folder in the
/// folder pane, and through the Outlook-style "Move Items" picker. Archives have their own implementation in MainWindow.MoveDrag.cs.
/// </summary>
public partial class MainWindow
{
    private const string OnlineDragPrefix = "OpenOutlook-online-move:v1\n";
    private readonly Dictionary<string, int> _msUnread = new(StringComparer.Ordinal);       // accountId|folderId -> unread count

    /// <summary>How many drags of online messages reached the system drag call (read by the headless tests, where that call never completes).</summary>
    internal int OnlineDragsStarted { get; private set; }

    private bool OnlineDragAvailable =>
        (_activeGmailFolder is not null || _activeMicrosoftFolder is not null) && MessageList.SelectedItems.OfType<GraphMessageListRow>().Any();

    internal static string FormatOnlineDragPayload(string accountId, string sourceFolderId, IEnumerable<string> messageIds) =>
        OnlineDragPrefix + accountId + "\n" + sourceFolderId + "\n" + string.Join("\n", messageIds) + "\n";

    internal static (string AccountId, string SourceFolderId, string[] MessageIds)? ParseOnlineDragPayload(string? text)
    {
        if (text is null || !text.StartsWith(OnlineDragPrefix, StringComparison.Ordinal)) return null;
        var lines = text[OnlineDragPrefix.Length..].Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 3) return null;
        return (lines[0], lines[1], lines.Skip(2).ToArray());
    }

    private async Task StartOnlineDragAsync(PointerEventArgs e)
    {
        string accountId, sourceId;
        if (_activeGmailFolder is { } g) (accountId, sourceId) = (g.Account.AccountId, g.LabelId);
        else if (_activeMicrosoftFolder is { } m) (accountId, sourceId) = (m.Account.AccountId, m.Id);
        else return;
        var ids = MessageList.SelectedItems.OfType<GraphMessageListRow>().Select(r => r.Message.Id).Distinct().ToArray();
        if (ids.Length == 0) return;
        var data = new DataObject();
        data.Set(DataFormats.Text, FormatOnlineDragPayload(accountId, sourceId, ids));
        OnlineDragsStarted++;
        try { await DragDrop.DoDragDrop(e, data, DragDropEffects.Move | DragDropEffects.Copy); }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { /* drag aborted */ }
        finally { _lastDragEndUtc = DateTime.UtcNow; }
    }

    /// <summary>Makes a Gmail or Microsoft folder node accept dragged messages of the same account.</summary>
    private void EnableOnlineDrops(TreeViewItem item)
    {
        DragDrop.SetAllowDrop(item, true);
        bool CanDrop(IDataObject data)
        {
            var payload = data.Contains(DataFormats.Text) ? ParseOnlineDragPayload(data.GetText()) : null;
            if (payload is not { } p) return false;
            return item.Tag switch
            {
                GmailFolderSelection g => g.Account.AccountId == p.AccountId && g.LabelId != p.SourceFolderId &&
                                          g.LabelId is not ("SENT" or "DRAFT" or "STARRED" or "IMPORTANT"),
                MicrosoftFolderSelection m => m.Account.AccountId == p.AccountId && m.Id != p.SourceFolderId,
                _ => false
            };
        }
        item.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            if (item.Tag is not (GmailFolderSelection or MicrosoftFolderSelection)) return;
            if (IsCrossStoreDrop(e.Data, item.Tag)) { e.DragEffects = DragDropEffects.Move; e.Handled = true; }   // another account, or messages of a data file
            else if (CanDrop(e.Data)) { e.DragEffects = DragDropEffects.Move; e.Handled = true; }
            else if (!e.Data.Contains(FolderDragFormat)) { e.DragEffects = DragDropEffects.None; e.Handled = true; }   // another account, the same folder, a file...: say "not allowed"
        });
        item.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            if (item.Tag is not (GmailFolderSelection or MicrosoftFolderSelection)) return;
            if (IsCrossStoreDrop(e.Data, item.Tag))
            {
                e.DragEffects = DragDropEffects.Move;
                e.Handled = true;
                await DropAcrossStoresAsync(e.Data, item.Tag, item);
                return;
            }
            if (!CanDrop(e.Data)) return;
            var payload = ParseOnlineDragPayload(e.Data.GetText())!.Value;
            e.DragEffects = DragDropEffects.Move;
            e.Handled = true;
            if (_messageDragRightButton)
            {
                _messageDragRightButton = false;
                var menu = new MenuFlyout();
                var moveHere = new MenuItem { Header = "Move Here" };
                var copyHere = new MenuItem { Header = "Copy Here" };
                moveHere.Click += async (_, _) => await MoveOnlineAsync(payload.MessageIds, item.Tag!, copy: false);
                copyHere.Click += async (_, _) => await MoveOnlineAsync(payload.MessageIds, item.Tag!, copy: true);
                menu.Items.Add(moveHere);
                menu.Items.Add(copyHere);
                menu.ShowAt(item);
            }
            else await MoveOnlineAsync(payload.MessageIds, item.Tag!, copy: false);
        });
    }

    /// <summary>Moves or copies messages of the folder that is showing into another folder of the same mailbox, then refreshes what is on screen.</summary>
    private async Task MoveOnlineAsync(IReadOnlyList<string> messageIds, object destination, bool copy)
    {
        if (destination is GmailFolderSelection gmailDest && _activeGmailFolder is { } gmailSource)
        {
            if (!gmailSource.Account.CanModifyGmail)
            {
                await ExplainMailActionAsync("This Gmail sign-in is read-only. Sign in again from Account setup to allow organizing mail.", gmailSource.Account);
                return;
            }
            if (_gmailActionBusy) return;
            _gmailActionBusy = true;
            try { StatusText.Text = await GmailMoveCoreAsync(gmailSource, messageIds, gmailDest, copy); }
            catch (GmailReadException error) { StatusText.Text = error.Message; }
            catch (OperationCanceledException) { }
            catch (Exception) { StatusText.Text = "Gmail change failed. Check the connection and retry."; }
            finally { _gmailActionBusy = false; }
            await ReloadGmailFolderAsync(gmailSource);
            return;
        }
        if (destination is MicrosoftFolderSelection msDest && _activeMicrosoftFolder is { } msSource)
        {
            var account = msSource.Account;
            if (!account.CanWriteMicrosoftMail)
            {
                await ExplainMailActionAsync("This saved Microsoft sign-in has read-only mail access. Sign in again to allow organizing mail.", account);
                return;
            }
            if (_mailActionBusy) return;
            _mailActionBusy = true;
            var done = 0;
            try
            {
                var writer = new GraphMailWriter(_graphHttp, account.AccountId);
                var token = await GetMicrosoftSession(account).GetAccessTokenAsync();
                foreach (var id in messageIds)
                {
                    StatusText.Text = $"{(copy ? "Copying" : "Moving")} {done + 1} of {messageIds.Count}…";
                    if (copy) await writer.CopyAsync(token, id, msDest.Id); else await writer.MoveAsync(token, id, msDest.Id);
                    done++;
                }
                StatusText.Text = done == 1 ? $"Message {(copy ? "copied" : "moved")} to {msDest.Name}." : $"{done} messages {(copy ? "copied" : "moved")} to {msDest.Name}.";
            }
            catch (GraphMailException error)
            {
                StatusText.Text = (done > 0 ? $"{(copy ? "Copied" : "Moved")} {done} of {messageIds.Count}. " : "") +
                    (error.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized
                        ? "Microsoft declined mail access. Use Account setup to sign in again, then retry." : error.Message);
            }
            catch (Exception) { StatusText.Text = "Mail action failed. Check the connection and retry."; }
            finally { _mailActionBusy = false; }
            if (_activeMicrosoftFolder == msSource) await RefreshMicrosoftFolderAsync();
        }
    }

    private static TreeViewItem? FindMicrosoftNode(TreeViewItem from, string folderId)
    {
        foreach (var child in from.Items.OfType<TreeViewItem>())
        {
            if (child.Tag is MicrosoftFolderSelection s && s.Id == folderId) return child;
            if (FindMicrosoftNode(child, folderId) is { } deeper) return deeper;
        }
        return null;
    }
}
