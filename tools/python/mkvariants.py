"""Bisect helper: Z0 = our folder+copy (same NIDs as Outlook's test_P1); Zx = Z0 with some nodes transplanted from test_P1."""
import shutil, os, struct, sys
import pstactions as A, pstops as O, pstwrite as W, pstedit as E, pstcore as P
SRC = 'rmarrash_2.pst'
def fresh(name):
    shutil.copyfile(SRC, name)
    if os.path.exists(name + '.journal'): os.remove(name + '.journal')
fresh('test_Z0.pst')
s = A.Session('test_Z0.pst'); s.folder_info(); n = s.create_folder(s.ipm_root, 'OutlookDest'); n = getattr(n, 'nid', n)
s = A.Session('test_Z0.pst'); s.folder_info(); print(s.copy_messages([0x2000e4], n))

def import_node(dst_ops, src_ed, nid):
    bd, bs, par = src_ed.entry(nid)
    ed, w = dst_ops.ed, dst_ops.w
    nbd, nbs = clone_from(ed, src_ed, bd, bs)
    old = ed.entry(nid)
    if old:
        w.nbt.put(nid, struct.pack('<QQQII', nid, nbd, nbs, par, 0)); w.release(old[0])
        if old[1]: w.release(old[1])
    else:
        w.nbt.put(nid, struct.pack('<QQQII', nid, nbd, nbs, par, 0))

def clone_from(ed, src_ed, bd, bs):
    blocks = src_ed.leaf_blocks(bd)
    nbd = ed.put_blocks([bytes(b) for b in blocks])
    nbs = 0
    if bs:
        new = {k: clone_from(ed, src_ed, a, b) for k, (a, b) in src_ed.subnodes(bs).items()}
        body = struct.pack('<BBHI', 2, 0, len(new), 0)
        for k in sorted(new): body += struct.pack('<QQQ', k, *new[k])
        nbs = ed.w.add_block(body, internal=True)
    return nbd, nbs

VARS = {'ZA': [0x61], 'ZB': [0xee1, 0xf01], 'ZD': [0x200164, 0x20018e], 'ZE': [0x61, 0xee1, 0xf01, 0x200164, 0x20018e, 0x802d, 0x200182],
        'ZF': [0x61, 0xee1, 0xf01, 0x200164, 0x20018e, 0x802d, 0x200182, 0x261, 0xc01, 0x80ad, 0x80027]}
for name, nodes in VARS.items():
    fresh('test_%s.pst' % name)
    shutil.copyfile('test_Z0.pst', 'test_%s.pst' % name)
    if os.path.exists('test_%s.pst.journal' % name): os.remove('test_%s.pst.journal' % name)
    src = W.PSTWriter('test_P1.pst'); sed = E.Editor(src, P.MPBB_I)
    ops = O.Ops('test_%s.pst' % name)
    try:
        for nid in nodes: import_node(ops, sed, nid)
        ops.w.unique = max(ops.w.unique, 0x24ff4)
        ops.commit()
    finally:
        ops.close(); src.close()
    print(name, nodes)
