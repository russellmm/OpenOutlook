#!/usr/bin/env python3
"""Python reference for the C write API: the same commands as `openpst FILE wmove|wcopy|wdel|wpurge|fcreate|frename|fmove|fdelete|fpurge`
but with numeric NIDs only, executed by the Python writer (pstactions.Session). With OPST_TEST_RANDOM=1 os.urandom is replaced by the
generator the C library uses in that mode, so both implementations produce byte-identical files even for operations that draw random ids.
usage: pyops.py REPO_DIR FILE COMMAND args...      (NIDS = comma separated, decimal or 0x hex)"""
import os, sys

if os.environ.get('OPST_TEST_RANDOM') == '1':
    _st = [0x12345678]

    def _urandom(n):
        out = bytearray()
        for _ in range(n):
            s = _st[0]
            s ^= (s << 13) & 0xFFFFFFFF
            s ^= s >> 17
            s ^= (s << 5) & 0xFFFFFFFF
            _st[0] = s
            out.append((s >> 24) & 0xFF)
        return bytes(out)
    os.urandom = _urandom

sys.path.insert(0, sys.argv[1])
import pstactions as A
import pstfolders as F


def nids(s):
    return [int(x, 0) for x in s.split(',') if x]


def main(argv):
    path, cmd, a = argv[0], argv[1], argv[2:]
    s = A.Session(path)
    if cmd == 'wmove':
        s.move_messages(nids(a[0]), int(a[1], 0)); print('moved')
    elif cmd == 'wcopy':
        print('copied; new NIDs:', ' '.join(hex(x) for x in s.copy_messages(nids(a[0]), int(a[1], 0))))
    elif cmd == 'wdel':
        print('deleted', s.delete_messages(nids(a[0])))
    elif cmd == 'wpurge':
        s.delete_messages_permanently(nids(a[0])); print('purged')
    elif cmd == 'fcreate':
        print('created', hex(s.create_folder(int(a[0], 0), a[1], a[2] if len(a) > 2 else 'IPF.Note')))
    elif cmd == 'frename':
        s.rename_folder(int(a[0], 0), a[1]); print('renamed')
    elif cmd == 'fmove':
        s.move_folder(int(a[0], 0), int(a[1], 0)); print('moved')
    elif cmd == 'fdelete':
        print(s.delete_folder(int(a[0], 0)))
    elif cmd == 'fpurge':
        n = int(a[0], 0)
        s.folder_info()
        print(s._run(F.FolderOps, lambda o: o.purge(n)))
    elif cmd == 'wxcopy':
        import pstxcopy as X
        print('copied; new NIDs:', ' '.join(hex(x) for x in X.copy_messages(a[0], nids(a[1]), path, int(a[2], 0))))
    elif cmd == 'wfix':
        import pstfix as FX
        fx = FX.Fixer(path)
        apply = '--apply' in a
        try:
            rep = fx.run(apply)
            if apply and any(rep.values()):
                fx.commit()
        finally:
            fx.close()
        print({k: len(v) for k, v in rep.items()})
    else:
        raise SystemExit('unknown command ' + cmd)


if __name__ == '__main__':
    main(sys.argv[2:] if False else sys.argv[2:])
