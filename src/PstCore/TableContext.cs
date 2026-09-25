using System.Text;

namespace PstCore;

internal sealed class TableColumn
{
    public required uint Tag { get; init; }
    public ushort Id => (ushort)(Tag >> 16);
    public ushort Type => (ushort)(Tag & 0xFFFF);
    public required ushort IbData { get; init; }
    public required byte CbData { get; init; }
    public required byte IBit { get; init; }
}

internal sealed class TableRow
{
    public required uint RowId { get; init; }
    public required Dictionary<ushort, byte[]> Cells { get; init; }
}

internal sealed class TableContext
{
    public IReadOnlyList<TableColumn> Columns { get; }
    public IReadOnlyList<TableRow> Rows { get; }

    private TableContext(IReadOnlyList<TableColumn> columns, IReadOnlyList<TableRow> rows)
    {
        Columns = columns;
        Rows = rows;
    }

    public static TableContext? TryLoad(Ndb ndb, uint nid)
    {
        if (!ndb.TryGetNode(nid, out var node)) return null;
        try
        {
            var heap = HeapOnNode.Load(ndb, node);
            return Load(heap);
        }
        catch
        {
            return null;
        }
    }

    public static TableContext Load(HeapOnNode heap)
    {
        var info = heap.GetItem(heap.UserRoot);
        if (info.Length < 22)
            throw new PstException("Table context header is truncated.");

        var cCols = info[1];
        var tci4 = BinaryUtil.ReadU16(info, 2);
        var tci2 = BinaryUtil.ReadU16(info, 4);
        var tci1 = BinaryUtil.ReadU16(info, 6);
        var tciBm = BinaryUtil.ReadU16(info, 8);
        var hidRowIndex = BinaryUtil.ReadU32(info, 10);
        var hnidRows = BinaryUtil.ReadU32(info, 14);

        var columns = new List<TableColumn>(cCols);
        const int colOff = 22;
        for (var i = 0; i < cCols; i++)
        {
            var o = colOff + i * 8;
            if (o + 8 > info.Length) break;
            columns.Add(new TableColumn
            {
                Tag = BinaryUtil.ReadU32(info, o),
                IbData = BinaryUtil.ReadU16(info, o + 4),
                CbData = info[o + 6],
                IBit = info[o + 7]
            });
        }

        var rowIdToIndex = new Dictionary<uint, uint>();
        foreach (var (key, rec, _, _) in heap.WalkBth(hidRowIndex))
        {
            if (rec.Length < 8) continue;
            var rowId = BinaryUtil.ReadU32(rec, 0);
            var rowIndex = BinaryUtil.ReadU32(rec, 4);
            rowIdToIndex[rowId] = rowIndex;
        }

        var rows = new List<TableRow>();
        if (hnidRows == 0 || tciBm == 0)
            return new TableContext(columns, rows);

        var matrix = heap.GetItem(hnidRows);
        var rowSize = tciBm;
        if (rowSize <= 0) return new TableContext(columns, rows);

        var trailer = heap.Ndb.Unicode ? 16 : 12;
        var blockData = 8192 - trailer;
        var rowsPerBlock = Math.Max(1, blockData / rowSize);

        foreach (var (rowId, rowIndex) in rowIdToIndex.OrderBy(p => p.Value))
        {
            int offset;
            if (matrix.Length >= (rowIndex + 1) * rowSize && matrix.Length % rowSize == 0)
                offset = (int)(rowIndex * rowSize);
            else
            {
                var blockIndex = (int)(rowIndex / (uint)rowsPerBlock);
                var inBlock = (int)(rowIndex % (uint)rowsPerBlock);
                offset = blockIndex * blockData + inBlock * rowSize;
            }

            if (offset < 0 || offset + rowSize > matrix.Length)
                continue;

            var rowSpan = matrix.AsSpan(offset, rowSize);
            var cells = new Dictionary<ushort, byte[]>();
            foreach (var col in columns)
            {
                var bit = col.IBit;
                var existByte = tci1 + (bit >> 3);
                if (existByte >= rowSize) continue;
                if ((rowSpan[existByte] & (1 << (bit & 7))) == 0) continue;

                var cell = ReadCell(heap, col, rowSpan);
                if (cell != null)
                    cells[col.Id] = cell;
            }

            rows.Add(new TableRow { RowId = rowId, Cells = cells });
        }

        return new TableContext(columns, rows);
    }

    private static byte[]? ReadCell(HeapOnNode heap, TableColumn col, ReadOnlySpan<byte> row)
    {
        var ib = col.IbData;
        var cb = col.CbData;
        if (ib + cb > row.Length) return null;

        if (!PropType.IsVariable(col.Type) && PropType.FixedSize(col.Type) is > 0 and <= 8 && cb <= 8)
        {
            var data = new byte[cb];
            row.Slice(ib, cb).CopyTo(data);
            return data;
        }

        uint hnid;
        if (cb >= 4)
            hnid = BinaryUtil.ReadU32(row, ib);
        else if (cb == 2)
            hnid = BinaryUtil.ReadU16(row, ib);
        else
            return row.Slice(ib, cb).ToArray();

        if (hnid == 0) return [];
        try
        {
            return PropertyContext.ResolveValue(heap, col.Type, hnid);
        }
        catch (PstException)
        {
            return [];
        }
    }

    public string CellString(TableRow row, ushort id, Encoding? ansi)
    {
        if (!row.Cells.TryGetValue(id, out var data) || data.Length == 0) return string.Empty;
        var col = Columns.FirstOrDefault(c => c.Id == id);
        var type = col?.Type ?? 0;
        if (type == PropType.Unicode || (type == 0 && LooksUtf16(data)))
            return BinaryUtil.DecodeString(data, true, ansi);
        if (type == PropType.String8 || type == 0)
            return BinaryUtil.DecodeString(data, false, ansi);
        return BinaryUtil.DecodeString(data, false, ansi);
    }

    public int CellInt(TableRow row, ushort id, int fallback = 0)
    {
        if (!row.Cells.TryGetValue(id, out var data) || data.Length == 0) return fallback;
        return data.Length >= 4 ? BinaryUtil.ReadI32(data, 0) : data[0];
    }

    public DateTime CellTime(TableRow row, ushort id)
    {
        if (!row.Cells.TryGetValue(id, out var data) || data.Length < 8) return DateTime.MinValue;
        return BinaryUtil.FromFileTime(BinaryUtil.ReadU64(data, 0));
    }

    private static bool LooksUtf16(byte[] data) => data.Length >= 4 && data.Length % 2 == 0 && data.Contains((byte)0);
}
