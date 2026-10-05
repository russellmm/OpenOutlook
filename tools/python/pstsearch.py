#!/usr/bin/env python3
"""pstsearch.py - search a PST (read-only; headless; used by the GUI search box and usable from the command line).

    python3 pstsearch.py FILE "query" [--body] [--folder NAME] [--limit N] [--deleted]

Query syntax (case- and accent-insensitive; all terms must match):
    word                 in subject, sender or recipients (and in the body when --body / "body on")
    "exact phrase"       same, as a phrase
    -word                must NOT match
    from:anna  to:bob  cc:x  subject:invoice  body:refund    restrict a term to one field
    has:attachment       messages with attachments
    is:unread  is:read
    after:2024-01-31  before:2024-12-01   (date sent, or received when there is no sent date)
    folder:Inbox         only folders whose name contains the text

Row-level fields (subject, sender, recipients, dates, flags) come from the contents tables, so they search a 15,000-message file in a few
seconds. Body terms (and unfielded terms with body on) must open each message and are much slower; the call can be cancelled and reports progress.
"""
import os, re, sys, time, struct, datetime, unicodedata, shlex

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import pstcore as P

SUBJECT, SENDER, SENDER2, TO, CC, SENT, RECV, FLAGS, ATTACH = 0x0037, 0x0042, 0x0C1A, 0x0E04, 0x0E03, 0x0039, 0x0E06, 0x0E07, 0x0E1B
FIELDS = ('from', 'to', 'cc', 'subject', 'body')


def fold(s):
    if not s:
        return ''
    if isinstance(s, bytes):
        s = s.decode('utf-8', 'replace')
    s = unicodedata.normalize('NFKD', str(s))
    return ''.join(c for c in s if not unicodedata.combining(c)).casefold()


class Query:
    def __init__(self, text):
        self.terms = []          # (field or None, folded text, negated)
        self.flags = {}          # has_attachment, unread
        self.after = self.before = None
        self.folder = None
        self.error = None
        try:
            toks = shlex.split(text.replace("“", '"').replace("”", '"'), posix=True)
        except ValueError:
            toks = text.split()
        for t in toks:
            neg = t.startswith('-') and len(t) > 1
            if neg:
                t = t[1:]
            m = re.match(r'^(from|to|cc|subject|body|has|is|after|before|folder):(.*)$', t, re.I)
            if m:
                k, v = m.group(1).lower(), m.group(2)
                if k == 'has':
                    if v.lower().startswith('attach'):
                        self.flags['has_attachment'] = not neg
                elif k == 'is':
                    if v.lower() in ('unread', 'read'):
                        self.flags['unread'] = (v.lower() == 'unread') != neg
                elif k in ('after', 'before'):
                    try:
                        d = datetime.datetime.strptime(v, '%Y-%m-%d')
                        if k == 'after':
                            self.after = d + datetime.timedelta(days=1) if False else d
                        else:
                            self.before = d
                    except ValueError:
                        self.error = 'bad date "%s" (use YYYY-MM-DD)' % v
                elif k == 'folder':
                    self.folder = fold(v)
                elif v:
                    self.terms.append((k, fold(v), neg))
            elif t:
                self.terms.append((None, fold(t), neg))

    @property
    def needs_body(self):
        return any(f == 'body' for f, _t, _n in self.terms)

    def empty(self):
        return not (self.terms or self.flags or self.after or self.before or self.folder)


class Hit:
    __slots__ = ('nid', 'folder', 'folder_path', 'subject', 'sender', 'date', 'to', 'size', 'att', 'path')

    def __init__(self, **kw):
        for k, v in kw.items():
            setattr(self, k, v)

    def __repr__(self):
        return '<Hit 0x%x %s | %s | %s>' % (self.nid, self.folder_path, self.sender, self.subject)


def _strip_html(h):
    h = re.sub(r'(?is)<(script|style).*?</\1>', ' ', h)
    h = re.sub(r'(?s)<[^>]+>', ' ', h)
    import html
    return html.unescape(h)


def message_text(msg):
    """Plain text of a message body (text, else HTML stripped, else RTF stripped)."""
    t = msg.body_text()
    if t:
        return t
    h = msg.body_html()
    if h:
        return _strip_html(h)
    try:
        r = msg.body_rtf()
    except Exception:
        r = None
    if r:
        try:
            out = P.rtf_to_html_or_text(r)
            out = out[1] if isinstance(out, tuple) else out
            return _strip_html(out) if '<' in out else out
        except Exception:
            return ''
    return ''


class Searcher:
    def __init__(self, path_or_store):
        self.own = isinstance(path_or_store, str)
        self.st = P.Store(path_or_store) if self.own else path_or_store
        self.cancelled = False

    def close(self):
        if self.own:
            try:
                self.st.pst.close()
            except Exception:
                pass

    def cancel(self):
        self.cancelled = True

    def folders(self, include_deleted=True, only=None):
        deleted = None
        try:
            raw = self.st.store_pc.raw(0x35E3) if self.st.store_pc else None
            if raw:
                deleted = struct.unpack_from('<I', raw, len(raw) - 4)[0]
        except Exception:
            pass
        out = []

        def walk(f, path, in_del):
            for sf in f.subfolders():
                p = path + [sf.name]
                d = in_del or sf.nid == deleted
                out.append((sf, '/'.join(p), d))
                walk(sf, p, d)
        walk(self.st.root, [], False)
        res = []
        for f, p, d in out:
            if d and not include_deleted:
                continue
            if only is not None and f.nid not in only:
                continue
            res.append((f, p))
        return res

    def search(self, query, body=False, include_deleted=True, only_folders=None, limit=None, progress=None):
        """Generator of Hit. `query` is text or a Query. body=True also matches unfielded terms in the message body."""
        q = query if isinstance(query, Query) else Query(query)
        if q.error or q.empty():
            return
        need_body = q.needs_body or (body and any(f is None for f, _t, _n in q.terms))
        flds = self.folders(include_deleted, only_folders)
        n = 0
        for fi, (folder, path) in enumerate(flds):
            if self.cancelled:
                return
            if q.folder and q.folder not in fold(path):
                continue
            if progress:
                progress(fi, len(flds), path)
            try:
                rows = folder.contents()
            except Exception:
                continue
            for r in rows:
                if self.cancelled:
                    return
                if not self._row_ok(r, q):
                    continue
                msg = None
                text = None
                ok = True
                for field, term, neg in q.terms:
                    if field == 'body' or (field is None and need_body and not self._term_in_row(r, term, None)):
                        if msg is None:
                            try:
                                msg = self.st.message(r['nid'])
                                text = fold(message_text(msg))
                            except Exception:
                                text = ''
                        in_body = term in text
                        found = in_body or (field is None and self._term_in_row(r, term, None))
                    else:
                        found = self._term_in_row(r, term, field)
                    if found == neg:
                        ok = False
                        break
                if not ok:
                    continue
                n += 1
                yield Hit(nid=r['nid'], folder=folder.nid, folder_path=path, subject=self._subject(r),
                          sender=r.get(SENDER) or r.get(SENDER2) or '', to=r.get(TO) or '',
                          date=r.get(RECV) or r.get(SENT), size=r.get(0x0E08),
                          att=bool(isinstance(r.get(FLAGS), int) and r.get(FLAGS) & 0x10))
                if limit and n >= limit:
                    return

    # ---- row helpers --------------------------------------------------------------------------------------------
    @staticmethod
    def _subject(r):
        s = r.get(SUBJECT) or ''
        if isinstance(s, str) and s[:1] == '\x01' and len(s) >= 2:
            s = s[2:]
        return s

    def _term_in_row(self, r, term, field):
        def f(pid):
            v = r.get(pid)
            return fold(v if isinstance(v, str) else '') if v else ''
        sender = f(SENDER) + ' ' + f(SENDER2)
        if field == 'subject':
            return term in fold(self._subject(r))
        if field == 'from':
            return term in sender
        if field == 'to':
            return term in f(TO)
        if field == 'cc':
            return term in f(CC)
        return term in fold(self._subject(r)) or term in sender or term in f(TO) or term in f(CC)

    @staticmethod
    def _row_ok(r, q):
        fl = r.get(FLAGS)
        if 'unread' in q.flags and isinstance(fl, int):
            is_unread = not (fl & 1)
            if is_unread != q.flags['unread']:
                return False
        if 'has_attachment' in q.flags:
            has = bool(isinstance(fl, int) and fl & 0x10)
            if has != q.flags['has_attachment']:
                return False
        if q.after or q.before:
            d = r.get(SENT) or r.get(RECV)
            if not isinstance(d, datetime.datetime):
                return False
            if q.after and d < q.after:
                return False
            if q.before and d >= q.before:
                return False
        return True


def main(argv):
    args = [a for a in argv if not a.startswith('--')]
    opt = lambda n: n in argv
    if len(args) < 2:
        print(__doc__)
        return 2
    limit, only = 50, None
    for i, a in enumerate(argv):
        if a == '--limit':
            limit = int(argv[i + 1]); args = [x for x in args if x != argv[i + 1]]
    folder = None
    for i, a in enumerate(argv):
        if a == '--folder':
            folder = argv[i + 1]; args = [x for x in args if x != folder]
    path, text = args[0], ' '.join(args[1:])
    if folder:
        text += ' folder:"%s"' % folder
    s = Searcher(path)
    t = time.time()
    n = 0
    try:
        for h in s.search(text, body=opt('--body'), include_deleted=opt('--deleted') or True, limit=limit):
            n += 1
            print('%-6d 0x%-8x %-28s %-22s %s  %s' % (n, h.nid, h.folder_path[:28], str(h.sender)[:22],
                                                    h.date.strftime('%Y-%m-%d') if h.date else '          ', h.subject[:70]))
    finally:
        s.close()
    print('%d hit(s) in %.1f s' % (n, time.time() - t))
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
