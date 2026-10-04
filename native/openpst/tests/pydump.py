"""pydump.py - print the same TSV as `openpst FILE dump`, using the Python reference reader (pstcore.py).
usage: pydump.py PYTHON_SOURCE_DIR FILE.pst
"""
import sys, datetime
sys.path.insert(0, sys.argv[1])
import pstcore as P


def tsv(s):
    return (s or '').rstrip('\0').replace('\t', ' ').replace('\n', ' ').replace('\r', ' ')


def strip(subj):
    if subj[:1] == '\x01' and len(subj) >= 2:
        subj = subj[2:]
    return subj


def iso(dt):
    return dt.strftime('%Y-%m-%dT%H:%M:%SZ') if dt else ''


def ulen(s):
    return len(s.rstrip('\0').encode('utf-8', 'replace')) if s is not None else 0


def dump_msg(st, nid):
    m = st.message(nid)
    t = m.body_text()
    h = m.body_html()
    r = m.body_rtf() if m.pc.props.get(0x1009) else None
    recs = m.recipients()
    atts = m.attachments()
    typ = {'To': 1, 'Cc': 2, 'Bcc': 3}
    print('D\t%d\t%d\t%d\t%d\t%d\t%d\t%s\t%s\t%s' % (
        nid, ulen(t), ulen(h), len(r) if r else 0, len(recs), len(atts),
        ','.join(str(a.size) for a in atts), ','.join(tsv(a.filename) for a in atts),
        ','.join('%d:%s' % (typ.get(x['type'], 0), tsv(x['email'])) for x in recs)))


def main():
    st = P.Store(sys.argv[2])
    for f, depth in st.walk():
        name = '' if f.nid == P.NID_ROOT_FOLDER else f.name
        cnt = f.pc.get(0x3602, 0) if f.pc and depth else 0
        print('F\t%d\t%d\t%s\t%d' % (depth, f.nid, tsv(name), cnt))
        for v in f.contents():
            subj = strip(v.get(0x0037, ''))
            frm = v.get(0x0C1A) or v.get(0x0042) or v.get(0x0E04) or ''
            print('M\t%d\t%d\t%s\t%d\t%s\t%s' % (v['nid'], v.get(0x0E07, 0) or 0, iso(v.get(0x0E06)), v.get(0x0E08, 0) or 0, tsv(subj), tsv(frm)))
            dump_msg(st, v['nid'])


main()
