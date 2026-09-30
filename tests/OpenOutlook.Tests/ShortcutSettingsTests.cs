using Avalonia.Input;
using OpenOutlook.Desktop;

namespace OpenOutlook.Tests;

public sealed class ShortcutSettingsTests
{
    [Fact]
    public void Delete_is_assigned_by_default_and_custom_shortcuts_survive_restart()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"openoutlook-shortcuts-{Guid.NewGuid():N}");
        try
        {
            var store = new ShortcutSettingsStore(directory);
            Assert.Equal(new MailShortcut("delete", Key.Delete, KeyModifiers.None),
                Assert.Single(store.Load().Bindings));
            store.Save(new ShortcutSettings { Bindings =
            [
                new("delete", Key.Delete, KeyModifiers.None),
                new("archive", Key.A, KeyModifiers.Control)
            ] });
            var restored = new ShortcutSettingsStore(directory).Load();
            Assert.Equal(2, restored.Bindings.Count);
            Assert.Contains(new MailShortcut("archive", Key.A, KeyModifiers.Control), restored.Bindings);
            Assert.Equal("Ctrl+A", ShortcutSettingsStore.Display(restored.Bindings[1]));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Invalid_or_duplicate_shortcuts_cannot_override_existing_actions()
    {
        var validated = ShortcutSettingsStore.Validate(new ShortcutSettings { Bindings =
        [
            new("delete", Key.Delete, KeyModifiers.None),
            new("archive", Key.Delete, KeyModifiers.None),
            new("unknown", Key.A, KeyModifiers.Control),
            new("delete", Key.B, KeyModifiers.Control)
        ] });
        Assert.Equal(new MailShortcut("delete", Key.Delete, KeyModifiers.None),
            Assert.Single(validated.Bindings));
    }
}
