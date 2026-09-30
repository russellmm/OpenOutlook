using OpenOutlook.Desktop;
using PstCore;

namespace OpenOutlook.Tests;

public sealed class PstFolderEmlExporterTests
{
    [Fact]
    public async Task ExportsNestedFoldersToNewPrivateDirectoryWithoutOverwriting()
    {
        using var temp = new TestDirectory();
        var root = Folder(10, "Inbox");
        var child = Folder(11, "Travel & tickets");
        root.Children.Add(child);
        var first = Summary(101, root.Nid);
        var second = Summary(102, root.Nid);
        var third = Summary(103, child.Nid);
        var destination = temp.PathFor(PstFolderEmlExporter.SuggestedDirectoryName(root));
        var result = await PstFolderEmlExporter.ExportAsync(root, destination,
            folder => folder.Nid == root.Nid ? [first, second] : [third],
            summary => Message(summary), PstMessageEmlExporter.ExportAsync);
        Assert.Equal(new PstFolderExportResult(2, 3), result);
        Assert.True(File.Exists(Path.Combine(destination, "message-00000065.eml")));
        Assert.True(File.Exists(Path.Combine(destination, "message-00000066.eml")));
        var nested = Path.Combine(destination, PstFolderEmlExporter.SuggestedDirectoryName(child));
        Assert.True(File.Exists(Path.Combine(nested, "message-00000067.eml")));
        Assert.Contains("Content-Type: text/plain", File.ReadAllText(Path.Combine(nested, "message-00000067.eml")));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(destination));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(nested));
        }
        await Assert.ThrowsAsync<IOException>(() => PstFolderEmlExporter.ExportAsync(root, destination,
            _ => [first], summary => Message(summary), PstMessageEmlExporter.ExportAsync));
        Assert.Equal(2, Directory.GetFiles(destination, "*.eml").Length);
        Assert.Single(Directory.GetDirectories(temp.Path));
    }

    [Fact]
    public async Task FailedMessageOrCancellationRemovesEntireTemporaryExport()
    {
        using var temp = new TestDirectory();
        var root = Folder(1, "Inbox");
        var summaries = new[] { Summary(3, root.Nid), Summary(4, root.Nid) };
        var destination = temp.PathFor("Inbox-00000001");
        var failure = await Assert.ThrowsAsync<PstFolderExportException>(() => PstFolderEmlExporter.ExportAsync(root, destination,
            _ => summaries, summary => Message(summary), async (message, path, ct) =>
            {
                if (message.Summary.Nid == 4) throw new IOException("Unreadable message");
                await PstMessageEmlExporter.ExportAsync(message, path, ct);
            }));
        Assert.Equal((uint)4, failure.MessageNid);
        Assert.DoesNotContain("Unreadable message", failure.Message);
        Assert.Empty(Directory.GetFileSystemEntries(temp.Path));

        using var cancellation = new CancellationTokenSource();
        var progress = new CallbackProgress(_ => cancellation.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PstFolderEmlExporter.ExportAsync(root,
            destination, _ => summaries, summary => Message(summary), PstMessageEmlExporter.ExportAsync,
            progress, cancellation.Token));
        Assert.Empty(Directory.GetFileSystemEntries(temp.Path));
    }

    [Fact]
    public async Task CyclicFolderTreeAndDuplicateMessageIdsFailClosed()
    {
        using var temp = new TestDirectory();
        var root = Folder(1, "Root");
        root.Children.Add(root);
        await Assert.ThrowsAsync<InvalidDataException>(() => PstFolderEmlExporter.ExportAsync(root,
            temp.PathFor("cycle"), _ => [], summary => Message(summary), PstMessageEmlExporter.ExportAsync));
        Assert.Empty(Directory.GetFileSystemEntries(temp.Path));
        root.Children.Clear();
        var duplicated = Summary(3, root.Nid);
        await Assert.ThrowsAsync<InvalidDataException>(() => PstFolderEmlExporter.ExportAsync(root,
            temp.PathFor("duplicate"), _ => [duplicated, duplicated], summary => Message(summary),
            PstMessageEmlExporter.ExportAsync));
        Assert.Empty(Directory.GetFileSystemEntries(temp.Path));
    }

    [Fact]
    public async Task RejectsSymbolicLinkInDestinationPath()
    {
        using var temp = new TestDirectory();
        var real = temp.PathFor("real");
        Directory.CreateDirectory(real);
        var link = temp.PathFor("linked");
        try { Directory.CreateSymbolicLink(link, real); }
        catch (Exception exception) when (exception is PlatformNotSupportedException or UnauthorizedAccessException or IOException)
        { return; }
        await Assert.ThrowsAsync<IOException>(() => PstFolderEmlExporter.ExportAsync(Folder(1, "Inbox"),
            Path.Combine(link, "export"), _ => [], summary => Message(summary),
            PstMessageEmlExporter.ExportAsync));
        Assert.Empty(Directory.GetFileSystemEntries(real));
    }

    private static MailFolder Folder(uint nid, string name) => new() { Nid = nid, Name = name, ParentNid = 0 };
    private static MailSummary Summary(uint nid, uint folderNid) => new()
    {
        Nid = nid, FolderNid = folderNid, From = "alice@example.org", To = "bob@example.org", Subject = "Example"
    };
    private static MailMessage Message(MailSummary summary) => new() { Summary = summary, BodyText = "body" };

    private sealed class CallbackProgress(Action<PstFolderExportProgress> callback) : IProgress<PstFolderExportProgress>
    {
        public void Report(PstFolderExportProgress value) => callback(value);
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "openoutlook-folder-export-test-" + Guid.NewGuid().ToString("N"));
        public TestDirectory() => Directory.CreateDirectory(Path);
        public string PathFor(string name) => System.IO.Path.Combine(Path, name);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
