using System.Text;

namespace PstCore;

internal readonly struct Hid
{
    public uint Value { get; }
    public Hid(uint value) => Value = value;
    public int Index => (int)((Value >> 5) & 0x7FF);
    public int BlockIndex => (int)(Value >> 16);
    public bool IsHid => (Value & 0x1F) == 0;
    public override string ToString() => $"HID 0x{Value:X}";
}

internal sealed class HeapBlock
{
    public Bid Bid { get; }
    public byte[] Data { get; }
    public bool CanRewrite { get; }

    public HeapBlock(Bid bid, byte[] data, bool canRewrite)
    {
        Bid = bid;
        Data = data;
        CanRewrite = canRewrite;
    }
}

internal sealed class HeapOnNode
{
    private readonly HeapBlock[] _blocks;
    private readonly Dictionary<uint, SubNodeEntry> _subNodes;
    private readonly Ndb _ndb;

    public uint UserRoot { get; }
    public byte ClientSig { get; }
    public Ndb Ndb => _ndb;
    public Dictionary<uint, SubNodeEntry> SubNodes => _subNodes;
    public IReadOnlyList<HeapBlock> Blocks => _blocks;

    private HeapOnNode(HeapBlock[] blocks, uint userRoot, byte clientSig, Dictionary<uint, SubNodeEntry> subNodes, Ndb ndb)
    {
        _blocks = blocks;
        UserRoot = userRoot;
        ClientSig = clientSig;
        _subNodes = subNodes;
        _ndb = ndb;
    }

    public static HeapOnNode Load(Ndb ndb, NbtEntry node)
    {
        var blocks = ndb.ReadDataTreeBlocks(node.Data);
        Dictionary<uint, SubNodeEntry> sub;
        try
        {
            sub = ndb.ReadSubNodes(node);
        }
        catch (Exception)
        {
            sub = [];
        }
        return FromBlocks(ndb, blocks, sub);
    }

    public static HeapOnNode Load(Ndb ndb, Bid dataBid, Dictionary<uint, SubNodeEntry> subNodes)
    {
        var blocks = ndb.ReadDataTreeBlocks(dataBid);
        return FromBlocks(ndb, blocks, subNodes);
    }

    internal static HeapOnNode LoadBounded(Ndb ndb, NbtEntry node, int maxHeapBytes, CancellationToken cancellationToken)
    {
        var blocks = ndb.ReadDataTreeBlocks(node.Data, maxHeapBytes, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return FromBlocks(ndb, blocks, ndb.ReadSubNodes(node.Sub, cancellationToken));
    }

    internal static HeapOnNode LoadBounded(Ndb ndb, Bid dataBid, Dictionary<uint, SubNodeEntry> subNodes,
        int maxHeapBytes, CancellationToken cancellationToken)
    {
        var blocks = ndb.ReadDataTreeBlocks(dataBid, maxHeapBytes, cancellationToken);
        return FromBlocks(ndb, blocks, subNodes);
    }

    private static HeapOnNode FromBlocks(Ndb ndb, List<(Bid Bid, byte[] Data)> blocks, Dictionary<uint, SubNodeEntry> subNodes)
    {
        if (blocks.Count == 0)
            throw new PstException("Heap-on-Node has no data blocks.");
        var heapBlocks = blocks
            .Select(b => new HeapBlock(b.Bid, b.Data, !b.Bid.IsInternal && b.Bid.Value != 0))
            .ToArray();
        var first = heapBlocks[0].Data;
        if (first.Length < 12)
            throw new PstException("Heap-on-Node is truncated.");
        if (first[2] != 0xEC)
            throw new PstException("Heap signature is not 0xEC.");
        var userRoot = BinaryUtil.ReadU32(first, 4);
        var clientSig = first[3];
        return new HeapOnNode(heapBlocks, userRoot, clientSig, subNodes, ndb);
    }

    public byte[] GetItem(uint hnid)
    {
        if (hnid == 0) return [];
        if (HeapOnNode.IsHid(hnid))
            return GetHid(new Hid(hnid));

        if (_subNodes.TryGetValue(hnid, out var sub))
            return _ndb.ReadDataTree(sub.Data);
        if (_subNodes.TryGetValue(hnid & 0xFFFFFFE0, out sub))
            return _ndb.ReadDataTree(sub.Data);
        throw new PstException($"Heap item 0x{hnid:X} was not found.");
    }

    internal byte[] GetItem(uint hnid, int maxBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (hnid == 0) return [];
        if (IsHid(hnid))
        {
            var (block, offset, length) = LocateHid(new Hid(hnid));
            if (length > maxBytes)
                throw new PstException("Attachment exceeds the permitted size.");
            return block.Data.AsSpan(offset, length).ToArray();
        }
        if (!_subNodes.TryGetValue(hnid, out var sub) &&
            !_subNodes.TryGetValue(hnid & 0xFFFFFFE0, out sub))
            throw new PstException("Attachment data subnode was not found.");
        return _ndb.ReadDataTree(sub.Data, maxBytes, cancellationToken);
    }

    public byte[] GetHid(Hid hid)
    {
        var (block, start, len) = LocateHid(hid);
        var item = new byte[len];
        Buffer.BlockCopy(block.Data, start, item, 0, len);
        return item;
    }

    public (HeapBlock Block, int Offset, int Length) LocateHid(Hid hid)
    {
        if (hid.Index == 0) return (_blocks[0], 0, 0);
        if (hid.BlockIndex < 0 || hid.BlockIndex >= _blocks.Length)
            throw new PstException($"HID block index {hid.BlockIndex} is out of range.");

        var block = _blocks[hid.BlockIndex];
        var mapOff = BinaryUtil.ReadU16(block.Data, 0);
        if (mapOff + 4 > block.Data.Length)
            throw new PstException("Heap page map is out of range.");
        var cAlloc = BinaryUtil.ReadU16(block.Data, mapOff);
        if (hid.Index > cAlloc)
            throw new PstException($"HID index {hid.Index} exceeds allocation count {cAlloc}.");

        var start = BinaryUtil.ReadU16(block.Data, mapOff + 4 + (hid.Index - 1) * 2);
        var end = BinaryUtil.ReadU16(block.Data, mapOff + 4 + hid.Index * 2);
        if (end < start || end > block.Data.Length)
            throw new PstException("Heap allocation range is invalid.");
        return (block, start, end - start);
    }

    public IEnumerable<(uint Key, byte[] Record, Hid LeafHid, int RecordOffset)> WalkBth(uint hidHeader)
    {
        var header = GetItem(hidHeader);
        if (header.Length < 8)
            yield break;
        var cbKey = header[1];
        var cbEnt = header[2];
        var levels = header[3];
        var hidRoot = BinaryUtil.ReadU32(header, 4);
        if (hidRoot == 0)
            yield break;

        foreach (var rec in WalkBthLevel(new Hid(hidRoot), cbKey, cbEnt, levels))
            yield return rec;
    }

    private IEnumerable<(uint Key, byte[] Record, Hid LeafHid, int RecordOffset)> WalkBthLevel(Hid hid, byte cbKey, byte cbEnt, int level)
    {
        var (block, start, length) = LocateHid(hid);
        var recSize = level == 0 ? cbKey + cbEnt : cbKey + 4;
        if (recSize <= 0 || length < recSize)
            yield break;
        var count = length / recSize;
        for (var i = 0; i < count; i++)
        {
            var recOff = start + i * recSize;
            var rec = block.Data.AsSpan(recOff, recSize);
            if (level == 0)
            {
                uint key = cbKey switch
                {
                    2 => BinaryUtil.ReadU16(rec, 0),
                    4 => BinaryUtil.ReadU32(rec, 0),
                    _ => BinaryUtil.ReadU32(rec, 0)
                };
                yield return (key, rec.ToArray(), hid, i * recSize);
            }
            else
            {
                var child = new Hid(BinaryUtil.ReadU32(rec, cbKey));
                foreach (var nested in WalkBthLevel(child, cbKey, cbEnt, level - 1))
                    yield return nested;
            }
        }
    }

    public bool TryPatchHidBytes(Hid hid, int offsetInItem, ReadOnlySpan<byte> bytes)
    {
        var (block, start, length) = LocateHid(hid);
        if (!block.CanRewrite) return false;
        if (offsetInItem < 0 || offsetInItem + bytes.Length > length) return false;
        bytes.CopyTo(block.Data.AsSpan(start + offsetInItem));
        _ndb.RewriteExternalBlock(block.Bid, block.Data);
        return true;
    }

    public static bool IsHid(uint hnid) => (hnid & 0x1F) == 0;
}

internal static class PropType
{
    public const ushort Short = 0x0002;
    public const ushort Long = 0x0003;
    public const ushort Float = 0x0004;
    public const ushort Double = 0x0005;
    public const ushort Currency = 0x0006;
    public const ushort Boolean = 0x000B;
    public const ushort Object = 0x000D;
    public const ushort LongLong = 0x0014;
    public const ushort String8 = 0x001E;
    public const ushort Unicode = 0x001F;
    public const ushort SysTime = 0x0040;
    public const ushort Guid = 0x0048;
    public const ushort Binary = 0x0102;
    public const ushort MvMask = 0x1000;

    public static bool IsVariable(ushort type)
    {
        var t = (ushort)(type & ~MvMask);
        return t is String8 or Unicode or Binary or Object or Guid || (type & MvMask) != 0;
    }

    public static int FixedSize(ushort type) => type switch
    {
        Short => 2,
        Long => 4,
        Float => 4,
        Boolean => 1,
        Double => 8,
        Currency => 8,
        LongLong => 8,
        SysTime => 8,
        Guid => 16,
        _ => 0
    };
}

internal static class Pid
{
    public const ushort MessageClass = 0x001A;
    public const ushort Subject = 0x0037;
    public const ushort ClientSubmitTime = 0x0039;
    public const ushort SentRepresentingName = 0x0042;
    public const ushort SentRepresentingEmail = 0x0065;
    public const ushort DisplayTo = 0x0E04;
    public const ushort DisplayCc = 0x0E03;
    public const ushort MessageDeliveryTime = 0x0E06;
    public const ushort MessageFlags = 0x0E07;
    public const ushort MessageSize = 0x0E08;
    public const ushort NormalizedSubject = 0x0E1D;
    public const ushort InternetCodepage = 0x3FDE;
    public const ushort MessageCodepage = 0x3FFD;
    public const ushort Body = 0x1000;
    public const ushort RtfCompressed = 0x1009;
    public const ushort BodyHtml = 0x1013;
    public const ushort SenderName = 0x0C1A;
    public const ushort SenderEmail = 0x0C1F;
    public const ushort DisplayName = 0x3001;
    public const ushort EmailAddress = 0x3003;
    public const ushort SmtpAddress = 0x39FE;
    public const ushort RecipientType = 0x0C15;
    public const ushort Importance = 0x0017;
    public const ushort FlagStatus = 0x1090;
    public const ushort TransportHeaders = 0x007D;
    public const ushort ContentCount = 0x3602;
    public const ushort ContentUnread = 0x3603;
    public const ushort Subfolders = 0x360A;
    public const ushort LtpRowId = 0x67F2;
    public const ushort AttachData = 0x3701;
    public const ushort AttachFilename = 0x3704;
    public const ushort AttachMethod = 0x3705;
    public const ushort AttachLongFilename = 0x3707;
    public const ushort AttachMimeTag = 0x370E;
    public const ushort AttachSize = 0x0E20;
    public const ushort AttachContentId = 0x3712;
    public const ushort IpmSubTreeEntryId = 0x35E0;
    public const ushort IpmWastebasketEntryId = 0x35E3;
}

internal sealed class PropertyBag
{
    private readonly Dictionary<ushort, PropertyValue> _props = [];
    public IReadOnlyDictionary<ushort, PropertyValue> All => _props;
    public void Add(PropertyValue value) => _props[value.Id] = value;
    public bool TryGet(ushort id, out PropertyValue value) => _props.TryGetValue(id, out value!);

    public string GetString(ushort id, Encoding? ansi)
    {
        if (!_props.TryGetValue(id, out var p) || p.Raw.Length == 0) return string.Empty;
        return p.Type switch
        {
            PropType.Unicode => BinaryUtil.DecodeString(p.Raw, true, ansi),
            PropType.String8 => BinaryUtil.DecodeString(p.Raw, false, ansi),
            PropType.Binary => BinaryUtil.DecodeString(p.Raw, false, ansi),
            _ => string.Empty
        };
    }

    public int GetInt(ushort id, int fallback = 0)
    {
        if (!_props.TryGetValue(id, out var p) || p.Raw.Length == 0) return fallback;
        return p.Raw.Length >= 4 ? BinaryUtil.ReadI32(p.Raw, 0) : p.Raw[0];
    }

    public DateTime GetTime(ushort id)
    {
        if (!_props.TryGetValue(id, out var p) || p.Raw.Length < 8) return DateTime.MinValue;
        return BinaryUtil.FromFileTime(BinaryUtil.ReadU64(p.Raw, 0));
    }

    public byte[] GetBinary(ushort id) => _props.TryGetValue(id, out var p) ? p.Raw : [];
}

internal sealed class PropertyValue
{
    public required ushort Id { get; init; }
    public required ushort Type { get; init; }
    public required byte[] Raw { get; init; }
    public uint Hnid { get; init; }
}

internal static class PropertyContext
{
    public static PropertyBag Read(HeapOnNode heap)
    {
        var bag = new PropertyBag();
        foreach (var (key, rec, _, _) in heap.WalkBth(heap.UserRoot))
        {
            if (rec.Length < 8) continue;
            var id = (ushort)key;
            var type = BinaryUtil.ReadU16(rec, 2);
            var hnid = BinaryUtil.ReadU32(rec, 4);
            byte[] raw;
            try { raw = ResolveValue(heap, type, hnid); }
            catch (PstException) { raw = []; }
            bag.Add(new PropertyValue { Id = id, Type = type, Raw = raw, Hnid = hnid });
        }
        return bag;
    }

    public static bool TryPatchFixedUInt32(HeapOnNode heap, ushort propId, uint value)
    {
        foreach (var (key, rec, leafHid, recOff) in heap.WalkBth(heap.UserRoot))
        {
            if ((ushort)key != propId || rec.Length < 8) continue;
            var type = BinaryUtil.ReadU16(rec, 2);
            if (PropType.IsVariable(type) || PropType.FixedSize(type) > 4) return false;
            var bytes = BitConverter.GetBytes(value);
            return heap.TryPatchHidBytes(leafHid, recOff + 4, bytes);
        }
        return false;
    }

    public static byte[] ResolveValue(HeapOnNode heap, ushort type, uint hnid)
    {
        if ((type & PropType.MvMask) != 0)
            return hnid == 0 ? [] : heap.GetItem(hnid);

        var size = PropType.FixedSize(type);
        if (!PropType.IsVariable(type) && size is > 0 and <= 4)
        {
            var exact = new byte[size];
            var src = BitConverter.GetBytes(hnid);
            Buffer.BlockCopy(src, 0, exact, 0, size);
            return exact;
        }

        return hnid == 0 ? [] : heap.GetItem(hnid);
    }
}
