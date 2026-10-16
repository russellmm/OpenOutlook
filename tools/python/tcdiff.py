"""tcdiff.py A B TABLE_NID [TABLE_NID ...] - row-level diff of a table context (a folder's contents table, type 0xE) between two PST files.
Use after pstdiff.py when SCANPST rewrote a table in place: lists rows added / removed and every cell that changed, by row id (the message NID).
Run from tools/python."""
import sys
from collections import Counter
sys.path.insert(0, '.')
import pstcore as P


def rows(path, nid):
    pst = P.PST(path)
    tc = P.TC(pst.node(nid))
    out = {}
    for rid, r in tc.rows():
        out[rid] = {pid: (pt, bytes(raw) if isinstance(raw, (bytes, bytearray, memoryview)) else raw) for pid, (pt, raw) in r.items()}
    return out


def short(v):
    pt, raw = v
    if isinstance(raw, (bytes, bytearray)):
        try:
            d = P.decode_value(pt, raw)
            if isinstance(d, str): return repr(d[:60])
            if not isinstance(d, (bytes, bytearray)): return repr(d)
        except Exception:
            pass
        return raw[:24].hex() + ('...' if len(raw) > 24 else '')
    return repr(raw)


def main():
    a, b = sys.argv[1], sys.argv[2]
    for nid in [int(x, 0) for x in sys.argv[3:]]:
        ra, rb = rows(a, nid), rows(b, nid)
        print(f'===== table 0x{nid:x}: {len(ra)} -> {len(rb)} rows')
        for rid in sorted(set(ra) - set(rb), key=str): print(f'  row removed 0x{rid:x}' if isinstance(rid, int) else f'  row removed {rid}')
        for rid in sorted(set(rb) - set(ra), key=str): print(f'  row added   0x{rid:x}' if isinstance(rid, int) else f'  row added {rid}')
        changed = Counter()
        shown = 0
        for rid in sorted(set(ra) & set(rb), key=str):
            for pid in sorted(set(ra[rid]) | set(rb[rid])):
                va, vb = ra[rid].get(pid), rb[rid].get(pid)
                if va == vb: continue
                changed[pid] += 1
                if shown < 12:
                    shown += 1
                    print(f'  row 0x{rid:x} cell 0x{pid:04X}: {short(va) if va else "(none)"} -> {short(vb) if vb else "(none)"}')
        for pid, n in changed.most_common():
            print(f'  cell 0x{pid:04X} changed in {n} rows')


if __name__ == '__main__':
    main()
