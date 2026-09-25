using System.Buffers.Binary;
using System.Text;

namespace PstCore;

internal static class EncodingUtil
{
    public static Encoding Ansi1252()
    {
        try { return Encoding.GetEncoding(1252); }
        catch { return Encoding.Latin1; }
    }

    public static Encoding FromCodePage(int codePage)
    {
        try { return Encoding.GetEncoding(codePage); }
        catch { return Ansi1252(); }
    }
}

internal static class BinaryUtil
{
    public static ushort ReadU16(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt16LittleEndian(s[o..]);
    public static uint ReadU32(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt32LittleEndian(s[o..]);
    public static ulong ReadU64(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt64LittleEndian(s[o..]);
    public static int ReadI32(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadInt32LittleEndian(s[o..]);

    public static void WriteU16(Span<byte> s, int o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(s[o..], v);
    public static void WriteU32(Span<byte> s, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(s[o..], v);

    public static ulong ReadBid(ReadOnlySpan<byte> s, int o, bool unicode) =>
        unicode ? ReadU64(s, o) : ReadU32(s, o);

    public static int BidSize(bool unicode) => unicode ? 8 : 4;

    public static DateTime FromFileTime(ulong fileTime)
    {
        if (fileTime == 0) return DateTime.MinValue;
        try
        {
            return DateTime.FromFileTimeUtc((long)fileTime).ToLocalTime();
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    public static string DecodeString(ReadOnlySpan<byte> data, bool unicodeProperty, Encoding? ansiEncoding)
    {
        if (data.IsEmpty) return string.Empty;
        if (unicodeProperty)
        {
            var len = data.Length;
            if (len >= 2 && data[len - 1] == 0 && data[len - 2] == 0)
                len -= 2;
            return Encoding.Unicode.GetString(data[..len]);
        }

        var encoding = ansiEncoding ?? Encoding.GetEncoding(1252);
        var n = data.Length;
        if (n > 0 && data[n - 1] == 0) n--;
        return encoding.GetString(data[..n]);
    }
}
