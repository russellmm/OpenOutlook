#!/usr/bin/env python3
"""Makes a PST file inconsistent in the ways opst_fix / pstfix repair (for testing the fixer; run on a COPY).
   R1: removes the ID cells of two message rows and one folder row      R2: deletes a message node behind the ID map's back
   R3: drops two messages from their message-index bucket              R4: gives two rows the same row version
   R5: lowers the header NID high-water marks of types 5 and 31
usage: pydamage.py REPO_DIR FILE.pst CONTENTS_TABLE_NID"""
import os, sys, struct
sys.path.insert(0, sys.argv[1])
import pstcore as P
import pstops as O
import pstedit as E

path, tn = sys.argv[2], int(sys.argv[3], 0)
ops = O.Ops(path)
ed, w = ops.ed, ops.w
tc = ed.load_tc(tn)
c30, c33, c34, cv = (tc.col_by_pid.get(p) for p in (0x0E30, 0x0E33, 0x0E34, 0x67F3))
rows = [r for r, _c in tc.rows]
assert len(rows) >= 8, 'need a contents table with at least 8 rows'
# R1
for rid in rows[:2]:
    cells = tc.rows[tc.find(rid)][1]
    for ci in (c30, c33, c34):
        cells.pop(ci, None)
# R4
a, b = tc.find(rows[3]), tc.find(rows[4])
tc.rows[b][1][cv] = tc.rows[a][1][cv]
ed.store_tc(tn, tc)
# R2: remove a message node without touching the ID map
victim = rows[6]
tc = ed.load_tc(tn)
tc.remove(victim)
ed.store_tc(tn, tc)
bd, bs, _p = ed.entry(victim)
w.nbt.delete(victim)
w.release(bd)
if bs:
    w.release(bs)
# R3: take two messages out of their message-index bucket
e = ed.entry(0xE01)
bd, bs, par = e
blocks = [bytearray(b) for b in ed.leaf_blocks(bd)]
hp = E.Heap(blocks)
_k, _v, recs = hp.bth(0x40)
drop = {rows[5], rows[7]}
done = 0
for _key, val in recs:
    hid = struct.unpack('<I', val)[0]
    lst = bytes(hp.get(hid))
    ns = [struct.unpack_from('<I', lst, i)[0] for i in range(0, len(lst), 4)]
    keep = [n for n in ns if n not in drop]
    if len(keep) != len(ns) and keep:
        assert O._grow_heap_item(blocks, hid, b''.join(struct.pack('<I', n) for n in keep))
        done += len(ns) - len(keep)
        hp = E.Heap(blocks)
assert done, 'messages not found in a bucket with other members'
nbd = ed.put_blocks([bytes(x) for x in blocks])
w.nbt.put(0xE01, struct.pack('<QQQII', 0xE01, nbd, bs, par, 0))
w.release(bd)
# R5
for t in (5, 31):
    struct.pack_into('<I', w.hdr, 44 + 4 * t, 1)
ops.commit()
ops.close()
print('damaged: rows', [hex(r) for r in rows[:8]])
