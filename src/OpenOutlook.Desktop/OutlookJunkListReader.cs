using System.Diagnostics;
using OpenOutlook.JunkCleaner;
using OpenPst;

namespace OpenOutlook.Desktop;

/// <summary>
/// Reads Outlook desktop's Safe Senders, Safe Recipients and Blocked Senders from its offline copy of the mailbox
/// (<c>%LOCALAPPDATA%\Microsoft\Outlook\&lt;address&gt;.ost</c>; docs/junk-lists-plan.md section 1b). Read-only; only while Outlook is closed
/// (Outlook locks the file while it runs); Windows only. The file is never written.
/// </summary>
public static class OutlookJunkListReader
{
    public sealed record Result(JunkLists Lists, OutlookJunkOptions Options, string Source, long FileStamp);

    /// <summary>Why nothing was read (shown in Junk Email Options and the log), or null after a successful read.</summary>
    public sealed record Outcome(Result? Read, string? Problem, bool Unchanged = false);

    /// <summary>Outlook's offline file for <paramref name="address"/> in its default folder, or null.</summary>
    public static string? FindOfflineFile(string address, string? outlookFolder = null)
    {
        var folder = outlookFolder ?? (OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Outlook") : null);
        if (folder is null || string.IsNullOrWhiteSpace(address) || !Directory.Exists(folder)) return null;
        try
        {
            return Directory.EnumerateFiles(folder, "*.ost")
                .FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), address.Trim(), StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public static bool OutlookIsRunning()
    {
        try { var p = Process.GetProcessesByName("OUTLOOK"); foreach (var x in p) x.Dispose(); return p.Length > 0; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>
    /// Reads the lists for <paramref name="address"/>. Skips the read (Unchanged) when the file's last write time equals
    /// <paramref name="knownStamp"/>, unless <paramref name="force"/>.
    /// </summary>
    public static Outcome Read(string address, long knownStamp, bool force = false, string? outlookFolder = null, Func<bool>? outlookRunning = null)
    {
        if (!OperatingSystem.IsWindows() && outlookFolder is null) return new Outcome(null, "Outlook's lists can be read only on Windows.");
        var file = FindOfflineFile(address, outlookFolder);
        if (file is null) return new Outcome(null, $"Outlook desktop has no offline file for {address} on this computer.");
        long stamp;
        try { stamp = File.GetLastWriteTimeUtc(file).Ticks; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new Outcome(null, "Outlook's offline file cannot be read: " + e.Message); }
        if (!force && stamp == knownStamp) return new Outcome(null, null, Unchanged: true);
        if ((outlookRunning ?? OutlookIsRunning)()) return new Outcome(null, "Outlook is running; its lists are read the next time OpenOutlook starts while Outlook is closed.");
        try { return new Outcome(ReadFile(file) with { FileStamp = stamp }, null); }
        catch (PstException e) { return new Outcome(null, "Outlook's offline file could not be read: " + e.Message); }
        catch (FormatException e) { return new Outcome(null, "Outlook's junk rule could not be read: " + e.Message); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
        { return new Outcome(null, "Outlook's offline file could not be read: " + e.Message); }
    }

    /// <summary>Finds the "Junk E-mail Rule" (IPM.ExtendedRule.Message, provider "JunkEmailRule") among the hidden items of the mailbox folders.</summary>
    public static Result ReadFile(string path)
    {
        using var pst = new PstFile(path);
        foreach (var folder in Folders(pst, pst.RootFolder, 0))
        {
            IReadOnlyList<PstMessageRow> hidden;
            try { hidden = pst.AssociatedMessages(folder); }
            catch (PstException) { continue; }
            foreach (var row in hidden.Where(r => r.MessageClass.Equals("IPM.ExtendedRule.Message", StringComparison.OrdinalIgnoreCase)))
            {
                using var message = pst.OpenMessage(row.Nid);
                if (!string.Equals(message.Str(0x65EB), "JunkEmailRule", StringComparison.Ordinal)) continue;
                var condition = message.Binary(0x0E9A) ?? throw new FormatException("The junk rule has no condition.");
                var options = new OutlookJunkOptions(message.Int(0x6101, 0xFFFFFFFF) & 0xFFFFFFFF, message.Int(0x6100) != 0,
                    message.Int(0x6102) != 0, message.Int(0x6103) != 0);
                return new Result(OutlookJunkRuleParser.Parse(condition), options, path, 0);
            }
        }
        throw new FormatException("Outlook's offline file has no junk rule (Outlook may not have synchronized it yet).");
    }

    /// <summary>The Inbox holds the rule; the walk covers the top two levels (the Inbox is "Root - Mailbox > IPM_SUBTREE > Inbox").</summary>
    private static IEnumerable<uint> Folders(PstFile pst, uint parent, int depth)
    {
        if (depth > 2) yield break;
        IReadOnlyList<PstFolder> children;
        try { children = pst.Children(parent); }
        catch (PstException) { yield break; }
        foreach (var child in children.OrderBy(c => c.Name.Equals("Inbox", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
        {
            if (depth == 2 || child.Name.Equals("Inbox", StringComparison.OrdinalIgnoreCase)) yield return child.Nid;
            foreach (var nid in Folders(pst, child.Nid, depth + 1)) yield return nid;
        }
    }
}
