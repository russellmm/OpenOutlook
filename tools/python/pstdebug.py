#!/usr/bin/env python3
"""pstdebug.py - prints full tracebacks for the first few distinct failures.
Usage: python3 pstdebug.py FILE.pst"""
import sys, os, traceback
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pstcore as P

st = P.Store(sys.argv[1])
seen = set()
def tb(where):
    t = traceback.format_exc()
    key = tuple(t.strip().splitlines()[-4:])
    if key not in seen and len(seen) < 4:
        seen.add(key)
        print('--- %s ---' % where)
        print(t)
for f, d in st.walk():
    try:
        rows = f.contents()
    except Exception:
        tb('folder %s nid 0x%x' % (f.name, f.nid))
        n = st.pst.node((f.nid & ~0x1F) | 0x0E)
        if n:
            tc = P.TC(n)
            print('  contents TC: cols=%d rowsize=%d rgib=%s hid_rowindex=0x%x hnid_rows=0x%x blocks=%d'
                  % (len(tc.cols), tc.rowsize, tc.rgib, tc.hid_rowindex, tc.hnid_rows, len(n.blocks())))
        continue
    for m in rows:
        try:
            msg = st.message(m['nid'])
            msg.headers(); msg.available_bodies(); msg.recipients(); msg.attachments()
        except Exception:
            tb('message 0x%x in %s' % (m['nid'], f.name))
            n = st.pst.node(m['nid'])
            print('  node blocks=%d subs=%s' % (len(n.blocks()), [hex(k) for k in n.subs()]))
print('done')
