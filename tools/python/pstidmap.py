"""pstidmap.py - the 16-byte-ID -> NID lookup kept in node 0xC01 (a heap with client signature 0x9C).

Every folder row (hierarchy table) and message row (contents table) carries PidTagReplItemid-style cells:
  0x0E30  16-byte ID           (key of this map; value = the folder / message NID)
  0x0E33  change number (int64)
  0x0E34  version history      01000000 + 16-byte replica GUID + 01000000
SCANPST (16.0.17932) expects: a record for every live folder/message, and the NID of a record whose object
was deleted set to 0 (it zeroes them itself on repair; it does not remove the record).
Layout: heap item 0x20 = 4 bytes (HID of the BTH header); BTH key 16, value 4 (NID), keys sorted bytewise.
"""
import os, struct
import pstedit as E

IDMAP_NID = 0xC01
CLIENT = 0x9C


class IdMap:
    def __init__(self, ed):
        self.ed = ed
        e = ed.entry(IDMAP_NID)
        self.present = e is not None
        self.recs = {}
        self.dirty = False
        if e:
            hp = E.Heap(ed.leaf_blocks(e[0]))
            hh = struct.unpack('<I', hp.get(hp.root))[0]
            self.recs = {k: struct.unpack('<I', v)[0] for k, v in hp.bth(hh)[2]}
            self._by_nid = None

    def _index(self):
        if self._by_nid is None:
            self._by_nid = {}
            for k, n in self.recs.items():
                if n:
                    self._by_nid.setdefault(n, []).append(k)
        return self._by_nid

    def add(self, guid, nid):
        if not self.present:
            return
        if len(guid) != 16:
            raise E.EditError('ID must be 16 bytes')
        self.recs[guid] = nid
        if self._by_nid is not None:
            self._by_nid.setdefault(nid, []).append(guid)
        self.dirty = True

    def zero_nid(self, nid):
        """SCANPST convention for a deleted object: keep the record, set its NID to 0."""
        if not self.present:
            return 0
        n = 0
        for k in self._index().pop(nid, []):
            if self.recs.get(k) == nid:
                self.recs[k] = 0; n += 1
        if n:
            self.dirty = True
        return n

    @staticmethod
    def new_guid():
        return os.urandom(16)

    def flush(self):
        if not (self.present and self.dirty):
            return False
        ed, w = self.ed, self.ed.w
        bd, bs, par = ed.entry(IDMAP_NID)
        hb = E.HeapBuilder(CLIENT)
        h0 = hb.alloc(4)
        recs = [(k, struct.pack('<I', v)) for k, v in sorted(self.recs.items())]
        hh = E.build_bth(hb, 16, 4, recs)
        hb.set(h0, struct.pack('<I', hh))
        blocks = hb.finalize(h0)
        new_bd = ed.put_blocks(blocks)
        if bs: w.add_ref(bs)
        w.nbt.put(IDMAP_NID, struct.pack('<QQQII', IDMAP_NID, new_bd, bs, par, 0))
        w.release(bd)
        if bs: w.release(bs)
        self.dirty = False
        return True
