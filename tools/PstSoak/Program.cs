using System.Text.RegularExpressions;
using OpenOutlook.PstNative;
using PstCore;

// PstSoak: applies a long, seeded, random sequence of real engine operations to a COPY of a known-clean archive and runs the integrity
// checker as it goes, so a corrupting operation is caught at the operation that introduced it (the log of the last operations is printed).
//   PstSoak FILE.pst [--seed N] [--ops N] [--check-every N] [--second OTHER.pst] [--keep OUT.pst]
// Both archives must already be clean (checker findings = 0; confirm with SCANPST first). The input is never modified. Afterwards run SCANPST on the --keep copy for the independent verdict.

string? file = null, second = null, keep = null;
int seed = 1, ops = 300, every = 1;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--seed": seed = int.Parse(args[++i]); break;
        case "--ops": ops = int.Parse(args[++i]); break;
        case "--check-every": every = int.Parse(args[++i]); break;
        case "--second": second = args[++i]; break;
        case "--keep": keep = args[++i]; break;
        default: file = args[i]; break;
    }
}
if (file is null || !File.Exists(file)) { Console.Error.WriteLine("usage: PstSoak FILE.pst [--seed N] [--ops N] [--check-every N] [--second OTHER.pst] [--keep OUT.pst]"); return 2; }

string Copy(string src)
{
    var dst = Path.Combine(Path.GetTempPath(), "soak-" + Guid.NewGuid().ToString("N") + ".pst");
    File.Copy(src, dst);
    return dst;
}
void Cleanup(string p) { foreach (var f in new[] { p, p + ".lck", p + ".journal" }) try { File.Delete(f); } catch (IOException) { } }

static List<string> Findings(IPstEngine e) => e.VerifyIntegrity().Where(l => l.StartsWith("  "))
    .Select(l => Regex.Replace(l, @"0x[0-9a-fA-F]+|\d+", "#")).Distinct().ToList();

var work = Copy(file);
var other = second is null ? null : Copy(second);
var rng = new Random(seed);
var log = new List<string>();
IPstEngine eng = NativePstEngine.Open(work, write: true);
IPstEngine? eng2 = other is null ? null : NativePstEngine.Open(other, write: true);
var baseline = Findings(eng);
var baseline2 = eng2 is null ? [] : Findings(eng2);
Console.WriteLine($"baseline findings: {baseline.Count}" + (eng2 is null ? "" : $" / second {baseline2.Count}"));
if (baseline.Count > 0 || baseline2.Count > 0)
{
    Console.Error.WriteLine("REFUSING TO RUN: the starting file is not clean, so a later finding could not be blamed on an operation. Repair it first (SCANPST or wfix) and confirm with SCANPST.");
    foreach (var l in baseline.Concat(baseline2).Take(5)) Console.Error.WriteLine("   " + l);
    eng.Dispose(); eng2?.Dispose(); Cleanup(work); if (other is not null) Cleanup(other);
    return 3;
}
var created = new List<uint>();
var failures = 0;
var refused = 0;

bool Normal(MailFolder f) => (f.Nid & 0x1F) == 2 && f.ParentNid != 0 && f.Name.Length > 0;
List<MailFolder> Folders(IPstEngine e) => e.AllFolders().Where(Normal).ToList();
T Pick<T>(IReadOnlyList<T> l) => l[rng.Next(l.Count)];
MailSummary? PickMessage(IPstEngine e, out MailFolder? folder)
{
    var fs = Folders(e).Where(f => f.ContentCount > 0).ToList();
    folder = null;
    if (fs.Count == 0) return null;
    folder = Pick(fs);
    var ms = e.GetMessages(folder);
    return ms.Count == 0 ? null : Pick(ms);
}
MailImport Synthetic(int n)
{
    var m = new MailImport
    {
        Subject = $"soak {seed}/{n} é€", SenderName = "Soak Test", SenderEmail = "soak@example.test",
        BodyText = string.Join("\r\n", Enumerable.Range(0, rng.Next(1, 400)).Select(i => $"line {i} of soak message {n}")),
        BodyHtml = rng.Next(2) == 0 ? "" : $"<html><body><p>soak {n}</p></body></html>",
        Sent = DateTime.UtcNow.AddMinutes(-n), Received = DateTime.UtcNow.AddMinutes(-n), Read = rng.Next(2) == 0
    };
    m.Recipients.Add(new ImportRecipient("To Person", "to@example.test", RecipientKind.To));
    var na = rng.Next(0, 3);
    for (var i = 0; i < na; i++)
    {
        var data = new byte[rng.Next(1, 150_000)];
        rng.NextBytes(data);
        m.Attachments.Add(new ImportAttachment($"att{n}_{i}.bin", "application/octet-stream", "", data));
    }
    return m;
}

for (var n = 1; n <= ops; n++)
{
    string what;
    try
    {
        var r = rng.Next(100);
        if (r < 22)
        {
            var m = PickMessage(eng, out _);
            if (m is null) { what = "skip"; goto check; }
            var read = !m.IsRead;
            eng.SetReadState(m, read);
            what = $"read={read} 0x{m.Nid:X}";
        }
        else if (r < 32)
        {
            var m = PickMessage(eng, out _);
            if (m is null) { what = "skip"; goto check; }
            eng.SetFlagged(m, !m.Flagged);
            what = $"flag 0x{m.Nid:X}";
        }
        else if (r < 47)
        {
            var m = PickMessage(eng, out var from);
            var dests = Folders(eng).Where(f => f.Nid != from?.Nid).ToList();
            if (m is null || dests.Count == 0) { what = "skip"; goto check; }
            var d = Pick(dests);
            eng.MoveMessage(m, d);
            what = $"move 0x{m.Nid:X} -> {d.Name}";
        }
        else if (r < 57)
        {
            var m = PickMessage(eng, out _);
            var dests = Folders(eng);
            if (m is null || dests.Count == 0) { what = "skip"; goto check; }
            var d = Pick(dests);
            eng.CopyMessage(m, d);
            what = $"copy 0x{m.Nid:X} -> {d.Name}";
        }
        else if (r < 67)
        {
            var m = PickMessage(eng, out _);
            if (m is null) { what = "skip"; goto check; }
            eng.DeleteMessage(m);
            what = $"delete 0x{m.Nid:X}";
        }
        else if (r < 80)
        {
            var d = Pick(Folders(eng));
            var k = rng.Next(1, 4);
            eng.ImportMessages(d, Enumerable.Range(0, k).Select(i => Synthetic(n * 10 + i)).ToList());
            what = $"import {k} -> {d.Name}";
        }
        else if (r < 85)
        {
            var parents = Folders(eng);
            var p = Pick(parents);
            var nf = eng.CreateFolder(p.Nid, $"soak folder {seed}-{n}");
            created.Add(nf.Nid);
            what = $"create folder 0x{nf.Nid:X} under {p.Name}";
        }
        else if (r < 90)
        {
            var live = created.Where(c => eng.FindFolder(c) is not null).ToList();
            if (live.Count == 0) { what = "skip"; goto check; }
            var c = Pick(live);
            eng.DeleteFolder(c);
            what = $"delete folder 0x{c:X}";
        }
        else if (r < 97 && eng2 is not null)
        {
            var m = PickMessage(eng, out _);
            var d = Folders(eng2);
            if (m is null || d.Count == 0) { what = "skip"; goto check; }
            var dest = Pick(d);
            eng.CopyMessagesTo(eng2, dest, [m]);
            what = $"copy-to-other 0x{m.Nid:X} -> {dest.Name}";
        }
        else
        {
            eng.Dispose();
            eng2?.Dispose();
            eng = NativePstEngine.Open(work, write: true);
            if (other is not null) eng2 = NativePstEngine.Open(other, write: true);
            what = "close and reopen";
        }
    }
    catch (PstException ex) when (ex.Message.Contains("first.") || ex.Message.Contains("move or delete them") || ex.Message.Contains("create folders below"))
    {
        what = $"refused (by design): {ex.Message}";
        refused++;
    }
    catch (Exception ex)
    {
        what = $"EXCEPTION {ex.GetType().Name}: {ex.Message}";
        failures++;
    }
check:
    log.Add($"{n}: {what}");
    if (what.StartsWith("EXCEPTION")) Console.WriteLine($"op {log[^1]}");
    if (n % every != 0 && n != ops) continue;
    foreach (var (e, b, label) in new[] { (eng, baseline, "archive"), (eng2!, baseline2, "second") })
    {
        if (e is null) continue;
        var now = Findings(e);
        var added = now.Except(b).ToList();
        if (added.Count == 0) continue;
        failures++;
        Console.WriteLine($"NEW FINDINGS in {label} after op {n}:");
        foreach (var a in added.Take(8)) Console.WriteLine("   " + a);
        Console.WriteLine("last operations:");
        foreach (var l in log.TakeLast(6)) Console.WriteLine("   " + l);
        baseline = label == "archive" ? now : baseline;
        if (label == "second") baseline2 = now;
    }
}

eng.Dispose();
eng2?.Dispose();
if (keep is not null) { File.Copy(work, keep, true); Console.WriteLine($"kept the result: {keep}"); }
if (keep is not null && other is not null) { File.Copy(other, Path.ChangeExtension(keep, ".second.pst"), true); }
Cleanup(work);
if (other is not null) Cleanup(other);
Console.WriteLine($"seed {seed}: {ops} operations, {refused} refused by design, {failures} failure(s)");
return failures == 0 ? 0 : 1;
