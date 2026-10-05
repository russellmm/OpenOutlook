import struct, pstcore as P, pstwrite as W, pstedit as E
def bth_of(path):
    w = W.PSTWriter(path); ed = E.Editor(w, P.MPBB_I)
    hp = E.Heap(ed.leaf_blocks(ed.entry(0xc01)[0]))
    cb, ce, recs = hp.bth(hp.root)
    return ed, hp, cb, ce, recs
ea, ha, cb, ce, ra = bth_of('test_E.bak'); eb, hb, cb2, ce2, rb = bth_of('test_E.pst')
print('client sig %x, cbKey %d cbEnt %d; records bak=%d rep=%d' % (ha.client, cb, ce, len(ra), len(rb)))
da, db = dict(ra), dict(rb)
print('only in rep:', [(k.hex(), v.hex()) for k, v in rb if k not in da])
print('only in bak:', [(k.hex(), v.hex()) for k, v in ra if k not in db])
print('changed:', [(k.hex(), da[k].hex(), db[k].hex()) for k in da if k in db and da[k] != db[k]][:5])
# what are the values? compare with the 0x0E30 GUIDs and folder nids
import itertools
wb = W.PSTWriter('test_E.pst'); e2 = E.Editor(wb, P.MPBB_I)
tc = e2.load_tc(0x802d)
guid = {}
for rid, cells in tc.rows:
    for ci, v in cells.items():
        if tc.cols[ci].pid == 0x0e30: guid[v] = rid
hit = [(k.hex(), v.hex(), hex(guid[k])) for k, v in rb if k in guid]
print('keys that equal a 0x0E30 value in Top-of-store hierarchy rows:', len(hit), hit[:5])
print('sample first records:', [(k.hex(), v.hex()) for k, v in rb[:4]])
