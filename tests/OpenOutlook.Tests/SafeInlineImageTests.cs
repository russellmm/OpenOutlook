using OpenOutlook.Desktop;

namespace OpenOutlook.Tests;

public sealed class SafeInlineImageTests
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII=");

    /// <summary>A syntactically readable PNG header that merely declares the given size.</summary>
    private static byte[] DeclaresSize(int width, int height)
    {
        var bytes = new byte[24];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
        bytes[12] = (byte)'I'; bytes[13] = (byte)'H'; bytes[14] = (byte)'D'; bytes[15] = (byte)'R';
        BitConverter.GetBytes((uint)width).Reverse().ToArray().CopyTo(bytes, 16);
        BitConverter.GetBytes((uint)height).Reverse().ToArray().CopyTo(bytes, 20);
        return bytes;
    }

    [Fact]
    public void AcceptsAnOrdinaryImageAndReportsItsDimensions()
    {
        Assert.Equal((1, 1), SafeInlineImage.Validate(OnePixelPng));
    }

    [Fact]
    public void RejectsDeclaredSizesBeyondThePerImageCeiling()
    {
        Assert.Throws<InvalidDataException>(() => SafeInlineImage.Validate(DeclaresSize(9000, 10)));
        Assert.Throws<InvalidDataException>(() => SafeInlineImage.Validate(DeclaresSize(8192, 4096)));
    }

    [Fact]
    public void RejectsFormatsThatAreNotPngGifOrJpeg()
    {
        Assert.Throws<InvalidDataException>(() => SafeInlineImage.Validate("not an image"u8));
    }

    [Fact]
    public void OrdinaryMessageFitsThePixelBudgetEntirely()
    {
        var budget = new DecodedPixelBudget(SafeInlineImage.MaximumMessagePixels);
        // A generous real-world message: 64 inline images at 1200x800.
        for (var i = 0; i < 64; i++)
            Assert.True(budget.TryReserve(1200 * 800), $"image {i} should have been accepted");
    }

    [Fact]
    public void PixelBudgetRefusesManySmallFilesThatEachDeclareAHugeBitmap()
    {
        // The attack: every file is tiny and passes Validate(), but each decodes to ~64 MB of RGBA.
        var bomb = DeclaresSize(4096, 4096);
        Assert.Equal((4096, 4096), SafeInlineImage.Validate(bomb));

        var budget = new DecodedPixelBudget(SafeInlineImage.MaximumMessagePixels);
        var accepted = 0;
        for (var i = 0; i < 64; i++)
            if (budget.TryReserve(4096L * 4096)) accepted++;

        Assert.Equal(4, accepted);
        Assert.Equal(4 * 4096L * 4096, budget.Used);
    }

    [Fact]
    public void BudgetIgnoresNonPositiveReservations()
    {
        var budget = new DecodedPixelBudget(1024);
        Assert.False(budget.TryReserve(0));
        Assert.False(budget.TryReserve(-5));
        Assert.Equal(0, budget.Used);
    }

    [Fact]
    public void ConcurrentReservationsNeverOversubscribeTheBudget()
    {
        var budget = new DecodedPixelBudget(100_000);
        var accepted = 0;
        Parallel.For(0, 500, _ => { if (budget.TryReserve(10_000)) Interlocked.Increment(ref accepted); });
        Assert.Equal(10, accepted);
        Assert.Equal(100_000, budget.Used);
    }
}
