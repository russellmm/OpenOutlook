#!/usr/bin/env python3
"""pstltp.py - READ-ONLY structural check of every heap-on-node (property contexts, table contexts) in a PST,
plus calibration statistics against Outlook-written heaps.
  python3 pstltp.py FILE.pst
Needs pstcore.py, pstcheck.py and pstedit.py in the same folder.
"""
import os, sys, struct
from collections import Counter
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pstedit as E
from pstedit import Heap, BLOCKMAX, _fill_level


def heap_report(blocks):
    """Returns (problems, calib) for one heap. calib has fill-level comparison candidates."""
    probs = []
    try:
        hp = Heap(blocks)
    except E.EditError as e:
        return ['not a heap: %s' % e], None
    probs += hp.check()
    hdr = []
    b0 = blocks[0]
    fl = struct.unpack_from('<I', b0, 8)[0]
    calib = Counter()
    for bi, b in enumerate(blocks[:8]):
        pm = struct.unpack_from('<H', b, 0)[0]
        ca = struct.unpack_from('<H', b, pm)[0]
        last_alloc_end = struct.unpack_from('<%dH' % (ca + 1), b, pm + 4)[-1]
        stored_lo = (fl >> (4 * bi)) & 0xF
        stored_hi = (fl >> (28 - 4 * bi)) & 0xF
        for name, free in (('len', BLOCKMAX - len(b)), ('gap', BLOCKMAX - last_alloc_end - (4 + 2 * (ca + 1)))):
            exp = _fill_level(free)
            calib['%s/lowfirst' % name] += (exp == stored_lo)
            calib['%s/highfirst' % name] += (exp == stored_hi)
        calib['blocks'] += 1
    # BTH / TC / PC walk
    try:
        if hp.client == 0xBC:
            hp.bth(hp.root)
        elif hp.client == 0x7C:
            info = hp.get(hp.root)
            ccols = info[1]
            rgib = struct.unpack_from('<4H', info, 2)
            hid_ri, hnid_rows = struct.unpack_from('<II', info, 10)
            if len(info) != 22 + 8 * ccols:
                probs.append('TCINFO size %d != %d' % (len(info), 22 + 8 * ccols))
            _k, _e, recs = hp.bth(hid_ri)
            if hnid_rows and hnid_rows & 0x1F == 0:
                m = hp.get(hnid_rows)
                if len(m) != rgib[3] * len(recs):
                    probs.append('row matrix %d bytes != %d rows x %d' % (len(m), len(recs), rgib[3]))
    except Exception as e:
        probs.append('walk failed: %r' % e)
    return probs, calib


def main(path):
    import pstcore as P
    import pstcheck as C
    f = C.F(path)
    crypt = f.h[0x201]
    def leaf_blocks(bid):
        if not (bid & 2):
            raw = bytes(f.raw(bid))
            return [raw.translate(bytes(P.MPBB_I)) if crypt == 1 else raw]
        d = f.raw(bid)
        out = []
        for i in range(struct.unpack_from('<H', d, 2)[0]):
            out += leaf_blocks(struct.unpack_from('<Q', d, 8 + 8 * i)[0])
        return out
    def subs_of(bs):
        d = f.raw(bs)
        if d[0] != 2:
            return []
        n = struct.unpack_from('<H', d, 2)[0]
        if d[1] == 0:
            return [(struct.unpack_from('<Q', d, 8 + 24 * i)[0] & 0xFFFFFFFF, struct.unpack_from('<QQ', d, 16 + 24 * i)) for i in range(n)]
        out = []
        for i in range(n):
            out += subs_of(struct.unpack_from('<QQ', d, 8 + 16 * i)[1])
        return out
    stats = Counter(); calib = Counter(); badnodes = []
    multi = Counter()
    targets = []
    for nid, bd, bs, par in f.nbt:
        targets.append((nid, bd))
        if bs:
            for sn, (sd, ss) in subs_of(bs):
                targets.append(((nid, sn), sd))
    for nid, bd in targets:
        try:
            blocks = leaf_blocks(bd)
        except Exception as e:
            continue
        b0 = blocks[0]
        if len(b0) < 12 or b0[2] != 0xEC:
            continue
        pr, cb = heap_report(blocks)
        stats['heaps'] += 1
        stats['client 0x%02x' % b0[3]] += 1
        if len(blocks) > 1:
            multi['multi-block heaps'] += 1
            multi['blocks in them'] += len(blocks)
        if cb: calib.update(cb)
        if pr:
            badnodes.append((nid, pr))
    print('heaps examined:', dict(stats))
    print('multi-block:', dict(multi))
    print('problems in %d heaps' % len(badnodes))
    for nid, pr in badnodes[:15]:
        print('   node', nid if not isinstance(nid, tuple) else '%#x/%#x' % nid, pr[:3])
    if calib['blocks']:
        n = calib['blocks']
        print('fill-level calibration over %d Outlook-written blocks (first 8 per heap):' % n)
        for k in ('len/lowfirst', 'len/highfirst', 'gap/lowfirst', 'gap/highfirst'):
            print('   %-14s matches %d / %d' % (k, calib[k], n))
    return len(badnodes)


if __name__ == '__main__':
    sys.exit(1 if main(sys.argv[1]) else 0)
