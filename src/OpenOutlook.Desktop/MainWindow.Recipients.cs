using OpenOutlook.Auth;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Desktop;

/// <summary>
/// What the compose window offers as recipients (owner 2026-10-17): the account's Microsoft contacts (read at most once an hour) and the addresses
/// it wrote to (recent-recipients.json: every send; for a Microsoft account the newest 200 Sent Items are read once per run while the list is short).
/// Gmail accounts have recent recipients only (Google contacts are not connected).
/// </summary>
public sealed partial class MainWindow
{
    private readonly Dictionary<string, (DateTime At, IReadOnlyList<RecipientEntry> Contacts)> _recipientContacts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _sentItemsRead = new(StringComparer.Ordinal);
    internal RecentRecipientsStore RecentRecipients { get; set; } = new();

    internal async Task<(IReadOnlyList<RecipientEntry> Contacts, IReadOnlyList<RecipientEntry> Recent)> LookupRecipientsAsync(string address)
    {
        ConnectedAccount? account;
        try { account = _accountRegistry.Load().FirstOrDefault(a => string.Equals(a.DisplayAddress, address, StringComparison.OrdinalIgnoreCase)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { account = null; }
        IReadOnlyList<RecipientEntry> contacts = [];
        if (account is { Provider: OAuthProvider.MicrosoftConsumers })
        {
            var token = await GetMicrosoftSession(account).GetAccessTokenAsync();
            if (_sentItemsRead.Add(account.AccountId) && RecentRecipients.Load(address).Count < 50)
            {
                try
                {
                    var sent = await new GraphMailWriter(_graphHttp, account.AccountId).ListSentRecipientsAsync(token, 200);
                    RecentRecipients.RecordMany(address, sent.Where(s => !string.Equals(s.Address, address, StringComparison.OrdinalIgnoreCase)).Select(s => (s.Name, s.Address, s.Sent)));
                    AppLog.Note("compose", $"recent recipients of {address}: {sent.Select(s => s.Address.ToLowerInvariant()).Distinct().Count()} address(es) read from Sent Items");
                }
                catch (Exception e) when (e is GraphMailException or HttpRequestException or ArgumentException or IOException or OperationCanceledException)
                { AppLog.Note("compose", "Sent Items not read for suggestions: " + e.Message); }
            }
            if (account.CanManageMicrosoftContacts)
            {
                if (_recipientContacts.TryGetValue(account.AccountId, out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromHours(1)) contacts = cached.Contacts;
                else
                {
                    var client = new GraphContactsClient(_graphHttp, account.AccountId);
                    var list = new List<RecipientEntry>();
                    foreach (var folder in (await client.ListFoldersAsync(token)).Take(20))
                        foreach (var c in await client.ListContactsAsync(token, folder))
                            foreach (var e in c.Emails.Where(e => !string.IsNullOrWhiteSpace(e.Address)))
                                list.Add(new RecipientEntry(string.IsNullOrWhiteSpace(c.DisplayName) ? e.Name : c.DisplayName, e.Address.Trim(), RecipientSource.Contact));
                    contacts = list;
                    _recipientContacts[account.AccountId] = (DateTime.UtcNow, contacts);
                }
            }
        }
        return (contacts, RecentRecipients.Load(address));
    }
}
