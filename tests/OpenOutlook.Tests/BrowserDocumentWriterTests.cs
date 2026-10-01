using OpenOutlook.Desktop;

namespace OpenOutlook.Tests;

public sealed class BrowserDocumentWriterTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "oo-writer-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    [Fact]
    public void WritesTheDocumentAndReturnsAReadablePath()
    {
        var path = BrowserDocumentWriter.Write("<html><body><p>Hello</p></body></html>", _directory);
        Assert.EndsWith(".html", path);
        Assert.Contains("Hello", File.ReadAllText(path));
    }

    [Fact]
    public void RestrictsTheMessageFileToItsOwner()
    {
        if (!OperatingSystem.IsLinux()) return;   // permissions model differs elsewhere
        var path = BrowserDocumentWriter.Write("<html><body>mail</body></html>", _directory);
        var mode = Convert.ToString(
            Convert.ToInt32(new FileInfo(path).UnixFileMode), 8)[^3..];
        Assert.Equal("600", mode);
    }

    [Fact]
    public void RemovesEarlierMessagesSoReadingDoesNotAccumulateCopies()
    {
        var first = BrowserDocumentWriter.Write("<html>first</html>", _directory);
        var second = BrowserDocumentWriter.Write("<html>second</html>", _directory);
        Assert.False(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.Single(Directory.GetFiles(_directory, "message-*.html"));
    }

    [Fact]
    public void LeavesUnrelatedFilesAloneWhenPruning()
    {
        var unrelated = Path.Combine(_directory, "not-a-message.txt");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(unrelated, "keep me");
        BrowserDocumentWriter.Write("<html>mail</html>", _directory);
        Assert.True(File.Exists(unrelated));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RefusesToWriteAnEmptyDocument(string document) =>
        Assert.ThrowsAny<ArgumentException>(() => BrowserDocumentWriter.Write(document, _directory));

    [Fact]
    public void RejectsADocumentBeyondTheLimit()
    {
        var huge = new string('x', (int)Math.Min(BrowserDocumentWriter.MaximumDocumentCharacters + 10, 70L * 1024 * 1024));
        Assert.Throws<InvalidDataException>(() => BrowserDocumentWriter.Write(huge, _directory));
    }
}
