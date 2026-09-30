using OpenOutlook.Desktop;

namespace OpenOutlook.Tests;

public sealed class AttachedPstSettingsStoreTests
{
    [Fact]
    public void Attached_paths_survive_restart_and_detach_without_requiring_files_to_exist()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"openoutlook-psts-{Guid.NewGuid():N}");
        try
        {
            var first = System.IO.Path.Combine(directory, "first.pst");
            var missing = System.IO.Path.Combine(directory, "temporarily-missing.pst");
            var store = new AttachedPstSettingsStore(directory);
            store.Add(first);
            store.Add(first);
            store.Add(missing);
            Assert.Equal([first, missing], new AttachedPstSettingsStore(directory).Load());

            store.Remove(first);
            Assert.Equal([missing], new AttachedPstSettingsStore(directory).Load());
            if (OperatingSystem.IsLinux())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(store.Path));
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Invalid_saved_paths_are_rejected_without_opening_archives()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"openoutlook-psts-{Guid.NewGuid():N}");
        try
        {
            var store = new AttachedPstSettingsStore(directory);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(store.Path)!);
            File.WriteAllText(store.Path, "[\"relative.pst\"]");
            Assert.Throws<InvalidDataException>(() => store.Load());
            Assert.Throws<InvalidDataException>(() => store.Add(System.IO.Path.Combine(directory, "valid.pst")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
