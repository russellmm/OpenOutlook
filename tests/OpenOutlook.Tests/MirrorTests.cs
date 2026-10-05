using OpenOutlook.Mirror;
using Xunit;

namespace OpenOutlook.Tests;

public sealed class MirrorLocationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oo-mirror-" + Guid.NewGuid().ToString("N"));
    public MirrorLocationTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void Defaults_are_mirroring_on_12_months_25_MB()
    {
        var s = new MirrorAccountSettings();
        Assert.True(s.Enabled);
        Assert.Equal(12, s.KeepMonths);
        Assert.Equal(25L * 1024 * 1024, s.MaxAttachmentBytes);
    }

    [Fact]
    public void Default_root_follows_the_operating_system_conventions()
    {
        var unix = MirrorLocations.DefaultRoot(Path.Combine(_dir, "data"));
        Assert.Equal(Path.Combine(_dir, "data", "openoutlook", "mail"), unix);
        var os = MirrorLocations.DefaultRoot();
        if (OperatingSystem.IsWindows()) Assert.EndsWith(Path.Combine("OpenOutlook", "Mail"), os);
        else Assert.EndsWith(Path.Combine("openoutlook", "mail"), os);
    }

    [Fact]
    public void Account_folder_prefers_override_then_chosen_default_then_os_default()
    {
        var chosen = Path.Combine(_dir, "chosen");
        var over = Path.Combine(_dir, "over");
        var settings = new MirrorSettings(chosen, new() { ["a"] = new MirrorAccountSettings(FolderOverride: over) });
        Assert.Equal(over, MirrorLocations.FolderFor(settings, "a"));
        Assert.Equal(chosen, MirrorLocations.FolderFor(settings, "b"));
        Assert.Equal(MirrorLocations.DefaultRoot(), MirrorLocations.FolderFor(MirrorSettings.Empty, "b"));
        Assert.Equal(Path.Combine(chosen, "me@example.org.pst"), MirrorLocations.PstPathFor(settings, "b", "me@example.org"));
    }

    [Theory]
    [InlineData("me@example.org", "me@example.org")]
    [InlineData("a/b\\c:d*e?f", "a_b_c_d_e_f")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("name. ", "name")]
    [InlineData("...", "account")]
    [InlineData("", "account")]
    public void File_names_are_valid_on_windows_and_linux(string input, string expected) => Assert.Equal(expected, MirrorLocations.SafeFileName(input));

    [Fact]
    public void Folder_check_creates_and_accepts_a_writable_folder_and_rejects_relative_paths()
    {
        var folder = Path.Combine(_dir, "new", "deep");
        var ok = MirrorLocations.Check(folder, 1024);
        Assert.True(ok.Ok);
        Assert.True(Directory.Exists(folder));
        Assert.Empty(Directory.GetFiles(folder));                       // the write probe is removed
        Assert.Equal(MirrorFolderProblem.NotAbsolute, MirrorLocations.Check("relative/path").Problem);
        Assert.Equal(MirrorFolderProblem.NotEnoughSpace, MirrorLocations.Check(folder, long.MaxValue / 4).Problem);
    }

    [Fact]
    public void Folder_check_warns_about_cloud_synced_folders()
    {
        var folder = Path.Combine(_dir, "Dropbox", "mail");
        var check = MirrorLocations.Check(folder);
        Assert.True(check.Ok);
        Assert.Contains(check.Warnings, w => w.Contains("cloud", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Settings_round_trip_and_a_corrupt_file_gives_defaults()
    {
        var store = new MirrorSettingsStore(_dir);
        Assert.Equal(MirrorSettings.Empty.Accounts.Count, store.Load().Accounts.Count);
        store.Save(new MirrorSettings(Path.Combine(_dir, "x"), new() { ["acc"] = new MirrorAccountSettings(false, null, 3, 1000) }));
        var loaded = store.Load();
        Assert.Equal(Path.Combine(_dir, "x"), loaded.DefaultFolder);
        Assert.False(loaded.For("acc").Enabled);
        Assert.Equal(3, loaded.For("acc").KeepMonths);
        Assert.True(loaded.For("other").Enabled);                      // unknown account: defaults
        File.WriteAllText(store.Path, "{ not json");
        Assert.Null(store.Load().DefaultFolder);
    }

    [Fact]
    public void Sync_state_keeps_folders_messages_tokens_and_the_journal_across_reopen()
    {
        var path = Path.Combine(_dir, "state", "acc.sync");
        using (var db = new SyncStateStore(path))
        {
            db.InTransaction(() =>
            {
                db.UpsertFolder(new MirrorFolderState("inbox", 0x8042, "tok1", new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc)));
                db.UpsertMessage(new MirrorMessageState("m1", "inbox", 0x200024, "ck1", false, false));
                db.UpsertMessage(new MirrorMessageState("m1", "label-x", 0x200044, null, true, true));      // same Gmail message under another label
            });
            db.Enqueue("read", "{\"id\":\"m1\"}");
            db.Enqueue("move", "{\"id\":\"m1\",\"to\":\"archive\"}");
        }
        using var again = new SyncStateStore(path);
        Assert.Equal("tok1", again.GetFolder("inbox")!.SyncToken);
        Assert.Equal(0x8042u, again.GetFolder("inbox")!.PstNid);
        Assert.Single(again.MessagesIn("inbox"));
        Assert.True(again.GetMessage("m1", "label-x")!.Flagged);
        var pending = again.Pending();
        Assert.Equal(["read", "move"], pending.Select(p => p.Op).ToArray());
        again.Failed(pending[0].Seq);
        Assert.Equal(1, again.Pending()[0].Attempts);
        again.Complete(pending[0].Seq);
        Assert.Equal(1, again.PendingCount());
        again.UpsertMessage(new MirrorMessageState("m1", "inbox", 0x200024, "ck2", true, false));
        Assert.Equal("ck2", again.GetMessage("m1", "inbox")!.ChangeKey);
        again.DeleteFolder("inbox");
        Assert.Empty(again.MessagesIn("inbox"));
        Assert.Single(again.MessagesIn("label-x"));
    }

    [Fact]
    public void A_failed_transaction_changes_nothing()
    {
        using var db = new SyncStateStore(Path.Combine(_dir, "tx.sync"));
        Assert.Throws<InvalidOperationException>(() => db.InTransaction(() =>
        {
            db.UpsertFolder(new MirrorFolderState("f", 1, null, null));
            throw new InvalidOperationException();
        }));
        Assert.Null(db.GetFolder("f"));
    }
}
