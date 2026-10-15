using System.Text.RegularExpressions;
using Avalonia.Media;
using OpenOutlook.Providers.Microsoft;

namespace OpenOutlook.Desktop;

/// <summary>Name, address and display rules for contacts, shared by the People page and the contact window (kept free of UI so they can be tested).</summary>
public static partial class ContactFormat
{
    private static readonly string[] Prefixes = ["mr", "mrs", "ms", "miss", "dr", "prof", "rev", "sir"];
    private static readonly string[] Suffixes = ["jr", "sr", "ii", "iii", "iv", "phd", "md", "esq"];

    public sealed record NameParts(string Title, string Given, string Middle, string Surname, string Suffix);

    /// <summary>Splits a Full Name the way Outlook's Check Full Name does: "Dr. Ann B. Example Jr.", "Example, Ann B." and single names.</summary>
    public static NameParts ParseFullName(string? fullName)
    {
        var text = Regex.Replace(fullName ?? "", @"\s+", " ").Trim();
        if (text.Length == 0) return new NameParts("", "", "", "", "");
        string title = "", suffix = "";
        if (text.Contains(','))
        {
            var comma = text.IndexOf(',');
            var last = text[..comma].Trim();
            var rest = text[(comma + 1)..].Trim();
            var restWords = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (restWords.Count > 0 && IsWord(restWords[^1], Suffixes)) { suffix = restWords[^1]; restWords.RemoveAt(restWords.Count - 1); }
            if (restWords.Count > 0 && IsWord(restWords[0], Prefixes)) { title = restWords[0]; restWords.RemoveAt(0); }
            if (restWords.Count == 0 && IsWord(rest, Suffixes)) return new NameParts("", "", "", last, rest);
            return new NameParts(title, restWords.FirstOrDefault() ?? "", string.Join(" ", restWords.Skip(1)), last, suffix);
        }
        var words = text.Split(' ').ToList();
        if (words.Count > 1 && IsWord(words[0], Prefixes)) { title = words[0]; words.RemoveAt(0); }
        if (words.Count > 1 && IsWord(words[^1], Suffixes)) { suffix = words[^1]; words.RemoveAt(words.Count - 1); }
        return words.Count switch
        {
            1 => new NameParts(title, words[0], "", "", suffix),
            2 => new NameParts(title, words[0], "", words[1], suffix),
            _ => new NameParts(title, words[0], string.Join(" ", words.Skip(1).Take(words.Count - 2)), words[^1], suffix)
        };
    }

    private static bool IsWord(string word, string[] list) => list.Contains(word.TrimEnd('.').ToLowerInvariant());

    /// <summary>"Title Given Middle Surname Suffix", the contact's own display name when it has no name parts.</summary>
    public static string FullName(GraphContact c)
    {
        var parts = string.Join(" ", new[] { c.Title, c.GivenName, c.MiddleName, c.Surname, c.Suffix }.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()));
        return parts.Length > 0 ? parts : c.DisplayName.Trim();
    }

    /// <summary>The name shown in lists: the display name, else the full name, else the company, else the first email address.</summary>
    public static string ShownName(GraphContact c)
    {
        if (!string.IsNullOrWhiteSpace(c.DisplayName)) return c.DisplayName.Trim();
        var full = FullName(c);
        if (full.Length > 0) return full;
        if (!string.IsNullOrWhiteSpace(c.CompanyName)) return c.CompanyName.Trim();
        return c.Emails.FirstOrDefault()?.Address ?? "(no name)";
    }

    /// <summary>The File as choices Outlook offers for a name and company.</summary>
    public static IReadOnlyList<string> FileAsChoices(string given, string surname, string company)
    {
        given = given.Trim(); surname = surname.Trim(); company = company.Trim();
        var choices = new List<string>();
        void Add(string value) { if (value.Length > 0 && !choices.Contains(value)) choices.Add(value); }
        if (surname.Length > 0 && given.Length > 0) Add(surname + ", " + given);
        Add(string.Join(" ", new[] { given, surname }.Where(p => p.Length > 0)));
        Add(company);
        if (company.Length > 0 && surname.Length > 0 && given.Length > 0)
        {
            Add(surname + ", " + given + " (" + company + ")");
            Add(company + " (" + surname + ", " + given + ")");
        }
        return choices;
    }

    /// <summary>Sort key of the People list: File as, else "Surname, Given", else the shown name (Outlook's default order).</summary>
    public static string SortKey(GraphContact c)
    {
        if (!string.IsNullOrWhiteSpace(c.FileAs)) return c.FileAs.Trim();
        if (!string.IsNullOrWhiteSpace(c.Surname))
            return c.Surname.Trim() + (string.IsNullOrWhiteSpace(c.GivenName) ? "" : ", " + c.GivenName.Trim());
        return ShownName(c);
    }

    /// <summary>The letter of the jump index a contact belongs to: "a".."z", or "123" for anything else.</summary>
    public static string IndexKey(string sortKey)
    {
        var first = sortKey.TrimStart().FirstOrDefault();
        var folded = char.ToLowerInvariant(RemoveAccent(first));
        return folded is >= 'a' and <= 'z' ? folded.ToString() : "123";
    }

    private static char RemoveAccent(char c)
    {
        if (c < 128) return c;
        var decomposed = c.ToString().Normalize(System.Text.NormalizationForm.FormD);
        return decomposed.Length > 0 && decomposed[0] < 128 ? decomposed[0] : c;
    }

    /// <summary>One or two initials for the round avatar ("Ann Example" -> "AE", "Coworkers" -> "C").</summary>
    public static string Initials(string name)
    {
        var words = Regex.Split(name.Trim(), @"[\s,]+").Where(w => w.Length > 0 && char.IsLetterOrDigit(w[0])).ToArray();
        if (words.Length == 0) return "?";
        var first = char.ToUpperInvariant(words[0][0]).ToString();
        return words.Length == 1 ? first : first + char.ToUpperInvariant(words[^1][0]);
    }

    private static readonly Color[] AvatarColors =
    [
        Color.FromRgb(0x8E, 0x56, 0xC2), Color.FromRgb(0x5B, 0x6E, 0x7D), Color.FromRgb(0x7A, 0x14, 0x2C), Color.FromRgb(0xC2, 0x39, 0xB3),
        Color.FromRgb(0x46, 0x6A, 0xE8), Color.FromRgb(0xCA, 0x50, 0x10), Color.FromRgb(0x03, 0x83, 0x87), Color.FromRgb(0x49, 0x82, 0x05),
        Color.FromRgb(0xE3, 0x00, 0x8C), Color.FromRgb(0x00, 0x5B, 0x70), Color.FromRgb(0x8B, 0x5C, 0x2B), Color.FromRgb(0x69, 0x79, 0x7E)
    ];

    /// <summary>A stable colour per name (the same contact always gets the same circle).</summary>
    public static Color AvatarColor(string name)
    {
        uint hash = 2166136261;
        foreach (var ch in name.ToLowerInvariant()) hash = (hash ^ ch) * 16777619;
        return AvatarColors[hash % (uint)AvatarColors.Length];
    }

    /// <summary>The line under a name in the list: first email address, else first phone number.</summary>
    public static string SubLine(GraphContact c) =>
        c.Emails.FirstOrDefault()?.Address ?? c.BusinessPhones.Concat(c.HomePhones).Append(c.MobilePhone).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) ?? "";

    /// <summary>"street\ncity, state postal code\ncountry", the way the address box shows it.</summary>
    public static string FormatAddress(GraphPostalAddress a)
    {
        var lines = new List<string>();
        if (a.Street.Trim().Length > 0) lines.AddRange(a.Street.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
        var cityLine = a.City.Trim();
        var statePostal = string.Join(" ", new[] { a.State.Trim(), a.PostalCode.Trim() }.Where(p => p.Length > 0));
        if (statePostal.Length > 0) cityLine = cityLine.Length > 0 ? cityLine + ", " + statePostal : statePostal;
        if (cityLine.Length > 0) lines.Add(cityLine);
        if (a.CountryOrRegion.Trim().Length > 0) lines.Add(a.CountryOrRegion.Trim());
        return string.Join("\n", lines);
    }

    /// <summary>Reads the address box back: the last line is the country when it has no digits and three or more lines are given,
    /// the line before is "City, State Postal" (or "City ST 12345"), everything above it is the street.</summary>
    public static GraphPostalAddress ParseAddress(string? text)
    {
        var lines = (text ?? "").Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0) return GraphPostalAddress.Empty;
        var country = "";
        if (lines.Count >= 3 && !lines[^1].Any(char.IsDigit) && !lines[^1].Contains(',')) { country = lines[^1]; lines.RemoveAt(lines.Count - 1); }
        string city = "", state = "", postal = "";
        if (lines.Count >= 2)
        {
            var last = lines[^1];
            var withComma = CityCommaLine().Match(last);
            var usStyle = CityStateZipLine().Match(last);
            if (withComma.Success)
            {
                city = withComma.Groups["city"].Value.Trim();
                var rest = withComma.Groups["rest"].Value.Trim();
                var postalMatch = PostalAtEnd().Match(rest);
                if (postalMatch.Success) { postal = postalMatch.Groups["postal"].Value; state = rest[..postalMatch.Index].Trim(); }
                else state = rest;
                lines.RemoveAt(lines.Count - 1);
            }
            else if (usStyle.Success)
            {
                city = usStyle.Groups["city"].Value.Trim(); state = usStyle.Groups["state"].Value; postal = usStyle.Groups["postal"].Value;
                lines.RemoveAt(lines.Count - 1);
            }
        }
        return new GraphPostalAddress(string.Join("\n", lines), city, state, postal, country);
    }

    [GeneratedRegex(@"^(?<city>[^,]+),\s*(?<rest>.*)$")]
    private static partial Regex CityCommaLine();
    [GeneratedRegex(@"^(?<city>.+?)\s+(?<state>[A-Z]{2})\s+(?<postal>\d{5}(?:-\d{4})?)$")]
    private static partial Regex CityStateZipLine();
    [GeneratedRegex(@"(?<postal>[A-Za-z0-9]*\d[A-Za-z0-9 -]*)$")]
    private static partial Regex PostalAtEnd();
}
