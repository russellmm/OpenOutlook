#!/usr/bin/env python3
"""Reference for the C RTF code: writes DIR/<nid>.html (pstrtf.rtf_to_html) and DIR/<nid>.txt (pstsearch.message_text path for RTF)
for every message with an RTF body, the same file names that `openpst FILE rtfall DIR` writes.
usage: pyrtf.py REPO_DIR FILE.pst OUTDIR"""
import os, sys
sys.path.insert(0, sys.argv[1])
import pstcore as P
import pstrtf, pstsearch

st = P.Store(sys.argv[2])
out = sys.argv[3]
os.makedirs(out, exist_ok=True)


def walk(f):
    for sf in f.subfolders():
        try:
            rows = sf.contents()
        except Exception:
            rows = []
        for r in rows:
            try:
                msg = st.message(r['nid'])
                rtf = msg.body_rtf()
            except Exception:
                continue
            if not rtf:
                continue
            try:
                html = pstrtf.rtf_to_html(rtf)
            except Exception as e:
                html = 'EXC %s' % e
            try:
                kind, o = P.rtf_to_html_or_text(rtf)
                # pstsearch strips '<...>' from plain text too (dropping e-mail addresses); only HTML is stripped here
                txt = pstsearch._strip_html(o) if kind == 'html' else o
            except Exception as e:
                txt = 'EXC %s' % e
            with open(os.path.join(out, '%d.html' % r['nid']), 'wb') as fh:
                fh.write(html.encode('utf-8', 'replace'))
            with open(os.path.join(out, '%d.txt' % r['nid']), 'wb') as fh:
                fh.write(txt.encode('utf-8', 'replace'))
        walk(sf)


walk(st.root)
