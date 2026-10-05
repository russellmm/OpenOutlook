#!/usr/bin/env python3
"""pstdebug2.py - pinpoints the failing TC cell. Usage: python3 pstdebug2.py FILE.pst"""
import sys, os, struct, traceback
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pstcore as P

reports = []
orig = P.TC._parse_row

def patched(self, row):
    ceb = row[self.rgib[3]:]
    for pid, ptype, ibd, cbd, ibit in self.cols:
        if not (ceb[ibit >> 3] & (0x80 >> (ibit & 7))):
            continue
        if ptype in P.PTYPE_FIXED:
            continue
        hnid = struct.unpack_from('<I', row, ibd)[0]
        if hnid and hnid & 0x1F == 0:
            blk = hnid >> 16
            if blk >= len(self.heap.blocks) or ((hnid >> 5) & 0x7FF) == 0:
                reports.append((self.node.nid, pid, ptype, ibd, cbd, ibit, hnid, len(self.heap.blocks),
                                self.rowsize, self.rgib, row[:16].hex(), row[ibd:ibd + 8].hex()))
                raise P.PSTError('bad HID')
    return orig(self, row)

P.TC._parse_row = patched
st = P.Store(sys.argv[1])
shown = 0
for f, d in st.walk():
    try:
        rows = f.contents()
    except Exception as e:
        pass
    else:
        for m in rows:
            try:
                msg = st.message(m['nid']); msg.recipients(); msg.attachments()
            except Exception:
                pass
    while reports and shown < 6:
        r = reports.pop(0); shown += 1
        print('FOLDER %s: TC node 0x%x pid=0x%04X ptype=0x%04X ibData=%d cbData=%d iBit=%d hnid=0x%x heapblocks=%d rowsize=%d rgib=%s' % ((f.name,) + r[:10]))
        print('   row[:16]=%s  cellbytes=%s' % (r[10], r[11]))
    reports.clear()
print('done')
