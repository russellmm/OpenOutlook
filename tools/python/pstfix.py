#!/usr/bin/env python3
"""pstfix.py - fixes, in place and without rebuilding the file, the "minor inconsistencies" SCANPST reports in PST files that
Outlook has edited (every rule was found by repairing a COPY with SCANPST and diffing it against the original; see handoff rev 5).

    python3 pstfix.py FILE            report only (nothing is written)
    python3 pstfix.py FILE --apply    fix everything it can, in one journaled commit, then re-check

Rules (known cases only - "no known issues" is not "SCANPST will find nothing"):
  R1 ids     message rows (contents tables) and folder rows (hierarchy tables, except the root table 0x12D) without the ID cells
             0x0E30/0x0E33/0x0E34 get them, plus an ID-map record (node 0xC01).
  R2 idmap   ID-map records whose node no longer exists get NID 0 (SCANPST's convention for a deleted object).
  R3 index   messages missing from Outlook's message index (node 0xE01) are appended to the bucket of the message they were
             copied from (found by comparing the properties of messages with the same conversation topic); the highest-NID field is raised.
  R4 rowver  duplicate PidTagLtpRowVer values (0x67F3) get new store-wide unique values; dwUnique is kept above all of them.
  R7 amap    blocks in the block tree that the allocation maps do not mark as allocated are marked, and the header's cbAMapFree is set to the
             free space the maps show (SCANPST: "Block is not allocated in AMAP!", "Computed cbAMapFree of X, but header has Y"). Without this
             SCANPST treats the nodes that own those blocks as invalid and reports follow-on errors (tables with missing columns, orphaned folders).
  R8 folders folders missing their hierarchy / contents / FAI tables, or missing from their parent's hierarchy table, get them (tables copied
             from a sibling, row built from the folder's own properties); the parent's has-subfolders flag follows (SCANPST: "Adding folder (nid=...)
             back to the database"). Such folders were left behind by early versions of the folder-creation code.
  R6 rowcells contents-table rows lacking the row-only cells 0x0E17 (message status, 0) and 0x3013 (a per-row GUID) get them
             (found by repairing files whose messages were imported by the C library with SCANPST).
Always run on a COPY first and finish with SCANPST (Analyze only).
"""
import os, sys, struct, collections

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import pstcore as P
import pstedit as E
import pstops as O
import pstidmap as M
import pstfolders as FO

ROOT_HIER = 0x12D          # root's hierarchy table: its search-folder rows legitimately have no ID cells; its normal-folder rows do


def _tables(ops):
    for v in ops.w.nbt.items():
        nid = struct.unpack_from('<Q', v, 0)[0] & 0xFFFFFFFF
        if nid & 0x1F in (0xD, 0xE):
            yield nid


def _replica_blob(ops):
    for nid in _tables(ops):
        try:
            tc = ops.ed.load_tc(nid)
        except Exception:
            continue
        ci = tc.col_by_pid.get(0x0E34)
        if ci is None:
            continue
        for _r, c in tc.rows:
            if c.get(ci):
                return c[ci]
    try:                                    # no row has one yet (a store whose ID map is still empty): the message store's own 0x0E34
        return ops.pc_props(0x21)[0x0E34][1] or None
    except Exception:
        return None


class Fixer(FO.FolderOps):
    def run(self, apply):
        ed, w = self.ed, self.w
        rep = collections.OrderedDict()
        blob = _replica_blob(self)
        change = w.bid_next_b
        r7 = self._amap(apply)
        rep['R7 allocation-map problems'] = r7
        # ---- R1 ids ------------------------------------------------------------------------------------------
        r1 = []
        idm = self.idmap
        for tn in list(_tables(self)):
            try:
                tc = ed.load_tc(tn)
            except Exception:
                continue
            c30, c33, c34 = (tc.col_by_pid.get(p) for p in (0x0E30, 0x0E33, 0x0E34))
            if c30 is None or not idm.present or blob is None:
                continue
            changed = False
            for rid, cells in tc.rows:
                if cells.get(c30):
                    continue
                if tn == ROOT_HIER and rid & 0x1F != 2:
                    continue                          # search folders in the root table carry no ID; its normal folders do (SCANPST adds them)
                r1.append((tn, rid))
                if apply:
                    g = M.IdMap.new_guid()
                    cells[c30] = g
                    if c33 is not None:
                        change += 4
                        cells[c33] = struct.pack('<Q', change)
                    if c34 is not None:
                        cells[c34] = blob
                    idm.add(g, rid)
                    changed = True
            if changed:
                ed.store_tc(tn, tc)
        rep['R1 rows without ID cells'] = r1
        # ---- R2 dangling ID-map records ------------------------------------------------------------------------
        r2 = [(k.hex(), n) for k, n in idm.recs.items() if n and ed.entry(n) is None]
        if apply:
            for _k, n in r2:
                idm.zero_nid(n)
        rep['R2 ID-map records pointing at deleted nodes'] = r2
        # ---- R3 message index ------------------------------------------------------------------------------------
        r3, pairs = self._index_missing(apply)
        rep['R3 messages missing from the message index'] = r3
        # ---- R4 duplicate row versions ---------------------------------------------------------------------------
        r4 = self._rowvers(apply)
        rep['R4 duplicate / too-high row versions'] = r4
        r5 = self._hwm(apply)
        rep['R5 header NID high-water marks too low'] = r5
        r6 = self._rowcells(apply)
        rep['R6 rows without the row cells 0x0E17 / 0x3013'] = r6
        r8 = self._folders(apply)
        rep['R8 incomplete folders (tables / parent row)'] = r8
        if apply and any(rep.values()):
            if pairs:
                self.note_max_message_nid(pairs)
            else:
                self.note_max_message_nid()
        return rep

    # ---- R7 --------------------------------------------------------------------------------------------------------
    def _amap(self, apply):
        w = self.w
        issues = []
        for e in w.bbt.items():
            bid, ib, cb = struct.unpack_from('<QQH', e, 0)
            if not w.is_allocated(ib, cb + 16):
                issues.append(('not allocated', hex(bid), hex(ib)))
        computed = w.free_from_maps()
        if apply and issues:
            w.reconcile()                                   # marks the blocks and re-derives cbAMapFree from the maps
            computed = w.free_from_maps()
        if computed != w.cb_amap_free + (0 if not apply else w.free_delta):
            issues.append(('cbAMapFree', w.cb_amap_free + w.free_delta, computed))
            if apply:
                w.cb_amap_free, w.free_delta = computed, 0
                w.dirty.add(0)                              # force a commit even when only the header changes
        return issues

    # ---- R8 --------------------------------------------------------------------------------------------------------
    def _folders(self, apply):
        ed, w = self.ed, self.w
        nodes = {}
        for v in w.nbt.items():
            n, _bd, _bs, par, _ = struct.unpack_from('<QQQII', v, 0)
            nodes[n & 0xFFFFFFFF] = par
        issues = []
        hier = {}
        for nid, par in sorted(nodes.items()):
            if nid & 0x1F != 2 or par == nid or par & 0x1F != 2 or par not in nodes:
                continue
            base = nid & ~0x1F
            missing = [t for t in (0xD, 0xE, 0xF) if (base | t) not in nodes]
            hn = (par & ~0x1F) | 0xD
            if hn not in hier:
                try:
                    hier[hn] = {r for r, _c in ed.load_tc(hn).rows}
                except Exception:
                    hier[hn] = None
            no_row = hier[hn] is not None and nid not in hier[hn]
            if not missing and not no_row:
                continue
            issues.append((nid, par, [hex(t) for t in missing], no_row))
            if not apply:
                continue
            props = self.pc_props(nid)
            if missing:
                self._make_tables(nid, par, only_missing=True)
            if no_row:
                z4 = b'\0\0\0\0'
                name16 = props.get(0x3001, (0x1F, b''))[1]
                cls16 = props.get(0x3613, (0x1F, b''))[1] or None
                extra = {pid: props[pid][1] for pid in (0x3602, 0x3603, 0x360A, 0x6635, 0x6636) if pid in props}
                self._add_hier_row(nid, par, name16, cls16, extra, mirror=props)
                hier[hn].add(nid)
            self._set_has_sub(par)
            self._sync_row(par)
        issues += self._rows_vs_folders(apply)
        return issues

    def _rows_vs_folders(self, apply):
        """Hierarchy-table rows must mirror their folder's properties (name, counts, has-subfolders, class); has-subfolders is the truth
        about the children. SCANPST: "Hierarchy Table for X, row doesn't match sub-object"."""
        ed = self.ed
        issues = []
        nodes = set()
        for v in self.w.nbt.items():
            nodes.add(struct.unpack_from('<Q', v, 0)[0] & 0xFFFFFFFF)
        for nid in sorted(n for n in nodes if n & 0x1F == 2):
            par = self.parent_of(nid) if nid != FO.NID_ROOT else None
            if not par or par & 0x1F != 2 or (par & ~0x1F) | 0xD not in nodes:
                continue
            hn = (par & ~0x1F) | 0xD
            tc = ed.load_tc(hn)
            i = tc.find(nid)
            if i < 0:
                continue
            try:
                props = self.pc_props(nid)
            except Exception:
                continue
            truth = {pid: v[1] for pid, v in props.items() if pid in self.ROW_PIDS}
            if 0x360A in truth:
                truth[0x360A] = bytes([1 if self.children(nid) else 0])
            cells = tc.rows[i][1]
            bad = []
            for pid in self.ROW_PIDS:
                ci = tc.col_by_pid.get(pid)
                if ci is None:
                    continue
                have, want = cells.get(ci), truth.get(pid)
                if have != want and not (have in (None, b'') and want in (None, b'')):
                    bad.append(pid)
            if bad:
                issues.append((nid, [hex(p) for p in bad]))
                if apply:
                    for pid in bad:
                        ci = tc.col_by_pid[pid]
                        if truth.get(pid) is None:
                            cells.pop(ci, None)
                        else:
                            cells[ci] = truth[pid]
                    ed.store_tc(hn, tc)
        return issues

    def _sync_row(self, nid):
        pass

    # ---- R3 --------------------------------------------------------------------------------------------------------
    def _buckets(self):
        e = self.ed.entry(0xE01)
        if e is None:
            return None, None
        blocks = [bytearray(b) for b in self.ed.leaf_blocks(e[0])]
        hp = E.Heap(blocks)
        if hp.client != 0xCC:
            return None, None
        _k, _v, recs = hp.bth(0x40)
        members = {}
        for key, val in recs:
            lst = hp.get(struct.unpack('<I', val)[0])
            for i in range(0, len(lst), 4):
                members[struct.unpack_from('<I', lst, i)[0]] = key
        return members, hp

    def _index_missing(self, apply):
        members, _hp = self._buckets()
        if members is None:
            return [], []
        missing = sorted(n for n in self._all_message_nids() if n not in members)
        if not missing:
            return [], []
        st = P.Store(self.path)
        try:
            topic, pcs = {}, {}

            def props(n):
                if n not in pcs:
                    try:
                        pcs[n] = {k: v[1] for k, v in st.message(n).pc.props.items()}
                    except Exception:
                        pcs[n] = {}
                return pcs[n]
            by_topic = collections.defaultdict(list)
            for n in members:
                by_topic[props(n).get(0x70, b'')].append(n)
            out, pairs, keyed = [], [], []
            for n in missing:
                e = self.ed.entry(n)
                k = O.blocks_conv_key([bytearray(b) for b in self.ed.leaf_blocks(e[0])]) if e else None
                if k is not None:                      # the bucket key is a function of the conversation GUID (see pstops.conv_key)
                    keyed.append((k, n))
                    out.append((n, None))
                    continue
                pn = props(n)
                cands = by_topic.get(pn.get(0x70, b''), [])
                best, score = None, -1
                for c in sorted(cands):
                    pc = props(c)
                    sc = sum(1 for k, v in pn.items() if pc.get(k) == v)
                    if sc > score:
                        best, score = c, sc
                out.append((n, best))
                if best is not None:
                    pairs.append((best, n))
            groups = collections.OrderedDict()
            keyed_n = {n for _k, n in keyed}
            for n, best in out:
                if best is None and n not in keyed_n:
                    groups.setdefault(props(n).get(0x70, b''), []).append(n)
            if apply and (pairs or groups or keyed):
                self.note_max_message_nid(pairs, groups=list(groups.items()), keyed=keyed)
                pairs = []
            return out, pairs
        finally:
            st.pst.close()

    # ---- R5 --------------------------------------------------------------------------------------------------------
    def _hwm(self, apply):
        ed, w = self.ed, self.w
        mx = {}

        def note(n):
            t, i = n & 0x1F, n >> 5
            if t not in (6, 7, 0x10) and i > mx.get(t, 0):      # 6, 7, 0x10 hold NID-shaped values, not counters
                mx[t] = i

        def walk(bs, depth=0):
            if depth > 4:
                return
            for k, (_sd, ss) in ed.subnodes(bs).items():
                note(k)
                if ss:
                    walk(ss, depth + 1)
        for v in w.nbt.items():
            n, _bd, bs = struct.unpack_from('<QQQ', v, 0)
            note(n & 0xFFFFFFFF)
            if bs:
                try:
                    walk(bs)
                except Exception:
                    pass
        out = []
        for t, i in sorted(mx.items()):
            cur = struct.unpack_from('<I', w.hdr, 44 + 4 * t)[0]
            if i > cur:
                out.append((t, cur, i))
                if apply:
                    struct.pack_into('<I', w.hdr, 44 + 4 * t, i)
        return out

    # ---- R6 --------------------------------------------------------------------------------------------------------
    def _rowcells(self, apply):
        ed = self.ed
        issues = []
        for tn in sorted(_tables(self)):
            if tn & 0x1F != 0x0E:
                continue
            try:
                tc = ed.load_tc(tn)
            except Exception:
                continue
            c17, c30 = tc.col_by_pid.get(0x0E17), tc.col_by_pid.get(0x3013)
            dirty = False
            for rid, cells in tc.rows:
                if c17 is not None and not cells.get(c17):
                    issues.append((tn, rid, 0x0E17))
                    if apply:
                        cells[c17] = struct.pack('<I', 0); dirty = True
                if c30 is not None and not cells.get(c30):
                    issues.append((tn, rid, 0x3013))
                    if apply:
                        g = bytearray(os.urandom(16)); g[7] = (g[7] & 0x0F) | 0x40; g[8] = (g[8] & 0x3F) | 0x80
                        cells[c30] = bytes(g); dirty = True
            if apply and dirty:
                ed.store_tc(tn, tc)
        return issues

    # ---- R4 --------------------------------------------------------------------------------------------------------
    def _rowvers(self, apply):
        ed, w = self.ed, self.w
        seen, issues, tabs = {}, [], {}
        for tn in list(_tables(self)):
            try:
                tc = ed.load_tc(tn)
            except Exception:
                continue
            ci = tc.col_by_pid.get(0x67F3)
            if ci is None:
                continue
            tabs[tn] = (tc, ci)
        for tn in sorted(tabs):
            tc, ci = tabs[tn]
            for rid, cells in tc.rows:
                b = cells.get(ci)
                if not b:
                    continue
                v = struct.unpack('<I', b[:4])[0]
                if v in seen:
                    issues.append((tn, rid, v))
                seen[v] = (tn, rid)
        top = max(seen) if seen else 0
        if top >= w.unique:
            issues.append(('dwUnique', w.unique, top))
        if apply and issues:
            w.unique = max(w.unique, top)
            dirty = set()
            seen2 = set()
            for tn in sorted(tabs):
                tc, ci = tabs[tn]
                for rid, cells in tc.rows:
                    b = cells.get(ci)
                    if not b:
                        continue
                    v = struct.unpack('<I', b[:4])[0]
                    if v in seen2:
                        cells[ci] = struct.pack('<I', self.next_row_ver())
                        dirty.add(tn)
                    else:
                        seen2.add(v)
            for tn in sorted(dirty):            # ascending: the result must not depend on Python's set order
                ed.store_tc(tn, tabs[tn][0])
        return issues


def main(argv):
    args = [a for a in argv if not a.startswith('--')]
    apply = '--apply' in argv
    if len(args) != 1:
        print(__doc__)
        return 2
    path = args[0]
    if apply and os.path.exists(path + '.journal'):
        print('a journal file exists next to the PST; open it once with the editor (it recovers) first')
        return 1
    fx = Fixer(path)
    try:
        rep = fx.run(apply)
        total = sum(len(v) for v in rep.values())
        for name, items in rep.items():
            print('%-48s %d' % (name, len(items)))
            for it in items[:3]:
                print('     e.g.', it)
        if apply and total:
            fx.commit()
            print('applied %d fixes in one commit' % total)
        elif total:
            print('\n%d issues found; run again with --apply (on a copy first)' % total)
        else:
            print('no known issues')
    except Exception:
        if apply:
            try:
                fx.w.rollback_pending()
            except Exception:
                pass
        raise
    finally:
        fx.close()
    if apply and total:
        fx2 = Fixer(path)
        try:
            again = fx2.run(False)
        finally:
            fx2.close()
        left = sum(len(v) for v in again.values())
        print('re-check: %d known issues left' % left)
        return 0 if left == 0 else 1
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
