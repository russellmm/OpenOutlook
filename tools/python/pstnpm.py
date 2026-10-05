#!/usr/bin/env python3
"""pstnpm.py - the Name-to-ID map (node 0x61): named property id <-> (GUID, name) lookup, adding names, and id translation between stores.

Named properties (ids 0x8000 and up) have store-specific ids, so a message copied from one PST into another must have its named property
ids translated. Format (MS-PST 2.4.7): the node is a property context whose properties are
    0x0001 bucket count (normally 251)         0x0002 GUID stream (16-byte GUIDs)
    0x0003 entry stream (8-byte NAMEID records) 0x0004 string stream (length-prefixed UTF-16 names, 4-byte aligned)
    0x1000 + n   hash bucket n: NAMEID records whose dwPropertyID is the CRC-32 of the name for string names
NAMEID = dwPropertyID (numeric id, or offset into the string stream when N=1), wGuid (bit 0 = N, bits 1..15 = guid index:
1 = PS_MAPI, 2 = PS_PUBLIC_STRINGS, >= 3 = index+3 into the GUID stream), wPropIdx (property id = 0x8000 + wPropIdx).
bucket = (dwPropertyID ^ wGuid) % bucket count.

    nm = NameMap(editor)                       # read a store's map
    nm.names()                                 # {propid: (guid16 or None, is_string, id_or_name)}
    nm.lookup(guid, is_string, key)            # -> propid or None
    nm.add(guid, is_string, key)               # -> propid (appended in memory; call save(ops) to write)
    nm.save(ops)                               # rebuild node 0x61 (one PC rebuild, streams in subnodes when > 3580 bytes)
"""
import os, sys, struct, zlib

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import pstedit as E
import pstcore as P

NID_NPM = 0x61
PS_MAPI = bytes.fromhex('2903020000000000c000000000000046')
PS_PUBLIC_STRINGS = bytes.fromhex('0829030000000000c000000000000046')


def _crc(b):
    # the PST CRC (no initial / final inversion), NOT zlib.crc32: checked against all 157 string names of a real store
    return P.pst_crc(b)


class NameMap:
    def __init__(self, ed):
        self.ed = ed
        e = ed.entry(NID_NPM)
        self.present = e is not None
        self.guids, self.entries, self.strings = [], [], bytearray()
        self.buckets = 251
        self.dirty = False
        self.sub_nids = {}
        if not e:
            return
        bd, bs, _par = e
        hp = E.Heap(ed.leaf_blocks(bd))
        if hp.client != 0xBC:
            raise E.EditError('node 0x61 is not a property context')
        subs = ed.subnodes(bs) if bs else {}
        props = {}
        for k, v in hp.bth(hp.root)[2]:
            pid = struct.unpack('<H', k)[0]
            pt, hn = struct.unpack('<HI', v)
            props[pid] = (pt, hn)

        def blob(pid):
            if pid not in props:
                return b''
            _t, h = props[pid]
            if h == 0:
                return b''
            if h & 0x1F:
                self.sub_nids[pid] = h
                sd, _ss = subs[h]
                return b''.join(ed.leaf_blocks(sd))
            return bytes(hp.get(h))
        if 1 in props:
            self.buckets = props[1][1] or 251
        g = blob(2)
        self.guids = [g[i:i + 16] for i in range(0, len(g), 16)]
        en = blob(3)
        self.entries = [struct.unpack_from('<IHH', en, i) for i in range(0, len(en) - 7, 8)]
        self.strings = bytearray(blob(4))
        self._index = None

    # ---- reading ---------------------------------------------------------------------------------------------
    def _guid_of(self, wguid):
        gi = wguid >> 1
        if gi == 1:
            return PS_MAPI
        if gi == 2:
            return PS_PUBLIC_STRINGS
        if gi >= 3 and gi - 3 < len(self.guids):
            return self.guids[gi - 3]
        return None

    def _string_at(self, off):
        n = struct.unpack_from('<I', self.strings, off)[0]
        return bytes(self.strings[off + 4:off + 4 + n]).decode('utf-16-le', 'replace')

    def names(self):
        out = {}
        for dw, wg, idx in self.entries:
            n = wg & 1
            out[0x8000 + idx] = (self._guid_of(wg), bool(n), self._string_at(dw) if n else dw)
        return out

    def _idx(self):
        if self._index is None:
            self._index = {}
            for pid, (g, n, k) in self.names().items():
                self._index[(g, n, k.lower() if n else k)] = pid
        return self._index

    def lookup(self, guid, is_string, key):
        return self._idx().get((guid, bool(is_string), key.lower() if is_string else key))

    # ---- adding ----------------------------------------------------------------------------------------------
    def _guid_index(self, guid):
        if guid == PS_MAPI:
            return 1
        if guid == PS_PUBLIC_STRINGS:
            return 2
        if guid in self.guids:
            return self.guids.index(guid) + 3
        self.guids.append(guid)
        return len(self.guids) - 1 + 3

    def add(self, guid, is_string, key):
        pid = self.lookup(guid, is_string, key)
        if pid is not None:
            return pid
        if len(self.entries) >= 0x7FFF:
            raise E.EditError('named property table is full')
        wg = (self._guid_index(guid) << 1) | (1 if is_string else 0)
        if is_string:
            raw = key.encode('utf-16-le')
            dw = len(self.strings)
            self.strings += struct.pack('<I', len(raw)) + raw + bytes((-len(raw)) % 4)
        else:
            dw = key
        idx = len(self.entries)
        self.entries.append((dw, wg, idx))
        self.dirty = True
        self._index = None
        return 0x8000 + idx

    # ---- writing ---------------------------------------------------------------------------------------------
    def _bucket_records(self):
        bk = {}
        for dw, wg, idx in self.entries:
            if wg & 1:
                n = struct.unpack_from('<I', self.strings, dw)[0]
                key = _crc(bytes(self.strings[dw + 4:dw + 4 + n]))
            else:
                key = dw
            bk.setdefault((key ^ wg) % self.buckets, []).append(struct.pack('<IHH', key, wg, idx))
        return bk

    def save(self, ops):
        if not self.dirty:
            return False
        ed, w = self.ed, ops.w
        bd, bs, par = ed.entry(NID_NPM)
        streams = {2: b''.join(self.guids), 3: b''.join(struct.pack('<IHH', *e) for e in self.entries), 4: bytes(self.strings)}
        for b, recs in self._bucket_records().items():
            streams[0x1000 + b] = b''.join(recs)
        hb = E.HeapBuilder(0xBC)
        hh = hb.alloc(8)
        recs, var = [(struct.pack('<H', 1), struct.pack('<HI', 3, self.buckets))], []
        nxt = 3
        subs, next_sub = {}, max([v & ~0x1F for v in self.sub_nids.values()] + [0x400 << 5]) + 0x20
        for pid in sorted(streams):
            data = streams[pid]
            if len(data) > E.MAXALLOC:
                nid = self.sub_nids.get(pid)
                if nid is None:
                    nid = next_sub | 0x1F
                    next_sub += 0x20
                subs[nid] = data
                recs.append((struct.pack('<H', pid), struct.pack('<HI', 0x0102, nid)))
            else:
                recs.append((struct.pack('<H', pid), struct.pack('<HI', 0x0102, nxt << 5)))
                var.append((nxt << 5, data)); nxt += 1
        recs.sort(key=lambda r: struct.unpack('<H', r[0])[0])
        # heap item numbering: header (1), BTH records (2), then the values in order of allocation
        E.build_bth(hb, 2, 6, recs, hdr_hid=hh)
        for want, v in var:
            got = hb.alloc(v)
            if got != want:
                raise E.EditError('internal: heap id mismatch while rebuilding the name-to-id map')
        new_bd = ed.put_blocks(hb.finalize(hh))
        new_bs = 0
        if subs:
            ents = {}
            for nid, data in subs.items():
                chunks = [data[i:i + E.BLOCKMAX] for i in range(0, len(data), E.BLOCKMAX)] or [b'']
                ents[nid] = (ed.put_blocks(chunks), 0)
            body = struct.pack('<BBHI', 2, 0, len(ents), 0)
            for nid in sorted(ents):
                body += struct.pack('<QQQ', nid, *ents[nid])
            new_bs = w.add_block(body, internal=True)
        w.nbt.put(NID_NPM, struct.pack('<QQQII', NID_NPM, new_bd, new_bs, par, 0))
        w.release(bd)
        if bs:
            w.release(bs)
        self.dirty = False
        return True


def selftest(path):
    import pstwrite as W
    w = W.PSTWriter(path)
    try:
        ed = E.Editor(w, P.MPBB_I)
        nm = NameMap(ed)
        names = nm.names()
        bad = 0
        # every record must be in the bucket the formula says
        e = ed.entry(NID_NPM)
        hp = E.Heap(ed.leaf_blocks(e[0]))
        subs = ed.subnodes(e[1]) if e[1] else {}
        found = 0
        for k, v in hp.bth(hp.root)[2]:
            pid = struct.unpack('<H', k)[0]
            if 0x1000 <= pid < 0x1000 + nm.buckets:
                _t, h = struct.unpack('<HI', v)
                b = b''.join(ed.leaf_blocks(subs[h][0])) if h & 0x1F else bytes(hp.get(h))
                for i in range(0, len(b), 8):
                    dw, wg, idx = struct.unpack_from('<IHH', b, i)
                    key = dw
                    if (dw ^ wg) % nm.buckets != pid - 0x1000:
                        bad += 1
                    found += 1
        print('%d named properties, %d GUIDs, %d bucket records, %d in the wrong bucket' % (len(names), len(nm.guids), found, bad))
        for pid, val in list(names.items())[:3]:
            assert nm.lookup(*val) == pid
        return bad == 0
    finally:
        w.close()


if __name__ == '__main__':
    sys.exit(0 if selftest(sys.argv[1]) else 1)
