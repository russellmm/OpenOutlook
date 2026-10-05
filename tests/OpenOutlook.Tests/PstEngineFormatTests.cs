using OpenOutlook.PstNative;
using OpenPst;
using PstCore;

namespace OpenOutlook.Tests;

/// <summary>
/// ANSI (wVer 14/15) and 4K-page (wVer 36/37, e.g. Outlook's .ost caches, with zlib-compressed blocks) files are read by the native
/// engine and opened read-only. Opt-in like the other fixtures:
///   OPENOUTLOOK_TEST_ANSI_PST   an ANSI .pst      OPENOUTLOOK_TEST_4K_PST   a 4K-page .pst / .ost (a COPY; Outlook may hold the original)
/// </summary>
public sealed class PstEngineFormatTests
{
    static string? Fixture(string variable)
    {
        var path = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(path) || !File.Exists(path) || !NativeLibraryLoader.IsAvailable ? null : path;
    }

    static void ReadsEverything(string path, Func<ushort, bool> versionOk)
    {
        using var engine = PstEngineFactory.Open(path);
        var native = Assert.IsType<NativePstEngine>(engine);
        Assert.True(versionOk(native.Header.WVer), $"unexpected wVer {native.Header.WVer}");
        Assert.False(native.CanWrite);
        var folders = native.AllFolders().ToList();
        Assert.True(folders.Count > 1);
        int messages = 0, opened = 0;
        foreach (var folder in folders.Where(f => f.ContentCount > 0))
        {
            var list = native.GetMessages(folder);
            Assert.Equal(folder.ContentCount, list.Count);          // the folder's own count agrees with its contents table
            foreach (var summary in list.Take(25))
            {
                messages++;
                var message = native.OpenMessage(summary);
                opened++;
                Assert.NotNull(message.Summary.Subject);
                foreach (var attachment in message.Attachments.Where(a => a.Method == 1).Take(1))
                    _ = native.ReadAttachmentData(summary, attachment);
            }
        }
        Assert.True(messages > 0, "the fixture has no messages");
        Assert.Equal(messages, opened);
    }

    static void NeverEditable(string path)
    {
        var ex = Assert.Throws<PstCore.PstException>(() => PstEngineFactory.Open(path, writable: true));
        Assert.Contains("can be read but not edited", ex.Message);
        using var edit = PstEngineFactory.OpenEditable(path, out var why);
        Assert.IsType<NativePstEngine>(edit);                        // still the native engine, read-only
        Assert.False(edit.CanWrite);
        Assert.Contains("can be read but not edited", why);
        Assert.False(File.Exists(path + ".lck"));                    // a failed write open leaves no lock behind
        Assert.Throws<PstCore.PstException>(() => edit.SetReadState(edit.GetMessages(edit.AllFolders().First(f => f.ContentCount > 0))[0], true));
    }

    [Fact]
    public void Ansi_files_are_read_by_the_native_engine_and_stay_read_only()
    {
        var path = Fixture("OPENOUTLOOK_TEST_ANSI_PST");
        if (path is null) return;
        ReadsEverything(path, v => v is 14 or 15);
        NeverEditable(path);
    }

    [Fact]
    public void FourK_page_files_with_compressed_blocks_are_read_by_the_native_engine_and_stay_read_only()
    {
        var path = Fixture("OPENOUTLOOK_TEST_4K_PST");
        if (path is null) return;
        ReadsEverything(path, v => v >= 36);
        NeverEditable(path);
    }
}
