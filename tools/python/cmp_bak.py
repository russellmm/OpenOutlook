import sys, struct, pstcore as P
a, b = P.PST(sys.argv[1]), P.PST(sys.argv[2])
print('sizes', len(a.nbt), len(b.nbt), 'nbt;', len(a.bbt), len(b.bbt), 'bbt')
ha, hb = open(sys.argv[1],'rb').read(564), open(sys.argv[2],'rb').read(564)
print('header diffs (offset: old -> new):')
for i in range(564):
    if ha[i] != hb[i]: print('  %d: %02x -> %02x' % (i, ha[i], hb[i]))
na, nb = a.nbt, b.nbt
only_a = sorted(set(na) - set(nb)); only_b = sorted(set(nb) - set(na))
print('NBT only in bak:', [hex(x) for x in only_a][:20], '; only in repaired:', [hex(x) for x in only_b][:20])
d = [(n, na[n], nb[n]) for n in na if n in nb and na[n] != nb[n]]
print('NBT entries changed:', len(d))
for n, x, y in d[:30]: print('  nid %x  bak(bd %x bs %x par %x) -> rep(bd %x bs %x par %x)' % ((n,)+tuple(x)+tuple(y)))
ba, bb = a.bbt, b.bbt
print('BBT only in bak:', len(set(ba)-set(bb)), [hex(x) for x in sorted(set(ba)-set(bb))][:20])
print('BBT only in repaired:', len(set(bb)-set(ba)), [hex(x) for x in sorted(set(bb)-set(ba))][:20])
c = [(k, ba[k], bb[k]) for k in ba if k in bb and ba[k] != bb[k]]
print('BBT entries changed:', len(c))
for k, x, y in c[:30]: print('  bid %x  bak(ib %x cb %d cref %d) -> rep(ib %x cb %d cref %d)' % ((k,)+tuple(x)+tuple(y)))
