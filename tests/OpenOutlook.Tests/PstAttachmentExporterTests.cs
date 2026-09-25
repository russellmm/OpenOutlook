using OpenOutlook.Desktop;
using PstCore;

namespace OpenOutlook.Tests;

public sealed class PstAttachmentExporterTests
{
    [Fact]
    public async Task ExportsSyntheticBytesToNewFile()
    {
        using var temp = new ExportDirectory();
        byte[] content = [1, 0, 2, 255];
        var destination = temp.PathFor("document.bin");
        await PstAttachmentExporter.ExportAsync(Attachment(size: content.Length), destination, () => content);
        Assert.Equal(content, File.ReadAllBytes(destination));
        Assert.Single(Directory.GetFiles(temp.Path));
    }

    [Fact]
    public async Task NeverOverwritesDuplicateName()
    {
        using var temp = new ExportDirectory();
        var destination = temp.PathFor("duplicate.bin");
        await PstAttachmentExporter.ExportAsync(Attachment(2), destination, () => [1, 2]);
        await Assert.ThrowsAsync<IOException>(() =>
            PstAttachmentExporter.ExportAsync(Attachment(2), destination, () => [3, 4]));
        Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(destination));
        Assert.Single(Directory.GetFiles(temp.Path));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("folder/file.txt")]
    [InlineData("folder\\file.txt")]
    [InlineData("C:drive.txt")]
    [InlineData("..")]
    [InlineData("CON.txt")]
    [InlineData("evil\nfile.txt")]
    public async Task RejectsSuspiciousSuggestedNames(string filename)
    {
        using var temp = new ExportDirectory();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            PstAttachmentExporter.ExportAsync(Attachment(1, filename: filename),
                temp.PathFor("safe.bin"), () => [1]));
        Assert.Empty(Directory.GetFiles(temp.Path));
    }

    [Fact]
    public async Task RejectsUnsafeOutputFilename()
    {
        using var temp = new ExportDirectory();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            PstAttachmentExporter.ExportAsync(Attachment(1), temp.PathFor("bad:name"), () => [1]));
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(1, 0)]
    public async Task RejectsEmptyOrMismatchedLengths(int declared, int actual)
    {
        using var temp = new ExportDirectory();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PstAttachmentExporter.ExportAsync(Attachment(declared), temp.PathFor("out.bin"),
                () => new byte[actual]));
        Assert.Empty(Directory.GetFiles(temp.Path));
    }

    [Fact]
    public async Task MissingMetadataLengthStillPermitsNonemptyData()
    {
        using var temp = new ExportDirectory();
        var destination = temp.PathFor("out.bin");
        await PstAttachmentExporter.ExportAsync(Attachment(0), destination, () => [42]);
        Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(destination));
    }

    [Fact]
    public async Task RejectsOversizeMetadataBeforeReaderAndOversizeDataAfterReader()
    {
        using var temp = new ExportDirectory();
        var called = false;
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PstAttachmentExporter.ExportAsync(Attachment(3), temp.PathFor("out.bin"),
                () => { called = true; return [1]; }, maximumBytes: 2));
        Assert.False(called);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PstAttachmentExporter.ExportAsync(Attachment(0), temp.PathFor("out.bin"),
                () => [1, 2, 3], maximumBytes: 2));
        Assert.Empty(Directory.GetFiles(temp.Path));
    }

    [Fact]
    public async Task RejectsUnsupportedMethodBeforeReading()
    {
        using var temp = new ExportDirectory();
        var called = false;
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            PstAttachmentExporter.ExportAsync(Attachment(1, method: 5), temp.PathFor("out.bin"),
                () => { called = true; return [1]; }));
        Assert.False(called);
    }

    [Fact]
    public async Task CancellationLeavesNoOutputOrTemporaryFile()
    {
        using var temp = new ExportDirectory();
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PstAttachmentExporter.ExportAsync(Attachment(1), temp.PathFor("out.bin"),
                () => { cancellation.Cancel(); return [1]; }, cancellationToken: cancellation.Token));
        Assert.Empty(Directory.GetFiles(temp.Path));
    }

    [Fact]
    public async Task DoesNotExposeReaderErrorText()
    {
        using var temp = new ExportDirectory();
        var error = await Assert.ThrowsAsync<IOException>(() =>
            PstAttachmentExporter.ExportAsync(Attachment(1), temp.PathFor("out.bin"),
                () => throw new Exception("secret raw attachment data")));
        Assert.DoesNotContain("secret", error.ToString());
    }

    [Fact]
    public async Task RejectsSymlinkDestinationEvenWhenDangling()
    {
        using var temp = new ExportDirectory();
        var destination = temp.PathFor("linked.bin");
        try { File.CreateSymbolicLink(destination, temp.PathFor("missing.bin")); }
        catch (Exception exception) when (exception is PlatformNotSupportedException or UnauthorizedAccessException or IOException)
        { return; }
        await Assert.ThrowsAsync<IOException>(() =>
            PstAttachmentExporter.ExportAsync(Attachment(1), destination, () => [1]));
        Assert.NotNull(new FileInfo(destination).LinkTarget);
    }

    private static MailAttachment Attachment(int size, string filename = "document.bin", int method = 1) =>
        new() { Nid = 0x123, Size = size, FileName = filename, Method = method };

    private sealed class ExportDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "pst-export-test-" + Guid.NewGuid().ToString("N"));
        public ExportDirectory() => Directory.CreateDirectory(Path);
        public string PathFor(string name) => System.IO.Path.Combine(Path, name);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
