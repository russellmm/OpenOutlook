using OpenOutlook.PstNative;
using PstCore;
// imports probe messages (each differing from the baseline in one field) into the first normal folder of a copy
var T = new DateTime(2024, 3, 5, 10, 0, 0, DateTimeKind.Utc);
MailImport M(string label, Action<MailImport>? tweak = null)
{
    var m = new MailImport { Subject = "probe base", SenderName = "Sam Sender", SenderEmail = "sam@example.test", BodyText = "hello body",
        Sent = T, Received = T, MessageId = "<base@example.test>" };
    m.Recipients.Add(new ImportRecipient("Rita", "rita@example.test", RecipientKind.To));
    tweak?.Invoke(m);
    return m;
}
var list = new List<(string, MailImport)>
{
    ("base-1", M("")), ("base-2", M("")),
    ("subject", M("", m => m.Subject = "probe other")),
    ("RE-prefix", M("", m => m.Subject = "RE: probe base")),
    ("sent+1h", M("", m => { m.Sent = T.AddHours(1); m.Received = T.AddHours(1); })),
    ("body", M("", m => m.BodyText = "different body")),
    ("sender", M("", m => { m.SenderName = "Other"; m.SenderEmail = "other@example.test"; })),
    ("msgid", M("", m => m.MessageId = "<other@example.test>")),
    ("recipient", M("", m => { m.Recipients.Clear(); m.Recipients.Add(new ImportRecipient("Zed", "zed@example.test", RecipientKind.To)); })),
    ("read", M("", m => m.Read = true)),
};
using var e = NativePstEngine.Open(args[0], write: true);
var f = e.AllFolders().First(x => x.Name == "treasurydirect");
var res = e.ImportMessages(f, list.Select(x => x.Item2).ToList());
for (var i = 0; i < res.Count; i++) Console.WriteLine($"{list[i].Item1}\t0x{res[i].Nid:X}\tfolder {f.Name}");
