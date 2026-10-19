using System.Text;
using System.Text.Json;

namespace OpenOutlook.JunkCleaner;

/// <summary>The lists of Outlook's Junk Email Options (docs/junk-lists-plan.md).</summary>
public enum JunkListKind { SafeSenders, SafeRecipients, BlockedSenders, BlockedTopLevelDomains }

/// <summary>What the lists say about one message. Safe wins over blocked, as in Outlook.</summary>
public enum JunkListVerdict { None, Safe, Blocked }

/// <summary>
/// One set of the four lists. Entries are normalised: lower case, an address (<c>name@example.com</c>), a domain (<c>@example.com</c>; a bare
/// <c>example.com</c> is read as that), or for the top-level domain list a suffix without the dot (<c>ru</c>).
/// </summary>
public sealed record JunkLists
{
    public const int MaxEntriesPerList = 20000;
    public const int MaxEntryLength = 320;

    public IReadOnlyList<string> SafeSenders { get; init; } = [];
    public IReadOnlyList<string> SafeRecipients { get; init; } = [];
    public IReadOnlyList<string> BlockedSenders { get; init; } = [];
    public IReadOnlyList<string> BlockedTopLevelDomains { get; init; } = [];

    public static readonly JunkLists Empty = new();

    public IReadOnlyList<string> Get(JunkListKind kind) => kind switch
    {
        JunkListKind.SafeSenders => SafeSenders,
        JunkListKind.SafeRecipients => SafeRecipients,
        JunkListKind.BlockedSenders => BlockedSenders,
        _ => BlockedTopLevelDomains,
    };

    public JunkLists With(JunkListKind kind, IEnumerable<string> entries)
    {
        var list = Normalize(kind, entries);
        return kind switch
        {
            JunkListKind.SafeSenders => this with { SafeSenders = list },
            JunkListKind.SafeRecipients => this with { SafeRecipients = list },
            JunkListKind.BlockedSenders => this with { BlockedSenders = list },
            _ => this with { BlockedTopLevelDomains = list },
        };
    }

    /// <summary>Both sets of entries (duplicates once), each list sorted as Outlook shows it: domains first, then addresses.</summary>
    public JunkLists Union(JunkLists other) => new JunkLists()
        .With(JunkListKind.SafeSenders, SafeSenders.Concat(other.SafeSenders))
        .With(JunkListKind.SafeRecipients, SafeRecipients.Concat(other.SafeRecipients))
        .With(JunkListKind.BlockedSenders, BlockedSenders.Concat(other.BlockedSenders))
        .With(JunkListKind.BlockedTopLevelDomains, BlockedTopLevelDomains.Concat(other.BlockedTopLevelDomains));

    /// <summary>These lists without the given entries (per list).</summary>
    public JunkLists Without(JunkLists removed) => new JunkLists()
        .With(JunkListKind.SafeSenders, SafeSenders.Except(removed.SafeSenders))
        .With(JunkListKind.SafeRecipients, SafeRecipients.Except(removed.SafeRecipients))
        .With(JunkListKind.BlockedSenders, BlockedSenders.Except(removed.BlockedSenders))
        .With(JunkListKind.BlockedTopLevelDomains, BlockedTopLevelDomains.Except(removed.BlockedTopLevelDomains));

    public int Count => SafeSenders.Count + SafeRecipients.Count + BlockedSenders.Count + BlockedTopLevelDomains.Count;

    /// <summary>The list an entry cannot be on at the same time (Safe Senders and Blocked Senders exclude each other); null for the others.</summary>
    public static JunkListKind? Opposite(JunkListKind kind) => kind switch
    {
        JunkListKind.SafeSenders => JunkListKind.BlockedSenders,
        JunkListKind.BlockedSenders => JunkListKind.SafeSenders,
        _ => null,
    };

    /// <summary>
    /// The verdict for a message: Safe when the sender is a safe sender or any recipient a safe recipient (or the sender is a contact and
    /// <paramref name="trustedContacts"/> is given); else Blocked when the sender is a blocked sender or ends with a blocked top-level domain.
    /// </summary>
    public JunkListVerdict Classify(string? sender, IEnumerable<string>? recipients = null, ISet<string>? trustedContacts = null)
    {
        var from = Address(sender);
        if (from is not null && (Matches(SafeSenders, from) || trustedContacts?.Contains(from) == true)) return JunkListVerdict.Safe;
        if (recipients is not null && recipients.Select(Address).Any(r => r is not null && Matches(SafeRecipients, r))) return JunkListVerdict.Safe;
        if (from is null) return JunkListVerdict.None;
        if (Matches(BlockedSenders, from)) return JunkListVerdict.Blocked;
        var at = from.LastIndexOf('@');
        var domain = at >= 0 ? from[(at + 1)..] : from;
        if (BlockedTopLevelDomains.Any(t => domain.EndsWith("." + t, StringComparison.Ordinal))) return JunkListVerdict.Blocked;
        return JunkListVerdict.None;
    }

    /// <summary>The entry of <paramref name="list"/> that covers <paramref name="address"/> (exact address, or its domain), or null.</summary>
    public static string? MatchingEntry(IReadOnlyList<string> list, string? address)
    {
        var a = Address(address);
        if (a is null) return null;
        var at = a.LastIndexOf('@');
        var domain = at >= 0 ? a[at..] : null;
        return list.FirstOrDefault(e => e == a || domain is not null && e == domain);
    }

    private static bool Matches(IReadOnlyList<string> list, string address) => MatchingEntry(list, address) is not null;

    /// <summary>A bare lower-case address from "Name &lt;a@b&gt;" or "a@b"; null when there is none.</summary>
    public static string? Address(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        var lt = v.LastIndexOf('<');
        if (lt >= 0 && v.EndsWith('>')) v = v[(lt + 1)..^1];
        v = v.Trim().Trim('"', '\'').ToLowerInvariant();
        return v.Contains('@') && !v.Any(char.IsWhiteSpace) ? v : null;
    }

    /// <summary>One entry as it is stored, or null when it cannot be one (empty, spaces, control characters, too long).</summary>
    public static string? NormalizeEntry(JunkListKind kind, string? entry)
    {
        if (string.IsNullOrWhiteSpace(entry)) return null;
        var e = entry.Trim().ToLowerInvariant();
        if (e.Length > MaxEntryLength || e.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '<' or '>' or ',' or ';' or '"')) return null;
        if (kind == JunkListKind.BlockedTopLevelDomains)
        {
            e = e.TrimStart('.');
            return e.Length is > 0 and <= 63 && e.All(c => char.IsLetterOrDigit(c) || c == '-') ? e : null;
        }
        if (e.StartsWith("*@", StringComparison.Ordinal)) e = e[1..];                                       // "*@example.com" as some tools write it
        if (!e.Contains('@')) e = "@" + e;                                                                     // "example.com" = the whole domain
        var at = e.IndexOf('@');
        if (at != e.LastIndexOf('@')) return null;
        var domain = e[(at + 1)..];
        return domain.Length > 0 && domain.Contains('.') && !domain.StartsWith('.') && !domain.EndsWith('.') ? e : null;
    }

    /// <summary>Normalised, without duplicates, domains first then addresses (each alphabetical, like Outlook), at most <see cref="MaxEntriesPerList"/>.</summary>
    public static IReadOnlyList<string> Normalize(JunkListKind kind, IEnumerable<string?> entries) =>
        entries.Select(e => NormalizeEntry(kind, e)).OfType<string>().Distinct(StringComparer.Ordinal)
            .OrderBy(e => e.StartsWith('@') ? 0 : 1).ThenBy(e => e, StringComparer.Ordinal).Take(MaxEntriesPerList).ToList();

    /// <summary>Outlook's "Import from File" format: a text file with one entry per line (UTF-8, UTF-16 or ANSI). Returns the entries it could read.</summary>
    public static IReadOnlyList<string> ParseTextFile(JunkListKind kind, byte[] bytes)
    {
        string text;
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) text = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        else if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        else text = Encoding.UTF8.GetString(bytes);
        return Normalize(kind, text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')));
    }

    /// <summary>Outlook's "Export to File" format: one entry per line, CRLF, UTF-8.</summary>
    public static byte[] ToTextFile(IEnumerable<string> entries) =>
        Encoding.UTF8.GetBytes(string.Join("\r\n", entries) + "\r\n");
}

/// <summary>The Outlook settings on the junk email rule (Options tab). Read from Outlook; OpenOutlook does not change Microsoft's filter.</summary>
public sealed record OutlookJunkOptions(long Threshold, bool TrustContacts, bool PermanentlyDelete, bool AddRecipientsToSafeSenders)
{
    /// <summary>Outlook's protection level for the threshold (PidTagJunkThreshold).</summary>
    public string Level => Threshold switch
    {
        0xFFFFFFFF or -1 => "No Automatic Filtering",
        6 => "Low",
        3 => "High",
        int.MinValue or 0x80000000 => "Safe Lists Only",
        _ => $"Custom ({Threshold})",
    };
}

/// <summary>
/// Reads the lists out of Outlook's "Junk E-mail Rule" ([MS-OXCSPAM]): <c>PidTagExtendedRuleMessageCondition</c> (0x0E9A) holds a restriction
/// tree; every list entry is a content restriction on the sender's address (0x0C1F001F) or, under the recipients subrestriction, on a
/// recipient's address (0x3003001F). Entries outside a NOT are blocked senders, inside a NOT safe senders / safe recipients. Bounded: a
/// damaged blob throws <see cref="FormatException"/> instead of reading past its end.
/// </summary>
public static class OutlookJunkRuleParser
{
    private const uint SenderEmail = 0x0C1F001F, RecipientEmail = 0x3003001F, SubRecipients = 0x0E12000D;
    private const int MaxDepth = 32, MaxNodes = 200000;

    public static JunkLists Parse(byte[] condition)
    {
        var reader = new Reader(condition);
        var named = reader.U16();
        for (var i = 0; i < named; i++)
        {
            var kind = reader.U8(); reader.Skip(16);
            if (kind == 0) reader.Skip(4); else reader.Skip(reader.U8());
        }
        var safeSenders = new List<string>(); var safeRecipients = new List<string>(); var blocked = new List<string>();
        var nodes = 0;
        void Walk(int depth, bool negated, bool recipients)
        {
            if (depth > MaxDepth || ++nodes > MaxNodes) throw new FormatException("The junk rule is nested too deeply.");
            var type = reader.U8();
            switch (type)
            {
                case 0: case 1:                                                                                  // AND / OR
                    var n = reader.U32();
                    if (n > MaxNodes) throw new FormatException("The junk rule has too many entries.");
                    for (var i = 0u; i < n; i++) Walk(depth + 1, negated, recipients);
                    break;
                case 2: Walk(depth + 1, !negated, recipients); break;                                         // NOT
                case 3:                                                                                          // CONTENT
                    reader.Skip(4);
                    var tag = reader.U32();
                    var value = reader.Value(reader.U32());
                    if (value is not string text) break;
                    if (tag == SenderEmail && !recipients) (negated ? safeSenders : blocked).Add(text);
                    else if (tag == RecipientEmail && recipients && negated) safeRecipients.Add(text);
                    break;
                case 4: reader.Skip(1); reader.Skip(4); reader.Value(reader.U32()); break;                    // PROPERTY
                case 5: reader.Skip(9); break;                                                                  // COMPAREPROPS
                case 6: reader.Skip(1); reader.Skip(4); reader.Skip(4); break;                                // BITMASK
                case 7: reader.Skip(1); reader.Skip(4); reader.Skip(4); break;                                // SIZE
                case 8: reader.Skip(4); break;                                                                  // EXIST
                case 9:                                                                                          // SUBRESTRICTION
                    var sub = reader.U32();
                    Walk(depth + 1, negated, recipients || sub == SubRecipients);
                    break;
                case 10:                                                                                         // COMMENT
                    var values = reader.U8();
                    for (var i = 0; i < values; i++) reader.Value(reader.U32());
                    if (reader.U8() != 0) Walk(depth + 1, negated, recipients);
                    break;
                case 11: reader.Skip(4); Walk(depth + 1, negated, recipients); break;                         // COUNT
                default: throw new FormatException($"Unknown restriction type {type} in the junk rule.");
            }
        }
        Walk(0, false, false);
        return new JunkLists()
            .With(JunkListKind.SafeSenders, safeSenders)
            .With(JunkListKind.SafeRecipients, safeRecipients)
            .With(JunkListKind.BlockedSenders, blocked);
    }

    private sealed class Reader(byte[] data)
    {
        private int _pos;
        private void Need(int n) { if (n < 0 || _pos + n > data.Length) throw new FormatException("The junk rule ends early."); }
        public byte U8() { Need(1); return data[_pos++]; }
        public ushort U16() { Need(2); var v = BitConverter.ToUInt16(data, _pos); _pos += 2; return v; }
        public uint U32() { Need(4); var v = BitConverter.ToUInt32(data, _pos); _pos += 4; return v; }
        public void Skip(int n) { Need(n); _pos += n; }

        public object? Value(uint tag)
        {
            switch (tag & 0xFFFF)
            {
                case 0x001F:
                    var end = _pos;
                    while (true) { Need(end - _pos + 2); if (data[end] == 0 && data[end + 1] == 0) break; end += 2; }
                    var s = Encoding.Unicode.GetString(data, _pos, end - _pos); _pos = end + 2; return s;
                case 0x001E:
                    var e8 = _pos;
                    while (true) { Need(e8 - _pos + 1); if (data[e8] == 0) break; e8++; }
                    var a = Encoding.Latin1.GetString(data, _pos, e8 - _pos); _pos = e8 + 1; return a;
                case 0x0002: case 0x000B: Skip(2); return null;
                case 0x0003: case 0x0004: case 0x000A: Skip(4); return null;
                case 0x0005: case 0x0006: case 0x0007: case 0x0014: case 0x0040: Skip(8); return null;
                case 0x0048: Skip(16); return null;
                case 0x0102: case 0x00FB: case 0x00FD: case 0x00FE: Skip(U16()); return null;
                default: throw new FormatException($"Unsupported value type 0x{tag & 0xFFFF:X4} in the junk rule.");
            }
        }
    }
}

/// <summary>
/// One account's junk lists, in <c>junk-lists.json</c> beside the other settings: OpenOutlook's own entries, what was last read from Outlook
/// (with where and when), and Outlook entries the user removed here (so they stay removed at the next read). The lists in effect are
/// (Outlook - removed) + own. A damaged file reads as empty.
/// </summary>
public sealed record JunkListsAccount
{
    public string AccountId { get; init; } = "";
    public JunkLists Own { get; init; } = JunkLists.Empty;
    public JunkLists FromOutlook { get; init; } = JunkLists.Empty;
    public JunkLists RemovedFromOutlook { get; init; } = JunkLists.Empty;
    public OutlookJunkOptions? OutlookOptions { get; init; }
    public string? OutlookSource { get; init; }
    public DateTimeOffset? OutlookReadAt { get; init; }
    /// <summary>Last write time of the Outlook file when it was read (UTC ticks): unchanged file = not read again.</summary>
    public long OutlookFileStamp { get; init; }
    /// <summary>Also trust email from my Contacts.</summary>
    public bool TrustContacts { get; init; }
    /// <summary>OpenOutlook applies the lists itself (safe mail out of Junk Email, blocked mail into it).</summary>
    public bool ApplyLists { get; init; } = true;
    /// <summary>Outlook's "Automatically add people I email to the Safe Senders List": every address a message is sent to from OpenOutlook.</summary>
    public bool AddRecipientsToSafeSenders { get; init; }
    /// <summary>Read Outlook's lists from its offline file when OpenOutlook starts (off: only "Read Outlook's lists now"; owner 2026-10-17).</summary>
    public bool ReadOutlookAtStart { get; init; }

    public JunkLists Effective => FromOutlook.Without(RemovedFromOutlook).Union(Own);

    /// <summary>The account after the user set <paramref name="kind"/> to <paramref name="entries"/> in the Junk Email Options window.</summary>
    public JunkListsAccount WithEdited(JunkListKind kind, IEnumerable<string> entries)
    {
        var wanted = JunkLists.Normalize(kind, entries);
        var outlook = FromOutlook.Get(kind);
        var removed = outlook.Except(wanted, StringComparer.Ordinal);
        var own = wanted.Except(outlook, StringComparer.Ordinal);
        return this with
        {
            Own = Own.With(kind, own),
            RemovedFromOutlook = RemovedFromOutlook.With(kind, removed),
        };
    }

    /// <summary>
    /// Adds entries to one list (right-click "Block Sender" / "Never Block Sender"); an Outlook entry removed earlier comes back. As in Outlook,
    /// an entry is on Safe Senders or on Blocked Senders, never both: adding it to one takes the same entry off the other (owner 2026-10-17:
    /// Block Sender, then Not Junk, left info@bloedelreserve.org on both).
    /// </summary>
    public JunkListsAccount WithAdded(JunkListKind kind, params string[] entries)
    {
        var added = JunkLists.Normalize(kind, entries);
        var account = WithEdited(kind, Effective.Get(kind).Concat(added));
        return JunkLists.Opposite(kind) is { } other
            ? account.WithEdited(other, account.Effective.Get(other).Except(added, StringComparer.Ordinal))
            : account;
    }
}

public sealed class JunkListsStore
{
    public const string FileName = "junk-lists.json";
    private const long MaximumBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public string Path { get; }

    public JunkListsStore(string settingsDirectory)
    {
        if (string.IsNullOrWhiteSpace(settingsDirectory)) throw new ArgumentException("A settings directory is required.", nameof(settingsDirectory));
        Path = System.IO.Path.Combine(System.IO.Path.GetFullPath(settingsDirectory), FileName);
    }

    public IReadOnlyDictionary<string, JunkListsAccount> LoadAll()
    {
        try
        {
            if (!File.Exists(Path) || new FileInfo(Path).Length > MaximumBytes) return new Dictionary<string, JunkListsAccount>();
            var list = JsonSerializer.Deserialize<List<JunkListsAccount>>(File.ReadAllText(Path), Json) ?? [];
            return list.Where(a => a is not null && !string.IsNullOrWhiteSpace(a.AccountId)).GroupBy(a => a.AccountId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => Clean(g.Last()), StringComparer.Ordinal);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        { return new Dictionary<string, JunkListsAccount>(); }
    }

    public JunkListsAccount Load(string accountId) =>
        LoadAll().TryGetValue(accountId, out var a) ? a : new JunkListsAccount { AccountId = accountId };

    public void Save(JunkListsAccount account)
    {
        if (string.IsNullOrWhiteSpace(account.AccountId)) throw new ArgumentException("An account id is required.", nameof(account));
        var all = new Dictionary<string, JunkListsAccount>(LoadAll(), StringComparer.Ordinal) { [account.AccountId] = Clean(account) };
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);
        var temporary = System.IO.Path.Combine(directory, $".junk-lists-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(all.Values.OrderBy(a => a.AccountId, StringComparer.Ordinal).ToList(), Json));
            File.Move(temporary, Path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>Entries that a hand-edited file may hold are normalised again on every load and save.</summary>
    private static JunkListsAccount Clean(JunkListsAccount a) => a with
    {
        Own = Renormalize(a.Own), FromOutlook = Renormalize(a.FromOutlook), RemovedFromOutlook = Renormalize(a.RemovedFromOutlook),
    };

    private static JunkLists Renormalize(JunkLists? l) => l is null ? JunkLists.Empty : new JunkLists()
        .With(JunkListKind.SafeSenders, l.SafeSenders ?? []).With(JunkListKind.SafeRecipients, l.SafeRecipients ?? [])
        .With(JunkListKind.BlockedSenders, l.BlockedSenders ?? []).With(JunkListKind.BlockedTopLevelDomains, l.BlockedTopLevelDomains ?? []);
}
