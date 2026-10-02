using System.Text;
using PstCore;

namespace OpenOutlook.Tests;

/// <summary>TEMPORARY Phase C probe - dumps real table/heap geometry to a log file, deleted after use.</summary>
public sealed class PhaseCProbeTests
{
    [Fact]
    public void DumpTableGeometry()
    {
        var src = Environment.GetEnvironmentVariable("OPENOUTLOOK_TEST_PST");
        if (string.IsNullOrWhiteSpace(src) || !File.Exists(src)) return;
        var tmp = Path.Combine(Path.GetTempPath(), $"probe-{Guid.NewGuid():N}.pst");
        File.Copy(src, tmp, true);
        var log = new StringBuilder();
        try
        {
            using var store = PstStore.Open(tmp, writable: false);
            var ndbField = typeof(PstStore).GetField("_ndb", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var ndb = (Ndb)ndbField.GetValue(store)!;

            foreach (var folder in store.AllFolders().OrderBy(f => f.Name))
            {
                uint tableNid = folder.Nid | (uint)NidType.ContentsTable;
                if (!ndb.TryGetNode(tableNid, out var tnode)) continue;
                log.AppendLine($"=== FOLDER {folder.Name} nid=0x{folder.Nid:X} tableNid=0x{tableNid:X}");
                var blocks = ndb.ReadDataTreeBlocks(tnode.Data);
                log.AppendLine($"  table node bidData=0x{tnode.Data.Value:X} leafblocks={blocks.Count} sizes=[{string.Join(",", blocks.Select(b => b.Data.Length))}]");
                var heap = HeapOnNode.Load(ndb, tnode);
                // block0 map geometry
                var b0 = heap.Blocks[0].Data;
                int mapOff = BinaryUtil.ReadU16(b0, 0);
                int cAlloc = BinaryUtil.ReadU16(b0, mapOff);
                log.AppendLine($"  heap block0 len={b0.Length} mapOff={mapOff} cAlloc={cAlloc}");
                var bounds = new List<int>();
                for (int i = 0; i <= cAlloc; i++) bounds.Add(BinaryUtil.ReadU16(b0, mapOff + 4 + i * 2));
                log.AppendLine($"  boundaries=[{string.Join(",", bounds)}]");
                int lastEnd = bounds.Count > 0 ? bounds[^1] : 0;
                log.AppendLine($"  lastAllocEnd={lastEnd} freeTail={mapOff - lastEnd} mapArrayEnd={mapOff + 4 + (cAlloc + 1) * 2} blockLen={b0.Length}");
                // subnodes overview
                foreach (var sn in heap.SubNodes.Values.OrderBy(s => s.Nid.Value))
                {
                    var lb = ndb.ReadDataTreeBlocks(sn.Data);
                    if (lb.Count <= 3)
                        log.AppendLine($"  subnode 0x{sn.Nid.Value:X} blocks={lb.Count} sizes=[{string.Join(",", lb.Select(b => b.Data.Length))}]");
                    else
                        log.AppendLine($"  subnode 0x{sn.Nid.Value:X} blocks={lb.Count} firstSizes=[{string.Join(",", lb.Take(3).Select(b => b.Data.Length))}] last={lb[^1].Data.Length}");
                }
                var table = TableContext.Load(heap);
                var info = heap.GetItem(heap.UserRoot);
                ushort tciBm = BinaryUtil.ReadU16(info, 8);
                uint hidRowIndex = BinaryUtil.ReadU32(info, 10);
                uint hnidRows = BinaryUtil.ReadU32(info, 14);
                log.AppendLine($"  TCINFO: cCols={info[1]} tciBm(rowSize)={tciBm} tci1={BinaryUtil.ReadU16(info, 6)} hidRowIndex=0x{hidRowIndex:X} hnidRows=0x{hnidRows:X}");
                foreach (var c in table.Columns)
                    log.AppendLine($"    col {c.Id:X4}/{c.Type:X4} ib={c.IbData} cb={c.CbData} bit={c.IBit}");
                // BTH leaf geometry
                var bthHdr = heap.GetItem(hidRowIndex);
                log.AppendLine($"  BTH hdr len={bthHdr.Length} cbKey={bthHdr[1]} cbEnt={bthHdr[2]} levels={bthHdr[3]} rootHid=0x{BinaryUtil.ReadU32(bthHdr, 4):X}");
                var leaf = heap.GetItem(BinaryUtil.ReadU32(bthHdr, 4));
                int recSize = bthHdr[1] + bthHdr[2];
                log.AppendLine($"  BTH leaf len={leaf.Length} recSize={recSize} count={leaf.Length / Math.Max(1, recSize)} slackBytes={(leaf.Length % recSize) + (mapOff - lastEnd)}");
                foreach (var (key, _, leafHid, recOff) in heap.WalkBth(hidRowIndex))
                    log.AppendLine($"    rowId=0x{key:X} off={recOff} leafBlockCanRewrite={heap.LocateHid(leafHid).Block.CanRewrite}");
                // matrix geometry
                var matrix = heap.GetItem(hnidRows);
                int trailer = 16, blockData = 8192 - trailer;
                int rowsPerBlock = Math.Max(1, blockData / tciBm);
                log.AppendLine($"  matrix merged len={matrix.Length} rowsPerBlock={rowsPerBlock} capacityIfPadded={(matrix.Length / blockData) * rowsPerBlock + Math.Max(0, (matrix.Length % blockData) / tciBm)}");
                var idxs = new List<uint>();
                foreach (var (key, rec, _, _) in heap.WalkBth(hidRowIndex))
                    if (rec.Length >= 8 && key != 0) idxs.Add(BinaryUtil.ReadU32(rec, 4));
                log.AppendLine($"  rowIndexes=[{string.Join(",", idxs.OrderBy(x => x))}] max={(idxs.Count == 0 ? 0u : idxs.Max())}");
                // dump first row record raw + find where the nid value appears
                if (table.Rows.Count > 0)
                {
                    var r0 = table.Rows[0];
                    long ri = idxs.OrderBy(x => x).First();
                    int off;
                    if (matrix.Length >= (ri + 1) * tciBm && matrix.Length % tciBm == 0) off = (int)(ri * tciBm);
                    else { int bi = (int)(ri / rowsPerBlock); int ib2 = (int)(ri % rowsPerBlock); off = bi * blockData + ib2 * tciBm; }
                    var rawRec = matrix.AsSpan(off, Math.Min(tciBm, matrix.Length - off)).ToArray();
                    log.AppendLine($"  row0 rowId=0x{r0.RowId:X} recordOff={off} first64hex={Convert.ToHexString(rawRec.AsSpan(0, Math.Min(64, rawRec.Length)))}");
                    // scan for nid bytes at even offsets
                    var nidBytes = BitConverter.GetBytes(r0.RowId);
                    for (int o = 0; o + 4 <= rawRec.Length; o += 2)
                        if (rawRec[o] == nidBytes[0] && rawRec[o + 1] == nidBytes[1] && rawRec[o + 2] == nidBytes[2] && rawRec[o + 3] == nidBytes[3])
                            log.AppendLine($"    -> nid value found at record offset {o}");
                    foreach (var cell in r0.Cells.OrderBy(c => c.Key))
                        log.AppendLine($"    cell {cell.Key:X4}: {(cell.Value.Length <= 8 ? Convert.ToHexString(cell.Value) : $"hnid-data len={cell.Value.Length}")}");
                }
            }
        }
        finally
        {
            File.WriteAllText("/tmp/phase-c-probe.txt", log.ToString());
            File.Delete(tmp);
        }
    }
}
