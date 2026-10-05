#!/usr/bin/env python3
"""pstcheck.py - READ-ONLY scan-style checks (a Linux stand-in for much of SCANPST).
  pstcheck.py refs   FILE   block reference counts / orphan blocks  (SCANPST: 'validate BBT refcounts')
  pstcheck.py nids   FILE   header NID high-water marks             (SCANPST: 'validate header NID high-water marks')
  pstcheck.py tables FILE   folder counts, parent links, contents-table rows vs messages (needs pstcore.py)
  pstcheck.py xblocks FILE  data-tree blocks: non-final blocks full, cbTotal consistent
  pstcheck.py subnodes FILE attachment sizes (PR_ATTACH_SIZE = sum of the sizes of the attachment's property values) and the header
                            counters of the local NIDs used inside messages            (found by repairing imported files with SCANPST)
  pstcheck.py rowcells FILE contents-table rows carry the row-only cells Outlook writes (0x0E17 message status, 0x3013 per-row key)
  pstcheck.py all    FILE
Calibration on your files: 'refs' on rmarrash_1.pst should list the orphan blocks SCANPST listed; on the repaired copy it should find none.
"""
import pio
import sys, os, struct

# --- portability (Windows has no os.pread/os.pwrite and needs O_BINARY) ---
_BIN = getattr(os, 'O_BINARY', 0)
if hasattr(os, 'pread'):
    _pread, _pwrite = os.pread, os.pwrite
else:
    def _pread(fd, n, off):
        os.lseek(fd, off, 0); return os.read(fd, n)
    def _pwrite(fd, data, off):
        os.lseek(fd, off, 0); return os.write(fd, data)
from collections import Counter

class F:
    def __init__(self, path):
        self.path = path
        self.fd = os.open(path, os.O_RDONLY | _BIN)
        h = self.rd(0, 564)
        if h[:4] != b'!BDN' or struct.unpack_from('<H', h, 10)[0] != 23:
            raise SystemExit('Unicode v23 PST required')
        self.h = h
        self.nbt_root = struct.unpack_from('<QQ', h, 216)
        self.bbt_root = struct.unpack_from('<QQ', h, 232)
        self.nbt = [struct.unpack_from('<QQQI', e, 0) for e in self.leaves(self.nbt_root)]
        self.bbt = {}
        for e in self.leaves(self.bbt_root):
            bid, ib, cb, cref = struct.unpack_from('<QQHH', e, 0)
            self.bbt[bid & ~1] = (bid, ib, cb, cref)

    def rd(self, off, n):
        return _pread(self.fd, n, off)

    def leaves(self, root):
        out = []
        def walk(ib):
            pg = self.rd(ib, 512)
            cent, cbent, lv = pg[488], pg[490], pg[491]
            for i in range(cent):
                e = pg[i * cbent:(i + 1) * cbent]
                if lv:
                    walk(struct.unpack_from('<Q', e, 16)[0])
                else:
                    out.append(e)
        walk(root[1])
        return out

    def raw(self, bid):
        e = self.bbt.get(bid & ~1)
        return None if e is None else self.rd(e[1], e[2])


def refs(f, out=print):
    refcount = Counter()
    queue = []
    probs = 0
    for nid, bd, bs, par in f.nbt:
        for b in (bd, bs):
            if b:
                refcount[b & ~1] += 1
                queue.append(b)
    expanded = set()
    while queue:
        b = queue.pop()
        k = b & ~1
        if k in expanded:
            continue
        expanded.add(k)
        if k not in f.bbt:
            out('  referenced block 0x%x is missing from the BBT' % b); probs += 1
            continue
        if not (b & 2):
            continue
        d = f.raw(b)
        bt, lv, cent = d[0], d[1], struct.unpack_from('<H', d, 2)[0]
        kids = []
        if bt == 1:
            kids = [struct.unpack_from('<Q', d, 8 + 8 * i)[0] for i in range(cent)]
        elif bt == 2 and lv == 0:
            for i in range(cent):
                _n, bd, bs = struct.unpack_from('<QQQ', d, 8 + 24 * i)
                kids += [x for x in (bd, bs) if x]
        elif bt == 2:
            kids = [struct.unpack_from('<QQ', d, 8 + 16 * i)[1] for i in range(cent)]
        for c in kids:
            refcount[c & ~1] += 1
            queue.append(c)
    orphans = sorted(k for k in f.bbt if refcount[k] == 0)
    for k in orphans:
        out("  ??Couldn't find BBT entry in the RBT (%X)" % f.bbt[k][0])
    bad = [(k, f.bbt[k][3], refcount[k] + 1) for k in f.bbt if refcount[k] and f.bbt[k][3] != refcount[k] + 1]
    for k, have, want in bad[:50]:
        out('  refcount mismatch for block 0x%x: BBT has %d, references imply %d' % (f.bbt[k][0], have, want))
    out('refs: %d blocks, %d orphans, %d refcount mismatches, %d missing blocks' % (len(f.bbt), len(orphans), len(bad), probs))
    return len(orphans) + len(bad) + probs


AMAP0, SLOTS = 0x4400, 496 * 8
SECT = SLOTS * 64


def amap(f, out=print):
    """Allocation maps: every block in the BBT must be marked allocated, and the header's cbAMapFree must equal the free space the maps show
    (SCANPST: "Block is not allocated in AMAP!", "Computed cbAMapFree of X, but header has Y")."""
    import struct as _s
    eof, _last, cbfree = _s.unpack_from('<QQQ', f.h, 184)
    nsec = (eof - AMAP0) // SECT
    maps = []
    free = 0
    for sec in range(nsec):
        pg = f.rd(AMAP0 + sec * SECT, 512)
        if len(pg) < 512 or pg[496] != 0x84:
            out('  AMap page missing or wrong type at section %d' % sec)
            return 1
        bits = int.from_bytes(pg[:496], 'big')
        maps.append(bits)
        free += (SLOTS - bin(bits).count('1')) * 64
    probs = 0
    unalloc = []
    for k, (bid, ib, cb, _cref) in f.bbt.items():
        n = (cb + 16 + 63) // 64
        k0 = (ib - AMAP0) // 64
        for slot in range(k0, k0 + n):
            sec, idx = divmod(slot, SLOTS)
            if sec >= nsec or not (maps[sec] >> (SLOTS - 1 - idx)) & 1:
                unalloc.append((bid, ib, cb)); break
    for bid, ib, cb in unalloc[:50]:
        out('  Block is not allocated in AMAP! BID 0x%X, IB 0x%X, CB 0x%X' % (bid, ib, cb)); probs += 1
    probs += max(0, len(unalloc) - 50)
    if free != cbfree:
        out('  Computed cbAMapFree of %d, but header has %d' % (free, cbfree)); probs += 1
    out('amap: %d sections, %d blocks not allocated, free %d (header %d), %d problems' % (nsec, len(unalloc), free, cbfree, probs))
    return probs


def folders(f, path, out=print):
    """Folder completeness: every folder node needs its hierarchy / contents / FAI tables (nid types 0xD / 0xE / 0xF with the folder's index)
    and a row in its parent folder's hierarchy table (SCANPST: "Adding folder (nid=...) back to the database", "Hierarchy Table ..., row doesn't match
    sub-object")."""
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import pstcore as P, pstwrite as W, pstedit as E
    nodes = {n: par for n, _bd, _bs, par in f.nbt}
    w = W.PSTWriter(path)
    ed = E.Editor(w, P.MPBB_I)
    probs = 0
    checked = 0
    rows_of = {}
    for nid, par in sorted(nodes.items()):
        if nid & 0x1F != 2 or par == nid or par & 0x1F != 2 or par not in nodes:
            continue
        checked += 1
        base = nid & ~0x1F
        missing = [t for t in (0xD, 0xE, 0xF) if (base | t) not in nodes]
        if missing:
            out('  folder 0x%x: missing table nodes of type %s' % (nid, ', '.join('0x%X' % t for t in missing))); probs += 1
        hn = (par & ~0x1F) | 0xD
        if hn not in rows_of:
            try:
                rows_of[hn] = {r for r, _c in ed.load_tc(hn).rows}
            except Exception:
                rows_of[hn] = None
        if rows_of[hn] is not None and nid not in rows_of[hn]:
            out("  folder 0x%x: not listed in the hierarchy table of its parent 0x%x" % (nid, par)); probs += 1
    out('folders: %d folders checked, %d problems' % (checked, probs))
    w.close()
    return probs


def nids(f, out=print):
    rg = struct.unpack_from('<32I', f.h, 44)
    mx = {}
    for nid, bd, bs, par in f.nbt:
        t, i = nid & 0x1F, nid >> 5
        mx[t] = max(mx.get(t, 0), i)
    probs = 0
    for t in sorted(mx):
        v = rg[t]
        hv = v        # raw index (types 6, 7, 0x10 hold NID-shaped values but are not counted); the old 'v >> 5' guess misfired when a raw index ended in the type bits
        flag = ''
        if t in (0x06, 0x07, 0x10):
            flag = '   (index derived from its folder; not counted)'
        elif mx[t] > hv:
            flag = '   <-- NBT has a higher index than the header allows'; probs += 1
        out('  type 0x%02x: highest index in NBT %d (0x%x), header says %d%s' % (t, mx[t], mx[t], hv, flag))
    out('nids: %d problems' % probs)
    return probs


def tables(path, out=print):
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import pstcore as P
    st = P.Store(path)
    pst = st.pst
    probs = 0
    listed = set()
    for folder, depth in st.walk():
        name = folder.name
        pc = folder.pc
        try:
            rows = folder.contents()
        except Exception as e:
            out('  folder %s: cannot read contents table: %r' % (name, e)); probs += 1; continue
        cnt = pc.get(0x3602) if pc else None
        unread = pc.get(0x3603) if pc else None
        real_unread = sum(1 for r in rows if not (r.get(0x0E07, 0) or 0) & 1)
        if cnt is not None and cnt != len(rows):
            out('  folder %s: content count property %s != %d table rows' % (name, cnt, len(rows))); probs += 1
        if unread is not None and unread != real_unread:
            out('  folder %s: unread property %s != %d unread rows' % (name, unread, real_unread)); probs += 1
        subs = folder.subfolders()
        flag = pc.get(0x360A) if pc else None
        if flag is not None and bool(flag) != bool(subs):
            out('  folder %s: has-subfolders property %s but %d subfolders' % (name, flag, len(subs))); probs += 1
        for sf in subs:
            e = pst.nbt.get(sf.nid)
            if e and e[2] != folder.nid:
                out('  folder %s: nidParent of subfolder %s is 0x%x, expected 0x%x' % (name, sf.name, e[2], folder.nid)); probs += 1
        for r in rows:
            nid = r['nid']
            listed.add(nid)
            e = pst.nbt.get(nid)
            if e is None:
                out('  folder %s: row 0x%x has no message node' % (name, nid)); probs += 1; continue
            if e[2] != folder.nid:
                out('  folder %s: message 0x%x has nidParent 0x%x' % (name, nid, e[2])); probs += 1
            try:
                m = P.Message(st, pst.node(nid))
            except Exception as ex:
                out('  folder %s: message 0x%x unreadable: %r' % (name, nid, ex)); probs += 1; continue
            diffs = []
            for pid, label in ((0x0037, 'subject'), (0x0E07, 'flags'), (0x0E08, 'size'), (0x0E06, 'delivery time'), (0x0C1A, 'sender')):
                if pid in r:
                    mv = m.pc.get(pid, None, m.codepage)
                    if mv != r[pid]:
                        diffs.append('%s: row=%r message=%r' % (label, r[pid], mv))
            if diffs:
                out('  folder %s: row 0x%x differs from its message:' % (name, nid))
                for d in diffs:
                    out('       ' + d[:150])
                probs += 1
    msgs = [nid for nid in pst.nbt if nid & 0x1F == 4]
    lone = [n for n in msgs if n not in listed]
    out('tables: %d messages listed in contents tables, %d message nodes not listed, %d problems' % (len(listed), len(lone), probs))
    return probs


def idmap(path, out=print):
    """Node 0xC01 (16-byte ID -> NID): every row ID maps to its object; no live record points at a missing node;
    records of deleted objects must have NID 0 (SCANPST zeroes them)."""
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import pstcore as P, pstwrite as W, pstedit as E, pstidmap as M
    w = W.PSTWriter(path)
    try:
        ed = E.Editor(w, P.MPBB_I)
        m = M.IdMap(ed)
        probs = 0
        if not m.present:
            out('idmap: no node 0xC01 in this file'); return 0
        st = P.Store(path)
        rows = noid = 0
        for folder, depth in st.walk():
            base = folder.nid & ~0x1F
            for t in (0x0D, 0x0E, 0x0F):
                if not ed.entry(base | t):
                    continue
                tc = ed.load_tc(base | t)
                ci = tc.col_by_pid.get(0x0E30)
                if ci is None:
                    continue
                for rid, cells in tc.rows:
                    rows += 1
                    g = cells.get(ci)
                    if not g:
                        noid += 1                                    # original rows of some files never had IDs
                    elif m.recs.get(g) != rid:
                        out('  %s table 0x%x row 0x%x: ID %s maps to %s' % (folder.name, base | t, rid, g.hex(), m.recs.get(g))); probs += 1
        stale = [(k, n) for k, n in m.recs.items() if n and ed.entry(n) is None]
        for k, n in stale[:10]:
            out('  record %s points to missing node 0x%x (should be 0)' % (k.hex(), n))
        probs += len(stale)
        out('idmap: %d records (%d live), %d rows checked (%d without an ID, informational), %d problems' % (len(m.recs), sum(1 for n in m.recs.values() if n), rows, noid, probs))
        return probs
    finally:
        w.close()


def xblocks(f, out=print):
    """SCANPST rule: in a data tree every non-final block must be full (8176 B) and cbTotal must equal the sum of the
    children (found when a 1,300-row contents table was written with unpadded row blocks)."""
    probs, n = [], 0
    for key, (bid, ib, cb, cref) in f.bbt.items():
        if not bid & 2:
            continue
        r = f.rd(ib, cb)
        if len(r) < 8 or r[0] != 1:
            continue
        lvl, cent, tot = r[1], struct.unpack_from('<H', r, 2)[0], struct.unpack_from('<I', r, 4)[0]
        if lvl not in (1, 2) or 8 + 8 * cent > len(r):
            continue
        n += 1
        ids = struct.unpack_from('<%dQ' % cent, r, 8)
        kids = [f.bbt.get(i & ~1) for i in ids]
        if any(k is None for k in kids):
            probs.append('XBLOCK 0x%x lists a block that is not in the BBT' % bid); continue
        if lvl == 1:
            sizes = [k[2] for k in kids]
            if tot != sum(sizes):
                probs.append('XBLOCK 0x%x: cbTotal %d != sum of children %d' % (bid, tot, sum(sizes)))
            if any(sz != 8176 for sz in sizes[:-1]):
                probs.append('XBLOCK 0x%x: a non-final block is not full (%s)' % (bid, sorted(set(sizes[:-1]))[:3]))
        else:
            sub = 0
            for k in kids:
                rr = f.rd(k[1], k[2]); sub += struct.unpack_from('<I', rr, 4)[0]
            if tot != sub:
                probs.append('XXBLOCK 0x%x: cbTotal %d != sum of children %d' % (bid, tot, sub))
    out('xblocks: %d data-tree blocks checked, %d problems' % (n, len(probs)))
    for p in probs[:10]:
        out('  ' + p)


def rowvers(path, out=print):
    """SCANPST rule found with copies: every PidTagLtpRowVer (0x67F3) in all hierarchy/contents tables must be unique
    store-wide, and below the header's dwUnique."""
    import pstwrite as W, pstedit as E, pstcore as P
    w = W.PSTWriter(path)
    try:
        ed = E.Editor(w, P.MPBB_I)
        seen, dups, tot = {}, [], 0
        for v in w.nbt.items():
            nid = struct.unpack_from('<Q', v, 0)[0] & 0xFFFFFFFF
            if nid & 31 not in (0xD, 0xE):
                continue
            try:
                tc = ed.load_tc(nid)
            except Exception:
                continue
            i = tc.col_by_pid.get(0x67F3)
            if i is None:
                continue
            for rid, cells in tc.rows:
                b = cells.get(i)
                if b:
                    ver = struct.unpack('<I', b[:4])[0]; tot += 1
                    if ver in seen:
                        dups.append('row version 0x%x used by 0x%x/0x%x and 0x%x/0x%x' % ((ver,) + seen[ver] + (nid, rid)))
                    seen[ver] = (nid, rid)
        probs = list(dups)
        if seen and max(seen) >= w.unique:
            probs.append('highest row version 0x%x >= dwUnique 0x%x' % (max(seen), w.unique))
    finally:
        w.close()
    out('rowvers: %d rows with a version, %d problems' % (tot, len(probs)))
    for p in probs[:10]:
        out('  ' + p)


_FIXED = {0x0002: 2, 0x0003: 4, 0x0004: 4, 0x000A: 4, 0x000B: 1}      # value stored inline in the property record: its own size counts


def subnodes(path, out=print):
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import pstcore as P
    pst = P.PST(path)
    rg = struct.unpack_from('<32I', open(path, 'rb').read(564), 44)
    mx, probs, atts, msgs, skipped = {}, 0, 0, 0, 0
    try:
        for nid in sorted(pst.nbt):
            if nid & 0x1F != 4:
                continue
            node = pst.node(nid)
            if not node or not node.bsub:
                continue
            try:
                subs = node.subs()
            except Exception:
                skipped += 1
                continue
            msgs += 1
            for snid, (bd, bs) in subs.items():
                t, i = snid & 0x1F, snid >> 5
                mx[t] = max(mx.get(t, 0), i)
                if t != 5:
                    continue
                try:
                    a = P.Node(pst, snid, bd, bs)
                    heap = P.Heap(a)
                    if heap.client_sig != 0xBC:
                        continue
                    total, stored, method = 0, None, None
                    for k, v in heap.bth(heap.user_root):
                        pid, ptype = struct.unpack('<HH', k + v[:2])
                        hnid = struct.unpack_from('<I', v, 2)[0]
                        if ptype in _FIXED:
                            total += _FIXED[ptype]
                            if pid == 0x0E20: stored = struct.unpack('<i', v[2:6])[0]
                            if pid == 0x3705: method = struct.unpack('<i', v[2:6])[0]
                        elif hnid:
                            total += len(heap.resolve(hnid))
                except Exception:
                    skipped += 1
                    continue
                if method != 1:        # embedded messages / OLE objects carry storage that is not a property value
                    continue
                atts += 1
                if stored is None:
                    out('  attachment 0x%x of message 0x%x: PR_ATTACH_SIZE is missing' % (snid, nid)); probs += 1
                elif stored != total:
                    out('  attachment 0x%x of message 0x%x: PR_ATTACH_SIZE %d != %d (the sum of its property value sizes)' % (snid, nid, stored, total)); probs += 1
    finally:
        pst.close()
    for t in (5, 0x1F):                # local attachment / LTP nids come from the header counters; the table nids (0x671, 0x692) are fixed
        if mx.get(t, 0) > rg[t]:
            out('  local nids of type 0x%02x: highest index in message subnodes %d (0x%x), header says %d   <-- below the highest NID in use' % (t, mx[t], mx[t], rg[t])); probs += 1
    out('subnodes: %d messages with subnodes, %d attachments, %d problems' % (msgs, atts, probs))
    return probs


def rowcells(path, out=print):
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import pstcore as P
    pst = P.PST(path)
    probs = rows = 0
    try:
        for nid in sorted(pst.nbt):
            if nid & 0x1F != 0x0E:
                continue
            tc = P.TC(pst.node(nid))
            cols = {c[0] for c in tc.cols}
            want = [pid for pid in (0x0E17, 0x3013) if pid in cols]
            table_rows = tc.rows()
            rows += len(table_rows)
            miss = sum(1 for _rid, cells in table_rows if any(pid not in cells for pid in want))
            if miss:
                out('  contents table 0x%x: %d of %d rows lack the row cells 0x0E17 / 0x3013 that Outlook writes (SCANPST adds them)' % (nid, miss, len(table_rows))); probs += 1
    finally:
        pst.close()
    out('rowcells: %d message rows, %d problems' % (rows, probs))
    return probs


if __name__ == '__main__':
    if len(sys.argv) < 3:
        print(__doc__); sys.exit(2)
    cmd, path = sys.argv[1], sys.argv[2]
    f = F(path)
    if cmd in ('refs', 'all'): refs(f)
    if cmd in ('amap', 'all'): amap(f)
    if cmd in ('folders', 'all'): folders(f, path)
    if cmd in ('nids', 'all'): nids(f)
    if cmd in ('tables', 'all'): tables(path)
    if cmd in ('idmap', 'all'): idmap(path)
    if cmd in ('xblocks', 'all'): xblocks(f)
    if cmd in ('rowvers', 'all'): rowvers(path)
    if cmd in ('subnodes', 'all'): subnodes(path)
    if cmd in ('rowcells', 'all'): rowcells(path)
