"""pstcore.py - from-scratch reader for Outlook Unicode PST files (MS-PST), stage 1 (read-only).
No third-party PST libraries. Layers: NDB -> LTP -> Messaging.
"""
import mmap, struct, datetime

MPBB_I = bytes([71, 241, 180, 230, 11, 106, 114, 72, 133, 78, 158, 235, 226, 248, 148, 83, 224, 187, 160, 2, 232, 90, 9, 171, 219, 227, 186, 198, 124, 195, 16, 221, 57, 5, 150, 48, 245, 55, 96, 130, 140, 201, 19, 74, 107, 29, 243, 251, 143, 38, 151, 202, 145, 23, 1, 196, 50, 45, 110, 49, 149, 255, 217, 35, 209, 0, 94, 121, 220, 68, 59, 26, 40, 197, 97, 87, 32, 144, 61, 131, 185, 67, 190, 103, 210, 70, 66, 118, 192, 109, 91, 126, 178, 15, 22, 41, 60, 169, 3, 84, 13, 218, 93, 223, 246, 183, 199, 98, 205, 141, 6, 211, 105, 92, 134, 214, 20, 247, 165, 102, 117, 172, 177, 233, 69, 33, 112, 12, 135, 159, 116, 164, 34, 76, 111, 191, 31, 86, 170, 46, 179, 120, 51, 80, 176, 163, 146, 188, 207, 25, 28, 167, 99, 203, 30, 77, 62, 75, 27, 155, 79, 231, 240, 238, 173, 58, 181, 89, 4, 234, 64, 85, 37, 81, 229, 122, 137, 56, 104, 82, 123, 252, 39, 174, 215, 189, 250, 7, 244, 204, 142, 95, 239, 53, 156, 132, 43, 21, 213, 119, 52, 73, 182, 18, 10, 127, 113, 136, 253, 157, 24, 65, 125, 147, 216, 88, 44, 206, 254, 36, 175, 222, 184, 54, 200, 161, 128, 166, 153, 152, 168, 47, 14, 129, 101, 115, 228, 194, 162, 138, 212, 225, 17, 208, 8, 139, 42, 242, 237, 154, 100, 63, 193, 108, 249, 236])

def _mk_crc():
    t = []
    for i in range(256):
        c = i
        for _ in range(8):
            c = (c >> 1) ^ 0xEDB88320 if c & 1 else c >> 1
        t.append(c)
    return t
_CRC = _mk_crc()

def pst_crc(data, crc=0):
    t = _CRC
    for b in data:
        crc = t[(crc ^ b) & 0xFF] ^ (crc >> 8)
    return crc & 0xFFFFFFFF

NID_MESSAGE_STORE = 0x21
NID_ROOT_FOLDER = 0x122
NID_TYPE_RECIPIENT_TABLE = 0x692
NID_TYPE_ATTACHMENT_TABLE = 0x671
PTYPE_FIXED = {0x0002: 2, 0x0003: 4, 0x0004: 4, 0x0005: 8, 0x0006: 8, 0x0007: 8,
               0x000A: 4, 0x000B: 1, 0x0014: 8, 0x0040: 8}
INLINE_PC = {0x0002, 0x0003, 0x0004, 0x000A, 0x000B}


class PSTError(Exception):
    pass


def filetime(v):
    if not v:
        return None
    try:
        return datetime.datetime(1601, 1, 1) + datetime.timedelta(microseconds=v // 10)
    except OverflowError:
        return None


def decode_value(ptype, raw, codepage=None):
    if ptype == 0x001F:
        return raw.decode('utf-16-le', 'replace')
    if ptype == 0x001E:
        return raw.decode(_cp(codepage), 'replace').rstrip('\x00')
    if ptype in (0x0002,):
        return struct.unpack('<h', raw[:2].ljust(2, b'\0'))[0]
    if ptype in (0x0003, 0x000A):
        return struct.unpack('<i', raw[:4].ljust(4, b'\0'))[0]
    if ptype == 0x000B:
        return bool(raw[:1] != b'\0' and raw[:1] != b'')
    if ptype == 0x0014:
        return struct.unpack('<q', raw[:8].ljust(8, b'\0'))[0]
    if ptype == 0x0040:
        return filetime(struct.unpack('<Q', raw[:8].ljust(8, b'\0'))[0])
    if ptype == 0x0005:
        return struct.unpack('<d', raw[:8].ljust(8, b'\0'))[0]
    return bytes(raw)


def _cp(codepage):
    if not codepage:
        return 'cp1252'
    if codepage == 65001:
        return 'utf-8'
    if codepage == 20127:
        return 'ascii'
    import codecs
    names = ['cp%d' % codepage]
    if 28591 <= codepage <= 28599:
        names.append('iso-8859-%d' % (codepage - 28590))
    special = {28605: 'iso-8859-15', 20866: 'koi8-r', 21866: 'koi8-u', 51932: 'euc-jp', 51949: 'euc-kr',
               54936: 'gb18030', 936: 'gbk', 950: 'big5', 932: 'shift_jis', 949: 'cp949', 65000: 'utf-7',
               1200: 'utf-16-le', 1201: 'utf-16-be', 10000: 'mac-roman', 38598: 'iso-8859-8', 20105: 'latin-1'}
    if codepage in special:
        names.append(special[codepage])
    for name in names:
        try:
            codecs.lookup(name)           # (decoding b'' does NOT check the name, so look it up properly)
            return name
        except LookupError:
            pass
    return 'cp1252'


class Node:
    def __init__(self, pst, nid, bdata, bsub, parent=0):
        self.pst, self.nid, self.bdata, self.bsub, self.parent = pst, nid, bdata, bsub, parent
        self._blocks = None
        self._subs = None

    def blocks(self):
        if self._blocks is None:
            self._blocks = self.pst.data_blocks(self.bdata) if self.bdata else []
        return self._blocks

    def data(self):
        return b''.join(self.blocks())

    def subs(self):
        if self._subs is None:
            self._subs = self.pst.read_subnodes(self.bsub) if self.bsub else {}
        return self._subs

    def sub(self, nid):
        s = self.subs().get(nid)
        if not s:
            return None
        return Node(self.pst, nid, s[0], s[1])


class PST:
    def __init__(self, path):
        self.path = path
        self.fh = open(path, 'rb')
        self.mm = mmap.mmap(self.fh.fileno(), 0, access=mmap.ACCESS_READ)
        h = self.mm[:1024]
        if h[:4] != b'!BDN':
            raise PSTError('Not a PST file (bad magic)')
        self.ver = struct.unpack_from('<H', h, 10)[0]
        if self.ver < 23:
            raise PSTError('ANSI PST (wVer=%d) not supported' % self.ver)
        if self.ver >= 36:
            raise PSTError('4K-page PST (wVer=%d) not supported yet' % self.ver)
        self.header = h
        self.eof = struct.unpack_from('<Q', h, 184)[0]
        self.nbt_root = struct.unpack_from('<QQ', h, 216)
        self.bbt_root = struct.unpack_from('<QQ', h, 232)
        self.crypt = h[0x201]
        if self.crypt not in (0, 1):
            raise PSTError('Encryption method %d not supported' % self.crypt)
        self.nbt = {}
        self.bbt = {}
        self.crc_errors = []
        self._load_tree(self.nbt_root, 0x81, self.nbt, nbt=True)
        self._load_tree(self.bbt_root, 0x80, self.bbt, nbt=False)

    def close(self):
        self.mm.close(); self.fh.close()

    # ---------------- NDB ----------------
    def _load_tree(self, bref, ptype, out, nbt):
        bid, ib = bref
        page = self.mm[ib:ib + 512]
        if page[496] != ptype or page[497] != ptype:
            raise PSTError('BTree page type mismatch at 0x%x' % ib)
        if struct.unpack_from('<I', page, 500)[0] != pst_crc(page[:496]):
            self.crc_errors.append(('page', ib))
        cent, centmax, cbent, clevel = page[488], page[489], page[490], page[491]
        for i in range(cent):
            e = page[i * cbent:(i + 1) * cbent]
            if clevel > 0:
                _key, cbid, cib = struct.unpack_from('<QQQ', e, 0)
                self._load_tree((cbid, cib), ptype, out, nbt)
            elif nbt:
                nid, bd, bs, par = struct.unpack_from('<QQQI', e, 0)
                out[nid & 0xFFFFFFFF] = (bd, bs, par)
            else:
                bid_, bib, cb, cref = struct.unpack_from('<QQHH', e, 0)
                out[bid_ & ~1] = (bib, cb, cref)

    def raw_block(self, bid):
        ent = self.bbt.get(bid & ~1)
        if ent is None:
            raise PSTError('BID 0x%x not in BBT' % bid)
        ib, cb, _ = ent
        return self.mm[ib:ib + cb]

    def read_block(self, bid):
        raw = bytes(self.raw_block(bid))
        if self.crypt == 1 and not (bid & 2):
            raw = raw.translate(MPBB_I)
        return raw

    def data_blocks(self, bid):
        if not (bid & 2):
            return [self.read_block(bid)]
        b = self.raw_block(bid)
        btype, clevel, cent = b[0], b[1], struct.unpack_from('<H', b, 2)[0]
        if btype != 1:
            raise PSTError('Expected XBLOCK for BID 0x%x' % bid)
        out = []
        for i in range(cent):
            cbid = struct.unpack_from('<Q', b, 8 + 8 * i)[0]
            out.extend(self.data_blocks(cbid))
        return out

    def read_subnodes(self, bid):
        b = bytes(self.raw_block(bid))
        btype, clevel, cent = b[0], b[1], struct.unpack_from('<H', b, 2)[0]
        if btype != 2:
            raise PSTError('Expected SLBLOCK/SIBLOCK')
        out = {}
        for i in range(cent):
            if clevel == 0:
                nid, bd, bs = struct.unpack_from('<QQQ', b, 8 + 24 * i)
                out[nid & 0xFFFFFFFF] = (bd, bs)
            else:
                _nid, cbid = struct.unpack_from('<QQ', b, 8 + 16 * i)
                out.update(self.read_subnodes(cbid))
        return out

    def node(self, nid):
        e = self.nbt.get(nid)
        if not e:
            return None
        return Node(self, nid, e[0], e[1], e[2])

    # ---------------- Verification ----------------
    def verify(self):
        problems = []
        h = self.header
        if struct.unpack_from('<I', h, 4)[0] != pst_crc(h[8:8 + 471]):
            problems.append('header partial CRC mismatch')
        if struct.unpack_from('<I', h, 0x20C - 0)[0] != pst_crc(h[8:8 + 516]):
            problems.append('header full CRC mismatch')
        for kind, ib in self.crc_errors:
            problems.append('%s CRC mismatch at 0x%x' % (kind, ib))
        nblk = 0
        for bid, (ib, cb, _c) in self.bbt.items():
            nblk += 1
            tot = (cb + 16 + 63) // 64 * 64
            tr = self.mm[ib + tot - 16: ib + tot]
            tcb, _sig, crc, tbid = struct.unpack('<HHIQ', tr)
            if tcb != cb:
                problems.append('block 0x%x trailer cb %d != %d' % (bid, tcb, cb))
            if crc != pst_crc(self.mm[ib:ib + cb]):
                problems.append('block 0x%x CRC mismatch' % bid)
        return nblk, problems


# ---------------- LTP: heap-on-node, BTH, PC, TC ----------------
class Heap:
    def __init__(self, node_or_blocks, node=None):
        if isinstance(node_or_blocks, Node):
            self.node = node_or_blocks
            self.blocks = node_or_blocks.blocks()
        else:
            self.node = node
            self.blocks = node_or_blocks
        b0 = self.blocks[0]
        if b0[2] != 0xEC:
            raise PSTError('Bad HN signature on node 0x%x' % (self.node.nid if self.node else 0))
        self.client_sig = b0[3]
        self.user_root = struct.unpack_from('<I', b0, 4)[0]

    def get(self, hid):
        idx = (hid >> 5) & 0x7FF
        blk = hid >> 16
        if idx == 0:
            return b''
        b = self.blocks[blk]
        pm = struct.unpack_from('<H', b, 0)[0]
        calloc = struct.unpack_from('<H', b, pm)[0]
        if idx > calloc:
            raise PSTError('HID 0x%x out of range' % hid)
        s, e = struct.unpack_from('<HH', b, pm + 4 + 2 * (idx - 1))
        return b[s:e]

    def bth(self, hid):
        hdr = self.get(hid)
        if hdr[0] != 0xB5:
            raise PSTError('Bad BTH header')
        cbkey, cbent, levels = hdr[1], hdr[2], hdr[3]
        root = struct.unpack_from('<I', hdr, 4)[0]
        out = []
        if root:
            self._bth_walk(root, levels, cbkey, cbent, out)
        return out

    def _bth_walk(self, hid, level, cbkey, cbent, out):
        d = self.get(hid)
        if level == 0:
            sz = cbkey + cbent
            for i in range(len(d) // sz):
                out.append((d[i * sz:i * sz + cbkey], d[i * sz + cbkey:(i + 1) * sz]))
        else:
            sz = cbkey + 4
            for i in range(len(d) // sz):
                child = struct.unpack_from('<I', d, i * sz + cbkey)[0]
                self._bth_walk(child, level - 1, cbkey, cbent, out)

    def resolve(self, hnid):
        """Resolve a value reference: HID into heap, or NID into subnode."""
        if hnid == 0:
            return b''
        if hnid & 0x1F == 0:
            return self.get(hnid)
        sn = self.node.sub(hnid) if self.node else None
        if sn is None:
            raise PSTError('Subnode 0x%x missing' % hnid)
        return sn.data()


class PC:
    """Property context: dict propid -> (ptype, raw bytes)."""
    def __init__(self, node):
        self.node = node
        self.heap = Heap(node)
        if self.heap.client_sig != 0xBC:
            raise PSTError('Node 0x%x is not a PC (bClientSig=0x%x)' % (node.nid, self.heap.client_sig))
        self.props = {}
        for k, v in self.heap.bth(self.heap.user_root):
            pid, ptype = struct.unpack('<HH', k + v[:2])
            val = v[2:6]
            hnid = struct.unpack('<I', val)[0]
            if ptype in INLINE_PC:
                raw = val
            else:
                try:
                    raw = self.heap.resolve(hnid)
                except PSTError:
                    raw = b''
            self.props[pid] = (ptype, raw)

    def raw(self, pid):
        v = self.props.get(pid)
        return v[1] if v else None

    def get(self, pid, default=None, codepage=None):
        v = self.props.get(pid)
        if v is None:
            return default
        return decode_value(v[0], v[1], codepage)


class TC:
    def __init__(self, node):
        self.node = node
        self.heap = Heap(node)
        if self.heap.client_sig != 0x7C:
            raise PSTError('Node 0x%x is not a TC' % node.nid)
        info = self.heap.get(self.heap.user_root)
        ccols = info[1]
        self.rgib = struct.unpack_from('<4H', info, 2)
        self.hid_rowindex, self.hnid_rows = struct.unpack_from('<II', info, 10)
        self.cols = []
        for i in range(ccols):
            tag, ibdata, cbdata, ibit = struct.unpack_from('<IHBB', info, 22 + 8 * i)
            self.cols.append((tag >> 16, tag & 0xFFFF, ibdata, cbdata, ibit))
        self.rowsize = self.rgib[3]

    def rows(self):
        if self.hnid_rows == 0 or self.hid_rowindex == 0:
            return []
        index = self.heap.bth(self.hid_rowindex)
        if self.hnid_rows & 0x1F == 0:
            blocks, rpb = [self.heap.get(self.hnid_rows)], None
        else:
            sn = self.node.sub(self.hnid_rows)
            blocks, rpb = (sn.blocks() if sn else []), (8192 - 16) // self.rowsize
        out = []
        for k, v in index:
            rowid = struct.unpack('<I', k)[0]
            ridx = struct.unpack('<I', v)[0]
            if rpb is None:
                buf, off = blocks[0], ridx * self.rowsize
            else:
                bi, ri = divmod(ridx, rpb)
                if bi >= len(blocks):
                    continue
                buf, off = blocks[bi], ri * self.rowsize
            row = buf[off:off + self.rowsize]
            if len(row) < self.rowsize:
                continue
            out.append((rowid, self._parse_row(row)))
        return out

    def _parse_row(self, row):
        ceb = row[self.rgib[2]:self.rgib[3]]
        cells = {}
        for pid, ptype, ibd, cbd, ibit in self.cols:
            if not (ceb[ibit >> 3] & (0x80 >> (ibit & 7))):
                continue
            if ptype in PTYPE_FIXED:
                raw = row[ibd:ibd + cbd]
            else:
                hnid = struct.unpack_from('<I', row, ibd)[0]
                try:
                    raw = self.heap.resolve(hnid)
                except PSTError:
                    raw = b''
            cells[pid] = (ptype, raw)
        return cells


def cells_to_values(cells, codepage=None):
    return {pid: decode_value(t, r, codepage) for pid, (t, r) in cells.items()}


# ---------------- Messaging layer ----------------
class Folder:
    def __init__(self, store, nid, parent=None):
        self.store, self.nid, self.parent = store, nid, parent
        self._pc = None

    @property
    def pc(self):
        if self._pc is None:
            n = self.store.pst.node(self.nid)
            self._pc = PC(n) if n else None
        return self._pc

    @property
    def name(self):
        return (self.pc.get(0x3001, '') if self.pc else '') or '(unnamed)'

    def subfolders(self):
        n = self.store.pst.node((self.nid & ~0x1F) | 0x0D)
        if not n:
            return []
        out = []
        for rowid, _cells in TC(n).rows():
            out.append(Folder(self.store, rowid, self))
        return sorted(out, key=lambda f: f.name.lower())

    def contents(self):
        """List of dicts: nid + summary columns."""
        n = self.store.pst.node((self.nid & ~0x1F) | 0x0E)
        if not n:
            return []
        out = []
        for rowid, cells in TC(n).rows():
            v = cells_to_values(cells)
            v['nid'] = rowid
            out.append(v)
        return out


class Store:
    def __init__(self, path):
        self.pst = PST(path)
        n = self.pst.node(NID_MESSAGE_STORE)
        self.store_pc = PC(n) if n else None
        self.root = Folder(self, NID_ROOT_FOLDER)

    @property
    def display_name(self):
        return self.store_pc.get(0x3001, '') if self.store_pc else ''

    def find_folder(self, path):
        cur = self.root
        for part in [p for p in path.split('/') if p]:
            for f in cur.subfolders():
                if f.name.lower() == part.lower():
                    cur = f
                    break
            else:
                raise PSTError('Folder not found: %s' % part)
        return cur

    def walk(self, folder=None, depth=0):
        folder = folder or self.root
        yield folder, depth
        for sf in folder.subfolders():
            yield from self.walk(sf, depth + 1)

    def message(self, nid):
        return Message(self, self.pst.node(nid))


class Attachment:
    def __init__(self, pc):
        self.pc = pc
        self.filename = pc.get(0x3707) or pc.get(0x3704) or pc.get(0x3001) or 'attachment'
        self.method = pc.get(0x3705)
        self.mime = pc.get(0x370E, '')
        self.cid = pc.get(0x3712, '')
        v = pc.props.get(0x3701)
        self.data = v[1] if v else b''
        self.size = pc.get(0x0E20, len(self.data))


class Message:
    def __init__(self, store, node):
        if node is None:
            raise PSTError('Message node missing')
        self.store, self.node = store, node
        self.pc = PC(node)
        self.codepage = self.pc.get(0x3FFD) or self.pc.get(0x3FDE)

    def s(self, pid, default=''):
        v = self.pc.props.get(pid)
        if v is None:
            return default
        return decode_value(v[0], v[1], self.codepage)

    @property
    def subject(self):
        subj = self.s(0x0037)
        if subj[:1] == '\x01' and len(subj) >= 2:
            subj = subj[2:]
        return subj

    def headers(self):
        return {'Subject': self.subject, 'From': self.s(0x0C1A), 'From address': self.s(0x5D01) or self.s(0x0C1F),
                'To': self.s(0x0E04), 'Cc': self.s(0x0E03), 'Bcc': self.s(0x0E02),
                'Sent': self.pc.get(0x0039), 'Received': self.pc.get(0x0E06)}

    def transport_headers(self):
        return self.s(0x007D)

    def body_text(self):
        v = self.pc.props.get(0x1000)
        return decode_value(v[0], v[1], self.codepage) if v else None

    def body_html(self):
        v = self.pc.props.get(0x1013)
        if not v:
            return None
        if v[0] == 0x001F:
            return v[1].decode('utf-16-le', 'replace')
        cp = self.pc.get(0x3FDE) or self.codepage
        return v[1].decode(_cp(cp), 'replace')

    def body_rtf(self):
        v = self.pc.props.get(0x1009)
        if not v:
            return None
        return lzfu_decompress(v[1])

    def available_bodies(self):
        out = []
        if self.pc.props.get(0x1013): out.append('html')
        if self.pc.props.get(0x1009): out.append('rtf')
        if self.pc.props.get(0x1000): out.append('text')
        return out

    def recipients(self):
        sn = self.node.sub(NID_TYPE_RECIPIENT_TABLE)
        if not sn:
            return []
        out = []
        for _rid, cells in TC(sn).rows():
            v = cells_to_values(cells, self.codepage)
            out.append({'name': v.get(0x3001, ''), 'email': v.get(0x39FE) or v.get(0x3003, ''),
                        'type': {1: 'To', 2: 'Cc', 3: 'Bcc'}.get(v.get(0x0C15), '?')})
        return out

    def attachments(self):
        sn = self.node.sub(NID_TYPE_ATTACHMENT_TABLE)
        if not sn:
            return []
        out = []
        for rid, _c in TC(sn).rows():
            a = self.node.sub(rid)
            if a:
                out.append(Attachment(PC(a)))
        return out


# ---------------- Compressed RTF (LZFu) ----------------
_LZ_INIT = (b'{\\rtf1\\ansi\\mac\\deff0\\deftab720{\\fonttbl;}{\\f0\\fnil \\froman \\fswiss \\fmodern '
            b'\\fscript \\fdecor MS Sans SerifSymbolArialTimes New RomanCourier{\\colortbl\\red0\\green0'
            b'\\blue0\r\n\\par \\pard\\plain\\f0\\fs20\\b\\i\\ul\\tx'
            b'\0\0\0')   # the specified initial dictionary is 207 bytes; the last 3 are never referenced by real streams


def lzfu_decompress(data):
    if len(data) < 16:
        return ''
    csize, rsize, magic, _crc = struct.unpack_from('<IIII', data, 0)
    if magic == 0x414C454D:
        return data[16:16 + rsize].decode('latin-1')
    if magic != 0x75465A4C:
        raise PSTError('Bad compressed-RTF magic')
    d = bytearray(_LZ_INIT) + bytearray(4096 - len(_LZ_INIT))
    wp = len(_LZ_INIT)
    out = bytearray()
    p = 16
    end = min(len(data), csize + 4)
    while p < end:
        ctl = data[p]; p += 1
        for bit in range(8):
            if ctl & (1 << bit):
                if p + 1 >= len(data) + 0 and p + 2 > len(data):
                    return out.decode('latin-1')
                hi, lo = data[p], data[p + 1]; p += 2
                off = (hi << 4) | (lo >> 4)
                ln = (lo & 0xF) + 2
                if off == wp:
                    return out.decode('latin-1')
                for _ in range(ln):
                    c = d[off]
                    out.append(c)
                    d[wp] = c
                    wp = (wp + 1) & 4095
                    off = (off + 1) & 4095
            else:
                if p >= end:
                    break
                c = data[p]; p += 1
                out.append(c)
                d[wp] = c
                wp = (wp + 1) & 4095
    return out.decode('latin-1')


def rtf_to_html_or_text(rtf):
    """Minimal RTF handling: de-encapsulate HTML if \\fromhtml1, else extract plain text."""
    fromhtml = '\\fromhtml' in rtf[:2000]
    out = []
    stack = []
    skip = 0
    htmlrtf = False
    in_tag = 0
    i, n = 0, len(rtf)
    uc = 1
    while i < n:
        c = rtf[i]
        if c == '{':
            stack.append((skip, htmlrtf, in_tag, uc))
            i += 1
            if rtf.startswith('\\*', i):
                j = i + 2
                m = j
                while m < n and rtf[m] == ' ':
                    m += 1
                if fromhtml and rtf.startswith('\\htmltag', m):
                    in_tag = 1
                elif rtf.startswith('\\mhtmltag', m) and fromhtml:
                    skip = 1
                else:
                    skip = 1 if not (fromhtml and in_tag) else skip
        elif c == '}':
            if stack:
                skip, htmlrtf, in_tag, uc = stack.pop()
            i += 1
        elif c == '\\':
            i += 1
            if i >= n:
                break
            ch = rtf[i]
            if ch in '\\{}':
                if not skip and not (fromhtml and htmlrtf):
                    out.append(ch)
                i += 1
            elif ch == "'":
                hx = rtf[i + 1:i + 3]
                i += 3
                if not skip and not (fromhtml and htmlrtf):
                    try:
                        out.append(bytes([int(hx, 16)]).decode('cp1252'))
                    except ValueError:
                        pass
            elif ch == '*':
                i += 1
            elif ch in '\r\n':
                i += 1
            else:
                j = i
                while j < n and rtf[j].isalpha():
                    j += 1
                word = rtf[i:j]
                k = j
                if k < n and (rtf[k] == '-' or rtf[k].isdigit()):
                    k += 1
                    while k < n and rtf[k].isdigit():
                        k += 1
                param = rtf[j:k]
                if k < n and rtf[k] == ' ':
                    k += 1
                i = k
                if word == 'htmlrtf':
                    htmlrtf = param != '0'
                elif word in ('fonttbl', 'colortbl', 'stylesheet', 'info', 'pict', 'header', 'footer'):
                    skip = 1
                elif word == 'uc' and param:
                    uc = int(param)
                elif word == 'u' and param and not skip:
                    v = int(param)
                    out.append(chr(v + 65536 if v < 0 else v))
                    i += uc if i < n and rtf[i:i + 1] != '\\' else 0
                elif not skip and not fromhtml:
                    if word in ('par', 'line'):
                        out.append('\n')
                    elif word == 'tab':
                        out.append('\t')
                    elif word == 'emdash':
                        out.append('\u2014')
                    elif word == 'endash':
                        out.append('\u2013')
        else:
            if c not in '\r\n' and not skip and not (fromhtml and htmlrtf and not in_tag):
                if not fromhtml or in_tag or not htmlrtf:
                    if fromhtml and not in_tag and htmlrtf:
                        pass
                    else:
                        out.append(c)
            i += 1
    text = ''.join(out)
    return ('html' if fromhtml else 'text'), text
