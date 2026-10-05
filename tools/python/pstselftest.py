#!/usr/bin/env python3
"""pstselftest.py - tests for pstwrite (stage A).
  python3 pstselftest.py synth                 build a synthetic PST and stress the writer (no real file needed)
  python3 pstselftest.py check FILE.pst        READ-ONLY: bit order of AMaps, signature formula, tree validation
  python3 pstselftest.py tx FILE.pst           WRITES to FILE (use a COPY!): add+remove blocks, rollback test, validate
"""
import sys, os, struct, shutil, random
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pstcore import pst_crc, MPBB_I
import pstwrite as W
from pstwrite import PSTWriter, page_bytes, sig, AMAP0, SECT


def make_empty_pst(path, sections=2, crypt=1):
    eof = AMAP0 + sections * SECT
    f = bytearray(eof)
    h = bytearray(564)
    h[0:4] = b'!BDN'; h[8:10] = b'SM'
    struct.pack_into('<HHBB', h, 10, 23, 19, 1, 1)
    h[512] = 0x80; h[513] = crypt
    h[256:512] = b'\xff' * 256
    for sec in range(sections):
        ib = AMAP0 + sec * SECT
        bits = bytearray(496); bits[0] = 0xFF
        f[ib:ib + 512] = page_bytes(bytes(bits), 0x84, ib, ib, signed=False)
    # PMap (page) and 2 BTree root pages in section 0
    pm_ib = AMAP0 + 512
    nbt_ib, bbt_ib = AMAP0 + 1024, AMAP0 + 1536
    bits = bytearray(f[AMAP0:AMAP0 + 496]); bits[1] = 0xFF; bits[2] = 0xFF; bits[3] = 0xFF   # slots 8..31 used
    f[AMAP0:AMAP0 + 512] = page_bytes(bytes(bits), 0x84, AMAP0, AMAP0, signed=False)
    f[pm_ib:pm_ib + 512] = page_bytes(bytes(496), 0x83, pm_ib, pm_ib, signed=False)
    for ib, pt, bid in ((nbt_ib, 0x81, 1), (bbt_ib, 0x80, 2)):
        body = bytes(488) + struct.pack('<BBBBI', 0, 488 // (32 if pt == 0x81 else 24), 32 if pt == 0x81 else 24, 0, 0)
        f[ib:ib + 512] = page_bytes(body, pt, ib, bid)
    struct.pack_into('<Q', h, 32, 16)          # bidNextP
    struct.pack_into('<I', h, 40, 1)
    struct.pack_into('<QQQ', h, 184, eof, AMAP0 + (sections - 1) * SECT, 0)
    struct.pack_into('<QQ', h, 216, 1, nbt_ib)
    struct.pack_into('<QQ', h, 232, 2, bbt_ib)
    h[248] = 2
    struct.pack_into('<Q', h, 516, 0x100)     # bidNextB
    struct.pack_into('<I', h, 4, pst_crc(bytes(h[8:479])))
    struct.pack_into('<I', h, 524, pst_crc(bytes(h[8:524])))
    f[0:564] = h
    open(path, 'wb').write(f)


def nbt_entry(nid, bd, bs=0, par=0):
    return struct.pack('<QQQII', nid, bd, bs, par, 0)


def synth():
    path = '/tmp/synth.pst'
    make_empty_pst(path)
    w = PSTWriter(path)
    assert w.validate() == [], w.validate()
    random.seed(7)
    blobs = {}
    # many nodes -> forces leaf splits and a root split in both trees
    for n in range(400):
        data = os.urandom(random.choice([10, 300, 4000, 8176, 20000]))
        top = w.add_data_tree(data)
        nid = (0x10000 + n) << 5 | 4
        w.nbt.put(nid, nbt_entry(nid, top))
        blobs[nid] = (top, data)
        if n % 50 == 49:
            w.commit()
            w.close(); w = PSTWriter(path)
            p = w.validate(); assert not p, p[:5]
    w.commit()
    print('after inserts: nbt entries', len(w.nbt.items()), 'bbt entries', len(w.bbt.items()))
    # read back through the data trees
    def read_tree(bid):
        if bid & 2:
            raw = w.read_block_raw(bid)
            lvl, cent = raw[1], struct.unpack_from('<H', raw, 2)[0]
            return b''.join(read_tree(struct.unpack_from('<Q', raw, 8 + 8 * i)[0]) for i in range(cent))
        return w.read_block_raw(bid).translate(bytes(MPBB_I))
    for nid, (top, data) in list(blobs.items())[::17]:
        assert read_tree(top) == data, hex(nid)
    # delete two thirds
    for nid, (top, data) in list(blobs.items()):
        if nid % 3:
            w.nbt.delete(nid); w.release(top); del blobs[nid]
    w.commit(); w.close(); w = PSTWriter(path)
    p = w.validate(); assert not p, p[:5]
    for nid, (top, data) in blobs.items():
        assert read_tree(top) == data
    print('after deletes: nbt entries', len(w.nbt.items()), 'bbt entries', len(w.bbt.items()))
    # delete everything
    for nid, (top, data) in list(blobs.items()):
        w.nbt.delete(nid); w.release(top)
    w.commit(); w.close(); w = PSTWriter(path)
    p = w.validate(); assert not p, p[:5]
    print('empty again: nbt', len(w.nbt.items()), 'bbt', len(w.bbt.items()), 'free bytes', w.cb_amap_free)
    # crash/rollback test: journal written, half the writes applied, then "power loss"
    before = open(path, 'rb').read()
    top = w.add_data_tree(os.urandom(30000)); w.nbt.put(0x8022, nbt_entry(0x8022, top))
    for sec in sorted(w.dirty):
        ib = AMAP0 + sec * SECT
        w.write(ib, page_bytes(bytes(w.amaps[sec]), 0x84, ib, ib, signed=False))
    recs = [(0, w.pread(0, 564))] + [(o, w.pread(o, len(d))) for o, d in w.pending if o < w.eof]
    w.journal.write(w.eof, recs)
    for o, d in w.pending[: len(w.pending) // 2]:
        os.pwrite(w.fd, d, o)
    w.close()
    assert open(path, 'rb').read() != before
    w = PSTWriter(path)
    assert w.recovered and open(path, 'rb').read() == before, 'rollback failed'
    print('journal rollback: file restored byte-for-byte')
    w.close()
    print('SYNTH OK')


def check(path):
    w = PSTWriter(path.replace('.pst', '') + '.pst') if False else None
    import mmap
    fd = open(path, 'rb')
    class R:  # read-only shim
        pass
    w = PSTWriter.__new__(PSTWriter)
    w.path = path; w.journal = W.Journal(path)
    if w.journal.exists():
        print('journal present - not touching'); return
    w.fd = os.open(path, os.O_RDONLY)
    h = w.pread(0, 564); w.hdr = bytearray(h); w.crypt = h[0x201]
    w._load_header(); w.pending = []; w.amaps = {}; w.dirty = set(); w.free_delta = 0
    w.nsec = (w.eof - AMAP0) // SECT
    w.nbt = W.BTree(w, 0x81); w.bbt = W.BTree(w, 0x80)
    print('sections:', w.nsec, ' file size matches section grid:', (w.eof - AMAP0) % SECT == 0, ' real size', os.path.getsize(path))
    # 1. AMap bit order: every BBT block must be marked allocated
    ents = w.bbt.items()
    msb_bad = lsb_bad = 0
    for e in ents:
        bid, ib, cb, cref = struct.unpack_from('<QQHH', e, 0)
        size = (cb + 16 + 63) // 64 * 64
        sec, off = divmod(ib - AMAP0, SECT)
        a = w.amap(sec); i0 = off // 64
        for i in range(i0, i0 + size // 64):
            if not a[i >> 3] & (0x80 >> (i & 7)): msb_bad += 1
            if not a[i >> 3] & (1 << (i & 7)): lsb_bad += 1
    print('AMap check over %d blocks: MSB-first violations=%d, LSB-first violations=%d' % (len(ents), msb_bad, lsb_bad))
    # 2. signature formula
    bad = 0
    for e in ents:
        bid, ib, cb, cref = struct.unpack_from('<QQHH', e, 0)
        size = (cb + 16 + 63) // 64 * 64
        tcb, tsig, tcrc, tbid = struct.unpack('<HHIQ', w.pread(ib + size - 16, 16))
        if tsig != sig(ib, bid): bad += 1
    print('block signature mismatches: %d of %d' % (bad, len(ents)))
    # 3. full structural validation
    p = w.validate()
    print('validate(): %d problems' % len(p))
    for x in p[:15]: print('   ', x)
    # 4. DList / header facts
    dl = w.pread(0x4200, 512)
    print('DList page type 0x%02x, entries %d' % (dl[496], dl[1]))
    print('header: fAMapValid=%d bidNextB=0x%x bidNextP=0x%x cbAMapFree=%d' % (w.hdr[248], w.bid_next_b, w.bid_next_p, w.cb_amap_free))
    free_bits = sum(w._bits(w.amap(s)).count('0') for s in range(w.nsec))
    print('free bytes by AMap scan: %d  (header says %d)' % (free_bits * 64, w.cb_amap_free))


def stress(path):
    w = PSTWriter(path)
    print('validate before:', w.validate()[:3])
    tops = []
    for i in range(300):
        top = w.add_data_tree(os.urandom(8176))
        nid = (0xF0000 + i) << 5 | 4
        w.nbt.put(nid, nbt_entry(nid, top)); tops.append((nid, top))
    print('dirty AMap sections:', len(w.dirty), ' reconcile fixes:', w.reconciled_fixes)
    w.commit(); w.close(); w = PSTWriter(path)
    p = w.validate(); print('validate after add:', len(p), p[:5])
    for nid, top in tops:
        w.nbt.delete(nid); w.release(top)
    w.commit(); w.close(); w = PSTWriter(path)
    p = w.validate(); print('validate after remove:', len(p), p[:5])
    w.close()


def tx(path):
    w = PSTWriter(path)
    print('opened; recovered journal:', w.recovered)
    import re
    before = w.validate()
    print('header fAMapValid (original):', w.orig_valid)
    print('validate before:', len(before), 'problems', before[:5])
    for pr in before:
        m = re.match(r'block (0x[0-9a-f]+) not allocated', pr)
        if m:
            e = w.bbt_entry(int(m.group(1), 16))
            sec, off = divmod(e[1] - W.AMAP0, W.SECT)
            print('   %s: ib=0x%x cb=%d cref=%d section=%d slot=%d' % (m.group(1), e[1], e[2], e[3], sec, off // 64))
    print('max BID in BBT: 0x%x   bidNextB: 0x%x' % (max(struct.unpack_from('<Q', e, 0)[0] for e in w.bbt.items()), w.bid_next_b))
    top = w.add_data_tree(os.urandom(20000))
    nid = 0xFFFF0 << 5 | 4
    w.nbt.put(nid, nbt_entry(nid, top))
    print('AMap bits repaired by reconcile:', w.reconciled_fixes)
    w.commit()
    print('committed 1 node (20000 bytes)')
    w.close(); w = PSTWriter(path)
    p = w.validate(); print('validate after add:', len(p), 'problems', p[:5])
    w.nbt.delete(nid); w.release(top); w.commit()
    w.close(); w = PSTWriter(path)
    p = w.validate(); print('validate after remove:', len(p), 'problems', p[:5])
    w.close()


if __name__ == '__main__':
    cmd = sys.argv[1]
    {'synth': synth, 'check': lambda: check(sys.argv[2]), 'tx': lambda: tx(sys.argv[2]), 'stress': lambda: stress(sys.argv[2])}[cmd]()
