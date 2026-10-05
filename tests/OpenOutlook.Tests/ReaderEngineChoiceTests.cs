using OpenOutlook.Desktop;

namespace OpenOutlook.Tests;

public sealed class ReaderEngineChoiceTests
{
    [Theory]
    [InlineData(true, false, null, null, null, true)]            // Windows
    [InlineData(false, true, null, null, null, true)]            // macOS
    [InlineData(false, false, null, "x11", null, true)]          // Linux on X11
    [InlineData(false, false, null, null, null, true)]           // Linux, unknown session
    [InlineData(false, false, "wayland-0", null, null, false)]   // Wayland socket
    [InlineData(false, false, null, "wayland", null, false)]     // Wayland session
    [InlineData(false, false, "wayland-0", null, "embedded", true)]
    [InlineData(true, false, null, null, "snapshot", false)]
    public void Chooses_the_renderer_a_message_starts_on(bool windows, bool mac, string? wayland, string? session, string? forced, bool embedded) =>
        Assert.Equal(embedded, ReaderEngineChoice.PreferEmbedded(windows, mac, wayland, session, forced));
}
