#!/usr/bin/env python3
"""pstcli.py - command-line front end for pstcore (stage 1, read-only).
  pstcli.py FILE info
  pstcli.py FILE verify
  pstcli.py FILE tree
  pstcli.py FILE list "Top of Personal Folders/Inbox"
  pstcli.py FILE show NID [--body auto|html|rtf|text]
  pstcli.py FILE props NID          (dump raw MAPI properties of a node)
  pstcli.py FILE save NID DIR       (save attachments)
"""
import sys, os, traceback
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pstcore as P


def main(a):
    if len(a) < 2:
        print(__doc__); return 2
    path, cmd, rest = a[0], a[1], a[2:]
    st = P.Store(path)
    pst = st.pst
    if cmd == 'info':
        print('wVer', pst.ver, 'crypt', pst.crypt, 'size', pst.eof)
        print('store name:', st.display_name)
        print('nodes in NBT:', len(pst.nbt), ' blocks in BBT:', len(pst.bbt))
    elif cmd == 'verify':
        n, probs = pst.verify()
        print('blocks checked:', n, ' problems:', len(probs))
        for p in probs[:50]:
            print('  ', p)
        bad = 0
        for f, d in st.walk():
            try:
                for m in f.contents():
                    try:
                        msg = st.message(m['nid'])
                        msg.headers(); msg.available_bodies(); msg.recipients(); msg.attachments()
                        if 'rtf' in msg.available_bodies():
                            msg.body_rtf()
                    except Exception as e:
                        bad += 1
                        print('  message 0x%x in %s: %r' % (m['nid'], f.name, e))
            except Exception as e:
                bad += 1
                print('  folder', f.name, repr(e))
        print('message parse failures:', bad)
    elif cmd == 'tree':
        for f, d in st.walk():
            try:
                cnt = len(f.contents())
            except Exception as e:
                cnt = '?%r' % e
            print('%s%s  [nid 0x%x, %s msgs]' % ('  ' * d, f.name, f.nid, cnt))
    elif cmd == 'list':
        f = st.find_folder(rest[0] if rest else '')
        for m in f.contents():
            print('0x%-8x %-20s %-28s %s' % (m['nid'], str(m.get(0x0E06) or ''), str(m.get(0x0C1A, ''))[:28], m.get(0x0037, '')))
    elif cmd == 'show':
        nid = int(rest[0], 0)
        mode = rest[rest.index('--body') + 1] if '--body' in rest else 'auto'
        m = st.message(nid)
        for k, v in m.headers().items():
            print('%s: %s' % (k, v))
        print('Bodies available:', m.available_bodies())
        print('Attachments:', [(x.filename, x.size) for x in m.attachments()])
        print('-' * 70)
        if mode == 'auto':
            b = m.available_bodies()
            mode = b[0] if b else 'text'
        if mode == 'html':
            print(m.body_html())
        elif mode == 'rtf':
            r = m.body_rtf()
            print(r if r is not None else '(no RTF)')
        else:
            t = m.body_text()
            if t is None and m.body_rtf():
                kind, t = P.rtf_to_html_or_text(m.body_rtf())
            print(t)
    elif cmd == 'props':
        nid = int(rest[0], 0)
        pc = P.PC(pst.node(nid))
        for pid, (t, raw) in sorted(pc.props.items()):
            v = P.decode_value(t, raw)
            print('0x%04X type 0x%04X len %-6d %s' % (pid, t, len(raw), repr(v)[:100]))
    elif cmd == 'save':
        m = st.message(int(rest[0], 0))
        os.makedirs(rest[1], exist_ok=True)
        for x in m.attachments():
            fn = os.path.join(rest[1], os.path.basename(x.filename) or 'attachment')
            open(fn, 'wb').write(x.data); print('saved', fn, len(x.data))
    else:
        print(__doc__); return 2
    return 0


if __name__ == '__main__':
    try:
        sys.exit(main(sys.argv[1:]))
    except Exception:
        traceback.print_exc(); sys.exit(1)
