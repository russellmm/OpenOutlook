using System.Text.Json;

namespace OpenOutlook.Desktop;

public enum RecipientSource { Contact, Recent }

/// <summary>One address the To / Cc / Bcc suggestions and the Select Names dialog offer.</summary>
public sealed record RecipientEntry(string Name, string Address, RecipientSource Source, DateTimeOffset LastUsed = default, int Uses = 0)
{
    /// <summary>What the list shows in bold: the name, else the address.</summary>
    public string Shown => string.IsNullOrWhiteSpace(Name) ? Address : Name;

    /// <summary>Outlook's avatar letters: first and last word of the name ("Petri, Bryan" = PB), else the first letter of the address.</summary>
    public string Initials
    {
        get
        {
            var words = (Name ?? "").Replace(",", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.Trim('\'', '"', '(', ')', '<', '>')).Where(w => w.Length > 0 && char.IsLetterOrDigit(w[0])).ToArray();
            if (words.Length >= 2) return (char.ToUpperInvariant(words[0][0]) + "" + char.ToUpperInvariant(words[^1][0]));
            if (words.Length == 1) return char.ToUpperInvariant(words[0][0]).ToString();
            return Address.Length > 0 ? char.ToUpperInvariant(Address[0]).ToString() : "?";
        }
    }
}

/// <summary>
/// The addresses a message can go to (owner 2026-10-17, Outlook's screenshots): the account's contacts plus the addresses recently written to.
/// Clicking into To / Cc lists the most recent; typing narrows by the start of any word of the name or of the address.
/// </summary>
public static class RecipientDirectory
{
    public const int MaxSuggestions = 8;
    private static readonly char[] Separators = [';', ','];

    /// <summary>One entry per address (case ignored): a contact's name wins over a remembered one; the recent use is kept.</summary>
    public static IReadOnlyList<RecipientEntry> Merge(IEnumerable<RecipientEntry> contacts, IEnumerable<RecipientEntry> recent)
    {
        var byAddress = new Dictionary<string, RecipientEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in contacts.Where(c => Valid(c.Address))) byAddress.TryAdd(c.Address.Trim(), c with { Address = c.Address.Trim() });
        foreach (var r in recent.Where(r => Valid(r.Address)))
        {
            var key = r.Address.Trim();
            byAddress[key] = byAddress.TryGetValue(key, out var known)
                ? known with { LastUsed = Max(known.LastUsed, r.LastUsed), Uses = known.Uses + r.Uses, Name = string.IsNullOrWhiteSpace(known.Name) ? r.Name : known.Name }
                : r with { Address = key };
        }
        return byAddress.Values.ToList();
        static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
    }

    /// <summary>Suggestions for what is being typed: nothing typed = the most recently used addresses; else entries whose name or address has a word starting with it.</summary>
    public static IReadOnlyList<RecipientEntry> Suggest(IReadOnlyList<RecipientEntry> all, string? typed, IEnumerable<string>? exclude = null, int max = MaxSuggestions)
    {
        var skip = new HashSet<string>(exclude ?? [], StringComparer.OrdinalIgnoreCase);
        var q = (typed ?? "").Trim();
        IEnumerable<RecipientEntry> pool = all.Where(e => !skip.Contains(e.Address));
        if (q.Length == 0)
            return pool.Where(e => e.LastUsed != default).OrderByDescending(e => e.LastUsed).ThenByDescending(e => e.Uses).Take(max).ToList();
        return pool.Where(e => Matches(e, q))
            .OrderByDescending(e => e.LastUsed != default).ThenByDescending(e => e.LastUsed).ThenByDescending(e => e.Uses)
            .ThenBy(e => e.Shown, StringComparer.CurrentCultureIgnoreCase).Take(max).ToList();
    }

    /// <summary>Word starts: "bry" finds "Petri, Bryan" and "bryan.petri@pse.com"; "pse" finds the domain.</summary>
    public static bool Matches(RecipientEntry e, string q, bool nameOnly = false)
    {
        static IEnumerable<string> Words(string s) => s.Split([' ', ',', '.', '@', '_', '-', '\'', '"', '(', ')'], StringSplitOptions.RemoveEmptyEntries);
        if (Words(e.Name ?? "").Any(w => w.StartsWith(q, StringComparison.CurrentCultureIgnoreCase)) || (e.Name ?? "").StartsWith(q, StringComparison.CurrentCultureIgnoreCase))
            return true;
        var parts = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);                     // "bryan petri" finds "Petri, Bryan": every word starts a word of the name
        if (parts.Length > 1 && parts.All(p => Words(e.Name ?? "").Any(w => w.StartsWith(p, StringComparison.CurrentCultureIgnoreCase)))) return true;
        if (nameOnly) return false;
        return e.Address.StartsWith(q, StringComparison.OrdinalIgnoreCase) || Words(e.Address).Any(w => w.StartsWith(q, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The part of a field being typed: after the last ';' or ',' (leading spaces dropped).</summary>
    public static string CurrentToken(string? text)
    {
        var t = text ?? "";
        var cut = t.LastIndexOfAny(Separators);
        return t[(cut + 1)..].TrimStart();
    }

    /// <summary>The field with the part being typed replaced by <paramref name="address"/>, ready for the next ("a@b.com; ").</summary>
    public static string Accept(string? text, string address)
    {
        var t = text ?? "";
        var cut = t.LastIndexOfAny(Separators);
        var head = cut < 0 ? "" : t[..(cut + 1)].TrimEnd() + " ";
        return head + address.Trim() + "; ";
    }

    /// <summary>Appends addresses to a field that may already hold some (no duplicates).</summary>
    public static string Append(string? text, IEnumerable<string> addresses)
    {
        var present = Addresses(text).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var t = (text ?? "").TrimEnd();
        if (t.Length > 0 && !t.EndsWith(';') && !t.EndsWith(',')) t += ";";
        foreach (var a in addresses.Select(a => a.Trim()).Where(a => a.Length > 0 && present.Add(a)))
            t += (t.Length > 0 ? " " : "") + a + ";";
        return t.Length > 0 ? t + " " : "";
    }

    /// <summary>
    /// Check Names (Outlook's Ctrl+K): every part of a To / Cc / Bcc box that is not an email address is looked up - "Name &lt;a@b&gt;" keeps the
    /// address; otherwise a contact or recent recipient whose name is exactly the text, or else the only one that matches it, gives its address.
    /// Returns the box rewritten and what could not be resolved (with why).
    /// </summary>
    public static (string Text, IReadOnlyList<string> Unresolved) Resolve(string? text, IReadOnlyList<RecipientEntry> all)
    {
        var parts = new List<string>();
        var unresolved = new List<string>();
        foreach (var part in Addresses(text))
        {
            if (IsAddress(part)) { parts.Add(part); continue; }
            var lt = part.LastIndexOf('<');
            if (lt >= 0 && part.EndsWith('>') && IsAddress(part[(lt + 1)..^1].Trim())) { parts.Add(part[(lt + 1)..^1].Trim()); continue; }
            var q = part.Trim('"', '\'', ' ');
            var exact = all.Where(e => string.Equals((e.Name ?? "").Trim('"', '\'', ' '), q, StringComparison.CurrentCultureIgnoreCase)).DistinctBy(e => e.Address.ToLowerInvariant()).ToList();
            var matches = exact.Count > 0 ? exact : all.Where(e => Matches(e, q)).DistinctBy(e => e.Address.ToLowerInvariant()).ToList();
            if (matches.Count == 1) parts.Add(matches[0].Address);
            else
            {
                parts.Add(part);
                unresolved.Add(matches.Count == 0 ? $"\"{part}\" (no contact or recent recipient)" : $"\"{part}\" ({matches.Count} matches)");
            }
        }
        return (parts.Count == 0 ? "" : string.Join("; ", parts) + "; ", unresolved);
    }

    public static bool IsAddress(string text) =>
        text.Length <= 320 && !text.Any(char.IsWhiteSpace) && System.Net.Mail.MailAddress.TryCreate(text, out var a) && a.Address == text;

    public static IEnumerable<string> Addresses(string? text) =>
        (text ?? "").Split(Separators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static bool Valid(string? address) =>
        !string.IsNullOrWhiteSpace(address) && address.Length <= 320 && address.Contains('@') && !address.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));
}

/// <summary>
/// recent-recipients.json beside the other settings: per sending account, the addresses written to (to, cc, bcc) with the last time and how often.
/// At most <see cref="MaxPerAccount"/> per account (the least recent go); a damaged file reads as empty. Outlook's own list ("Recipient Cache") is not read.
/// </summary>
public sealed class RecentRecipientsStore
{
    public const int MaxPerAccount = 500;
    private const long MaxBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    public string Path { get; }

    public RecentRecipientsStore(string? configRoot = null)
    {
        var root = configRoot ?? Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(root)) root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        Path = System.IO.Path.Combine(root, "OpenOutlook", "recent-recipients.json");
    }

    private sealed record Row(string Name, string Address, DateTimeOffset LastUsed, int Uses);

    private Dictionary<string, List<Row>> LoadAll()
    {
        try
        {
            if (!File.Exists(Path) || new FileInfo(Path).Length > MaxBytes) return new(StringComparer.OrdinalIgnoreCase);
            var d = JsonSerializer.Deserialize<Dictionary<string, List<Row>>>(File.ReadAllText(Path), Json);
            return d is null ? new(StringComparer.OrdinalIgnoreCase) : new(d.Where(kv => kv.Value is not null), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        { return new(StringComparer.OrdinalIgnoreCase); }
    }

    public IReadOnlyList<RecipientEntry> Load(string account) =>
        LoadAll().TryGetValue(account, out var rows)
            ? rows.Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Address)).Select(r => new RecipientEntry(r.Name ?? "", r.Address, RecipientSource.Recent, r.LastUsed, r.Uses)).ToList()
            : [];

    /// <summary>Notes that <paramref name="account"/> wrote to these addresses at <paramref name="when"/> (a send).</summary>
    public void Record(string account, IEnumerable<(string Name, string Address)> used, DateTimeOffset when, bool countUse = true) =>
        RecordMany(account, used.Select(u => (u.Name, u.Address, when)), countUse);

    /// <summary>Notes several uses at their own times in one write (Sent Items read once).</summary>
    public void RecordMany(string account, IEnumerable<(string Name, string Address, DateTimeOffset When)> used, bool countUse = true)
    {
        var all = LoadAll();
        var rows = all.TryGetValue(account, out var r) ? r : all[account] = [];
        foreach (var (name, address, when) in used)
        {
            var a = (address ?? "").Trim();
            if (a.Length == 0 || a.Length > 320 || !a.Contains('@')) continue;
            var i = rows.FindIndex(x => string.Equals(x.Address, a, StringComparison.OrdinalIgnoreCase));
            if (i < 0) rows.Add(new Row(name?.Trim() ?? "", a, when, countUse ? 1 : 0));
            else rows[i] = rows[i] with
            {
                LastUsed = rows[i].LastUsed > when ? rows[i].LastUsed : when, Uses = rows[i].Uses + (countUse ? 1 : 0),
                Name = string.IsNullOrWhiteSpace(rows[i].Name) ? name?.Trim() ?? "" : rows[i].Name,
            };
        }
        all[account] = rows.OrderByDescending(x => x.LastUsed).Take(MaxPerAccount).ToList();
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);
        var temporary = System.IO.Path.Combine(directory, $".recent-recipients-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(all, Json));
            File.Move(temporary, Path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
