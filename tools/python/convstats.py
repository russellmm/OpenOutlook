"""convstats.py FILE - for every contents-table row: is 0x3013 the GUID of the message's conversation index (bytes 6..22), MD5 of the upper-cased
topic (UTF-16LE), empty, absent, or something else? Splits the counts by the shape of the conversation index (22 bytes = thread start, longer = a
reply / forward that added 5-byte child blocks) and by whether the subject has a prefix. Prints a few examples per class. Run from tools/python."""
import sys, struct, hashlib
from collections import Counter, defaultdict
sys.path.insert(0, '.')
import pstcore as P


def main():
    pst = P.PST(sys.argv[1])
    nodes = {}
    for nid in list(pst.nbt.keys()) if hasattr(pst, 'nbt') else []:
        nodes[nid] = 1
    stats = Counter()
    examples = defaultdict(list)
    tables = [nid for nid in pst.nbt if (nid & 0x1F) == 0xE] if hasattr(pst, 'nbt') else []
    for tn in tables:
        try:
            rows = P.TC(pst.node(tn)).rows()
        except Exception:
            continue
        for rid, r in rows:
            if (rid & 0x1F) != 4:
                continue
            cell = r.get(0x3013)
            try:
                pc = P.PC(pst.node(rid)).props
            except Exception:
                stats['message unreadable'] += 1
                continue
            ci = bytes(pc[0x0071][1]) if 0x0071 in pc else b''
            topic = P.decode_value(*pc[0x0070]) if 0x0070 in pc else None
            subj = P.decode_value(*pc[0x0037]) if 0x0037 in pc else ''
            prefixed = bool(subj) and subj.startswith('\x01') and len(subj) > 1 and ord(subj[1]) > 1
            md5 = hashlib.md5((topic or '').upper().encode('utf-16-le')).digest() if topic is not None else None
            shape = 'no index' if not ci else ('index 22' if len(ci) == 22 else 'index longer')
            if cell is None: kind = 'no cell'
            else:
                v = bytes(cell[1])
                kind = 'empty' if not v else 'guid' if len(ci) >= 22 and v == ci[6:22] else 'md5(topic)' if md5 and v == md5 else 'other'
            key = (shape, 'prefix' if prefixed else 'no prefix', kind)
            stats[key] += 1
            if len(examples[key]) < 2:
                examples[key].append(f'0x{rid:x} {subj[2:50] if prefixed else subj[:50]!r}')
    for key, n in sorted(stats.items(), key=lambda kv: str(kv[0])):
        print(f'{n:6}  {key}   e.g. {"; ".join(examples.get(key, []))}')


if __name__ == '__main__':
    main()
