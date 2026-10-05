import struct, sys
f = open(sys.argv[1], 'rb'); h = f.read(1024)
magic, = struct.unpack_from('4s', h, 0)
ver, = struct.unpack_from('<H', h, 10)
print('magic', magic, 'wVer', ver)
if ver >= 23:
    crypt = h[0x201]
    eof, = struct.unpack_from('<Q', h, 184)
    nbt = struct.unpack_from('<QQ', h, 216)
    bbt = struct.unpack_from('<QQ', h, 232)
else:
    crypt = h[0x1CD]
    eof, = struct.unpack_from('<I', h, 168)
    nbt = bbt = None
print('crypt (0 none, 1 permute, 2 cyclic):', crypt)
print('ibFileEof', eof, 'actual size', f.seek(0, 2))
print('NBT root (bid, ib):', nbt, ' BBT root (bid, ib):', bbt)
