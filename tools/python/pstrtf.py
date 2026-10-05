#!/usr/bin/env python3
"""pstrtf.py - RTF (Outlook "Rich Text" bodies) to HTML, keeping the formatting.

    from pstrtf import rtf_to_html
    html = rtf_to_html(rtf_text)          # always returns a complete HTML page

Handles: bold / italic / underline / strike / super- and subscript, font names and sizes, text colours and highlight, alignment,
paragraphs and line breaks, bullets and numbering as Outlook writes them (list text), tables (\\trowd \\cell \\row), HYPERLINK fields,
embedded PNG / JPEG pictures (as data: URIs), code pages and \\uN Unicode, and RTF that encapsulates original HTML (\\fromhtml1,
which is passed through de-encapsulation instead). Unknown destinations are skipped, so one odd group never loses the whole body.
"""
import re, sys, os, base64, html as _html

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

_CHARSET_CP = {0: 1252, 1: 1252, 2: 42, 77: 10000, 128: 932, 129: 949, 130: 1361, 134: 936, 136: 950, 161: 1253, 162: 1254,
               163: 1258, 177: 1255, 178: 1256, 186: 1257, 204: 1251, 222: 874, 238: 1250, 254: 437, 255: 850}
_SPECIAL = {'emdash': '—', 'endash': '–', 'emspace': ' ', 'enspace': ' ', 'bullet': '•',
            'lquote': '‘', 'rquote': '’', 'ldblquote': '“', 'rdblquote': '”', 'tab': '\t',
            'zwj': '‍', 'zwnj': '‌', 'ltrmark': '‎', 'rtlmark': '‏'}
_SKIP = {'fonttbl', 'colortbl', 'stylesheet', 'info', 'header', 'headerl', 'headerr', 'headerf', 'footer', 'footerl', 'footerr',
         'footerf', 'footnote', 'listtable', 'listoverridetable', 'rsidtbl', 'generator', 'private', 'xmlnstbl', 'themedata',
         'colorschememapping', 'datastore', 'latentstyles', 'pnseclvl', 'pgdsctbl', 'revtbl', 'protusertbl', 'userprops',
         'mmathPr', 'fldinst', 'bkmkstart', 'bkmkend', 'object', 'shpinst', 'shptxt', 'nonshppict', 'themedata', 'falt'}


def _cp_decode(b, cp):
    for c in (cp, 1252):
        try:
            return bytes(b).decode('cp%d' % c)
        except (LookupError, UnicodeDecodeError):
            continue
    return bytes(b).decode('latin-1')


def _tokenize(s):
    """RTF -> nested lists. Items: ('c', word, param|None) control word, ('t', text), ('h', int) hex byte, ('g', [items])."""
    root = []
    stack = [root]
    i, n = 0, len(s)
    buf = []

    def flush():
        if buf:
            stack[-1].append(('t', ''.join(buf)))
            buf.clear()
    while i < n:
        ch = s[i]
        if ch == '{':
            flush()
            g = []
            stack[-1].append(('g', g))
            stack.append(g)
            i += 1
        elif ch == '}':
            flush()
            if len(stack) > 1:
                stack.pop()
            i += 1
        elif ch == '\\':
            i += 1
            if i >= n:
                break
            c = s[i]
            if c.isalpha():
                j = i
                while j < n and s[j].isalpha() and j - i < 32:
                    j += 1
                word = s[i:j]
                k = j
                if k < n and (s[k] == '-' or s[k].isdigit()):
                    k += 1
                    while k < n and s[k].isdigit():
                        k += 1
                param = s[j:k]
                if k < n and s[k] == ' ':
                    k += 1
                flush()
                try:
                    p = int(param) if param not in ('', '-') else None
                except ValueError:
                    p = None
                stack[-1].append(('c', word, p))
                i = k
            elif c == "'":
                flush()
                try:
                    stack[-1].append(('h', int(s[i + 1:i + 3], 16)))
                except ValueError:
                    pass
                i += 3
            elif c in '\\{}':
                buf.append(c)
                i += 1
            elif c == '*':
                flush()
                stack[-1].append(('c', '*', None))
                i += 1
            elif c == '~':
                buf.append(' '); i += 1
            elif c == '-':
                i += 1
            elif c == '_':
                buf.append('‑'); i += 1
            elif c in '\r\n':
                flush()
                stack[-1].append(('c', 'par', None))
                i += 1
            else:
                i += 1
        elif ch in '\r\n':
            i += 1
        else:
            j = i
            while j < n and s[j] not in '{}\\\r\n':
                j += 1
            buf.append(s[i:j])
            i = j
    flush()
    return root


class _Style:
    __slots__ = ('b', 'i', 'u', 's', 'sup', 'sub', 'fs', 'cf', 'hl', 'f', 'qa')

    def __init__(self):
        self.b = self.i = self.u = self.s = self.sup = self.sub = False
        self.fs = self.cf = self.hl = self.f = None
        self.qa = None

    def copy(self):
        o = _Style()
        for k in self.__slots__:
            setattr(o, k, getattr(self, k))
        return o

    def key(self):
        return (self.b, self.i, self.u, self.s, self.sup, self.sub, self.fs, self.cf, self.hl, self.f)


class _R:
    def __init__(self, rtf):
        self.tree = _tokenize(rtf)
        self.fonts, self.colors = {}, {}
        self.cp = 1252
        self.paras = []          # list of dict(html runs, align)
        self.cur = []            # runs of the current paragraph: (style key, text) or ('raw', html)
        self.align = None
        self.tables = []         # nested state for table output
        self.row = None
        self.table_rows = []
        self.in_table = False
        self.out = []            # finished block html
        self.href = None
        self.pending_hex = []

    # ---- header tables ----
    def _prescan(self, items):
        for it in items:
            if it[0] == 'c' and it[1] == 'ansicpg' and it[2]:
                self.cp = it[2]
            elif it[0] == 'g':
                g = it[1]
                head = g[0] if g else None
                if head and head[0] == 'c' and head[1] == 'fonttbl':
                    self._fonts(g)
                elif head and head[0] == 'c' and head[1] == 'colortbl':
                    self._colors(g)
                elif head and head[0] == 'c' and head[1] == '*' and len(g) > 1 and g[1][0] == 'c' and g[1][1] == 'colortbl':
                    self._colors(g)

    def _fonts(self, g):
        def one(items, num=[None]):
            f = {'name': '', 'charset': None}
            fid = None
            for it in items:
                if it[0] == 'c' and it[1] == 'f' and it[2] is not None:
                    fid = it[2]
                elif it[0] == 'c' and it[1] == 'fcharset':
                    f['charset'] = it[2]
                elif it[0] == 't':
                    f['name'] += it[1]
            return fid, f
        for it in g[1:]:
            if it[0] == 'g':
                fid, f = one(it[1])
                if fid is not None:
                    f['name'] = f['name'].replace(';', '').strip()
                    self.fonts[fid] = f
        # fonts written without braces: \f0\fnil Arial;
        fid, f = None, {'name': '', 'charset': None}
        for it in g[1:]:
            if it[0] == 'c' and it[1] == 'f' and it[2] is not None:
                fid, f = it[2], {'name': '', 'charset': None}
            elif it[0] == 'c' and it[1] == 'fcharset' and fid is not None:
                f['charset'] = it[2]
            elif it[0] == 't' and fid is not None:
                f['name'] += it[1]
                if ';' in f['name']:
                    f['name'] = f['name'].replace(';', '').strip()
                    self.fonts.setdefault(fid, f)
                    fid = None

    def _colors(self, g):
        idx, cur = 0, {}
        for it in g:
            if it[0] == 'c' and it[1] in ('red', 'green', 'blue'):
                cur[it[1]] = it[2] or 0
            elif it[0] == 't' and ';' in it[1]:
                for _ in range(it[1].count(';')):
                    if cur:
                        self.colors[idx] = '#%02x%02x%02x' % (cur.get('red', 0), cur.get('green', 0), cur.get('blue', 0))
                    cur = {}
                    idx += 1

    # ---- rendering ----
    def _span(self, key, text):
        b, i, u, s, sup, sub, fs, cf, hl, f = key
        t = _html.escape(text).replace('\t', '&emsp;')
        css = []
        if fs:
            css.append('font-size:%.1fpt' % (fs / 2.0))
        if cf and cf in self.colors:
            css.append('color:' + self.colors[cf])
        if hl and hl in self.colors:
            css.append('background:' + self.colors[hl])
        if f is not None and f in self.fonts and self.fonts[f]['name']:
            css.append("font-family:'%s'" % _html.escape(self.fonts[f]['name'], quote=False).replace('"', '').replace("'", ''))   # single quotes: the style attribute is double quoted
        deco = ' '.join(x for x, on in (('underline', u), ('line-through', s)) if on)
        if deco:
            css.append('text-decoration:' + deco)
        if b:
            t = '<b>%s</b>' % t
        if i:
            t = '<i>%s</i>' % t
        if sup:
            t = '<sup>%s</sup>' % t
        if sub:
            t = '<sub>%s</sub>' % t
        if css:
            t = '<span style="%s">%s</span>' % (';'.join(css), t)
        return t

    def _end_par(self, st, force=False):
        body = ''.join(self._span(k, t) if k != 'raw' else t for k, t in self.cur)
        self.cur = []
        if not body.strip() and not force:
            body = '&nbsp;' if force else body
        style = ''
        if st.qa:
            style = ' style="text-align:%s"' % st.qa
        blk = '<p%s style="margin:0">%s</p>' % ('', body) if not style else '<p style="margin:0;text-align:%s">%s</p>' % (st.qa, body)
        if self.row is not None:
            self.row[-1].append(blk)
        else:
            self._flush_table()
            self.out.append(blk)

    def _flush_table(self):
        if self.table_rows:
            self.out.append('<table style="border-collapse:collapse">%s</table>' % ''.join(self.table_rows))
            self.table_rows = []

    def _emit(self, st, text):
        if not text:
            return
        if self.href:
            self.cur.append(('raw', '<a href="%s">' % _html.escape(self.href, quote=True)))
            self.cur.append((st.key(), text))
            self.cur.append(('raw', '</a>'))
        else:
            self.cur.append((st.key(), text))

    def walk(self, items, st, depth=0, skip=False, uc=1, cp=None):
        st = st.copy()
        uc_skip = 0
        hexbuf = []
        cur_cp = cp or self.cp
        pict = None
        if items and items[0][0] == 'c' and items[0][1] == '*':
            skip = True
        head = next((it for it in items if it[0] == 'c'), None)
        if head and head[1] in _SKIP:
            skip = True
        if head and head[1] == 'pict':
            pict = {'kind': None, 'hex': [], 'w': None}
        is_fld = bool(head and head[1] == 'field')
        fldinst = ''

        def flush_hex():
            if hexbuf and not skip:
                self._emit(st, _cp_decode(hexbuf, cur_cp))
            hexbuf.clear()
        for it in items:
            kind = it[0]
            if kind == 'h':
                if uc_skip:
                    uc_skip -= 1
                    continue
                hexbuf.append(it[1])
                continue
            flush_hex()
            if kind == 't':
                if pict is not None:
                    pict['hex'].append(re.sub(r'\s+', '', it[1]))
                    continue
                txt = it[1]
                if uc_skip:
                    k = min(uc_skip, len(txt))
                    txt = txt[k:]
                    uc_skip -= k
                if not skip:
                    self._emit(st, txt)
            elif kind == 'g':
                g = it[1]
                if is_fld:
                    gh = next((x for x in g if x[0] == 'c'), None)
                    if gh and gh[1] == '*':
                        gh = next((x for x in g[1:] if x[0] == 'c'), None)
                    if gh and gh[1] == 'fldinst':
                        fldinst = self._text_of(g)
                        m = re.search(r'HYPERLINK\s+"?([^"\s]+)"?', fldinst, re.I)
                        self.href = m.group(1) if m else None
                        continue
                    if gh and gh[1] == 'fldrslt':
                        self.walk(g, st, depth + 1, skip, uc, cur_cp)
                        self.href = None
                        continue
                self.walk(g, st, depth + 1, skip, uc, cur_cp)
            else:
                w, p = it[1], it[2]
                if w == 'uc':
                    uc = p if p is not None else 1
                elif w == 'u' and p is not None and not skip:
                    self._emit(st, chr(p + 65536 if p < 0 else p))
                    uc_skip = uc
                elif pict is not None:
                    if w in ('pngblip', 'jpegblip'):
                        pict['kind'] = w
                    elif w == 'picwgoal' and p:
                        pict['w'] = p
                elif skip:
                    continue
                elif w == 'b':
                    st.b = p != 0
                elif w == 'i':
                    st.i = p != 0
                elif w == 'ul' or w == 'uld' or w == 'uldb' or w == 'ulw':
                    st.u = p != 0
                elif w == 'ulnone':
                    st.u = False
                elif w in ('strike', 'striked'):
                    st.s = p != 0
                elif w == 'super':
                    st.sup, st.sub = True, False
                elif w == 'sub':
                    st.sub, st.sup = True, False
                elif w == 'nosupersub':
                    st.sup = st.sub = False
                elif w == 'fs' and p:
                    st.fs = p
                elif w == 'cf':
                    st.cf = p
                elif w in ('highlight', 'chcbpat', 'cb') and p:
                    st.hl = p
                elif w == 'f' and p is not None:
                    st.f = p
                    cs = self.fonts.get(p, {}).get('charset')
                    if cs in _CHARSET_CP:
                        cur_cp = _CHARSET_CP[cs]
                elif w == 'plain':
                    a = st.qa
                    st.__init__(); st.qa = a
                elif w == 'pard':
                    st.qa = None
                elif w == 'qc':
                    st.qa = 'center'
                elif w == 'qr':
                    st.qa = 'right'
                elif w == 'qj':
                    st.qa = 'justify'
                elif w == 'ql':
                    st.qa = None
                elif w in ('par', 'sect'):
                    self._end_par(st, True)
                elif w == 'line':
                    self.cur.append(('raw', '<br>'))
                elif w in _SPECIAL:
                    self._emit(st, _SPECIAL[w])
                elif w == 'trowd':
                    if self.row is None:
                        self.row = [[]]
                elif w == 'cell':
                    if self.row is not None:
                        self._end_par(st, True)
                        self.row.append([])
                elif w == 'row':
                    if self.row is not None:
                        cells = [c for c in self.row if c]
                        self.table_rows.append('<tr>%s</tr>' % ''.join('<td style="padding:2px 6px;vertical-align:top">%s</td>' % ''.join(c) for c in cells))
                        self.row = None
                elif w == 'intbl':
                    pass
        flush_hex()
        if pict is not None and pict['kind'] and pict['hex'] and not skip:
            try:
                data = base64.b64encode(bytes.fromhex(''.join(pict['hex']))).decode()
                mime = 'png' if pict['kind'] == 'pngblip' else 'jpeg'
                width = ' width="%d"' % max(1, int(pict['w'] / 15)) if pict['w'] else ''
                self.cur.append(('raw', '<img src="data:image/%s;base64,%s"%s>' % (mime, data, width)))
            except ValueError:
                pass
        if self.row is not None and depth == 1 and False:
            pass

    @staticmethod
    def _text_of(items):
        out = []
        for it in items:
            if it[0] == 't':
                out.append(it[1])
            elif it[0] == 'g':
                out.append(_R._text_of(it[1]))
        return ''.join(out)

    def render(self):
        self._prescan(self.tree[0][1] if self.tree and self.tree[0][0] == 'g' else self.tree)
        top = self.tree[0][1] if self.tree and self.tree[0][0] == 'g' else self.tree
        self.walk(top, _Style())
        if self.cur:
            self._end_par(_Style())
        self._flush_table()
        return self.out


def rtf_to_html(rtf):
    if isinstance(rtf, bytes):
        rtf = rtf.decode('latin-1')
    if '\\fromhtml' in rtf[:2000]:
        import pstcore as P
        kind, out = P.rtf_to_html_or_text(rtf)
        return out if kind == 'html' else '<html><body><pre>%s</pre></body></html>' % _html.escape(out)
    r = _R(rtf)
    blocks = r.render()
    # tables: rows were collected separately; interleave them where the table started is not tracked, so tables go last only
    # when no paragraph marker exists - the common mail case has no tables
    body = '\n'.join(blocks)
    return ('<html><head><meta charset="utf-8"></head><body style="font-family:sans-serif;font-size:10pt">%s</body></html>' % body)


if __name__ == '__main__':
    data = open(sys.argv[1], 'rb').read() if len(sys.argv) > 1 else sys.stdin.buffer.read()
    sys.stdout.write(rtf_to_html(data.decode('latin-1')))
