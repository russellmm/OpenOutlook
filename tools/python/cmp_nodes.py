import sys, struct, pstcore as P, pstwrite as W, pstedit as E
def load(path):
    w = W.PSTWriter(path); return w, E.Editor(w, P.MPBB_I)
wa, ea = load('test_E.bak'); wb, eb = load('test_E.pst')
for nid in (0x802d, 0x806d, 0x828d):
    ta, tb = ea.load_tc(nid), eb.load_tc(nid)
    print('== table 0x%x  rows bak=%d rep=%d  rowsize %d/%d cols %d/%d' % (nid, len(ta.rows), len(tb.rows), ta.rowsize, tb.rowsize, len(ta.cols), len(tb.cols)))
    ra = {r: {ta.cols[c].pid: v for c, v in cells.items()} for r, cells in ta.rows}
    rb = {r: {tb.cols[c].pid: v for c, v in cells.items()} for r, cells in tb.rows}
    for r in sorted(set(ra) | set(rb)):
        if ra.get(r) != rb.get(r):
            print(' row %#x differs:' % r)
            for pid in sorted(set(ra.get(r, {})) | set(rb.get(r, {}))):
                x, y = ra.get(r, {}).get(pid), rb.get(r, {}).get(pid)
                if x != y: print('   pid %04x  bak=%s  rep=%s' % (pid, x.hex() if x is not None else None, y.hex() if y is not None else None))
    print(' column layout same:', [(c.pid,c.ibd,c.ibit) for c in ta.cols] == [(c.pid,c.ibd,c.ibit) for c in tb.cols], '; rgib', ta.rgib, tb.rgib)
for nid in (0xc01,):
    for nm, ed in (('bak', ea), ('rep', eb)):
        bd, bs, par = ed.entry(nid)
        bl = ed.leaf_blocks(bd)
        print('node 0x%x %s: bd=%x bs=%x len=%s first bytes %s' % (nid, nm, bd, bs, [len(x) for x in bl], bl[0][:40].hex()))
    a_, b_ = ea.leaf_blocks(ea.entry(nid)[0]), eb.leaf_blocks(eb.entry(nid)[0])
    if a_ != b_:
        x, y = b''.join(a_), b''.join(b_)
        print(' data differs: len', len(x), len(y))
        d = [i for i in range(min(len(x), len(y))) if x[i] != y[i]]
        print(' first diffs at', d[:20], 'count', len(d))
        for i in d[:6]: print('   @%d %02x -> %02x' % (i, x[i], y[i]))
