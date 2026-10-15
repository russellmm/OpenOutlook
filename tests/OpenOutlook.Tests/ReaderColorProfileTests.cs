using OpenOutlook.Desktop;

namespace OpenOutlook.Tests;

/// <summary>Options > Mail > "Message colors" (2026-10-15: WebView2 tinted white to cream on a monitor with a colour profile).</summary>
public sealed class ReaderColorProfileTests
{
    [Fact]
    public void Colors_as_written_add_the_srgb_flag_once_and_keep_other_arguments()
    {
        Assert.Equal("--force-color-profile=srgb", ReaderColorProfile.BrowserArguments(null, colorsAsWritten: true));
        Assert.Equal("--force-color-profile=srgb", ReaderColorProfile.BrowserArguments("  ", colorsAsWritten: true));
        Assert.Equal("--disable-gpu --force-color-profile=srgb", ReaderColorProfile.BrowserArguments("--disable-gpu", colorsAsWritten: true));
        var once = ReaderColorProfile.BrowserArguments(null, colorsAsWritten: true);
        Assert.Equal(once, ReaderColorProfile.BrowserArguments(once, colorsAsWritten: true));
    }

    [Fact]
    public void Monitor_profile_leaves_the_arguments_alone_and_a_users_own_profile_flag_wins()
    {
        Assert.Null(ReaderColorProfile.BrowserArguments(null, colorsAsWritten: false));
        Assert.Equal("--disable-gpu", ReaderColorProfile.BrowserArguments("--disable-gpu", colorsAsWritten: false));
        Assert.Equal("--force-color-profile=display-p3-d65",
            ReaderColorProfile.BrowserArguments("--force-color-profile=display-p3-d65", colorsAsWritten: true));
    }

    [Fact]
    public void The_option_is_on_by_default_and_survives_a_restart()
    {
        var root = Path.Combine(Path.GetTempPath(), "oo-colors-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new OptionsSettingsStore(root);
            Assert.True(store.Load().ReaderColorsAsWritten);
            store.Save(store.Load() with { ReaderColorsAsWritten = false });
            Assert.False(new OptionsSettingsStore(root).Load().ReaderColorsAsWritten);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
