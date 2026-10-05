#!/usr/bin/env python3
"""pstxcopy.py - copy messages from one PST file into a folder of another PST file (named properties translated, attachments kept).

    python3 pstxcopy.py SOURCE.pst DEST.pst --from "Inbox" --to "Imported" [--count N] [--create]
    (folder names are matched case-insensitively below the "Top of Outlook data file" folder, '/' separates levels)

What it does for every message (SOURCE is only read):
  * clones the message node with its recipient / attachment sub-nodes into DEST as new, independent blocks;
  * translates named property ids (0x8000+) in the message and in every attachment property context through both files' Name-to-ID
    maps (pstnpm.py), adding names missing in DEST; properties whose name is unknown in SOURCE are dropped;
  * adds the contents-table row (from SOURCE's row; named columns are dropped) with ID cells, an ID-map record and a unique row version;
  * updates the folder counts, the highest-message-NID field and (via pstfix rule R3) Outlook's message index.
Everything is one atomic journaled commit of DEST. SOURCE and DEST must be different files.
"""
import os, sys, struct

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import pstcore as P
import pstwrite as W
import pstedit as E
import pstops as O
import pstidmap as M
import pstnpm as N
import pstfix as FX


def _pc_remap(blocks, tmap, drop_unknown=True):
    """Re-key the properties of a property-context heap (single-level BTH). tmap: old pid -> new pid or None (drop)."""
    hp = E.Heap(blocks)
    if hp.client != 0xBC:
        return False
    h = hp.get(hp.root)
    if h[3] != 0:
        raise E.EditError('property context with an index level cannot be re-keyed (more than ~440 properties)')
    leaf = struct.unpack_from('<I', h, 4)[0]
    d = bytes(hp.get(leaf))
    recs = []
    for i in range(0, len(d), 8):
        pid = struct.unpack_from('<H', d, i)[0]
        if pid >= 0x8000:
            pid = tmap.get(pid)
            if pid is None:
                continue
        recs.append((pid, d[i + 2:i + 8]))
    recs.sort(key=lambda r: r[0])
    new = b''.join(struct.pack('<H', p) + rest for p, rest in recs)
    if new == d:
        return False
    if not O._grow_heap_item(blocks, leaf, new):
        raise E.EditError('no room to re-key a property context')
    return True


def _named_pids(blocks):
    hp = E.Heap(blocks)
    if hp.client != 0xBC:
        return set()
    out = set()
    for k, _v in hp.bth(hp.root)[2]:
        pid = struct.unpack('<H', k)[0]
        if pid >= 0x8000:
            out.add(pid)
    return out


class XOps(O.Ops):
    def copy_from(self, src_path, nids, dest):
        if os.path.abspath(src_path) == os.path.abspath(self.path):
            raise E.EditError('source and destination are the same file (use the normal copy)')
        ed, w = self.ed, self.w
        src = W.PSTWriter(src_path)
        try:
            sed = E.Editor(src, P.MPBB_I)
            snpm, dnpm = N.NameMap(sed), N.NameMap(ed)
            if not dnpm.present:
                raise E.EditError('destination has no name-to-id map (node 0x61)')
            snames = snpm.names()
            tmap = {}

            def translate(pids):
                for pid in sorted(pids):            # ascending: the new ids must not depend on Python's set order
                    if pid in tmap:
                        continue
                    nm = snames.get(pid)
                    tmap[pid] = dnpm.add(*nm) if nm else None

            hw = []

            def clone(bd, bs, top):
                blocks = [bytearray(x) for x in sed.leaf_blocks(bd)]
                translate(_named_pids(blocks))
                _pc_remap(blocks, tmap)
                nbd = ed.put_blocks([bytes(x) for x in blocks])
                nbs = 0
                if bs:
                    new = {}
                    for k, (sd, ss) in sed.subnodes(bs).items():
                        if k & 0x1F == 5:                      # attachment: a property context of its own
                            new[k] = clone(sd, ss, False)
                        else:
                            new[k] = _clone_plain(sd, ss)
                    hw.extend(new)
                    body = struct.pack('<BBHI', 2, 0, len(new), 0)
                    for k in sorted(new):
                        body += struct.pack('<QQQ', k, *new[k])
                    nbs = w.add_block(body, internal=True)
                return nbd, nbs

            def _clone_plain(bd, bs):
                nbd = ed.put_blocks([bytes(x) for x in sed.leaf_blocks(bd)])
                nbs = 0
                if bs:
                    new = {k: _clone_plain(a, b) for k, (a, b) in sed.subnodes(bs).items()}
                    hw.extend(new)
                    body = struct.pack('<BBHI', 2, 0, len(new), 0)
                    for k in sorted(new):
                        body += struct.pack('<QQQ', k, *new[k])
                    nbs = w.add_block(body, internal=True)
                return nbd, nbs

            dtc_nid = (dest & ~0x1F) | 0x0E
            dtc = ed.load_tc(dtc_nid)
            blob = FX._replica_blob(self)
            idm = self.idmap
            change = w.bid_next_b
            cnt = unread = 0
            new_nids = []
            rows_cache = {}
            for n in nids:
                e = sed.entry(n)
                if e is None or n & 0x1F != 4:
                    raise E.EditError('message 0x%x not found in the source file' % n)
                bd, bs, spar = e
                tcn = (spar & ~0x1F) | 0x0E
                if tcn not in rows_cache:
                    rows_cache[tcn] = sed.load_tc(tcn)
                stc = rows_cache[tcn]
                vals = stc.values(n)
                named = {p: vals.pop(p) for p in [p for p in vals if p >= 0x8000]}      # row cells of named properties: re-keyed below
                idx = struct.unpack_from('<I', w.hdr, 44 + 4 * 4)[0] + 1
                struct.pack_into('<I', w.hdr, 44 + 4 * 4, idx)
                new = (idx << 5) | 4
                nbd, nbs = clone(bd, bs, True)
                for p, v in named.items():                  # SCANPST compares these cells with the message: keep them under the destination's ids
                    np_ = tmap.get(p)
                    if np_:
                        vals[np_] = v
                w.nbt.put(new, self._entry(new, nbd, nbs, dest))
                vals[0x67F2] = struct.pack('<I', new)
                vals[0x67F3] = struct.pack('<I', self.next_row_ver())
                for pid in (0x0E30, 0x0E33, 0x0E34):
                    vals.pop(pid, None)
                if idm.present and blob is not None:
                    g = M.IdMap.new_guid()
                    vals[0x0E30] = g
                    change += 4
                    vals[0x0E33] = struct.pack('<Q', change)
                    vals[0x0E34] = blob
                    idm.add(g, new)
                dtc.add_by_pid(new, vals)
                cnt += 1
                unread += self._unread(vals)
                new_nids.append(new)
            ed.store_tc(dtc_nid, dtc)
            self.counts(dest, cnt, unread)
            dnpm.save(self)
            ne = ed.entry(0x61)
            if ne and ne[1]:
                hw.extend(ed.subnodes(ne[1]).keys())
            self.bump_hwm(hw)
            self.note_max_message_nid()
            return new_nids
        finally:
            src.close()


def copy_messages(src_path, nids, dst_path, dest):
    """Copy messages (NIDs in SOURCE) into folder `dest` (NID in DEST). Returns the new NIDs."""
    ops = XOps(dst_path)
    try:
        new = ops.copy_from(src_path, nids, dest)
        ops.commit()
    except Exception:
        try:
            ops.w.rollback_pending()
        except Exception:
            pass
        raise
    finally:
        ops.close()
    fx = FX.Fixer(dst_path)           # message index: put each new message next to its best match (rule R3)
    try:
        rep = fx.run(True)
        if any(rep.values()):
            fx.commit()
    finally:
        fx.close()
    return new


def _find_folder(store, path):
    """Find a folder by path; paths below the "Top of ..." folder may leave that folder out."""
    try:
        return store.find_folder(path).nid
    except P.PSTError:
        top = next((f for f in store.root.subfolders() if f.name.lower().startswith('top of')), None)
        if top is None:
            raise
        return store.find_folder(top.name + '/' + path).nid


def main(argv):
    import argparse
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('source'); ap.add_argument('dest')
    ap.add_argument('--from', dest='ffrom', required=True); ap.add_argument('--to', dest='fto', required=True)
    ap.add_argument('--count', type=int, default=0, help='copy only the first N messages of the source folder')
    ap.add_argument('--create', action='store_true', help='create the destination folder when it does not exist')
    a = ap.parse_args(argv)
    st = P.Store(a.source)
    sf = _find_folder(st, a.ffrom)
    nids = [r['nid'] for r in P.Folder(st, sf).contents()]
    st.pst.close()
    if a.count:
        nids = nids[:a.count]
    import pstactions as A
    s = A.Session(a.dest)
    s.folder_info()
    dst = None
    try:
        st2 = P.Store(a.dest)
        dst = _find_folder(st2, a.fto)
        st2.pst.close()
    except P.PSTError:
        if not a.create:
            print('destination folder %r not found (use --create)' % a.fto)
            return 1
        r = s.create_folder(s.ipm_root, a.fto)
        dst = getattr(r, 'nid', r)
    new = copy_messages(a.source, nids, a.dest, dst)
    print('copied %d message(s) into %r' % (len(new), a.fto))
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
