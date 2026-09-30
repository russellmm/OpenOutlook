using System.Buffers.Binary;

namespace OpenOutlook.Desktop;

/// <summary>Limits embedded image bytes and decoded dimensions before Avalonia sees them.</summary>
public static class SafeInlineImage
{
    public const int MaximumBytes = 8 * 1024 * 1024;
    public const int MaximumPixels = 16 * 1024 * 1024;

    public static (int Width, int Height) Validate(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > MaximumBytes)
            throw new InvalidDataException("Embedded image exceeds the preview limit.");
        int width;
        int height;
        if (bytes.Length >= 24 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
            bytes[12..16].SequenceEqual("IHDR"u8))
        {
            width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes[16..20]));
            height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes[20..24]));
        }
        else if (bytes.Length >= 10 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8)))
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..8]);
            height = BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..10]);
        }
        else if (bytes.Length >= 4 && bytes[0] == 0xff && bytes[1] == 0xd8)
        {
            (width, height) = ReadJpegSize(bytes);
        }
        else throw new InvalidDataException("Embedded image format is not supported.");
        if (width <= 0 || height <= 0 || width > 8192 || height > 8192 || (long)width * height > MaximumPixels)
            throw new InvalidDataException("Embedded image dimensions exceed the preview limit.");
        return (width, height);
    }

    private static (int Width, int Height) ReadJpegSize(ReadOnlySpan<byte> bytes)
    {
        var offset = 2;
        while (offset + 4 < bytes.Length)
        {
            if (bytes[offset++] != 0xff) break;
            while (offset < bytes.Length && bytes[offset] == 0xff) offset++;
            if (offset >= bytes.Length) break;
            var marker = bytes[offset++];
            if (marker is 0xd8 or 0xd9 or 0x01 || marker is >= 0xd0 and <= 0xd7) continue;
            if (marker == 0xda || offset + 2 > bytes.Length) break;
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset, 2));
            if (length < 2 || offset + length > bytes.Length) break;
            if (marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7 or 0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf)
            {
                if (length < 7) break;
                return (BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 5, 2)),
                    BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 3, 2)));
            }
            offset += length;
        }
        throw new InvalidDataException("Embedded JPEG dimensions are invalid.");
    }
}
