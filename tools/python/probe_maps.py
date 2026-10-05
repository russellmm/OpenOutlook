#!/usr/bin/env python3
"""probe_maps.py - READ-ONLY. Finds AMap/PMap/FMap/FPMap pages and tests how FMap bytes map to AMaps.
Usage: python3 probe_maps.py FILE.pst"""
import pio
import sys, os, struct, re
AMAP0, SECT, SLOTS = 0x4400, 253952, 3968
path = sys.argv[1]
size = os.path.getsize(path)
nsec = (size - AMAP0) // SECT
fd = os.open(path, os.O_RDONLY)
rd = lambda off, n: os.pread(fd, n, off)
names = {0x82: 'FMap', 0x83: 'PMap', 0x84: 'AMap', 0x85: 'FPMap', 0x86: 'DList'}
print('file size %d, sections %d, size-on-grid %s' % (size, nsec, (size - AMAP0) % SECT == 0))
hdr = rd(0, 564)
print('header: fAMapValid=%d  ibAMapLast=%d  cbAMapFree=%d  rgbFM[:8]=%s' % (hdr[248], *struct.unpack_from('<QQ', hdr, 192)[:1], struct.unpack_from('<Q', hdr, 200)[0], hdr[256:264].hex()))
runs = []
found = {0x82: [], 0x83: [], 0x85: []}
for s in range(nsec):
    base = AMAP0 + s * SECT
    pg = rd(base, 512)
    if pg[496] != 0x84:
        print('!! section %d: no AMap at 0x%x (type 0x%02x)' % (s, base, pg[496])); runs.append(None); continue
    bits = ''.join(format(b, '08b') for b in pg[:496])
    m = max((len(x) for x in re.findall('0+', bits)), default=0)
    runs.append(min(255, m))
    for k in range(1, 6):
        p2 = rd(base + 512 * k, 512)
        if len(p2) == 512 and p2[496] in found and p2[496] == p2[497]:
            found[p2[496]].append((s, k, base + 512 * k))
for t in (0x83, 0x82, 0x85):
    lst = found[t]
    print('%s pages found: %d' % (names[t], len(lst)))
    print('   first few (section, +512*k, ib):', [(s, k, hex(ib)) for s, k, ib in lst[:6]])
    if len(lst) > 1:
        print('   section gaps:', sorted(set(b[0] - a[0] for a, b in zip(lst, lst[1:])))[:6])
for s, k, ib in found[0x82][:4]:
    pg = rd(ib, 512)[:496]
    best = max(range(0, nsec), key=lambda a: sum(1 for i in range(496) if a + i < nsec and runs[a + i] is not None and pg[i] == runs[a + i]))
    match = sum(1 for i in range(496) if best + i < nsec and pg[i] == runs[best + i])
    print('FMap at 0x%x (section %d): best-aligned AMap start index = %d, %d of 496 bytes match computed max-run' % (ib, s, best, match))
    print('   first bytes:', list(pg[:12]), ' computed:', runs[best:best + 12])
last = nsec - 1
print('last section %d: computed max-run (cap 255) = %s' % (last, runs[last]))
bad = [(i, r) for i, r in enumerate(runs) if r is not None and r < 255][-8:]
print('last sections with max-run < 255:', bad)
