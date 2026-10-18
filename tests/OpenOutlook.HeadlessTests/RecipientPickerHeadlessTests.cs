using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using OpenOutlook.Desktop;
using Xunit;

namespace OpenOutlook.HeadlessTests;

/// <summary>To / Cc suggestions and the Select Names dialog in the compose window (owner 2026-10-17, Outlook's screenshots).</summary>
public sealed class RecipientPickerHeadlessTests
{
    private static readonly RecipientEntry[] Contacts =
    [
        new("Petri, Bryan", "Bryan.Petri@pse.com", RecipientSource.Contact),
        new("Michael Bryan", "michael.bryan@intel.com", RecipientSource.Contact),
        new("rick marrash", "rmarrash@beaconpm.com", RecipientSource.Contact),
    ];
    private static readonly RecipientEntry[] Recent =
    [
        new("'Jessica Marrash'", "jessica.marrash@gmail.com", RecipientSource.Recent, DateTimeOffset.UtcNow.AddHours(-1), 3),
        new("Imperial Valley Navy League", "ivnavyleague@yahoo.com", RecipientSource.Recent, DateTimeOffset.UtcNow.AddDays(-2), 1),
    ];

    private sealed class Backend : IComposeBackend
    {
        public string Address => "me@hotmail.test";
        public string Kind => "Microsoft";
        public long MaxAttachmentBytes => 1 << 20;
        public bool CanReopenDrafts => false;
        public string SavedMessage => "saved";
        public Task<LoadedDraft> LoadAsync(string draftId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SavedDraft> SaveAsync(string? draftId, ComposeDraft draft, IReadOnlyList<ComposeFile> files, IProgress<string> progress, CancellationToken ct = default) => Task.FromResult(new SavedDraft("d", null));
        public Task SendAsync(string? draftId, ComposeDraft draft, IReadOnlyList<ComposeFile> files, IProgress<string> progress, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveServerFileAsync(string draftId, string serverId, CancellationToken ct = default) => Task.CompletedTask;
    }

    [AvaloniaFact]
    public async Task Clicking_into_to_lists_recent_addresses_typing_narrows_and_a_pick_fills_the_box()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oo-picker-" + Guid.NewGuid().ToString("N"));
        var accounts = new List<ComposeAccount> { new("me@hotmail.test", "Microsoft", null, () => new Backend()) };
        var window = new ComposeWindow(accounts, accounts[0])
        {
            Width = 1000, Height = 760, RecentRecipients = new RecentRecipientsStore(dir),
            RecipientLookup = _ => Task.FromResult<(IReadOnlyList<RecipientEntry>, IReadOnlyList<RecipientEntry>)>((Contacts, Recent)),
        };
        string? sentFrom = null; IReadOnlyList<string> sentTo = [];
        window.Sent += (from, to) => { sentFrom = from; sentTo = to; };
        try
        {
            window.Show();
            await window.LoadRecipientsAsync();
            Dispatcher.UIThread.RunJobs();
            var to = window.FieldBox("To");
            var suggest = window.SuggestionsFor("To");
            to.Focus();
            suggest.Update();
            Assert.True(suggest.IsOpen);
            Assert.Equal(["jessica.marrash@gmail.com", "ivnavyleague@yahoo.com"], suggest.Shown.Select(e => e.Address));   // most recent first
            to.Text = "bry";
            suggest.Update();
            Assert.Equal(["Michael Bryan", "Petri, Bryan"], suggest.Shown.Select(e => e.Name));
            suggest.Accept(suggest.Shown[1]);
            Assert.Equal("Bryan.Petri@pse.com; ", to.Text);
            Assert.False(suggest.IsOpen);
            suggest.Update();                                                                         // the next part: already added ones are not offered again
            Assert.DoesNotContain(suggest.Shown, e => e.Address == "Bryan.Petri@pse.com");

            window.ApplySelectedNames("", "rmarrash@beaconpm.com", "jessica.marrash@gmail.com");      // what Select Names hands back
            Assert.Equal("rmarrash@beaconpm.com; ", window.FieldBox("Cc").Text);
            Assert.Equal("jessica.marrash@gmail.com; ", window.FieldBox("Bcc").Text);
            Assert.True(window.FieldBox("Bcc").IsEffectivelyVisible);                                 // the Bcc row opens

            to.Text = "Bryan.Petri@pse.com; rick marrash";                                          // Check Names (Ctrl+K)
            Assert.True(window.CheckNames());
            Assert.Equal("Bryan.Petri@pse.com; rmarrash@beaconpm.com; ", to.Text);
            window.FieldBox("Cc").Text = "bryan";
            Assert.False(window.CheckNames());                                                       // two Bryans: left for the user, and Send would stop
            window.FieldBox("Cc").Text = "rmarrash@beaconpm.com; ";
            to.Text = "Bryan.Petri@pse.com; ";

            // a send remembers the addresses for next time
            var content = typeof(ComposeWindow).GetMethod("CurrentContent", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
            typeof(ComposeWindow).GetMethod("RememberRecipients", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [content]);
            var remembered = new RecentRecipientsStore(dir).Load("me@hotmail.test");
            Assert.Equal(["bryan.petri@pse.com", "jessica.marrash@gmail.com", "rmarrash@beaconpm.com"], remembered.Select(e => e.Address.ToLowerInvariant()).Order());
            Assert.Equal(("me@hotmail.test", 3), (sentFrom, sentTo.Count));                         // and tells the main window (Safe Senders)
            Assert.Equal("Petri, Bryan", remembered.Single(e => e.Address == "Bryan.Petri@pse.com").Name);
        }
        finally
        {
            window.Close();
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact]
    public void Select_names_searches_contacts_or_recent_and_the_to_cc_bcc_buttons_fill_their_boxes()
    {
        var dialog = new SelectNamesWindow("rmarrash@hotmail.com", Contacts, Recent, "Cc");
        try
        {
            dialog.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Select Names: Contacts", dialog.Title);
            Assert.Equal(["Michael Bryan", "Petri, Bryan", "rick marrash"], dialog.Shown.Select(e => e.Name));     // contacts, by name
            Shot(dialog, "select-names");
            dialog.Search("pse");
            Assert.Equal(["Petri, Bryan"], dialog.Shown.Select(e => e.Name));
            dialog.Search("pse", nameOnly: true);
            Assert.Empty(dialog.Shown);                                                                 // "Name only" ignores the address
            dialog.Search("");
            dialog.Select(Contacts[0], Contacts[2]);
            dialog.AddSelected("To");
            dialog.AddSelected("To");                                                                   // twice: no duplicates
            Assert.Equal("Bryan.Petri@pse.com; rmarrash@beaconpm.com; ", dialog.Field("To"));
            dialog.ChooseBook(1);                                                                       // Recent recipients
            Assert.Equal(["jessica.marrash@gmail.com", "ivnavyleague@yahoo.com"], dialog.Shown.Select(e => e.Address));
            dialog.Select(dialog.Shown[0]);
            dialog.AddSelected("Bcc");
            dialog.Confirm();
            Assert.Equal(("Bryan.Petri@pse.com; rmarrash@beaconpm.com; ", "", "jessica.marrash@gmail.com; "), dialog.Result);
        }
        finally { dialog.Close(); }
    }

    private static void Shot(Window w, string name)
    {
        var dir = Environment.GetEnvironmentVariable("OO_HEADLESS_OUT");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        w.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        w.CaptureRenderedFrame()?.Save(Path.Combine(dir, name + ".png"));
    }
}
