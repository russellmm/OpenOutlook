namespace PstCore;

public enum PstFormatKind
{
    Ansi,
    Unicode
}

public enum PstCryptMethod : byte
{
    None = 0x00,
    Permute = 0x01,
    Cyclic = 0x02,
    WipEncrypted = 0x10
}

public sealed class PstHeader
{
    public required string Path { get; init; }
    public required PstFormatKind Format { get; init; }
    public required ushort WVer { get; init; }
    public required ushort WVerClient { get; init; }
    public required PstCryptMethod CryptMethod { get; init; }
    public required ulong FileEof { get; init; }
    public required ulong NbtRootBid { get; init; }
    public required ulong NbtRootIb { get; init; }
    public required ulong BbtRootBid { get; init; }
    public required ulong BbtRootIb { get; init; }
    public required byte AMapValid { get; init; }
    public required uint Unique { get; init; }

    public string FormatName => Format == PstFormatKind.Unicode ? "Unicode" : "ANSI";

    public string VersionDescription => WVer switch
    {
        14 => "ANSI (Outlook 97–2002, wVer=14)",
        15 => "ANSI (Outlook 97–2002, wVer=15)",
        23 => "Unicode (Outlook 2003–2007, wVer=23)",
        36 => "Unicode (Outlook 2010+, wVer=36)",
        37 => "Unicode with Windows Information Protection (wVer=37)",
        _ when WVer >= 23 => $"Unicode (wVer={WVer})",
        _ => $"Unknown (wVer={WVer})"
    };

    public string CryptDescription => CryptMethod switch
    {
        PstCryptMethod.None => "None",
        PstCryptMethod.Permute => "Permute (NDB_CRYPT_PERMUTE)",
        PstCryptMethod.Cyclic => "Cyclic (NDB_CRYPT_CYCLIC)",
        PstCryptMethod.WipEncrypted => "WIP / Information Protection (not readable)",
        _ => $"Unknown (0x{(byte)CryptMethod:X2})"
    };
}

public sealed class PstException : Exception
{
    public PstException(string message) : base(message) { }
    public PstException(string message, Exception inner) : base(message, inner) { }
}
