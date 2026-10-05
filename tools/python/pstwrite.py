"""pstwrite.py - incremental in-place writer for Unicode PST files (stage A: storage layer).

Design (see OpenOutlook design doc):
  * every change is a transaction; pending writes are buffered in memory
  * before the first byte of the file is touched, the ORIGINAL bytes of every region that will be
    overwritten are saved to  <file>.journal  (fsync'd).  A leftover journal is rolled back on open.
  * commit order: journal -> header fAMapValid=0 -> data/pages/AMaps -> final header -> fsync -> drop journal
  * B-tree pages are updated in place (the journal provides atomicity); pages are split when full.
  * file growth (new AMap sections) is NOT implemented yet: raises NoSpace.
"""
import pio
import os, re, struct

# --- portability (Windows has no os.pread/os.pwrite and needs O_BINARY) ---
_BIN = getattr(os, 'O_BINARY', 0)
if hasattr(os, 'pread'):
    _pread, _pwrite = os.pread, os.pwrite
else:
    def _pread(fd, n, off):
        os.lseek(fd, off, 0); return os.read(fd, n)
    def _pwrite(fd, data, off):
        os.lseek(fd, off, 0); return os.write(fd, data)
from pstcore import pst_crc, MPBB_I

AMAP0 = 0x4400
SECT = 253952
SLOTS = 3968
GROW_SECTIONS = 16            # sections appended when the file is full (about 4 MB)
MAX_SECTIONS = 65536          # about 16 GB; the format itself goes further
FPMAP_FIRST = 8192            # first section holding an FPMap page (the header FPMap covers the first 2 GB)
FPMAP_EVERY = 31744
DLIST_IB = 0x4200
MPBB_R = bytes(sorted(range(256), key=lambda x: MPBB_I[x]))   # inverse permutation (encode)


class NoSpace(Exception):
    pass


class PSTCorrupt(Exception):
    pass


def sig(ib, bid):
    x = (ib ^ bid) & 0xFFFFFFFF
    return ((x >> 16) ^ x) & 0xFFFF


def page_bytes(body496, ptype, ib, bid, signed=True):
    assert len(body496) == 496
    s = sig(ib, bid) if signed else 0
    return body496 + struct.pack('<BBHIQ', ptype, ptype, s, pst_crc(body496), bid)


class Journal:
    MAGIC = b'PSTJRNL1'

    def __init__(self, path):
        self.path = path + '.journal'

    def exists(self):
        return os.path.exists(self.path)

    def write(self, orig_eof, records):
        with open(self.path, 'wb') as j:
            j.write(self.MAGIC + struct.pack('<Q', orig_eof))
            for off, data in records:
                j.write(struct.pack('<QI', off, len(data)) + data)
            j.flush(); os.fsync(j.fileno())

    def rollback(self, target):
        with open(self.path, 'rb') as j:
            raw = j.read()
        if raw[:8] != self.MAGIC:
            os.remove(self.path); return False
        orig_eof = struct.unpack_from('<Q', raw, 8)[0]
        recs, p = [], 16
        while p + 12 <= len(raw):
            off, ln = struct.unpack_from('<QI', raw, p)
            if p + 12 + ln > len(raw):
                break
            recs.append((off, raw[p + 12:p + 12 + ln])); p += 12 + ln
        with open(target, 'r+b') as f:
            for off, data in reversed(recs):
                f.seek(off); f.write(data)
            f.truncate(orig_eof)
            f.flush(); os.fsync(f.fileno())
        os.remove(self.path)
        return True

    def drop(self):
        if self.exists():
            os.remove(self.path)


class PSTWriter:
    def __init__(self, path):
        self.path = path
        self.journal = Journal(path)
        self.recovered = False
        if self.journal.exists():
            self.recovered = self.journal.rollback(path)
        self.fd = os.open(path, os.O_RDWR | _BIN)
        h = self.pread(0, 564)
        if h[:4] != b'!BDN' or struct.unpack_from('<H', h, 10)[0] != 23:
            raise PSTCorrupt('only Unicode v23 PST files are supported')
        self.hdr = bytearray(h)
        self.crypt = h[0x201]
        if self.crypt not in (0, 1):
            raise PSTCorrupt('unsupported encryption')
        self._load_header()
        self.pending = []
        self._pidx = {}                     # 4 KB page -> indexes into self.pending (keeps read() from scanning every pending write)
        self.amaps = {}
        self._freecnt = {}                  # section -> number of free 64-byte slots (cache; makes alloc skip full sections fast)
        self.dirty = set()
        self.free_delta = 0
        self.nsec = (self.eof - AMAP0) // SECT
        self.nbt = BTree(self, 0x81)
        self.bbt = BTree(self, 0x80)
        self._reconciled = False
        self.reconciled_fixes = 0
        self.orig_valid = h[248]

    def close(self):
        os.close(self.fd)

    # ---- header ----
    def _load_header(self):
        h = self.hdr
        self.bid_next_p, = struct.unpack_from('<Q', h, 32)
        self.unique, = struct.unpack_from('<I', h, 40)
        self.eof, self.amap_last, self.cb_amap_free = struct.unpack_from('<QQQ', h, 184)
        self.nbt_root = struct.unpack_from('<QQ', h, 216)
        self.bbt_root = struct.unpack_from('<QQ', h, 232)
        self.bid_next_b, = struct.unpack_from('<Q', h, 516)
        self._eof_start = self.eof           # file size at the start of the current transaction

    def _header_bytes(self, valid):
        h = bytearray(self.hdr)
        struct.pack_into('<Q', h, 32, self.bid_next_p)
        struct.pack_into('<I', h, 40, (self.unique + 1) & 0xFFFFFFFF)
        struct.pack_into('<QQQ', h, 184, self.eof, self.amap_last, self.cb_amap_free)
        struct.pack_into('<QQ', h, 216, *self.nbt_root)
        struct.pack_into('<QQ', h, 232, *self.bbt_root)
        h[248] = valid
        struct.pack_into('<Q', h, 516, self.bid_next_b)
        struct.pack_into('<I', h, 4, pst_crc(bytes(h[8:8 + 471])))
        struct.pack_into('<I', h, 524, pst_crc(bytes(h[8:8 + 516])))
        return bytes(h)

    # ---- raw IO with pending overlay ----
    def pread(self, off, n):
        return _pread(self.fd, n, off)

    def read(self, off, n):
        buf = bytearray(self.pread(off, n))
        if len(buf) < n:
            buf += bytes(n - len(buf))
        if self.pending:
            ids = set()
            for pg in range(off >> 12, ((off + n - 1) >> 12) + 1):
                ids.update(self._pidx.get(pg, ()))
            for i in sorted(ids):
                o, d = self.pending[i]
                lo, hi = max(o, off), min(o + len(d), off + n)
                if lo < hi:
                    buf[lo - off:hi - off] = d[lo - o:hi - o]
        return bytes(buf)

    def write(self, off, data):
        data = bytes(data)
        i = len(self.pending)
        self.pending.append((off, data))
        for pg in range(off >> 12, ((off + max(len(data), 1) - 1) >> 12) + 1):
            self._pidx.setdefault(pg, []).append(i)

    # ---- allocation maps ----
    def amap(self, sec):
        if sec not in self.amaps:
            ib = AMAP0 + sec * SECT
            pg = self.read(ib, 512)
            if pg[496] != 0x84:
                raise PSTCorrupt('AMap page expected at 0x%x' % ib)
            self.amaps[sec] = bytearray(pg[:496])
        return self.amaps[sec]

    @staticmethod
    def _bits(a):
        return ''.join(format(b, '08b') for b in a)

    def _setbits(self, sec, start, n, val):
        a = self.amap(sec)
        for i in range(start, start + n):
            m = 0x80 >> (i & 7)
            if val: a[i >> 3] |= m
            else: a[i >> 3] &= ~m & 0xFF
        self.dirty.add(sec)
        self._freecnt.pop(sec, None)

    def reconcile(self):
        """Mark every block / tree page referenced by the B-trees as allocated (sets bits only)."""
        if self._reconciled:
            return 0
        self._reconciled = True
        spans = []
        for tree in (self.nbt, self.bbt):
            def walk(ib, bid, tree=tree):
                spans.append((ib, 512))
                n = tree._read(ib, bid)
                if n['level']:
                    for e in n['ents']:
                        _k, cb, ci = struct.unpack_from('<QQQ', e, 0)
                        walk(ci, cb)
            walk(tree.root[1], tree.root[0])
        for e in self.bbt.items():
            bid, ib, cb = struct.unpack_from('<QQH', e, 0)
            spans.append((ib, (cb + 16 + 63) // 64 * 64))
        fixed = 0
        for ib, size in spans:
            k0 = (ib - AMAP0) // 64
            for k in range(k0, k0 + size // 64):
                sec, i = divmod(k, SLOTS)
                a = self.amap(sec)
                m = 0x80 >> (i & 7)
                if not a[i >> 3] & m:
                    a[i >> 3] |= m
                    self.dirty.add(sec)
                    self._freecnt.pop(sec, None)
                    fixed += 1
        self.reconciled_fixes = fixed
        self.free_delta -= fixed * 64
        return fixed

    def alloc(self, size, align=64):
        self.reconcile()
        n = (size + 63) // 64
        ab = align // 64
        zeros = '0' * n
        for sec in range(self.nsec - 1, -1, -1):
            fc = self._freecnt.get(sec)
            if fc is None:
                fc = self._freecnt[sec] = SLOTS - bin(int.from_bytes(self.amap(sec), 'big')).count('1')
            if fc < n:
                continue
            s = self._bits(self.amap(sec))
            pos = 0
            while True:
                i = s.find(zeros, pos)
                if i < 0:
                    self._freecnt[sec] = max(len(x) for x in s.split('1'))      # exact longest free run: skip this section next time
                    break
                if i % ab == 0:
                    self._setbits(sec, i, n, 1)
                    self.free_delta -= n * 64
                    return AMAP0 + sec * SECT + i * 64
                pos = (i // ab + 1) * ab
        self.grow(GROW_SECTIONS)                     # raises NoSpace at the size limit
        return self.alloc(size, align)

    def grow(self, add):
        """Append `add` whole sections (AMap, plus PMap / FMap pages where the layout needs them)."""
        first = self.nsec
        if first + add > MAX_SECTIONS:
            raise NoSpace('file growth beyond %d sections (about 16 GB) is not supported' % MAX_SECTIONS)
        for s in range(first, first + add):
            base = AMAP0 + s * SECT
            used = 8                                 # the AMap page itself (512 bytes = 8 slots)
            if s % 8 == 0:
                self.write(base + 512, page_bytes(b'\xff' * 496, 0x83, base + 512, base + 512, signed=False))
                used += 8
            if s >= 128 and (s - 128) % 496 == 0:
                self.write(base + 1024, page_bytes(b'\xff' * 496, 0x82, base + 1024, base + 1024, signed=False))
                used += 8
            if s >= FPMAP_FIRST and (s - FPMAP_FIRST) % FPMAP_EVERY == 0:
                self.write(base + 1024, page_bytes(b'\xff' * 496, 0x85, base + 1024, base + 1024, signed=False))
                used += 8
            bits = bytearray(496)
            for i in range(used):
                bits[i >> 3] |= 0x80 >> (i & 7)
            self.amaps[s] = bits
            self._freecnt.pop(s, None)
            self.dirty.add(s)
            self.free_delta += (SLOTS - used) * 64
        self.nsec = first + add
        self.eof = AMAP0 + self.nsec * SECT
        self.amap_last = AMAP0 + (self.nsec - 1) * SECT
        return first

    def free(self, ib, size):
        n = (size + 63) // 64
        sec, off = divmod(ib - AMAP0, SECT)
        self._setbits(sec, off // 64, n, 0)
        self.free_delta += n * 64

    def is_allocated(self, ib, size):
        n = (size + 63) // 64
        sec, off = divmod(ib - AMAP0, SECT)
        s = self._bits(self.amap(sec))
        return '0' not in s[off // 64: off // 64 + n]

    # ---- blocks ----
    def new_bid(self, internal=False):
        b = self.bid_next_b | (2 if internal else 0)
        self.bid_next_b += 4
        return b

    def add_block(self, data, internal=False, cref=2):
        cb = len(data)
        if cb > 8176:
            raise ValueError('block too large')
        bid = self.new_bid(internal)
        size = (cb + 16 + 63) // 64 * 64
        ib = self.alloc(size, 64)
        enc = data.translate(MPBB_R) if (self.crypt == 1 and not internal) else bytes(data)
        buf = enc + bytes(size - 16 - cb) + struct.pack('<HHIQ', cb, sig(ib, bid), pst_crc(enc), bid)
        self.write(ib, buf)
        self.bbt.put(bid, struct.pack('<QQHHI', bid, ib, cb, cref, 0))
        return bid

    def bbt_entry(self, bid):
        e = self.bbt.get(bid & ~1)
        if e is None:
            raise PSTCorrupt('BID 0x%x not in BBT' % bid)
        return struct.unpack('<QQHHI', e)

    def read_block_raw(self, bid):
        _b, ib, cb, _c, _p = self.bbt_entry(bid)
        return self.read(ib, cb)

    def add_data_tree(self, data):
        """Store a node data stream; returns top-level bid."""
        if len(data) <= 8176:
            return self.add_block(data)
        ids = [self.add_block(data[i:i + 8176]) for i in range(0, len(data), 8176)]
        if len(ids) <= 1021:
            return self._xblock(1, ids, len(data))
        subs = []
        for i in range(0, len(ids), 1021):
            part = ids[i:i + 1021]
            subs.append((self._xblock(1, part, min(len(data) - i * 8176, len(part) * 8176)), len(part)))
        return self._xblock(2, [b for b, _ in subs], len(data))

    def _xblock(self, level, ids, total):
        body = struct.pack('<BBHI', 1, level, len(ids), total) + b''.join(struct.pack('<Q', b) for b in ids)
        return self.add_block(body, internal=True)

    def add_ref(self, bid):
        b, ib, cb, c, p = self.bbt_entry(bid)
        self.bbt.put(b, struct.pack('<QQHHI', b, ib, cb, c + 1, 0))

    def release(self, bid):
        """Drop one reference; free the block (and its children) when only the BBT reference is left."""
        if not bid:
            return
        b, ib, cb, c, p = self.bbt_entry(bid)
        if c - 1 > 1:
            self.bbt.put(b, struct.pack('<QQHHI', b, ib, cb, c - 1, 0)); return
        children = []
        if bid & 2:
            d = self.read(ib, cb)
            bt, lvl, cent = d[0], d[1], struct.unpack_from('<H', d, 2)[0]
            if bt == 1:
                children = [struct.unpack_from('<Q', d, 8 + 8 * i)[0] for i in range(cent)]
            elif bt == 2:
                if lvl == 0:
                    for i in range(cent):
                        _n, bd, bs = struct.unpack_from('<QQQ', d, 8 + 24 * i)
                        children += [bd, bs]
                else:
                    children = [struct.unpack_from('<QQ', d, 8 + 16 * i)[1] for i in range(cent)]
        self.free(ib, (cb + 16 + 63) // 64 * 64)
        self.bbt.delete(b)
        for ch in children:
            self.release(ch)

    # ---- pages ----
    def alloc_page_bid(self):
        b = self.bid_next_p
        self.bid_next_p += 1
        return b

    # ---- commit / rollback ----
    def rollback_pending(self):
        self.pending.clear(); self._pidx.clear(); self.amaps.clear(); self._freecnt.clear(); self.dirty.clear(); self.free_delta = 0
        self.hdr = bytearray(self.pread(0, 564)); self._load_header()
        self.nsec = (self.eof - AMAP0) // SECT

    def _dlist_update(self):
        d = bytearray(self.read(DLIST_IB, 512))
        if d[496] != 0x86:
            return
        cent = d[1]
        for i in range(cent):
            v, = struct.unpack_from('<I', d, 8 + 4 * i)
            pn = v & 0xFFFFF
            if pn in self.dirty:
                free = self._bits(self.amap(pn)).count('0')
                struct.pack_into('<I', d, 8 + 4 * i, pn | (free << 20))
        struct.pack_into('<I', d, 500, pst_crc(bytes(d[:496])))
        self.write(DLIST_IB, bytes(d))

    @staticmethod
    def _maxrun(a):
        bits = ''.join(format(b, '08b') for b in a)
        return min(255, max((len(x) for x in re.findall('0+', bits)), default=0))

    def _fmap_update(self):
        pages = {}
        for sec in sorted(self.dirty):
            val = self._maxrun(self.amap(sec))
            if sec < 128:
                self.hdr[256 + sec] = val
                continue
            ib = AMAP0 + (128 + 496 * ((sec - 128) // 496)) * SECT + 1024
            if ib not in pages:
                raw = self.read(ib, 512)
                if raw[496] != 0x82:
                    pages[ib] = None
                else:
                    pages[ib] = bytearray(raw[:496])
            if pages[ib] is not None:
                pages[ib][(sec - 128) % 496] = val
        for ib, pg in pages.items():
            if pg is not None:
                self.write(ib, page_bytes(bytes(pg), 0x82, ib, ib, signed=False))

    def commit(self):
        self.reconcile()
        if not self.pending and not self.dirty and self.eof == self._eof_start:
            return
        for sec in sorted(self.dirty):
            ib = AMAP0 + sec * SECT
            self.write(ib, page_bytes(bytes(self.amaps[sec]), 0x84, ib, ib, signed=False))
        self._fmap_update()
        self._dlist_update()
        self.cb_amap_free = max(0, self.cb_amap_free + self.free_delta)
        # journal original bytes of every touched region (merge identical regions)
        regions = {}
        for off, data in self.pending:
            regions[(off, len(data))] = None
        recs = [(0, self.pread(0, 564))]
        for off, ln in regions:
            if off < self._eof_start:
                recs.append((off, self.pread(off, min(ln, self._eof_start - off))))
        self.journal.write(self._eof_start, recs)
        _pwrite(self.fd, self._header_bytes(0), 0); os.fsync(self.fd)
        if self.eof > self._eof_start:
            os.ftruncate(self.fd, self.eof)          # grow to the exact section grid first
        for off, data in self.pending:
            _pwrite(self.fd, data, off)
        os.fsync(self.fd)
        final = self._header_bytes(2)
        _pwrite(self.fd, final, 0); os.fsync(self.fd)
        self.journal.drop()
        self.hdr = bytearray(final); self._load_header()
        self.pending.clear(); self._pidx.clear(); self.amaps.clear(); self._freecnt.clear(); self.dirty.clear(); self.free_delta = 0

    def validate(self):
        """Structural check; returns list of problems."""
        probs = []
        bbt_entries = []
        for tree, nm in ((self.nbt, 'NBT'), (self.bbt, 'BBT')):
            last = [-1]
            def walk(ib, bid, level, pkey=None):
                pg = self.read(ib, 512)
                if pkey is not None and pg[488] and struct.unpack_from('<Q', pg, 0)[0] != pkey:
                    probs.append('%s page 0x%x btkeyMin mismatch (page %x, parent %x)' % (nm, ib, struct.unpack_from('<Q', pg, 0)[0], pkey))
                if pg[496] != tree.ptype or pg[497] != tree.ptype:
                    probs.append('%s page type bad at 0x%x' % (nm, ib)); return
                if struct.unpack_from('<I', pg, 500)[0] != pst_crc(pg[:496]):
                    probs.append('%s page CRC bad at 0x%x' % (nm, ib))
                if struct.unpack_from('<H', pg, 498)[0] != sig(ib, struct.unpack_from('<Q', pg, 504)[0]):
                    probs.append('%s page sig bad at 0x%x' % (nm, ib))
                if struct.unpack_from('<Q', pg, 504)[0] != bid:
                    probs.append('%s page bid mismatch at 0x%x' % (nm, ib))
                if not self.is_allocated(ib, 512):
                    probs.append('%s page 0x%x not marked allocated in AMap' % (nm, ib))
                cent, cb_ent, lv = pg[488], pg[490], pg[491]
                if lv != level:
                    probs.append('%s level mismatch at 0x%x' % (nm, ib))
                for i in range(cent):
                    e = pg[i * cb_ent:(i + 1) * cb_ent]
                    if lv:
                        k, cbid, cib = struct.unpack_from('<QQQ', e, 0)
                        walk(cib, cbid, lv - 1, k)
                    else:
                        k = struct.unpack_from('<Q', e, 0)[0]
                        if k <= last[0]:
                            probs.append('%s keys out of order at 0x%x' % (nm, ib))
                        last[0] = k
                        if nm == 'BBT':
                            bbt_entries.append(struct.unpack_from('<QQHH', e, 0))
            root = tree.root
            rp = self.read(root[1], 512)
            walk(root[1], root[0], rp[491])
        for bid, ib, cb, cref in bbt_entries:
            size = (cb + 16 + 63) // 64 * 64
            if not self.is_allocated(ib, size):
                probs.append('block 0x%x not allocated in AMap' % bid)
            tr = self.read(ib + size - 16, 16)
            tcb, tsig, tcrc, tbid = struct.unpack('<HHIQ', tr)
            if tbid != bid or tcb != cb:
                probs.append('block 0x%x trailer mismatch' % bid)
            if tsig != sig(ib, bid):
                probs.append('block 0x%x sig mismatch' % bid)
            if tcrc != pst_crc(self.read(ib, cb)):
                probs.append('block 0x%x CRC mismatch' % bid)
        for sec in range(self.nsec):
            val = self._maxrun(self.amap(sec))
            if sec < 128:
                cur = self.hdr[256 + sec]
            else:
                pg = self.read(AMAP0 + (128 + 496 * ((sec - 128) // 496)) * SECT + 1024, 512)
                if pg[496] != 0x82:
                    continue
                cur = pg[(sec - 128) % 496]
            if cur != 0xFF and cur != val:
                probs.append('FMap byte for AMap %d is %d, expected %d' % (sec, cur, val))
        return probs


class BTree:
    def __init__(self, w, ptype):
        self.w, self.ptype = w, ptype
        self.leaf_sz = 32 if ptype == 0x81 else 24

    @property
    def root(self):
        return self.w.nbt_root if self.ptype == 0x81 else self.w.bbt_root

    def _set_root(self, bref):
        if self.ptype == 0x81: self.w.nbt_root = bref
        else: self.w.bbt_root = bref

    def _read(self, ib, bid):
        pg = self.w.read(ib, 512)
        if pg[496] != self.ptype:
            raise PSTCorrupt('BTree page type mismatch at 0x%x' % ib)
        cent, cbent, level = pg[488], pg[490], pg[491]
        ents = [pg[i * cbent:(i + 1) * cbent] for i in range(cent)]
        return {'ib': ib, 'bid': bid, 'level': level, 'ents': ents, 'idx': None}

    def _esz(self, level):
        return self.leaf_sz if level == 0 else 24

    def _page(self, node, ents):
        esz = self._esz(node['level'])
        body = b''.join(ents)
        body += bytes(488 - len(body))
        body += struct.pack('<BBBBI', len(ents), 488 // esz, esz, node['level'], 0)
        return page_bytes(body, self.ptype, node['ib'], node['bid'])

    @staticmethod
    def _key(e):
        return struct.unpack_from('<Q', e, 0)[0]

    def _descend(self, key):
        bid, ib = self.root
        path = []
        while True:
            node = self._read(ib, bid)
            path.append(node)
            if node['level'] == 0:
                return path
            idx = 0
            for i, e in enumerate(node['ents']):
                if self._key(e) <= key: idx = i
                else: break
            node['idx'] = idx
            _k, bid, ib = struct.unpack_from('<QQQ', node['ents'][idx], 0)

    def get(self, key):
        leaf = self._descend(key)[-1]
        for e in leaf['ents']:
            if self._key(e) == key:
                return e
        return None

    def items(self):
        out = []
        def walk(ib, bid):
            n = self._read(ib, bid)
            for e in n['ents']:
                if n['level']:
                    _k, cb, ci = struct.unpack_from('<QQQ', e, 0); walk(ci, cb)
                else:
                    out.append(e)
        walk(self.root[1], self.root[0])
        return out

    def put(self, key, entry):
        path = self._descend(key)
        leaf = path[-1]
        ents = list(leaf['ents'])
        for i, e in enumerate(ents):
            if self._key(e) == key:
                ents[i] = entry; break
            if self._key(e) > key:
                ents.insert(i, entry); break
        else:
            ents.append(entry)
        self._store(path, len(path) - 1, ents)

    def delete(self, key):
        path = self._descend(key)
        leaf = path[-1]
        ents = [e for e in leaf['ents'] if self._key(e) != key]
        if len(ents) == len(leaf['ents']):
            return False
        if not ents and len(path) > 1:
            self._remove_node(path, len(path) - 1)
        else:
            self._store(path, len(path) - 1, ents)
        return True

    def _remove_node(self, path, i):
        node = path[i]
        self.w.free(node['ib'], 512)
        parent = path[i - 1]
        pents = [e for j, e in enumerate(parent['ents']) if j != parent['idx']]
        if not pents and i - 1 > 0:
            self._remove_node(path, i - 1)
        elif not pents:
            # root became empty: turn it into an empty leaf
            parent['level'] = 0
            self.w.write(parent['ib'], self._page(parent, []))
            self.leaf_reset = True
        else:
            self._store(path, i - 1, pents)

    def _store(self, path, i, ents):
        node = path[i]
        esz = self._esz(node['level'])
        cmax = 488 // esz
        if len(ents) <= cmax:
            self.w.write(node['ib'], self._page(node, ents))
            node['ents'] = ents
            if i > 0 and ents:
                parent = path[i - 1]
                pe = parent['ents'][parent['idx']]
                k0 = self._key(ents[0])
                if self._key(pe) != k0:
                    pents = list(parent['ents'])
                    pents[parent['idx']] = struct.pack('<Q', k0) + pe[8:]
                    self._store(path, i - 1, pents)
            return
        mid = len(ents) // 2
        left, right = ents[:mid], ents[mid:]
        self.w.write(node['ib'], self._page(node, left))
        nbid = self.w.alloc_page_bid()
        nib = self.w.alloc(512, 512)
        nnode = {'ib': nib, 'bid': nbid, 'level': node['level']}
        self.w.write(nib, self._page(nnode, right))
        new_e = struct.pack('<QQQ', self._key(right[0]), nbid, nib)
        if i == 0:
            rbid = self.w.alloc_page_bid()
            rib = self.w.alloc(512, 512)
            rnode = {'ib': rib, 'bid': rbid, 'level': node['level'] + 1}
            old_e = struct.pack('<QQQ', self._key(left[0]), node['bid'], node['ib'])
            self.w.write(rib, self._page(rnode, [old_e, new_e]))
            self._set_root((rbid, rib))
        else:
            parent = path[i - 1]
            pents = list(parent['ents'])
            pents.insert(parent['idx'] + 1, new_e)
            self._store(path, i - 1, pents)
