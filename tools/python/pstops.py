#!/usr/bin/env python3
"""pstops.py - message operations on a PST (stage C). WRITES to the file: work on a copy first.
  pstops.py FILE move   NID[,NID...] "Folder/Path"     move messages (NIDs from 'pstcli.py FILE list ...')
  pstops.py FILE copy   NID[,NID...] "Folder/Path"     copy messages (blocks are shared, reference-counted)
  pstops.py FILE delete NID[,NID...]                   move to Deleted Items; messages already there are removed permanently
  pstops.py FILE purge  NID[,NID...]                   permanent delete, whatever folder they are in
Add --verify to run the full (slow) structural validation after the commit.
Needs pstcore.py, pstwrite.py, pstedit.py. Close Outlook first.
Not touched: search folders and the search-update queues (Outlook rebuilds those).
"""
import os, sys, struct
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pstcore as P
import pstwrite as W
import pstedit as E
import pstidmap as M


def u32(b, o): return struct.unpack_from('<I', b, o)[0]


def find_record(hp, bth_hid, key):
    """Locate a BTH leaf record by key; returns (block index, offset of the data part) or None."""
    h = hp.get(bth_hid)
    cbk, cbe, lv, root = h[1], h[2], h[3], u32(h, 4)

    def walk(x, level):
        blk, s, e = hp.loc(x)
        d = hp.blocks[blk][s:e]
        if level == 0:
            sz = cbk + cbe
            for i in range(len(d) // sz):
                if d[i * sz:i * sz + cbk] == key:
                    return blk, s + i * sz + cbk
        else:
            sz = cbk + 4
            for i in range(len(d) // sz):
                r = walk(u32(d, i * sz + cbk), level - 1)
                if r: return r
        return None
    return walk(root, lv) if root else None


def _grow_heap_item(blocks, hid, newdata):
    """Replace heap item `hid` by `newdata` (any size) inside its block, shifting the later items and rewriting the page
    map and the fill-level nibble. Returns False (nothing changed) if the block has no room."""
    hp = E.Heap(blocks)
    blk, _s, _e = hp.loc(hid)
    b = blocks[blk]
    idx = ((hid >> 5) & 0x7FF) - 1
    pm = struct.unpack_from('<H', b, 0)[0]
    ca, cf = struct.unpack_from('<HH', b, pm)
    offs = list(struct.unpack_from('<%dH' % (ca + 1), b, pm + 4))
    items = [bytes(b[offs[i]:offs[i + 1]]) for i in range(ca)]
    items[idx] = bytes(newdata)
    body = bytearray(b[:offs[0]])
    new_offs = [offs[0]]
    for it in items:
        body += it
        new_offs.append(len(body))
    if len(body) & 1:
        body += b'\0'
    newpm = len(body)
    body += struct.pack('<HH', ca, cf) + b''.join(struct.pack('<H', o) for o in new_offs)
    if len(body) > E.BLOCKMAX:
        return False
    fill = E._fill_level(E.BLOCKMAX - len(body))
    struct.pack_into('<H', body, 0, newpm)
    if len(b) == E.BLOCKMAX:
        body += bytes(E.BLOCKMAX - len(body))
    blocks[blk][:] = body
    if blk < 8:
        v = struct.unpack_from('<I', blocks[0], 8)[0]
        v = (v & ~(0xF << (4 * blk))) | (fill << (4 * blk))
        struct.pack_into('<I', blocks[0], 8, v)
    else:
        base = 8 + ((blk - 8) // 128) * 128
        pos = blk - base
        o = 2 + (pos >> 1)
        sh = 4 * (pos & 1)
        blocks[base][o] = (blocks[base][o] & ~(0xF << sh)) | (fill << sh)
    return True



def _set_fill(blocks, blk, fill):
    if blk < 8:
        v = struct.unpack_from('<I', blocks[0], 8)[0]
        v = (v & ~(0xF << (4 * blk))) | (fill << (4 * blk))
        struct.pack_into('<I', blocks[0], 8, v)
    else:
        base = 8 + ((blk - 8) // 128) * 128
        pos = blk - base
        o = 2 + (pos >> 1)
        sh = 4 * (pos & 1)
        blocks[base][o] = (blocks[base][o] & ~(0xF << sh)) | (fill << sh)


def _append_heap_item(blocks, data):
    """Allocate a new heap item at the end of the heap (in the last block, or in a new block) without moving any existing
    item, so every existing HID stays valid. Returns the new HID."""
    data = bytes(data)
    L = len(blocks) - 1
    b = blocks[L]
    pm = struct.unpack_from('<H', b, 0)[0]
    ca, cf = struct.unpack_from('<HH', b, pm)
    offs = list(struct.unpack_from('<%dH' % (ca + 1), b, pm + 4))
    body = bytearray(b[:offs[-1]]) + data
    offs.append(len(body))
    if len(body) & 1:
        body += b'\0'
    newpm = len(body)
    body += struct.pack('<HH', ca + 1, cf) + b''.join(struct.pack('<H', o) for o in offs)
    if len(body) <= E.BLOCKMAX and ca + 1 <= 2046:
        fill = E._fill_level(E.BLOCKMAX - len(body))
        struct.pack_into('<H', body, 0, newpm)
        if len(b) == E.BLOCKMAX:
            body += bytes(E.BLOCKMAX - len(body))
        blocks[L][:] = body
        _set_fill(blocks, L, fill)
        return (L << 16) | ((ca + 1) << 5)
    nb = L + 1
    if nb > 0xFFFF or (nb >= 8 and (nb - 8) % 128 == 0):
        raise E.EditError('message index heap needs a block kind that is not supported yet')
    if len(blocks[L]) < E.BLOCKMAX:                      # a non-final block must be full
        blocks[L].extend(bytes(E.BLOCKMAX - len(blocks[L])))
    body = bytearray(2) + data
    if len(body) & 1:
        body += b'\0'
    newpm = len(body)
    body += struct.pack('<HH', 1, 0) + struct.pack('<HH', 2, 2 + len(data))
    struct.pack_into('<H', body, 0, newpm)
    fill = E._fill_level(E.BLOCKMAX - len(body))
    blocks.append(bytearray(body))
    _set_fill(blocks, nb, fill)
    return (nb << 16) | (1 << 5)


def _bth_repoint(blocks, bth_hid, old_hid, new_hid):
    """In the BTH whose header is heap item `bth_hid`, change the 4-byte value `old_hid` to `new_hid` (in place)."""
    hp = E.Heap(blocks)
    h = hp.get(bth_hid)
    cbk, cbe, lv = h[1], h[2], h[3]
    root = struct.unpack_from('<I', h, 4)[0]
    stack = [(root, lv)]
    while stack:
        x, level = stack.pop()
        d = hp.get(x)
        blk, s, _e = hp.loc(x)
        if level == 0:
            sz = cbk + cbe
            for i in range(len(d) // sz):
                if cbe == 4 and struct.unpack_from('<I', d, i * sz + cbk)[0] == old_hid:
                    struct.pack_into('<I', blocks[blk], s + i * sz + cbk, new_hid)
                    return True
        else:
            sz = cbk + 4
            for i in range(len(d) // sz):
                stack.append((struct.unpack_from('<I', d, i * sz + cbk)[0], level - 1))
    return False


def _bth_insert(blocks, key, val):
    """Insert (key -> 4-byte value) into the BTH at heap item 0x40 (4-byte keys, 4-byte values, any number of index levels):
    sorted insertion, node split when a node would exceed the maximum heap allocation, index keys kept, root grown when needed.
    Heap ids of existing items never change."""
    hp = E.Heap(blocks)
    h = bytes(hp.get(0x40))
    if h[0] != 0xB5 or h[1] != 4 or h[2] != 4:
        raise E.EditError('message index BTH has an unexpected shape')
    lv = h[3]
    root = struct.unpack_from('<I', h, 4)[0]
    path = []
    cur, level = root, lv
    while True:
        d = bytes(hp.get(cur))
        if level == 0:
            path.append((cur, level, d, 0))
            break
        n = len(d) // 8
        pos = 0
        for i in range(n):
            if struct.unpack_from('<I', d, i * 8)[0] <= key:
                pos = i
        path.append((cur, level, d, pos))
        cur = struct.unpack_from('<I', d, pos * 8 + 4)[0]
        level -= 1
    # the leaf
    cur, level, d, _p = path[-1]
    ents = [d[i:i + 8] for i in range(0, len(d), 8)]
    kk = [struct.unpack_from('<I', e)[0] for e in ents]
    if key in kk:
        raise E.EditError('message index key 0x%08x already present' % key)
    at = sum(1 for k in kk if k < key)
    ents.insert(at, struct.pack('<II', key, val))

    def store(i, entries):
        hid = path[i][0]
        data = b''.join(entries)
        parts = [entries]
        if len(data) > E.MAXALLOC:
            half = len(entries) // 2
            parts = [entries[:half], entries[half:]]
        res = []
        for j, part in enumerate(parts):
            pdata = b''.join(part)
            if j == 0:
                if not _grow_heap_item(blocks, hid, pdata):
                    hid = _append_heap_item(blocks, pdata)
                nh = hid
            else:
                nh = _append_heap_item(blocks, pdata)
            res.append((struct.unpack_from('<I', part[0])[0], nh))
        return res
    res = store(len(path) - 1, ents)
    for i in range(len(path) - 2, -1, -1):
        d, pos = path[i][2], path[i][3]
        pe = [d[j:j + 8] for j in range(0, len(d), 8)]
        pe[pos:pos + 1] = [struct.pack('<II', k, v) for k, v in res]
        res = store(i, pe)
    hp = E.Heap(blocks)
    blk, s, _e = hp.loc(0x40)
    if len(res) == 1:
        if res[0][1] != root:
            struct.pack_into('<I', blocks[blk], s + 4, res[0][1])
    else:
        nr = _append_heap_item(blocks, b''.join(struct.pack('<II', k, v) for k, v in res))
        hp = E.Heap(blocks)
        blk, s, _e = hp.loc(0x40)
        struct.pack_into('<I', blocks[blk], s + 4, nr)
        blocks[blk][s + 3] = lv + 1


def conv_key(guid):
    """Key of a message's bucket in the message index (node 0xE01), found with SCANPST as an oracle (14,578 of 14,582 real keys, 167 of 170 probes):
    0xFFFF0000 xor (xor over the 16 bytes of the conversation GUID, byte j shifted left by 15 - j). The GUID is bytes 6..22 of PidTagConversationIndex."""
    h = 0
    for j, b in enumerate(guid):
        h ^= b << (15 - j)
    return 0xFFFF0000 ^ h


def blocks_conv_key(blocks):
    """conv_key of a message node's blocks, or None when it has no conversation index of at least 22 bytes."""
    hp = E.Heap(blocks)
    if hp.client != 0xBC:
        return None
    r = find_record(hp, hp.root, struct.pack('<H', 0x71))
    if r is None:
        return None
    blk, off = r
    hid = struct.unpack_from('<I', blocks[blk], off + 2)[0]
    if hid == 0 or hid & 0x1F:
        return None
    b, s, e = hp.loc(hid)
    if e - s < 22:
        return None
    return conv_key(bytes(blocks[b][s + 6:s + 22]))


class Ops:
    def __init__(self, path):
        self.path = path
        self.st = P.Store(path)                      # read-only view, used for name lookups only
        self.w = W.PSTWriter(path)
        self.ed = E.Editor(self.w, P.MPBB_I)
        raw = self.st.store_pc.raw(0x35E3) if self.st.store_pc else None
        self.deleted_nid = u32(raw, len(raw) - 4) if raw else None
        self._idmap = None

    @property
    def idmap(self):
        if self._idmap is None:
            self._idmap = M.IdMap(self.ed)
        return self._idmap

    def folder(self, path):
        return self.st.find_folder(path).nid

    def _entry(self, nid, bd, bs, par):
        return struct.pack('<QQQII', nid, bd, bs, par, 0)

    # ---- counters --------------------------------------------------------------
    def pc_add(self, nid, pid, delta):
        ed = self.ed
        bd, bs, par = ed.entry(nid)
        blocks = [bytearray(b) for b in ed.leaf_blocks(bd)]
        hp = E.Heap(blocks)
        r = find_record(hp, hp.root, struct.pack('<H', pid))
        if r is None:
            return False
        blk, off = r
        if struct.unpack_from('<H', blocks[blk], off)[0] != 0x0003:
            raise E.EditError('property 0x%04x of node 0x%x is not an int32' % (pid, nid))
        v = struct.unpack_from('<i', blocks[blk], off + 2)[0]
        struct.pack_into('<i', blocks[blk], off + 2, max(0, v + delta))
        new_bd = ed.put_blocks([bytes(b) for b in blocks])
        self.w.nbt.put(nid, self._entry(nid, new_bd, bs, par))
        self.w.release(bd)
        return True

    def hier_add(self, folder_nid, d_cnt, d_unread):
        ed = self.ed
        par = ed.entry(folder_nid)[2]
        hn = (par & ~0x1F) | 0x0D
        if ed.entry(hn) is None:
            return
        tc = ed.load_tc(hn)
        i = tc.find(folder_nid)
        if i < 0:
            return
        cells, changed = tc.rows[i][1], False
        for pid, d in ((0x3602, d_cnt), (0x3603, d_unread)):
            ci = tc.col_by_pid.get(pid)
            if d and ci is not None and ci in cells:
                v = struct.unpack('<i', cells[ci][:4])[0]
                cells[ci] = struct.pack('<i', max(0, v + d))
                changed = True
        if changed:
            vi = tc.col_by_pid.get(0x67F3)
            if vi is not None and vi in cells:
                cells[vi] = struct.pack('<I', self.next_row_ver())
            ed.store_tc(hn, tc)

    def next_row_ver(self):
        """PidTagLtpRowVer values must be unique across the whole store (SCANPST reports 'minor inconsistencies' when two
        rows share one; copies used to inherit the source row's value). Outlook draws them from a store-wide counter that
        stays below the header's dwUnique; so do we: bump dwUnique's in-memory value and use it."""
        self.w.unique = (self.w.unique + 1) & 0xFFFFFFFF
        return self.w.unique

    def counts(self, folder_nid, d_cnt, d_unread):
        if d_cnt: self.pc_add(folder_nid, 0x3602, d_cnt)
        if d_unread: self.pc_add(folder_nid, 0x3603, d_unread)
        self.hier_add(folder_nid, d_cnt, d_unread)

    @staticmethod
    def _unread(vals):
        f = vals.get(0x0E07)
        return bool(f is not None and len(f) >= 4 and not (struct.unpack('<i', f[:4])[0] & 1))

    def _group(self, nids):
        groups = {}
        for n in nids:
            e = self.ed.entry(n)
            if e is None:
                raise E.EditError('message 0x%x not found' % n)
            if n & 0x1F != 4:
                raise E.EditError('0x%x is not a normal message' % n)
            groups.setdefault(e[2], []).append(n)
        return groups

    # ---- operations ------------------------------------------------------------
    def move(self, nids, dest):
        ed, w = self.ed, self.w
        groups = self._group(nids)
        dtc_nid = (dest & ~0x1F) | 0x0E
        dtc = ed.load_tc(dtc_nid)
        d_cnt = d_unread = 0
        for src, lst in groups.items():
            if src == dest:
                continue
            stc_nid = (src & ~0x1F) | 0x0E
            stc = ed.load_tc(stc_nid)
            s_unread = 0
            for n in lst:
                vals = stc.values(n)
                stc.remove(n)
                dtc.add_by_pid(n, vals)
                bd, bs, _par = ed.entry(n)
                w.nbt.put(n, self._entry(n, bd, bs, dest))
                u = self._unread(vals)
                s_unread += u; d_unread += u; d_cnt += 1
            ed.store_tc(stc_nid, stc)
            self.counts(src, -len(lst), -s_unread)
        if d_cnt:
            ed.store_tc(dtc_nid, dtc)
            self.counts(dest, d_cnt, d_unread)
        return d_cnt

    @staticmethod
    def _fresh_keys(blocks):
        """A copy must not carry the original's identity: new PidTagSearchKey (0x300B, 16 B), new change key
        (0x65E2 = replica GUID + 6-byte counter) and the matching predecessor list (0x65E3 = 0x16 + the same XID)."""
        hp = E.Heap(blocks)
        if hp.client != 0xBC:
            return

        def slot(pid, size):
            r = find_record(hp, hp.root, struct.pack('<H', pid))
            if r is None: return None
            blk, off = r
            hid = struct.unpack_from('<I', blocks[blk], off + 2)[0]
            if hid == 0 or hid & 0x1F: return None
            b, s, e = hp.loc(hid)
            return (b, s) if e - s == size else None
        s_key = slot(0x300B, 16)
        if s_key:
            b, s = s_key; blocks[b][s:s + 16] = os.urandom(16)
        s_ck = slot(0x65E2, 22)
        if s_ck:
            b, s = s_ck
            old = bytes(blocks[b][s:s + 22])
            newc = (int.from_bytes(old[16:], 'big') + 1 + int.from_bytes(os.urandom(2), 'big')).to_bytes(6, 'big')
            new = old[:16] + newc
            blocks[b][s:s + 22] = new
            s_pl = slot(0x65E3, 23)
            if s_pl:
                b2, s2 = s_pl
                if bytes(blocks[b2][s2 + 1:s2 + 23]) == old:
                    blocks[b2][s2 + 1:s2 + 23] = new

    def copy(self, nids, dest, share=False, ids=True, fresh=False):
        """Copy messages. Default (verified clean in SCANPST, test_Y1/Y2): the message data is its own cloned block, the copy gets a
        unique row version, and its contents row gets ID cells 0x0E30/0E33/0E34 plus an ID-map record. Outlook itself leaves
        the ID cells off (ids=False), and SCANPST then reports 'minor inconsistencies' and adds them when repairing.
        share=True shares every block with the original (reference counted); ids=True gives the row new ID cells and an
        ID-map record; fresh=True also renews the search key / change key inside the message."""
        ed, w = self.ed, self.w
        groups = self._group(nids)
        dtc_nid = (dest & ~0x1F) | 0x0E
        dtc = ed.load_tc(dtc_nid)
        cnt = unread = 0
        new_nids = []
        pairs = []
        for src, lst in groups.items():
            stc = ed.load_tc((src & ~0x1F) | 0x0E)
            for n in lst:
                vals = stc.values(n)
                idx = struct.unpack_from('<I', w.hdr, 44 + 4 * 4)[0] + 1
                struct.pack_into('<I', w.hdr, 44 + 4 * 4, idx)
                new = (idx << 5) | 4
                vals[0x67F2] = struct.pack('<I', new)
                if 0x67F3 in vals:
                    vals[0x67F3] = struct.pack('<I', self.next_row_ver())
                bd, bs, _par = ed.entry(n)
                if share:
                    w.add_ref(bd)
                    if bs: w.add_ref(bs)
                    nbd, nbs = bd, bs
                else:
                    nbd, nbs = ed.clone_node(bd, bs, self._fresh_keys if fresh else None)
                w.nbt.put(new, self._entry(new, nbd, nbs, dest))
                if ids and 0x0E30 in vals and self.idmap.present:
                    g = M.IdMap.new_guid(); vals[0x0E30] = g; self.idmap.add(g, new)
                    if 0x0E33 in vals:
                        vals[0x0E33] = struct.pack('<Q', w.bid_next_b)
                else:
                    for pid in (0x0E30, 0x0E33, 0x0E34):
                        vals.pop(pid, None)
                dtc.add_by_pid(new, vals)
                cnt += 1; unread += self._unread(vals); new_nids.append(new); pairs.append((n, new))
        ed.store_tc(dtc_nid, dtc)
        self.counts(dest, cnt, unread)
        keyed, rest = self.keys_of(new_nids, pairs)
        self.note_max_message_nid(rest, keyed=keyed)
        return new_nids

    def keys_of(self, nids, pairs=()):
        """([(bucket key, nid)] for messages that have a conversation index, [(source, nid)] pairs for the others)."""
        keyed, rest = [], []
        src = {n: sn for sn, n in pairs}
        for n in nids:
            e = self.ed.entry(n)
            k = blocks_conv_key([bytearray(b) for b in self.ed.leaf_blocks(e[0])]) if e else None
            if k is None:
                if n in src:
                    rest.append((src[n], n))
            else:
                keyed.append((k, n))
        return keyed, rest

    def note_max_message_nid(self, pairs=(), groups=(), keyed=()):
        """Node 0xE01 (a heap with client signature 0xCC) is Outlook's index of message NIDs. Its root item (offset 8)
        holds the highest message NID. Item 0x40 is a BTH (4-byte key -> 4-byte HID); every value points at a list of
        NIDs: messages that belong together (Outlook puts a copy into the same bucket as the message it was copied
        from, appended at the end; seen in test_O.pst and test_P1.pst). pairs = [(source_nid, new_nid)] adds copies."""
        ed, w = self.ed, self.w
        e = ed.entry(0xE01)
        if e is None:
            return False
        bd, bs, par = e
        blocks = [bytearray(b) for b in ed.leaf_blocks(bd)]
        hp = E.Heap(blocks)
        if hp.client != 0xCC:
            return False
        changed = False
        blk, s, _e = hp.loc(hp.root)
        cur = struct.unpack_from('<I', blocks[blk], s + 8)[0]
        top = max((n for n in self._all_message_nids()), default=cur)
        if top > cur:
            struct.pack_into('<I', blocks[blk], s + 8, top)
            changed = True
        if pairs:
            try:
                _k, _v, recs = hp.bth(0x40)
            except Exception:
                recs = []
            lists = {}
            for _key, val in recs:
                hid = struct.unpack('<I', val)[0]
                try:
                    lists[hid] = hp.get(hid)
                except Exception:
                    pass
            for srcn, newn in pairs:
                for hid, lst in list(lists.items()):
                    ns = struct.unpack('<%dI' % (len(lst) // 4), lst)
                    if newn in ns:
                        break
                    if srcn in ns:
                        newlst = bytes(lst) + struct.pack('<I', newn)
                        if _grow_heap_item(blocks, hid, newlst):
                            lists[hid] = newlst
                            changed = True
                        else:                    # no room in its block: move the list to the end of the heap
                            nh = _append_heap_item(blocks, newlst)
                            if not _bth_repoint(blocks, 0x40, hid, nh):
                                raise E.EditError('message index: bucket 0x%x not found in its BTH' % hid)
                            del lists[hid]
                            lists[nh] = newlst
                            changed = True
                        break
        if keyed:
            try:
                _k, _v, recs = E.Heap(blocks).bth(0x40)
            except Exception:
                recs = []
            keymap = {struct.unpack('<I', k)[0]: struct.unpack('<I', v)[0] for k, v in recs}
            for key, newn in keyed:
                hid = keymap.get(key)
                if hid is None:                                  # no bucket with this key yet: a new one holding just this message
                    lh = _append_heap_item(blocks, struct.pack('<I', newn))
                    _bth_insert(blocks, key, lh)
                    keymap[key] = lh
                    changed = True
                    continue
                lst = bytes(E.Heap(blocks).get(hid))
                if newn in struct.unpack('<%dI' % (len(lst) // 4), lst):
                    continue
                newlst = lst + struct.pack('<I', newn)
                if _grow_heap_item(blocks, hid, newlst):
                    changed = True
                else:                                            # no room in its block: move the list to the end of the heap
                    nh = _append_heap_item(blocks, newlst)
                    if not _bth_repoint(blocks, 0x40, hid, nh):
                        raise E.EditError('message index: bucket 0x%x not found in its BTH' % hid)
                    keymap[key] = nh
                    changed = True
        if groups:
            import zlib
            used = {struct.unpack('<I', k)[0] for k, _v in E.Heap(blocks).bth(0x40)[2]}
            for topic, nids in groups:
                key = 0xFF800000 | (zlib.crc32(bytes(topic).lower()) & 0x7FFFFF)
                while key in used:
                    key = 0xFF800000 | ((key + 1) & 0x7FFFFF)
                used.add(key)
                lh = _append_heap_item(blocks, b''.join(struct.pack('<I', n) for n in nids))
                _bth_insert(blocks, key, lh)
                changed = True
        if not changed:
            return False
        new_bd = ed.put_blocks([bytes(b) for b in blocks])
        w.nbt.put(0xE01, self._entry(0xE01, new_bd, bs, par))
        w.release(bd)
        return True

    def bump_hwm(self, nids):
        """Raise the header NID high-water marks (header offset 44 + 4 * type = highest index used) to cover `nids`
        (sub-node ids such as attachments, type 5, and 0x1F streams are checked by SCANPST against everything in the file)."""
        w = self.w
        for n in nids:
            t, i = n & 0x1F, n >> 5
            if t in (5, 0x1F):
                cur = struct.unpack_from('<I', w.hdr, 44 + 4 * t)[0]
                if i > cur:
                    struct.pack_into('<I', w.hdr, 44 + 4 * t, i)

    def _all_message_nids(self):
        for v in self.w.nbt.items():
            n = struct.unpack_from('<Q', v, 0)[0]
            if n & 0x1F == 4:
                yield n

    def purge(self, nids):
        ed, w = self.ed, self.w
        for src, lst in self._group(nids).items():
            stc_nid = (src & ~0x1F) | 0x0E
            stc = ed.load_tc(stc_nid)
            unread = 0
            for n in lst:
                self.idmap.zero_nid(n)
                if stc.find(n) >= 0:
                    unread += self._unread(stc.values(n))
                    stc.remove(n)
                bd, bs, _par = ed.entry(n)
                w.nbt.delete(n)
                w.release(bd)
                if bs: w.release(bs)
            ed.store_tc(stc_nid, stc)
            self.counts(src, -len(lst), -unread)
        return len(nids)

    def delete(self, nids):
        """Outlook semantics: first delete moves to Deleted Items; deleting there is permanent."""
        if self.deleted_nid is None:
            raise E.EditError('Deleted Items folder not found in the store')
        in_deleted = [n for n in nids if self.ed.entry(n) and self.ed.entry(n)[2] == self.deleted_nid]
        other = [n for n in nids if n not in in_deleted]
        if other: self.move(other, self.deleted_nid)
        if in_deleted: self.purge(in_deleted)
        return len(other), len(in_deleted)

    def commit(self):
        if self._idmap is not None:
            self._idmap.flush()
        self.w.commit()

    def close(self):
        self.w.close()


def main(a):
    verify = '--verify' in a
    a = [x for x in a if x != '--verify']
    if len(a) < 3:
        print(__doc__); return 2
    path, cmd = a[0], a[1]
    nids = [int(x, 0) for x in a[2].split(',')]
    ops = Ops(path)
    try:
        if cmd == 'move':
            n = ops.move(nids, ops.folder(a[3])); print('moved %d message(s)' % n)
        elif cmd == 'copy':
            new = ops.copy(nids, ops.folder(a[3])); print('copied; new NIDs:', ', '.join(hex(x) for x in new))
        elif cmd == 'delete':
            m, p = ops.delete(nids); print('moved to Deleted Items: %d, permanently deleted: %d' % (m, p))
        elif cmd == 'purge':
            print('permanently deleted %d message(s)' % ops.purge(nids))
        else:
            print(__doc__); return 2
        ops.commit()
        print('committed.')
        if verify:
            p = ops.w.validate(); print('validate: %d problems' % len(p), p[:5])
    except Exception:
        ops.w.rollback_pending()
        raise
    finally:
        ops.close()
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
