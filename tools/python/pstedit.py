"""pstedit.py - stage B: heap-on-node, BTree-on-heap and table-context (TC) builders, plus node-level edits.

Layers in this file
  Heap / HeapBuilder   read and build heap-on-node data (HNHDR/HNPAGEHDR/HNBITMAPHDR, page map, fill levels)
  build_bth            build a BTree-on-heap (multi-level when needed)
  TC                   parse a table context into columns + rows, and build it back
  Editor               load / store a node's TC through a PSTWriter transaction (ref-counted, journaled)
No third-party PST code. Imports of pstcore/pstwrite happen only in the command-line section.
"""
import os, struct, sys

MAXALLOC = 3580
BLOCKMAX = 8176
FIXED = {0x0002: 2, 0x0003: 4, 0x0004: 4, 0x0005: 8, 0x0006: 8, 0x0007: 8,
         0x000A: 4, 0x000B: 1, 0x0014: 8, 0x0040: 8}
ROWS_NID_DEFAULT = 0x3F


class EditError(Exception):
    pass


# ------------------------------------------------------------------ heap reader
class Heap:
    def __init__(self, blocks):
        self.blocks = [bytes(b) for b in blocks]
        if not self.blocks or len(self.blocks[0]) < 12 or self.blocks[0][2] != 0xEC:
            raise EditError('not a heap-on-node')
        self.client = self.blocks[0][3]
        self.root = struct.unpack_from('<I', self.blocks[0], 4)[0]

    def loc(self, hid):
        idx, blk = (hid >> 5) & 0x7FF, hid >> 16
        if hid & 0x1F or idx == 0 or blk >= len(self.blocks):
            raise EditError('bad HID 0x%x' % hid)
        b = self.blocks[blk]
        pm = struct.unpack_from('<H', b, 0)[0]
        if idx > struct.unpack_from('<H', b, pm)[0]:
            raise EditError('HID 0x%x out of range' % hid)
        s, e = struct.unpack_from('<HH', b, pm + 4 + 2 * (idx - 1))
        return blk, s, e

    def get(self, hid):
        blk, s, e = self.loc(hid)
        return self.blocks[blk][s:e]

    def bth(self, hid):
        h = self.get(hid)
        if h[0] != 0xB5:
            raise EditError('bad BTH header')
        cbk, cbe, lv = h[1], h[2], h[3]
        root = struct.unpack_from('<I', h, 4)[0]
        out = []

        def walk(x, level):
            d = self.get(x)
            if level == 0:
                sz = cbk + cbe
                for i in range(len(d) // sz):
                    out.append((d[i * sz:i * sz + cbk], d[i * sz + cbk:(i + 1) * sz]))
            else:
                sz = cbk + 4
                for i in range(len(d) // sz):
                    walk(struct.unpack_from('<I', d, i * sz + cbk)[0], level - 1)
        if root:
            walk(root, lv)
        return cbk, cbe, out

    def check(self):
        """Structural self-check; returns list of problems."""
        probs = []
        for bi, b in enumerate(self.blocks):
            if len(b) > BLOCKMAX:
                probs.append('block %d too large (%d)' % (bi, len(b)))
            pm = struct.unpack_from('<H', b, 0)[0]
            ca, cf = struct.unpack_from('<HH', b, pm)
            offs = struct.unpack_from('<%dH' % (ca + 1), b, pm + 4)
            if offs[-1] > pm or any(offs[i] > offs[i + 1] for i in range(ca)):
                probs.append('block %d page map inconsistent' % bi)
            if pm & 1:
                probs.append('block %d page map not 2-byte aligned' % bi)
            if pm + 4 + 2 * (ca + 1) > len(b):
                probs.append('block %d page map runs past the end of the block' % bi)
        return probs


# ------------------------------------------------------------------ heap builder
def _fill_level(free):
    for lim, v in ((3584, 0), (2560, 1), (2048, 2), (1792, 3), (1536, 4), (1280, 5), (1024, 6), (768, 7),
                   (512, 8), (256, 9), (128, 10), (64, 11), (32, 12), (16, 13), (8, 14)):
        if free >= lim:
            return v
    return 15


class HeapBuilder:
    def __init__(self, client_sig):
        self.client = client_sig
        self.blocks = [{'items': [], 'used': 0}]

    @staticmethod
    def _hdr(bi):
        if bi == 0: return 12
        if bi >= 8 and (bi - 8) % 128 == 0: return 66
        return 2

    def _fits(self, blk, bi, n):
        end = self._hdr(bi) + blk['used'] + n
        end += end & 1
        return end + 4 + 2 * (len(blk['items']) + 2) <= BLOCKMAX

    def alloc(self, data):
        if isinstance(data, int):
            data = bytearray(data)
        else:
            data = bytearray(data)
        n = len(data)
        if n == 0 or n > MAXALLOC:
            raise EditError('heap allocation of %d bytes not allowed' % n)
        bi = len(self.blocks) - 1
        if not self._fits(self.blocks[bi], bi, n):
            self.blocks.append({'items': [], 'used': 0})
            bi += 1
            if bi > 0xFFFF or not self._fits(self.blocks[bi], bi, n):
                raise EditError('heap too large')
        blk = self.blocks[bi]
        if len(blk['items']) >= 2046:
            self.blocks.append({'items': [], 'used': 0}); bi += 1; blk = self.blocks[bi]
        blk['items'].append(data)
        blk['used'] += n
        return (bi << 16) | (len(blk['items']) << 5)

    def set(self, hid, data):
        item = self.blocks[hid >> 16]['items'][((hid >> 5) & 0x7FF) - 1]
        if len(item) != len(data):
            raise EditError('size changed for placeholder')
        item[:] = data

    def finalize(self, user_root):
        out, fills = [], []
        for bi, blk in enumerate(self.blocks):
            hdr = self._hdr(bi)
            body = bytearray(hdr)
            offs = [hdr]
            for it in blk['items']:
                body += it
                offs.append(len(body))
            if len(body) & 1:
                body += b'\0'
            pm = len(body)
            body += struct.pack('<HH', len(blk['items']), 0) + b''.join(struct.pack('<H', o) for o in offs)
            fills.append(_fill_level(BLOCKMAX - len(body)))
            struct.pack_into('<H', body, 0, pm)
            if bi < len(self.blocks) - 1:
                body += bytes(BLOCKMAX - len(body))
            out.append(body)
        b0 = out[0]
        b0[2] = 0xEC
        b0[3] = self.client
        struct.pack_into('<I', b0, 4, user_root)
        fl = 0
        for i in range(min(8, len(fills))):
            fl |= fills[i] << (4 * i)
        struct.pack_into('<I', b0, 8, fl)
        for bi in range(8, len(out), 128):
            for pos in range(128):
                j = bi + pos
                if j >= len(out): break
                v = fills[j]
                out[bi][2 + (pos >> 1)] |= v << (4 * (pos & 1))
        return [bytes(b) for b in out]


def build_bth(hb, cbkey, cbent, records, hdr_hid=None):
    """records: list of (key_bytes, value_bytes) already sorted. Returns the BTHHEADER hid."""
    hh = hb.alloc(8) if hdr_hid is None else hdr_hid
    root, levels = 0, 0
    if records:
        per = max(1, int(MAXALLOC * 0.9) // (cbkey + cbent))
        level = [(records[i][0], hb.alloc(b''.join(k + v for k, v in records[i:i + per])))
                 for i in range(0, len(records), per)]
        perI = max(1, int(MAXALLOC * 0.9) // (cbkey + 4))
        while len(level) > 1:
            level = [(level[i][0], hb.alloc(b''.join(k + struct.pack('<I', h) for k, h in level[i:i + perI])))
                     for i in range(0, len(level), perI)]
            levels += 1
        root = level[0][1]
    hb.set(hh, struct.pack('<BBBBI', 0xB5, cbkey, cbent, levels, root))
    return hh


# ------------------------------------------------------------------ table context
class Col:
    __slots__ = ('pid', 'ptype', 'ibd', 'cbd', 'ibit')

    def __init__(self, pid, ptype, ibd, cbd, ibit):
        self.pid, self.ptype, self.ibd, self.cbd, self.ibit = pid, ptype, ibd, cbd, ibit

    @property
    def fixed(self):
        return self.ptype in FIXED


class TC:
    def __init__(self, cols, rgib):
        self.cols = list(cols)
        self.rgib = tuple(rgib)
        self.rows = []              # list of (rowid, {col_index: bytes})
        self.rows_nid = 0           # subnode NID that held the row matrix (0 = heap)
        self.col_by_pid = {c.pid: i for i, c in enumerate(self.cols)}

    @property
    def rowsize(self):
        return self.rgib[3]

    @staticmethod
    def parse(blocks, sub_data):
        """blocks: decoded data blocks of the TC node. sub_data(nid) -> bytes of that subnode's data stream."""
        hp = Heap(blocks)
        if hp.client != 0x7C:
            raise EditError('not a table context')
        info = hp.get(hp.root)
        ccols = info[1]
        rgib = struct.unpack_from('<4H', info, 2)
        hid_ri, hnid_rows = struct.unpack_from('<II', info, 10)
        cols = []
        for i in range(ccols):
            tag, ibd, cbd, ibit = struct.unpack_from('<IHBB', info, 22 + 8 * i)
            cols.append(Col(tag >> 16, tag & 0xFFFF, ibd, cbd, ibit))
        tc = TC(cols, rgib)
        if hnid_rows == 0:
            return tc
        rs = rgib[3]
        index = sorted((struct.unpack('<I', v)[0], struct.unpack('<I', k)[0]) for k, v in hp.bth(hid_ri)[2])
        if hnid_rows & 0x1F == 0:
            matrix = hp.get(hnid_rows)
            def row_at(i): return matrix[i * rs:(i + 1) * rs]
        else:
            tc.rows_nid = hnid_rows
            data = sub_data(hnid_rows)
            raise_if = None
            rpb = BLOCKMAX // rs
            # sub_data returns the concatenation; recover per-block layout from its fixed stride rule
            def row_at(i):
                raise EditError('internal: use parse_blocks')
        return tc._load_rows(hp, index, hnid_rows, sub_data, rs)

    def _load_rows(self, hp, index, hnid_rows, sub_data, rs):
        if hnid_rows & 0x1F == 0:
            matrix = hp.get(hnid_rows)
            row_at = lambda i: matrix[i * rs:(i + 1) * rs]
        else:
            self.rows_nid = hnid_rows
            blocks = sub_data(hnid_rows, as_blocks=True)
            rpb = BLOCKMAX // rs
            def row_at(i):
                bi, ri = divmod(i, rpb)
                if bi >= len(blocks): return b''
                return blocks[bi][ri * rs:(ri + 1) * rs]
        for idx, rowid in index:
            row = row_at(idx)
            if len(row) < rs:
                raise EditError('row %d missing from row matrix' % idx)
            ceb = row[self.rgib[2]:self.rgib[3]]
            cells = {}
            for ci, c in enumerate(self.cols):
                if not ceb[c.ibit >> 3] & (0x80 >> (c.ibit & 7)):
                    continue
                if c.fixed:
                    cells[ci] = bytes(row[c.ibd:c.ibd + c.cbd])
                else:
                    h = struct.unpack_from('<I', row, c.ibd)[0]
                    if h == 0:
                        cells[ci] = b''
                    elif h & 0x1F == 0:
                        cells[ci] = bytes(hp.get(h))
                    else:
                        cells[ci] = bytes(sub_data(h))
            self.rows.append((rowid, cells))
        return self

    def build(self, rows_nid=None):
        """Returns (heap_blocks, row_blocks_or_None, rows_nid_used)."""
        rs, rgib = self.rowsize, self.rgib
        hb = HeapBuilder(0x7C)
        h_ri = hb.alloc(8)
        h_info = hb.alloc(22 + 8 * len(self.cols))
        rowbufs = []
        for rowid, cells in self.rows:
            row = bytearray(rs)
            ceb = bytearray(rgib[3] - rgib[2])
            for ci, c in enumerate(self.cols):
                if ci not in cells:
                    continue
                v = cells[ci]
                ceb[c.ibit >> 3] |= 0x80 >> (c.ibit & 7)
                if c.fixed:
                    row[c.ibd:c.ibd + c.cbd] = v[:c.cbd].ljust(c.cbd, b'\0')
                elif v:
                    struct.pack_into('<I', row, c.ibd, hb.alloc(v))
            row[rgib[2]:rgib[3]] = ceb
            rowbufs.append(bytes(row))
        total = sum(len(r) for r in rowbufs)
        hnid_rows, row_blocks = 0, None
        if rowbufs:
            if total <= MAXALLOC:
                hnid_rows = hb.alloc(b''.join(rowbufs))
            else:
                hnid_rows = rows_nid or self.rows_nid or ROWS_NID_DEFAULT
                rpb = BLOCKMAX // rs
                row_blocks = [b''.join(rowbufs[i:i + rpb]) for i in range(0, len(rowbufs), rpb)]
                # Outlook pads every non-final row block to a full block (8176 B); SCANPST checks the XBLOCK cbTotal against that
                row_blocks = [blk.ljust(BLOCKMAX, b'\0') for blk in row_blocks[:-1]] + row_blocks[-1:]
        recs = sorted(((struct.pack('<I', rid), struct.pack('<I', i)) for i, (rid, _c) in enumerate(self.rows)),
                      key=lambda kv: struct.unpack('<I', kv[0])[0])
        if len(set(r[0] for r in recs)) != len(recs):
            raise EditError('duplicate row ids')
        build_bth(hb, 4, 4, recs, hdr_hid=h_ri)
        info = struct.pack('<BB4HIII', 0x7C, len(self.cols), *rgib, h_ri, hnid_rows, 0)
        info += b''.join(struct.pack('<IHBB', (c.pid << 16) | c.ptype, c.ibd, c.cbd, c.ibit) for c in self.cols)
        hb.set(h_info, info)
        return hb.finalize(h_info), row_blocks, hnid_rows if row_blocks else 0

    # convenience
    def find(self, rowid):
        for i, (rid, _c) in enumerate(self.rows):
            if rid == rowid: return i
        return -1

    def remove(self, rowid):
        i = self.find(rowid)
        if i < 0: raise EditError('row 0x%x not in table' % rowid)
        del self.rows[i]

    def values(self, rowid):
        """pid -> raw bytes for a row"""
        i = self.find(rowid)
        if i < 0: raise EditError('row 0x%x not in table' % rowid)
        return {self.cols[ci].pid: v for ci, v in self.rows[i][1].items()}

    def add_by_pid(self, rowid, pidvals):
        if self.find(rowid) >= 0: raise EditError('row 0x%x already exists' % rowid)
        cells = {}
        for pid, v in pidvals.items():
            ci = self.col_by_pid.get(pid)
            if ci is not None:
                cells[ci] = v
        self.rows.append((rowid, cells))


# ------------------------------------------------------------------ node-level editor
class Editor:
    """Works through a pstwrite.PSTWriter (so every change is journaled and ref-counted)."""
    def __init__(self, w, mpbb_i):
        self.w = w
        self.mpbb_i = bytes(mpbb_i)

    def entry(self, nid):
        e = self.w.nbt.get(nid)
        if e is None:
            return None
        n, bd, bs, par, _p = struct.unpack('<QQQII', e)
        return bd, bs, par

    def leaf_blocks(self, bid):
        if not (bid & 2):
            raw = bytes(self.w.read_block_raw(bid))
            return [raw.translate(self.mpbb_i) if self.w.crypt == 1 else raw]
        d = self.w.read_block_raw(bid)
        if d[0] != 1:
            raise EditError('expected XBLOCK')
        out = []
        for i in range(struct.unpack_from('<H', d, 2)[0]):
            out += self.leaf_blocks(struct.unpack_from('<Q', d, 8 + 8 * i)[0])
        return out

    def subnodes(self, bs):
        if not bs:
            return {}
        d = self.w.read_block_raw(bs)
        if d[0] != 2 or d[1] != 0:
            raise EditError('subnode tree of this node is not a single SLBLOCK (not supported yet)')
        return {struct.unpack_from('<Q', d, 8 + 24 * i)[0] & 0xFFFFFFFF:
                struct.unpack_from('<QQ', d, 16 + 24 * i) for i in range(struct.unpack_from('<H', d, 2)[0])}

    def clone_node(self, bd, bs, patch=None):
        """Independent copy of a node's blocks (data tree and subnode tree, recursively); returns (new_bd, new_bs).
        Outlook never lets two nodes share blocks, and SCANPST quietly flags files where a copied message does."""
        w = self.w
        blocks = [bytearray(x) for x in self.leaf_blocks(bd)]
        if patch:
            patch(blocks)
        new_bd = self.put_blocks([bytes(x) for x in blocks])
        new_bs = 0
        if bs:
            new = {n: self.clone_node(sd, ss) for n, (sd, ss) in self.subnodes(bs).items()}
            body = struct.pack('<BBHI', 2, 0, len(new), 0)
            for n in sorted(new):
                body += struct.pack('<QQQ', n, *new[n])
            new_bs = w.add_block(body, internal=True)
        return new_bd, new_bs

    def put_blocks(self, blocks):
        """Store a list of data blocks (boundaries preserved) as a data tree; returns top bid."""
        w = self.w
        if len(blocks) == 1:
            return w.add_block(blocks[0])
        ids = [w.add_block(b) for b in blocks]
        if len(ids) <= 1021:
            return w._xblock(1, ids, sum(len(b) for b in blocks))
        subs = []
        for i in range(0, len(ids), 1021):
            part = blocks[i:i + 1021]
            subs.append(w._xblock(1, ids[i:i + 1021], sum(len(b) for b in part)))
        return w._xblock(2, subs, sum(len(b) for b in blocks))

    def load_tc(self, nid):
        e = self.entry(nid)
        if e is None:
            raise EditError('node 0x%x not found' % nid)
        bd, bs, par = e
        subs = self.subnodes(bs)

        def sub_data(n, as_blocks=False):
            if n not in subs:
                raise EditError('subnode 0x%x missing' % n)
            bl = self.leaf_blocks(subs[n][0])
            return bl if as_blocks else b''.join(bl)
        return TC.parse(self.leaf_blocks(bd), sub_data)

    def store_tc(self, nid, tc):
        w = self.w
        bd, bs, par = self.entry(nid)
        subs = self.subnodes(bs)
        heap_blocks, row_blocks, rows_nid = tc.build()
        new_subs = {n: v for n, v in subs.items() if n != tc.rows_nid}
        new_bd = self.put_blocks(heap_blocks)
        if row_blocks:
            new_subs[rows_nid] = (self.put_blocks(row_blocks), 0)
        for n, (sd, ss) in new_subs.items():
            if n in subs and subs[n] == (sd, ss):
                w.add_ref(sd)
                if ss: w.add_ref(ss)
        new_bs = 0
        if new_subs:
            body = struct.pack('<BBHI', 2, 0, len(new_subs), 0)
            for n in sorted(new_subs):
                body += struct.pack('<QQQ', n, *new_subs[n])
            new_bs = w.add_block(body, internal=True)
        w.nbt.put(nid, struct.pack('<QQQII', nid, new_bd, new_bs, par, 0))
        w.release(bd)
        if bs:
            w.release(bs)
        tc.rows_nid = rows_nid


# ------------------------------------------------------------------ command line
def _tables_of(path):
    import pstcore as P
    st = P.Store(path)
    out = []
    for folder, depth in st.walk():
        base = folder.nid & ~0x1F
        for t in (0x0D, 0x0E, 0x0F):
            if st.pst.node(base | t):
                out.append((folder.name, base | t))
    return out


def dump(path, outfile):
    from pstwrite import PSTWriter
    import pstcore as P
    w = PSTWriter(path)
    ed = Editor(w, P.MPBB_I)
    with open(outfile, 'w') as f:
        for name, nid in _tables_of(path):
            tc = ed.load_tc(nid)
            f.write('== %s table 0x%x cols=%d rowsize=%d rows=%d rows_nid=%#x\n' % (name, nid, len(tc.cols), tc.rowsize, len(tc.rows), tc.rows_nid))
            for rid, cells in sorted(tc.rows):
                f.write('  %#x ' % rid + ' '.join('%04x:%s' % (tc.cols[ci].pid, v.hex()) for ci, v in sorted(cells.items())) + '\n')
    w.close()
    print('dumped to', outfile)


def rebuild(path, which):
    from pstwrite import PSTWriter
    import pstcore as P
    tabs = _tables_of(path)
    if which != 'all':
        tabs = [(n, x) for n, x in tabs if n.lower() == which.lower()]
    w = PSTWriter(path)
    ed = Editor(w, P.MPBB_I)
    for name, nid in tabs:
        tc = ed.load_tc(nid)
        ed.store_tc(nid, tc)
        problems = Heap(tc.build()[0]).check()
        print('rebuilt %-22s table 0x%-8x rows=%-5d %s' % (name, nid, len(tc.rows), problems or 'ok'))
    w.commit()
    p = w.validate()
    print('validate after commit: %d problems' % len(p), p[:5])
    w.close()


if __name__ == '__main__':
    a = sys.argv[1:]
    if len(a) >= 3 and a[0] == 'dump':
        dump(a[1], a[2])
    elif len(a) >= 3 and a[0] == 'rebuild':
        rebuild(a[1], a[2])
    else:
        print('usage: pstedit.py dump FILE OUT.txt | pstedit.py rebuild FILE (all|FOLDERNAME)')
