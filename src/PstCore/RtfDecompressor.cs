using System.Text;

namespace PstCore;

internal static class RtfDecompressor
{
    private const string Prebuf =
        "{\\rtf1\\ansi\\mac\\deff0\\deftab720{\\fonttbl;}{\\f0\\fnil \\froman \\fswiss \\fmodern \\fscript \\fdecor MS Sans SerifSymbolArialTimes New RomanCourier{\\colortbl\\red0\\green0\\blue0\r\n\\par \\pard\\plain\\f0\\fs20\\b\\i\\u\\tab\\tx";

    public static string Decompress(byte[] data)
    {
        if (data.Length < 16) return string.Empty;
        var compSize = BinaryUtil.ReadU32(data, 0);
        var rawSize = BinaryUtil.ReadU32(data, 4);
        var magic = BinaryUtil.ReadU32(data, 8);
        if (magic == 0x414C454D) // 'MELA' uncompressed
        {
            var n = (int)Math.Min(rawSize, (uint)(data.Length - 16));
            return Encoding.Latin1.GetString(data, 16, n);
        }
        if (magic != 0x75465A4C) // 'LZFu'
            return Encoding.Latin1.GetString(data);

        var output = new byte[Math.Max(rawSize, 16)];
        var dict = new byte[4096];
        Encoding.Latin1.GetBytes(Prebuf, dict);
        var dictWrite = Prebuf.Length;
        var inPos = 16;
        var outPos = 0;
        var flagCount = 0;
        var flags = 0;

        while (inPos < data.Length && outPos < output.Length)
        {
            if (flagCount++ % 8 == 0)
            {
                if (inPos >= data.Length) break;
                flags = data[inPos++];
            }
            else
            {
                flags >>= 1;
            }

            if ((flags & 1) == 0)
            {
                if (inPos >= data.Length || outPos >= output.Length) break;
                var b = data[inPos++];
                output[outPos++] = b;
                dict[dictWrite++ & 0xFFF] = b;
            }
            else
            {
                if (inPos + 1 >= data.Length) break;
                var hi = data[inPos++];
                var lo = data[inPos++];
                var offset = (hi << 4) | (lo >> 4);
                var length = (lo & 0x0F) + 2;
                for (var i = 0; i < length && outPos < output.Length; i++)
                {
                    var b = dict[(offset + i) & 0xFFF];
                    output[outPos++] = b;
                    dict[dictWrite++ & 0xFFF] = b;
                }
            }
        }

        return Encoding.Latin1.GetString(output, 0, Math.Min(outPos, output.Length));
    }

    public static string ToPlainText(string rtf)
    {
        if (string.IsNullOrEmpty(rtf)) return string.Empty;
        var sb = new StringBuilder(rtf.Length);
        var i = 0;
        var ignorable = 0;
        while (i < rtf.Length)
        {
            var c = rtf[i];
            if (c == '{') { i++; continue; }
            if (c == '}') { i++; if (ignorable > 0) ignorable--; continue; }
            if (c == '\\')
            {
                i++;
                if (i >= rtf.Length) break;
                var n = rtf[i];
                if (n is '\\' or '{' or '}')
                {
                    if (ignorable == 0) sb.Append(n);
                    i++;
                    continue;
                }
                if (n == '\'')
                {
                    if (i + 2 < rtf.Length && ignorable == 0)
                    {
                        var hex = rtf.Substring(i + 1, 2);
                        if (byte.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var b))
                            sb.Append(Encoding.Latin1.GetString([b]));
                    }
                    i += 3;
                    continue;
                }

                var start = i;
                while (i < rtf.Length && char.IsLetter(rtf[i])) i++;
                var ctrl = rtf[start..i];
                var dest = ctrl is "fonttbl" or "colortbl" or "stylesheet" or "pict" or "info" or "header" or "footer" or "object";
                if (ctrl is "par" or "line" or "page")
                {
                    if (ignorable == 0) sb.AppendLine();
                }
                else if (ctrl == "tab" && ignorable == 0)
                    sb.Append('\t');
                else if (ctrl == "uc")
                {
                    while (i < rtf.Length && (char.IsDigit(rtf[i]) || rtf[i] == '-')) i++;
                }
                else if (ctrl == "u")
                {
                    var neg = i < rtf.Length && rtf[i] == '-';
                    if (neg) i++;
                    var numStart = i;
                    while (i < rtf.Length && char.IsDigit(rtf[i])) i++;
                    if (int.TryParse(rtf.AsSpan(numStart, i - numStart), out var cp))
                    {
                        if (neg) cp = -cp;
                        if (ignorable == 0) sb.Append((char)(ushort)cp);
                    }
                    if (i < rtf.Length && rtf[i] == '?') i++;
                }
                if (dest) ignorable++;
                if (i < rtf.Length && rtf[i] == ' ') i++;
                else
                {
                    while (i < rtf.Length && (char.IsDigit(rtf[i]) || rtf[i] == '-')) i++;
                    if (i < rtf.Length && rtf[i] == ' ') i++;
                }
                continue;
            }
            if (c == '\r' || c == '\n') { i++; continue; }
            if (ignorable == 0) sb.Append(c);
            i++;
        }
        return sb.ToString();
    }
}
