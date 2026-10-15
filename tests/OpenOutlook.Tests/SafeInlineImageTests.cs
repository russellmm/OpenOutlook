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

    /// <summary>A 1x1 lossless WebP (newsletters' CDNs answer WebP to a browser Accept header).</summary>
    private static readonly byte[] OnePixelWebP = Convert.FromBase64String("UklGRhoAAABXRUJQVlA4TA0AAAAvAAAAEAcQERGIiP4HAA==");

    [Fact]
    public void AcceptsWebPAndReadsItsSizeFromEveryHeaderKind()
    {
        Assert.Equal((1, 1), SafeInlineImage.Validate(OnePixelWebP));
        Assert.Equal("image/webp", SafeInlineImage.MimeType(OnePixelWebP));
        Assert.Equal("image/png", SafeInlineImage.MimeType(OnePixelPng));
        var extended = new byte[30];                                                   // "VP8X": canvas size minus one, 24 bits each
        "RIFF"u8.CopyTo(extended); "WEBP"u8.CopyTo(extended.AsSpan(8)); "VP8X"u8.CopyTo(extended.AsSpan(12));
        extended[24] = 0x1F; extended[25] = 0x03;                                      // width 800 = 0x31F + 1
        extended[27] = 0xBB; extended[28] = 0x01;                                      // height 444 = 0x1BB + 1
        Assert.Equal((800, 444), SafeInlineImage.Validate(extended));
        extended[26] = 0x01;                                                           // 66,336 wide: over the ceiling
        Assert.Throws<InvalidDataException>(() => SafeInlineImage.Validate(extended));
        var lossy = new byte[30];                                                      // "VP8 ": start code 9d 01 2a, then 14-bit sizes
        "RIFF"u8.CopyTo(lossy); "WEBP"u8.CopyTo(lossy.AsSpan(8)); "VP8 "u8.CopyTo(lossy.AsSpan(12));
        lossy[23] = 0x9d; lossy[24] = 0x01; lossy[25] = 0x2a; lossy[26] = 0x14; lossy[27] = 0x03; lossy[28] = 0xBC; lossy[29] = 0x01;
        Assert.Equal((788, 444), SafeInlineImage.Validate(lossy));
    }

    [Fact]
    public void Picture_requests_carry_a_browser_user_agent_and_accept_webp()
    {
        using var client = SafeRemoteImageLoader.CreateClient();
        Assert.Contains("Mozilla/5.0", client.DefaultRequestHeaders.UserAgent.ToString());       // without one, bot-protected image hosts answer 403
        Assert.Contains("image/webp", SafeRemoteImageLoader.AcceptedImageTypes);
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
