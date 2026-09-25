using OpenOutlook.Desktop;

namespace OpenOutlook.Tests;

public sealed class ViewLayoutSettingsTests
{
    [Fact]
    public void Column_order_widths_and_pane_proportions_survive_restart()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"openoutlook-layout-{Guid.NewGuid():N}");
        try
        {
            var store = new ViewLayoutSettingsStore(directory);
            var expected = new ViewLayoutSettings
            {
                FolderPaneWeight = 3,
                MessagePaneWeight = 6,
                ReaderPaneWeight = 2,
                Columns =
                [
                    new(0, 0, 42, false), new(1, 2, 185, false),
                    new(2, 3, 2, true), new(3, 4, 150, false), new(4, 1, 90, false)
                ]
            };
            store.Save(expected);

            var actual = new ViewLayoutSettingsStore(directory).Load();
            Assert.Equal(expected.FolderPaneWeight, actual.FolderPaneWeight);
            Assert.Equal(expected.MessagePaneWeight, actual.MessagePaneWeight);
            Assert.Equal(expected.ReaderPaneWeight, actual.ReaderPaneWeight);
            Assert.Equal(expected.Columns, actual.Columns);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Invalid_saved_layout_falls_back_to_safe_defaults()
    {
        var malformed = new ViewLayoutSettings
        {
            FolderPaneWeight = double.NaN,
            Columns = [new(0, 0, double.PositiveInfinity, false)]
        };
        var actual = ViewLayoutSettingsStore.Validate(malformed);
        Assert.Equal(2, actual.FolderPaneWeight);
        Assert.Equal(5, actual.MessagePaneWeight);
        Assert.Empty(actual.Columns);
    }
}
