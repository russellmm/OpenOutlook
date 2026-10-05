using OpenOutlook.Desktop;
using Xunit;

namespace OpenOutlook.Tests;

public sealed class AttachmentLauncherTests
{
    [Theory]
    [InlineData("setup.exe")]
    [InlineData("INVOICE.PDF.SCR")]
    [InlineData("run.ps1")]
    [InlineData("budget.xlsm")]
    [InlineData("trick.exe.")]
    [InlineData("link.lnk")]
    public void Types_that_run_code_are_not_opened(string name) => Assert.True(AttachmentLauncher.IsRisky(name));

    [Theory]
    [InlineData("Invoice 13957797.pdf")]
    [InlineData("photo.JPG")]
    [InlineData("notes.txt")]
    [InlineData("report.docx")]
    [InlineData("noextension")]
    public void Ordinary_documents_can_be_opened(string name) => Assert.False(AttachmentLauncher.IsRisky(name));

    [Fact]
    public void Each_opened_attachment_gets_its_own_empty_folder()
    {
        var a = AttachmentLauncher.NewDirectory();
        var b = AttachmentLauncher.NewDirectory();
        Assert.NotEqual(a, b);
        Assert.Empty(Directory.GetFileSystemEntries(a));
        AttachmentLauncher.CleanUp();
        Assert.False(Directory.Exists(a));
    }
}
