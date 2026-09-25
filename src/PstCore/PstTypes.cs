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

public enum NidType : byte
{
    Hid = 0x00,
    Internal = 0x01,
    NormalFolder = 0x02,
    SearchFolder = 0x03,
    NormalMessage = 0x04,
    Attachment = 0x05,
    SearchUpdateQueue = 0x06,
    SearchCriteriaObject = 0x07,
    AssocMessage = 0x08,
    ContentsTableIndex = 0x0A,
    ReceiveFolderTable = 0x0B,
    OutgoingQueueTable = 0x0C,
    HierarchyTable = 0x0D,
    ContentsTable = 0x0E,
    AssocContentsTable = 0x0F,
    SearchContentsTable = 0x10,
    AttachmentTable = 0x11,
    RecipientTable = 0x12,
    SearchTableIndex = 0x13,
    Ltp = 0x1F
}

public static class SpecialNids
{
    public const uint MessageStore = 0x21;
    public const uint NameToIdMap = 0x61;
    public const uint RootFolder = 0x122;
}

public readonly struct Nid : IEquatable<Nid>
{
    public uint Value { get; }

    public Nid(uint value) => Value = value;

    public NidType Type => (NidType)(Value & 0x1F);
    public uint Index => Value >> 5;

    public Nid WithType(NidType type) => new((Value & 0xFFFFFFE0) | (uint)type);

    public bool Equals(Nid other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is Nid other && Equals(other);
    public override int GetHashCode() => (int)Value;
    public override string ToString() => $"NID 0x{Value:X} ({Type})";
}

public readonly struct Bid : IEquatable<Bid>
{
    public ulong Value { get; }

    public Bid(ulong value) => Value = value;

    public bool IsInternal => (Value & 0x2) != 0;
    public ulong LookupKey => Value & ~1UL;

    public uint CyclicKey => (uint)Value;

    public bool Equals(Bid other) => LookupKey == other.LookupKey;
    public override bool Equals(object? obj) => obj is Bid other && Equals(other);
    public override int GetHashCode() => LookupKey.GetHashCode();
    public override string ToString() => $"BID 0x{Value:X}";
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
