using OpenOutlook.Desktop;
using Xunit;

namespace OpenOutlook.Tests;

public sealed class OptionsSettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oo-options-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private OptionsSettingsStore Store() => new(Path.Combine(_root, "config"));

    [Fact]
    public void MissingFileLoadsClassicDefaults()
    {
        var loaded = Store().Load();
        Assert.True(loaded.TaskbarEnvelopeIcon);
        Assert.True(loaded.CleanUpDontMoveCategorized);
        Assert.Equal("Never send a read receipt", loaded.ReadReceiptPolicy);
        Assert.Equal(76, loaded.WrapAtCharacter);
        Assert.Equal(2, loaded.ReadPaneWaitSeconds);
        Assert.False(loaded.PlaySound);
    }

    [Fact]
    public void RoundTripsChanges()
    {
        var store = Store();
        store.Save(new OptionsSettings
        {
            ComposeFormat = "HTML",
            PlaySound = true,
            WrapAtCharacter = 72,
            ReadReceiptPolicy = "Always send a read receipt",
            SpellCheckAsYouType = false,
            ReadPaneWaitSeconds = 5
        });
        var loaded = store.Load();
        Assert.Equal("HTML", loaded.ComposeFormat);
        Assert.True(loaded.PlaySound);
        Assert.Equal(72, loaded.WrapAtCharacter);
        Assert.Equal("Always send a read receipt", loaded.ReadReceiptPolicy);
        Assert.False(loaded.SpellCheckAsYouType);
        Assert.Equal(5, loaded.ReadPaneWaitSeconds);
    }

    [Fact]
    public void OutOfRangeNumbersAreClampedOnSaveAndLoad()
    {
        var store = Store();
        store.Save(new OptionsSettings { WrapAtCharacter = 99999, AutoSaveMinutes = -5, ReadPaneWaitSeconds = 4000 });
        var loaded = store.Load();
        Assert.Equal(700, loaded.WrapAtCharacter);
        Assert.Equal(1, loaded.AutoSaveMinutes);
        Assert.Equal(300, loaded.ReadPaneWaitSeconds);
    }

    [Fact]
    public void CorruptFileFallsBackToDefaults()
    {
        var store = Store();
        Directory.CreateDirectory(Path.GetDirectoryName(store.Path)!);
        File.WriteAllText(store.Path, "not json {{{");
        Assert.Equal(new OptionsSettings(), store.Load());
    }

    [Fact]
    public void OversizedFileFallsBackToDefaults()
    {
        var store = Store();
        Directory.CreateDirectory(Path.GetDirectoryName(store.Path)!);
        File.WriteAllText(store.Path, new string('x', OptionsSettingsStore.MaximumFileBytes + 1));
        Assert.Equal(new OptionsSettings(), store.Load());
    }

    [Fact]
    public void JunkStringValuesFallBack()
    {
        var store = Store();
        Directory.CreateDirectory(Path.GetDirectoryName(store.Path)!);
        File.WriteAllText(store.Path,
            "{\"ComposeFormat\":\"\",\"RichTextToInternet\":\"" + new string('q', 400) + "\"}");
        var loaded = store.Load();
        Assert.Equal("Rich Text", loaded.ComposeFormat);
        Assert.Equal("Convert to HTML format", loaded.RichTextToInternet);
    }

    [Fact]
    public void SaveLeavesNoTempFiles()
    {
        var store = Store();
        store.Save(new OptionsSettings { DesktopAlert = true });
        store.Save(new OptionsSettings { DesktopAlert = false });
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(store.Path)!, ".*.tmp"));
        Assert.False(Store().Load().DesktopAlert);
    }
}
