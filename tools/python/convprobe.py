"""convprobe.py FILE NID [NID ...] - the conversation properties of messages: subject, topic (0x0070), conversation index (0x0071), the row's 0x3013 in the
folder's contents table, and MD5(upper-cased topic as UTF-16LE) (rule 33). Used to explain why SCANPST rewrote or emptied 0x3013. Run from tools/python."""
import sys, hashlib
sys.path.insert(0, '.')
import pstcore as P


def main():
    path = sys.argv[1]
    pst = P.PST(path)
    for nid in [int(x, 0) for x in sys.argv[2:]]:
        node = pst.node(nid)
        pc = P.PC(node).props
        def s(pid):
            v = pc.get(pid)
            return P.decode_value(*v) if v else None
        topic = s(0x0070)
        ci = pc.get(0x0071)
        ci = bytes(ci[1]) if ci else b''
        tn = (node.parent & ~0x1F) | 0xE
        cell = None
        try:
            for rid, r in P.TC(pst.node(tn)).rows():
                if rid == nid:
                    c = r.get(0x3013)
                    cell = bytes(c[1]).hex() if c else '(no cell)'
        except Exception as ex:
            cell = 'error ' + str(ex)
        md5 = hashlib.md5((topic or '').upper().encode('utf-16-le')).hexdigest() if topic is not None else None
        print(f'0x{nid:x} folder 0x{node.parent:x}')
        print(f'   subject {s(0x0037)!r}')
        print(f'   topic   {topic!r}   md5(topic) {md5}')
        print(f'   conv index {len(ci)} bytes {ci.hex()}' + (f'  guid {ci[6:22].hex()}' if len(ci) >= 22 else ''))
        print(f'   row 0x3013 {cell}   message 0x3013 {bytes(pc[0x3013][1]).hex() if 0x3013 in pc else "(none)"}')
        print(f'   0x0E06 delivery {s(0x0E06)}  0x1035 msgid {s(0x1035)!r}')


if __name__ == '__main__':
    main()
