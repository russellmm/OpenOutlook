using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("OpenOutlook.Tests")]

namespace PstCore;

internal sealed class NbtEntry
{
    public required Nid Nid { get; init; }
    public required Bid Data { get; init; }
    public required Bid Sub { get; init; }
    public Nid Parent { get; set; }
    public ulong PageOffset { get; init; }
    public int EntryOffset { get; init; }
}

internal sealed class BbtEntry
{
    public required Bid Bid { get; init; }
    public required ulong Offset { get; init; }
    public required ushort Size { get; init; }
    public required ushort RefCount { get; init; }
}

internal sealed class SubNodeEntry
{
    public required Nid Nid { get; init; }
    public required Bid Data { get; init; }
    public required Bid Sub { get; init; }
}

internal sealed class Ndb : IDisposable
{
    public PstHeader Header { get; }
    public bool Unicode { get; }

    private readonly FileStream _stream;
    private readonly Dictionary<ulong, BbtEntry> _bbt = new();
    private readonly Dictionary<uint, NbtEntry> _nbt = new();
    private readonly object _io = new();

    private Ndb(FileStream stream, PstHeader header)
    {
        _stream = stream;
        Header = header;
        Unicode = header.Format == PstFormatKind.Unicode;
    }

    public static Ndb Open(string path, FileAccess access)
    {
        var share = access == FileAccess.Read ? FileShare.ReadWrite : FileShare.Read;
        var stream = new FileStream(path, FileMode.Open, access, share);
        try
        {
            var header = ReadHeader(stream, path);
            if (header.CryptMethod == PstCryptMethod.WipEncrypted)
                throw new PstException("This PST is protected with Windows Information Protection and cannot be opened.");
            var ndb = new Ndb(stream, header);
            ndb.LoadBTrees();
            return ndb;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public static PstHeader Inspect(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return ReadHeader(stream, path);
    }

    public IReadOnlyCollection<NbtEntry> Nodes => _nbt.Values;

    public bool TryGetNode(uint nid, out NbtEntry entry) => _nbt.TryGetValue(nid, out entry!);

    public NbtEntry GetNode(uint nid)
    {
        if (!_nbt.TryGetValue(nid, out var entry))
            throw new PstException($"Node 0x{nid:X} was not found in the PST.");
        return entry;
    }

    public byte[] ReadNodeData(NbtEntry node) => ReadDataTree(node.Data);

    public Dictionary<uint, SubNodeEntry> ReadSubNodes(NbtEntry node) => ReadSubNodes(node.Sub);

    public Dictionary<uint, SubNodeEntry> ReadSubNodes(Bid subBid)
    {
        var map = new Dictionary<uint, SubNodeEntry>();
        if (subBid.Value == 0) return map;
        ReadSubNodeTree(subBid, map);
        return map;
    }

    internal Dictionary<uint, SubNodeEntry> ReadSubNodes(Bid subBid, CancellationToken cancellationToken)
    {
        var map = new Dictionary<uint, SubNodeEntry>();
        cancellationToken.ThrowIfCancellationRequested();
        if (subBid.Value == 0) return map;
        var visited = new HashSet<ulong>();
        ReadSubNodeTree(subBid, map, visited, cancellationToken);
        return map;
    }

    public byte[] ReadDataTree(Bid bid)
    {
        if (bid.Value == 0) return [];
        var blocks = ReadDataTreeBlocks(bid);
        if (blocks.Count == 1) return blocks[0].Data;
        var total = 0;
        foreach (var b in blocks) total += b.Data.Length;
        var merged = new byte[total];
        var o = 0;
        foreach (var b in blocks)
        {
            Buffer.BlockCopy(b.Data, 0, merged, o, b.Data.Length);
            o += b.Data.Length;
        }
        return merged;
    }

    public List<(Bid Bid, byte[] Data)> ReadDataTreeBlocks(Bid bid)
    {
        var list = new List<(Bid, byte[])>();
        if (bid.Value == 0) return list;
        CollectDataBlocks(bid, list);
        return list;
    }

    // Unlike the legacy reader, check each BBT size before allocating its block. The
    // traversal limit also bounds zero-length blocks and cyclic/malformed XBLOCK trees.
    internal byte[] ReadDataTree(Bid bid, int maxBytes, CancellationToken cancellationToken)
    {
        var blocks = ReadDataTreeBlocks(bid, maxBytes, cancellationToken);
        if (blocks.Count == 0) return [];
        if (blocks.Count == 1) return blocks[0].Data;
        var total = 0;
        foreach (var block in blocks) total = checked(total + block.Data.Length);
        var merged = new byte[total];
        var offset = 0;
        foreach (var block in blocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Buffer.BlockCopy(block.Data, 0, merged, offset, block.Data.Length);
            offset += block.Data.Length;
        }
        return merged;
    }

    internal List<(Bid Bid, byte[] Data)> ReadDataTreeBlocks(Bid bid, int maxBytes, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        var list = new List<(Bid, byte[])>();
        cancellationToken.ThrowIfCancellationRequested();
        if (bid.Value == 0) return list;
        var remaining = maxBytes;
        var visits = 0;
        var ancestors = new HashSet<ulong>();
        CollectBoundedDataBlocks(bid, list, ref remaining, ref visits, ancestors, cancellationToken);
        return list;
    }

    private void CollectBoundedDataBlocks(Bid bid, List<(Bid Bid, byte[] Data)> list,
        ref int remaining, ref int visits, HashSet<ulong> ancestors, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (++visits > 65536 || ancestors.Count >= 32)
            throw new PstException("Attachment data tree is too complex.");
        if (!_bbt.TryGetValue(bid.LookupKey, out var entry))
            throw new PstException("Attachment data block is missing.");
        if (!bid.IsInternal)
        {
            if (entry.Size > remaining)
                throw new PstException("Attachment exceeds the permitted size.");
            remaining -= entry.Size;
            list.Add((bid, ReadExternalBlock(bid)));
            return;
        }

        if (!ancestors.Add(bid.LookupKey))
            throw new PstException("Attachment data tree contains a cycle.");
        try
        {
            var raw = ReadRawBlock(bid); // PST internal blocks are at most 65535 bytes.
            if (raw.Length < 8 || raw[0] != 1 || raw[1] is not (1 or 2))
                throw new PstException("Attachment data tree is malformed.");
            var level = raw[1];
            var count = BinaryUtil.ReadU16(raw, 2);
            var bidSize = BinaryUtil.BidSize(Unicode);
            if (count > (raw.Length - 8) / bidSize)
                throw new PstException("Attachment data tree is truncated.");
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var child = new Bid(BinaryUtil.ReadBid(raw, 8 + i * bidSize, Unicode));
                if (child.Value == 0 || child.IsInternal != (level == 2))
                    throw new PstException("Attachment data tree is malformed.");
                CollectBoundedDataBlocks(child, list, ref remaining, ref visits, ancestors, cancellationToken);
            }
        }
        finally { ancestors.Remove(bid.LookupKey); }
    }

    public void RewriteExternalBlock(Bid bid, ReadOnlySpan<byte> plaintext)
    {
        if (bid.IsInternal)
            throw new PstException("Cannot rewrite an internal (XBLOCK) node in place.");
        if (!_bbt.TryGetValue(bid.LookupKey, out var entry))
            throw new PstException($"Block 0x{bid.Value:X} was not found.");
        if (plaintext.Length != entry.Size)
            throw new PstException("In-place edits must keep the original block size.");

        var encoded = plaintext.ToArray();
        PstCrypto.Encode(encoded, Header.CryptMethod, bid.CyclicKey);
        var crc = PstCrypto.ComputeCrc(encoded);
        WriteBlockBytes(entry, encoded, crc);
    }

    public void Dispose() => _stream.Dispose();

    private static PstHeader ReadHeader(FileStream stream, string path)
    {
        stream.Seek(0, SeekOrigin.Begin);
        var buf = new byte[564];
        var n = stream.Read(buf, 0, buf.Length);
        if (n < 512)
            throw new PstException("File is too small to be a PST.");
        if (buf[0] != 0x21 || buf[1] != 0x42 || buf[2] != 0x44 || buf[3] != 0x4E)
            throw new PstException("Not a PST file (missing !BDN signature).");
        if (buf[8] != 0x53 || buf[9] != 0x4D)
            throw new PstException("Not a PST file (missing SM client magic).");

        var wVer = BinaryUtil.ReadU16(buf, 10);
        var wVerClient = BinaryUtil.ReadU16(buf, 12);
        var unicode = wVer >= 23;
        byte crypt;
        ulong fileEof, nbtBid, nbtIb, bbtBid, bbtIb;
        byte amap;
        uint unique;

        if (unicode)
        {
            unique = BinaryUtil.ReadU32(buf, 0x28);
            const int root = 0xB4;
            fileEof = BinaryUtil.ReadU64(buf, root + 4);
            nbtBid = BinaryUtil.ReadU64(buf, root + 36);
            nbtIb = BinaryUtil.ReadU64(buf, root + 44);
            bbtBid = BinaryUtil.ReadU64(buf, root + 52);
            bbtIb = BinaryUtil.ReadU64(buf, root + 60);
            amap = buf[root + 68];
            crypt = buf[0x201];
            if (buf[0x200] != 0x80)
                throw new PstException("Unicode PST header sentinel is invalid.");
        }
        else
        {
            if (wVer is not (14 or 15))
                throw new PstException($"Unrecognized PST wVer={wVer}. Expected 14/15 (ANSI) or >=23 (Unicode).");
            unique = BinaryUtil.ReadU32(buf, 0x28);
            const int root = 0xA4;
            fileEof = BinaryUtil.ReadU32(buf, root + 4);
            nbtBid = BinaryUtil.ReadU32(buf, root + 20);
            nbtIb = BinaryUtil.ReadU32(buf, root + 24);
            bbtBid = BinaryUtil.ReadU32(buf, root + 28);
            bbtIb = BinaryUtil.ReadU32(buf, root + 32);
            amap = buf[root + 36];
            crypt = buf[0x1CD];
            if (buf[0x1CC] != 0x80)
                throw new PstException("ANSI PST header sentinel is invalid.");
        }

        return new PstHeader
        {
            Path = path,
            Format = unicode ? PstFormatKind.Unicode : PstFormatKind.Ansi,
            WVer = wVer,
            WVerClient = wVerClient,
            CryptMethod = (PstCryptMethod)crypt,
            FileEof = fileEof,
            NbtRootBid = nbtBid,
            NbtRootIb = nbtIb,
            BbtRootBid = bbtBid,
            BbtRootIb = bbtIb,
            AMapValid = amap,
            Unique = unique
        };
    }

    private void LoadBTrees()
    {
        WalkBt(Header.BbtRootIb, isNbt: false);
        WalkBt(Header.NbtRootIb, isNbt: true);
    }

    private void WalkBt(ulong ib, bool isNbt)
    {
        var page = ReadPage(ib);
        var unicode = Unicode;
        var cEntOff = unicode ? 488 : 496;
        var cEnt = page[cEntOff];
        var cbEnt = page[cEntOff + 2];
        var cLevel = page[cEntOff + 3];
        var trailerOff = unicode ? 496 : 500;
        var ptype = page[trailerOff];
        var expected = isNbt ? (byte)0x81 : (byte)0x80;
        if (ptype != expected)
            throw new PstException($"Unexpected B-tree page type 0x{ptype:X2}.");

        for (var i = 0; i < cEnt; i++)
        {
            var e = page.AsSpan(i * cbEnt, cbEnt);
            if (cLevel > 0)
            {
                var childIb = unicode ? BinaryUtil.ReadU64(e, 16) : BinaryUtil.ReadU32(e, 8);
                WalkBt(childIb, isNbt);
            }
            else if (isNbt)
            {
                Nid nid;
                Bid data, sub;
                Nid parent;
                if (unicode)
                {
                    nid = new Nid(BinaryUtil.ReadU32(e, 0));
                    data = new Bid(BinaryUtil.ReadU64(e, 8));
                    sub = new Bid(BinaryUtil.ReadU64(e, 16));
                    parent = new Nid(BinaryUtil.ReadU32(e, 24));
                }
                else
                {
                    nid = new Nid(BinaryUtil.ReadU32(e, 0));
                    data = new Bid(BinaryUtil.ReadU32(e, 4));
                    sub = new Bid(BinaryUtil.ReadU32(e, 8));
                    parent = new Nid(BinaryUtil.ReadU32(e, 12));
                }
                _nbt[nid.Value] = new NbtEntry
                {
                    Nid = nid,
                    Data = data,
                    Sub = sub,
                    Parent = parent,
                    PageOffset = ib,
                    EntryOffset = i * cbEnt
                };
            }
            else
            {
                Bid bid;
                ulong offset;
                ushort size, refs;
                if (unicode)
                {
                    bid = new Bid(BinaryUtil.ReadU64(e, 0));
                    offset = BinaryUtil.ReadU64(e, 8);
                    size = BinaryUtil.ReadU16(e, 16);
                    refs = BinaryUtil.ReadU16(e, 18);
                }
                else
                {
                    bid = new Bid(BinaryUtil.ReadU32(e, 0));
                    offset = BinaryUtil.ReadU32(e, 4);
                    size = BinaryUtil.ReadU16(e, 8);
                    refs = BinaryUtil.ReadU16(e, 10);
                }
                _bbt[bid.LookupKey] = new BbtEntry { Bid = bid, Offset = offset, Size = size, RefCount = refs };
            }
        }
    }

    private byte[] ReadPage(ulong ib)
    {
        var page = new byte[512];
        lock (_io)
        {
            _stream.Seek((long)ib, SeekOrigin.Begin);
            if (_stream.Read(page, 0, 512) != 512)
                throw new PstException($"Failed to read 512-byte page at offset {ib}.");
        }
        return page;
    }

    private byte[] ReadRawBlock(Bid bid)
    {
        if (!_bbt.TryGetValue(bid.LookupKey, out var entry))
            throw new PstException($"Block 0x{bid.Value:X} was not found in the Block BTree.");
        var data = new byte[entry.Size];
        lock (_io)
        {
            _stream.Seek((long)entry.Offset, SeekOrigin.Begin);
            if (_stream.Read(data, 0, data.Length) != data.Length)
                throw new PstException($"Failed to read block 0x{bid.Value:X}.");
        }
        return data;
    }

    private byte[] ReadExternalBlock(Bid bid)
    {
        var data = ReadRawBlock(bid);
        PstCrypto.Decode(data, Header.CryptMethod, bid.CyclicKey);
        return data;
    }

    private void CollectDataBlocks(Bid bid, List<(Bid Bid, byte[] Data)> list)
    {
        if (!bid.IsInternal)
        {
            list.Add((bid, ReadExternalBlock(bid)));
            return;
        }

        var raw = ReadRawBlock(bid);
        var btype = raw[0];
        var cLevel = raw[1];
        var cEnt = BinaryUtil.ReadU16(raw, 2);
        var bidSize = BinaryUtil.BidSize(Unicode);
        if (btype != 1)
            throw new PstException($"Unexpected internal block type 0x{btype:X2}.");

        for (var i = 0; i < cEnt; i++)
        {
            var child = new Bid(BinaryUtil.ReadBid(raw, 8 + i * bidSize, Unicode));
            if (cLevel == 1)
                list.Add((child, ReadExternalBlock(child)));
            else
                CollectDataBlocks(child, list);
        }
    }

    private void ReadSubNodeTree(Bid bid, Dictionary<uint, SubNodeEntry> map)
    {
        var raw = ReadRawBlock(bid);
        if (raw.Length < 4)
            throw new PstException("Subnode block is truncated.");
        var btype = raw[0];
        var cLevel = raw[1];
        var cEnt = BinaryUtil.ReadU16(raw, 2);
        if (btype != 2)
            throw new PstException($"Unexpected subnode block type 0x{btype:X2}.");

        var unicode = Unicode;
        var entrySize = cLevel > 0
            ? (unicode ? 16 : 8)
            : (unicode ? 24 : 12);
        var header = SubNodeHeaderSize(raw.Length, cEnt, entrySize, unicode);

        if (cLevel > 0)
        {
            var max = Math.Max(0, (raw.Length - header) / entrySize);
            var n = Math.Min((int)cEnt, max);
            for (var i = 0; i < n; i++)
            {
                var o = header + i * entrySize;
                var child = new Bid(unicode ? BinaryUtil.ReadU64(raw, o + 8) : BinaryUtil.ReadU32(raw, o + 4));
                if (child.Value != 0)
                    ReadSubNodeTree(child, map);
            }
            return;
        }

        {
            var max = Math.Max(0, (raw.Length - header) / entrySize);
            var n = Math.Min((int)cEnt, max);
            for (var i = 0; i < n; i++)
            {
                var o = header + i * entrySize;
                Nid nid;
                Bid data, sub;
                if (unicode)
                {
                    nid = new Nid(BinaryUtil.ReadU32(raw, o));
                    data = new Bid(BinaryUtil.ReadU64(raw, o + 8));
                    sub = new Bid(BinaryUtil.ReadU64(raw, o + 16));
                }
                else
                {
                    nid = new Nid(BinaryUtil.ReadU32(raw, o));
                    data = new Bid(BinaryUtil.ReadU32(raw, o + 4));
                    sub = new Bid(BinaryUtil.ReadU32(raw, o + 8));
                }
                if (nid.Value != 0)
                    map[nid.Value] = new SubNodeEntry { Nid = nid, Data = data, Sub = sub };
            }
        }
    }

    private void ReadSubNodeTree(Bid bid, Dictionary<uint, SubNodeEntry> map,
        HashSet<ulong> visited, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (visited.Count >= 65536 || !visited.Add(bid.LookupKey))
            throw new PstException("Attachment subnode tree is too complex.");
        var raw = ReadRawBlock(bid);
        if (raw.Length < 4 || raw[0] != 2 || raw[1] is not (0 or 1))
            throw new PstException("Attachment subnode tree is malformed.");
        var count = BinaryUtil.ReadU16(raw, 2);
        var internalLevel = raw[1] == 1;
        var entrySize = internalLevel ? (Unicode ? 16 : 8) : (Unicode ? 24 : 12);
        var header = SubNodeHeaderSize(raw.Length, count, entrySize, Unicode);
        if (count > (raw.Length - header) / entrySize)
            throw new PstException("Attachment subnode tree is truncated.");
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = header + i * entrySize;
            if (internalLevel)
            {
                var child = new Bid(Unicode ? BinaryUtil.ReadU64(raw, offset + 8) : BinaryUtil.ReadU32(raw, offset + 4));
                if (!child.IsInternal)
                    throw new PstException("Attachment subnode tree is malformed.");
                ReadSubNodeTree(child, map, visited, cancellationToken);
            }
            else
            {
                var nid = new Nid(BinaryUtil.ReadU32(raw, offset));
                var data = new Bid(Unicode ? BinaryUtil.ReadU64(raw, offset + 8) : BinaryUtil.ReadU32(raw, offset + 4));
                var sub = new Bid(Unicode ? BinaryUtil.ReadU64(raw, offset + 16) : BinaryUtil.ReadU32(raw, offset + 8));
                if (nid.Value != 0 && !map.TryAdd(nid.Value, new SubNodeEntry { Nid = nid, Data = data, Sub = sub }))
                    throw new PstException("Attachment subnode tree has duplicate entries.");
            }
        }
    }

    private static int SubNodeHeaderSize(int rawLength, int cEnt, int entrySize, bool unicode)
    {
        if (unicode) return 8;
        var needed4 = 4 + cEnt * entrySize;
        var needed8 = 8 + cEnt * entrySize;
        if (needed4 <= rawLength) return 4;
        if (needed8 <= rawLength) return 8;
        return rawLength >= 8 ? 8 : 4;
    }

    public void RewriteNidParent(NbtEntry node, Nid newParent)
    {
        var page = ReadPage(node.PageOffset);
        var unicode = Unicode;
        var parentOff = node.EntryOffset + (unicode ? 24 : 12);
        BinaryUtil.WriteU32(page, parentOff, newParent.Value);
        var trailerOff = unicode ? 496 : 500;
        var crcLen = trailerOff;
        var crc = PstCrypto.ComputeCrc(page.AsSpan(0, crcLen));
        if (unicode)
            BinaryUtil.WriteU32(page, trailerOff + 4, crc);
        else
            BinaryUtil.WriteU32(page, trailerOff + 8, crc);

        lock (_io)
        {
            _stream.Seek((long)node.PageOffset, SeekOrigin.Begin);
            _stream.Write(page, 0, page.Length);
            _stream.Flush();
        }
        node.Parent = newParent;
    }

    private void WriteBlockBytes(BbtEntry entry, byte[] encoded, uint crc)
    {
        var trailerSize = Unicode ? 16 : 12;
        var total = ((entry.Size + trailerSize + 63) / 64) * 64;
        var trailerOff = (int)(total - trailerSize);

        lock (_io)
        {
            _stream.Seek((long)entry.Offset, SeekOrigin.Begin);
            _stream.Write(encoded, 0, encoded.Length);

            var pad = trailerOff - encoded.Length;
            if (pad > 0)
                _stream.Write(new byte[pad], 0, pad);

            var trailer = new byte[trailerSize];
            BinaryUtil.WriteU16(trailer, 0, entry.Size);
            BinaryUtil.WriteU16(trailer, 2, PstCrypto.ComputeSig(entry.Offset, entry.Bid.Value));
            if (Unicode)
            {
                BinaryUtil.WriteU32(trailer, 4, crc);
                BitConverter.TryWriteBytes(trailer.AsSpan(8), entry.Bid.Value);
            }
            else
            {
                BinaryUtil.WriteU32(trailer, 4, (uint)entry.Bid.Value);
                BinaryUtil.WriteU32(trailer, 8, crc);
            }
            _stream.Write(trailer, 0, trailer.Length);
            _stream.Flush();
        }
    }
}
