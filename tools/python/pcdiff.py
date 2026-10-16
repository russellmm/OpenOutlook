"""pcdiff.py A B NID [NID ...] - property-level diff of messages (or any property-context node) between two PST files.
Use after pstdiff.py: pstdiff names the nodes SCANPST changed, pcdiff shows which properties it added, removed or rewrote.
Run from tools/python. Also prints the message's recipient-table rows when they differ."""
import sys
sys.path.insert(0, '.')
import pstcore as P

TYPES = {0x2: 'i16', 0x3: 'i32', 0xB: 'bool', 0x14: 'i64', 0x40: 'time', 0x1F: 'str', 0x1E: 'str8', 0x102: 'bin', 0x48: 'guid'}


def show(ptype, raw):
    try:
        v = P.decode_value(ptype, raw)
    except Exception:
        v = None
    if isinstance(v, str):
        return repr(v if len(v) <= 160 else v[:160] + '...')
    if v is not None and not isinstance(v, (bytes, bytearray)):
        return repr(v)
    raw = bytes(raw or b'')
    return raw[:48].hex() + ('...' if len(raw) > 48 else '') + f' ({len(raw)} bytes)'


def props(path, nid):
    pst = P.PST(path)
    node = pst.node(nid)
    return P.PC(node).props, node, pst


def table_rows(node, pst, sub_nid):
    try:
        sub = node.sub(sub_nid)
        return [tuple(sorted((pid, bytes(raw) if isinstance(raw, (bytes, bytearray)) else raw) for pid, (pt, raw) in r.items()))
                for r in P.TC(sub).rows()] if sub else []
    except Exception as ex:
        return [('error', str(ex))]


def main():
    a, b, nids = sys.argv[1], sys.argv[2], [int(x, 0) for x in sys.argv[3:]]
    for nid in nids:
        pa, na, sa = props(a, nid)
        pb, nb, sb = props(b, nid)
        print(f'===== node 0x{nid:x}: {len(pa)} -> {len(pb)} properties')
        for pid in sorted(set(pa) | set(pb)):
            va, vb = pa.get(pid), pb.get(pid)
            if va == vb:
                continue
            name = f'0x{pid:04X}'
            if va is None:
                print(f'  + {name} {TYPES.get(vb[0], hex(vb[0]))} = {show(*vb)}')
            elif vb is None:
                print(f'  - {name} {TYPES.get(va[0], hex(va[0]))} = {show(*va)}')
            else:
                print(f'  ~ {name} {TYPES.get(va[0], hex(va[0]))}: {show(*va)}  ->  {show(*vb)}')
        subj = pb.get(0x0037)
        if subj:
            print('  subject:', show(*subj))
        for label, sub_nid in (('recipients', 0x692), ('attachments', 0x671)):
            ra, rb = table_rows(na, sa, sub_nid), table_rows(nb, sb, sub_nid)
            if ra != rb:
                print(f'  {label} table differs: {len(ra)} -> {len(rb)} rows')


if __name__ == '__main__':
    main()
