#!/usr/bin/env python3
"""pstfolders.py - folder operations on a PST (stage D). WRITES to the file: work on a copy first.
  pstfolders.py FILE create "Parent/Path" "New name" [CONTAINERCLASS]   default class IPF.Note; "" = root-level parent
  pstfolders.py FILE rename "Folder/Path" "New name"
  pstfolders.py FILE move   "Folder/Path" "New/Parent/Path"
  pstfolders.py FILE delete "Folder/Path"      move to Deleted Items; a folder already inside Deleted Items is removed permanently
  pstfolders.py FILE purge  "Folder/Path"      permanent delete, wherever it is
Add --verify to run the (slow) structural validation after the commit.
Special folders (root, top of store, Deleted Items, search/views roots, default-folder entry ids) are protected.
Not touched: search folders and search-update queues. Needs pstcore, pstwrite, pstedit, pstops. Close Outlook first.
"""
import os, sys, struct
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pstcore as P
import pstedit as E
import pstops as O
import pstidmap as M

NID_ROOT = 0x122
T_HIER, T_CONT, T_FAI = 0x0D, 0x0E, 0x0F


def u16s(b):
    return b.decode('utf-16-le', 'replace')


def enc16(s):
    return s.encode('utf-16-le')


class FolderOps(O.Ops):
    # ---- helpers ---------------------------------------------------------------
    def _base(self, nid): return nid & ~0x1F

    def parent_of(self, nid):
        e = self.ed.entry(nid)
        if e is None:
            raise E.EditError('folder 0x%x not found' % nid)
        return e[2]

    def children(self, nid):
        """[(child_nid, name)] from the folder's hierarchy table."""
        hn = self._base(nid) | T_HIER
        if self.ed.entry(hn) is None:
            return []
        tc = self.ed.load_tc(hn)
        ci = tc.col_by_pid.get(0x3001)
        out = []
        for rid, cells in tc.rows:
            out.append((rid, u16s(cells[ci]) if ci is not None and ci in cells else ''))
        return out

    def name_of(self, nid):
        par = self.parent_of(nid)
        for c, n in self.children(par):
            if c == nid:
                return n
        raise E.EditError('folder 0x%x is not listed in its parent' % nid)

    def ancestors(self, nid):
        out = []
        while nid != NID_ROOT:
            out.append(nid)
            nid = self.parent_of(nid)
            if len(out) > 256:
                raise E.EditError('folder hierarchy loop')
        out.append(NID_ROOT)
        return out                      # nid, parent, ..., root

    def protected(self):
        """NIDs that may not be moved/deleted/renamed."""
        prot = {NID_ROOT}
        pc = self.st.store_pc
        for pid in range(0x35E0, 0x35E8):
            raw = pc.raw(pid) if pc else None
            if raw and len(raw) >= 4:
                prot.add(u32(raw, len(raw) - 4))
        ipm = [n for n in prot if n != NID_ROOT and self.ed.entry(n)]
        for fn in [NID_ROOT] + ipm:                       # default-folder entry ids on root / IPM subtree
            try:
                props = self.pc_props(fn)
            except E.EditError:
                continue
            for pid in range(0x36D0, 0x36D8):
                if pid in props and len(props[pid][1]) >= 4:
                    prot.add(u32(props[pid][1], len(props[pid][1]) - 4))
        for c, _n in self.children(NID_ROOT):             # everything directly under the root is structural
            prot.add(c)
        prot.discard(0)
        return prot

    def _check_user_folder(self, nid):
        if nid & 0x1F != 2:
            raise E.EditError('0x%x is not a normal folder' % nid)
        if nid in self.protected():
            raise E.EditError('folder 0x%x is a special folder and cannot be changed' % nid)

    def _check_name(self, name):
        if not name or name != name.strip() or any(c in name for c in '/\\\0') or len(name) > 255:
            raise E.EditError('invalid folder name %r' % name)

    def _sibling_clash(self, parent, name, ignore=None):
        for c, n in self.children(parent):
            if c != ignore and n.lower() == name.lower():
                return True
        return False

    # ---- property context (generic rebuild) -----------------------------------
    def pc_props(self, nid):
        """{pid: (ptype, raw bytes)} of a property context node (heap-stored values only)."""
        e = self.ed.entry(nid)
        if e is None:
            raise E.EditError('node 0x%x not found' % nid)
        hp = E.Heap(self.ed.leaf_blocks(e[0]))
        if hp.client != 0xBC:
            raise E.EditError('node 0x%x is not a property context' % nid)
        out = {}
        for k, v in hp.bth(hp.root)[2]:
            pid = struct.unpack('<H', k)[0]
            pt, val = struct.unpack('<HI', v)
            if pt in E.FIXED and E.FIXED[pt] <= 4:
                out[pid] = (pt, val.to_bytes(4, 'little')[:E.FIXED[pt]])
            elif val == 0:
                out[pid] = (pt, b'')
            elif val & 0x1F:
                raise E.EditError('property 0x%04x of node 0x%x is stored in a subnode (unsupported)' % (pid, nid))
            else:
                out[pid] = (pt, bytes(hp.get(val)))
        return out

    @staticmethod
    def pc_build(props):
        hb = E.HeapBuilder(0xBC)
        n = len(props)
        if n == 0 or n * 8 > E.MAXALLOC:
            raise E.EditError('property count out of range')
        hh = hb.alloc(8)
        recs, nxt = [], 3                       # index of next heap item after header (1) and records (2)
        var = []
        for pid in sorted(props):
            pt, v = props[pid]
            if pt in E.FIXED and E.FIXED[pt] <= 4:
                val = int.from_bytes(v.ljust(4, b'\0')[:4], 'little')
            elif not v:
                val = 0
            else:
                val = nxt << 5; nxt += 1; var.append((val, v))
            recs.append((struct.pack('<H', pid), struct.pack('<HI', pt, val)))
        E.build_bth(hb, 2, 6, recs, hdr_hid=hh)
        for want, v in var:
            got = hb.alloc(v)
            if got != want:
                raise E.EditError('internal: heap id mismatch while building a property context')
        return hb.finalize(hh)

    def pc_store(self, nid, props):
        bd, bs, par = self.ed.entry(nid)
        new_bd = self.ed.put_blocks(self.pc_build(props))
        if bs: self.w.add_ref(bs)
        self.w.nbt.put(nid, self._entry(nid, new_bd, bs, par))
        self.w.release(bd)
        if bs: self.w.release(bs)

    def pc_set(self, nid, pid, ptype, raw):
        props = self.pc_props(nid)
        props[pid] = (ptype, raw)
        self.pc_store(nid, props)

    # ---- hierarchy table helpers ------------------------------------------------
    def _hier_set_cell(self, folder_nid, pid, raw):
        """Update a cell of folder_nid's row in its parent's hierarchy table."""
        hn = self._base(self.parent_of(folder_nid)) | T_HIER
        tc = self.ed.load_tc(hn)
        i, ci = tc.find(folder_nid), tc.col_by_pid.get(pid)
        if i < 0 or ci is None:
            return
        tc.rows[i][1][ci] = raw
        self.ed.store_tc(hn, tc)

    def _set_has_sub(self, nid):
        has = 1 if self.children(nid) else 0
        props = self.pc_props(nid)
        if 0x360A in props and props[0x360A][1][:1] == bytes([has]):
            return
        self.pc_set(nid, 0x360A, 0x000B, bytes([has]))
        if nid != NID_ROOT and self.ed.entry(nid):
            self._hier_set_cell(nid, 0x360A, bytes([has]))

    def _replica_blob(self, folder):
        """0x0E34 cell of any existing folder row, searching the folder's own table, then its ancestors'."""
        for f in self.ancestors(folder):
            hn = self._base(f) | T_HIER
            if not self.ed.entry(hn):
                continue
            tc = self.ed.load_tc(hn)
            ci = tc.col_by_pid.get(0x0E34)
            for _r, c in tc.rows:
                if ci is not None and ci in c and c[ci]:
                    return c[ci]
        return None

    def _row_ver(self, tc):
        self.w.unique = (self.w.unique + 1) & 0xFFFFFFFF      # store-wide unique row versions (see Ops.next_row_ver)
        return self.w.unique

    # ---- operations -------------------------------------------------------------
    def create(self, parent, name, cls='IPF.Note'):
        ed, w = self.ed, self.w
        self._check_name(name)
        if parent & 0x1F != 2 or ed.entry(parent) is None:
            raise E.EditError('parent 0x%x is not a folder' % parent)
        if parent == NID_ROOT:
            raise E.EditError('create folders below "Top of ..." rather than at the store root')
        if self._sibling_clash(parent, name):
            raise E.EditError('a folder named %r already exists there' % name)
        off = 44 + 4 * 2
        idx = struct.unpack_from('<I', w.hdr, off)[0] + 1
        nid = (idx << 5) | 2
        if any(ed.entry((idx << 5) | t) for t in (2, T_HIER, T_CONT, T_FAI, 6, 7, 0x10)):
            raise E.EditError('folder index 0x%x already in use' % idx)
        struct.pack_into('<I', w.hdr, off, idx)
        for t in (T_HIER, T_CONT, T_FAI):                 # derived table types share the folder index
            o = 44 + 4 * t
            if struct.unpack_from('<I', w.hdr, o)[0] < idx:
                struct.pack_into('<I', w.hdr, o, idx)

        # property context
        z4 = b'\0\0\0\0'
        props = {0x3001: (0x001F, enc16(name)), 0x3602: (3, z4), 0x3603: (3, z4), 0x360A: (0x000B, b'\0'),
                 0x6635: (3, z4), 0x6636: (3, z4)}
        if cls:
            props[0x3613] = (0x001F, enc16(cls))
        w.nbt.put(nid, self._entry(nid, ed.put_blocks(self.pc_build(props)), 0, parent))

        # empty tables, layouts copied from a sibling / the parent / Deleted Items
        order = [c for c, _n in self.children(parent) if c & 0x1F == 2] + [parent, self.deleted_nid]
        for t in (T_HIER, T_CONT, T_FAI):
            tmpl = next((self._base(c) | t for c in order if c and ed.entry(self._base(c) | t)), None)
            if tmpl is None:
                raise E.EditError('no template table of type 0x%x found' % t)
            tbd, tbs, _p = ed.entry(tmpl)
            tc = ed.load_tc(tmpl)
            if not tc.rows and not tbs:
                w.add_ref(tbd); bd = tbd                       # Outlook shares identical empty tables
            else:
                tc.rows = []
                tc.rows_nid = 0
                bd = ed.put_blocks(tc.build()[0])
            w.nbt.put(self._base(nid) | t, self._entry(self._base(nid) | t, bd, 0, 0))

        # row in the parent's hierarchy table
        hn = self._base(parent) | T_HIER
        tc = ed.load_tc(hn)
        cells = {0x3001: enc16(name), 0x3602: z4, 0x3603: z4, 0x360A: b'\0', 0x6635: z4, 0x6636: z4,
                 0x67F2: struct.pack('<I', nid), 0x67F3: struct.pack('<I', self._row_ver(tc))}
        if cls:
            cells[0x3613] = enc16(cls)
        if self.idmap.present and tc.col_by_pid.get(0x0E30) is not None:
            guid = M.IdMap.new_guid()
            cells[0x0E30] = guid
            cells[0x0E33] = struct.pack('<Q', w.bid_next_b)             # change number: any increasing value
            blob = self._replica_blob(parent)                           # replica blob copied from an existing row
            if blob:
                cells[0x0E34] = blob
            self.idmap.add(guid, nid)
        tc.add_by_pid(nid, cells)
        ed.store_tc(hn, tc)
        self._set_has_sub(parent)
        return nid

    def rename(self, nid, name):
        self._check_user_folder(nid)
        self._check_name(name)
        par = self.parent_of(nid)
        if self._sibling_clash(par, name, ignore=nid):
            raise E.EditError('a folder named %r already exists there' % name)
        self.pc_set(nid, 0x3001, 0x001F, enc16(name))
        self._hier_set_cell(nid, 0x3001, enc16(name))

    def move(self, nid, dest, _skip_checks=False):
        ed, w = self.ed, self.w
        self._check_user_folder(nid)
        if dest & 0x1F != 2 or ed.entry(dest) is None:
            raise E.EditError('destination 0x%x is not a folder' % dest)
        if dest == NID_ROOT:
            raise E.EditError('cannot move a folder to the store root')
        if nid in self.ancestors(dest):
            raise E.EditError('cannot move a folder into itself or one of its subfolders')
        old = self.parent_of(nid)
        if old == dest:
            return False
        name = self.name_of(nid)
        if self._sibling_clash(dest, name):
            raise E.EditError('%r already exists in the destination folder' % name)
        # take the row out of the old parent's table, put it in the new one's
        hn_old, hn_new = self._base(old) | T_HIER, self._base(dest) | T_HIER
        otc = ed.load_tc(hn_old)
        vals = otc.values(nid)
        otc.remove(nid)
        ed.store_tc(hn_old, otc)
        ntc = ed.load_tc(hn_new)
        vals[0x67F3] = struct.pack('<I', self._row_ver(ntc))
        ntc.add_by_pid(nid, vals)
        ed.store_tc(hn_new, ntc)
        bd, bs, _par = ed.entry(nid)
        w.nbt.put(nid, self._entry(nid, bd, bs, dest))
        self._set_has_sub(old)
        self._set_has_sub(dest)
        return True

    def in_deleted(self, nid):
        return self.deleted_nid in self.ancestors(nid)[1:]

    def _purge_node(self, n):
        e = self.ed.entry(n)
        if e is None:
            return
        self.idmap.zero_nid(n)
        self.w.nbt.delete(n)
        self.w.release(e[0])
        if e[1]: self.w.release(e[1])

    def purge(self, nid):
        """Permanently remove a folder, all its messages and all its subfolders."""
        ed = self.ed
        self._check_user_folder(nid)
        if nid & 0x1F != 2:
            raise E.EditError('not a folder')
        stats = {'folders': 0, 'messages': 0}
        parent = self.parent_of(nid)
        # nodes whose NBT parent is a folder but that its tables do not list (stale/hidden) must go too
        self._kids = {}
        for v in self.w.nbt.items():
            n, _bd, _bs, par, _p = struct.unpack('<QQQII', v)
            if n & 0x1F in (2, 4, 8) and par:
                self._kids.setdefault(par, []).append(n)
        self._purge_tree(nid, stats)
        hn = self._base(parent) | T_HIER
        tc = ed.load_tc(hn)
        if tc.find(nid) >= 0:
            tc.remove(nid)
            ed.store_tc(hn, tc)
        self._set_has_sub(parent)
        return stats

    def _purge_tree(self, nid, stats):
        ed = self.ed
        kids = self._kids.get(nid, [])
        subs = {c for c, _n in self.children(nid)} | {k for k in kids if k & 0x1F == 2}
        for c in sorted(subs):
            if c != nid and ed.entry(c):
                self._purge_tree(c, stats)
        base = self._base(nid)
        msgs = {k for k in kids if k & 0x1F in (4, 8)}
        for t in (T_CONT, T_FAI):
            tn = base | t
            if ed.entry(tn):
                msgs |= {rid for rid, _cells in ed.load_tc(tn).rows if ed.entry(rid)}
        for m in sorted(msgs):
            self._purge_node(m); stats['messages'] += 1
        for t in (T_HIER, T_CONT, T_FAI):
            self._purge_node(base | t)
        self._purge_node(nid)
        stats['folders'] += 1

    def delete(self, nid):
        """Outlook semantics: first delete moves to Deleted Items; deleting inside Deleted Items is permanent."""
        self._check_user_folder(nid)
        if self.deleted_nid is None:
            raise E.EditError('Deleted Items folder not found in the store')
        if self.in_deleted(nid):
            return ('purged', self.purge(nid))
        if self.ed.entry(nid)[2] != self.deleted_nid:
            name = self.name_of(nid)
            if self._sibling_clash(self.deleted_nid, name):          # Outlook-style disambiguation
                k = 2
                while self._sibling_clash(self.deleted_nid, '%s (%d)' % (name, k)):
                    k += 1
                self.rename(nid, '%s (%d)' % (name, k))
            self.move(nid, self.deleted_nid)
        return ('moved', None)


u32 = O.u32


def main(a):
    verify = '--verify' in a
    a = [x for x in a if x != '--verify']
    if len(a) < 3:
        print(__doc__); return 2
    path, cmd = a[0], a[1]
    ops = FolderOps(path)
    try:
        if cmd == 'create':
            if len(a) < 4: print(__doc__); return 2
            nid = ops.create(ops.folder(a[2]), a[3], a[4] if len(a) > 4 else 'IPF.Note')
            print('created folder %r, NID 0x%x' % (a[3], nid))
        elif cmd == 'rename':
            ops.rename(ops.folder(a[2]), a[3]); print('renamed')
        elif cmd == 'move':
            print('moved' if ops.move(ops.folder(a[2]), ops.folder(a[3])) else 'already there')
        elif cmd == 'delete':
            how, st = ops.delete(ops.folder(a[2]))
            print('moved to Deleted Items' if how == 'moved' else 'permanently deleted: %(folders)d folder(s), %(messages)d message(s)' % st)
        elif cmd == 'purge':
            print('permanently deleted: %(folders)d folder(s), %(messages)d message(s)' % ops.purge(ops.folder(a[2])))
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
