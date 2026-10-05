#!/usr/bin/env python3
"""pstgui.py - GTK4 viewer/editor for PST files. Reads with pstcore.py; edits (only after "Allow editing" is
switched on) through pstactions.py. Close Outlook first; work on a copy until you trust it.
Right-click folders/messages for the menu; drag messages or folders onto a folder (hold Ctrl to copy messages).
Ubuntu packages:  sudo apt install python3-gi gir1.2-gtk-4.0 gir1.2-webkit-6.0
Run:  python3 pstgui.py [file.pst]
"""
import sys, os, re, html as htmlmod, traceback, warnings
warnings.filterwarnings('ignore', category=DeprecationWarning)
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import gi
gi.require_version('Gtk', '4.0')
from gi.repository import Gtk, Gdk, GLib, Gio, GObject, Pango
try:
    gi.require_version('WebKit', '6.0')
    from gi.repository import WebKit
except (ValueError, ImportError):
    WebKit = None
import base64
import pstcore as P
import pstedit as E
import pstactions as A
import pstsearch as S
import threading

CSP = ('<meta http-equiv="Content-Security-Policy" content="default-src \'none\'; '
       'img-src data:; style-src \'unsafe-inline\'; font-src data:">')


def esc(s):
    return GLib.markup_escape_text(str(s if s is not None else ''))


def strip_tags(h):
    h = re.sub(r'(?is)<(script|style).*?</\1>', '', h)
    h = re.sub(r'(?i)<br\s*/?>|</p>|</div>|</tr>', '\n', h)
    return htmlmod.unescape(re.sub(r'<[^>]+>', '', h))


def human(n):
    n = float(n or 0)
    for u in ('B', 'KB', 'MB', 'GB'):
        if n < 1024 or u == 'GB':
            return '%d %s' % (n, u) if u == 'B' else '%.1f %s' % (n, u)
        n /= 1024


class Win(Gtk.ApplicationWindow):
    def __init__(self, app):
        super().__init__(application=app, title='OpenOutlook - PST Viewer')
        sc = 1.5
        try:
            sc = float(os.environ.get('OPENOUTLOOK_SCALE', '1.5'))
        except ValueError:
            pass
        w0, h0 = int(1400 * sc), int(850 * sc)
        try:
            mon = Gdk.Display.get_default().get_monitors().get_item(0)
            g = mon.get_geometry()
            w0, h0 = min(w0, int(g.width * 0.96)), min(h0, int(g.height * 0.92))
        except Exception:
            pass
        self.set_default_size(w0, h0)
        self.set_resizable(True)
        st = Gtk.Settings.get_default()
        if st:
            st.set_property('gtk-enable-animations', False)
            try:
                st.set_property('gtk-xft-dpi', int(96 * sc * 1024))      # text 50 % larger (OPENOUTLOOK_SCALE=1 for the old size)
            except Exception:
                pass     # popups are slow/flickery under software rendering
        self.store = None
        self.msg = None
        self.mode = None
        self.pst = {}                     # path -> {'store', 'sess', 'info'}: every open PST file
        self.busy = False
        self._sgen = 0
        self._searcher = None
        self.searching = False

        hb = Gtk.HeaderBar()
        hb.set_show_title_buttons(True)
        hb.set_decoration_layout(':minimize,maximize,close')      # always show minimize / maximize / close
        self.set_titlebar(hb)
        b = Gtk.Button(label='Open PST...')
        b.connect('clicked', self.on_open)
        hb.pack_start(b)
        self.status = Gtk.Label(label='Read-only viewer', xalign=0)
        self.path = None
        self.info = {}
        self.sess = None
        self.editing = False
        self.edit_sw = Gtk.Switch(valign=Gtk.Align.CENTER)
        self.edit_sw.connect('state-set', self.on_edit_toggle)
        self.mode_lbl = Gtk.Label(label='READ-ONLY')
        hb.pack_end(self.edit_sw)
        hb.pack_end(Gtk.Label(label='Editing'))
        self.new_btn = Gtk.Button(label='New folder...')
        self.new_btn.connect('clicked', lambda *_: self.act_new_folder())
        hb.pack_start(self.new_btn)
        self.close_btn = Gtk.Button(label='Close PST')
        self.close_btn.set_tooltip_text('Close the PST file that contains the selected folder')
        self.close_btn.connect('clicked', lambda *_: self.act_close_pst())
        hb.pack_start(self.close_btn)
        self.sentry = Gtk.SearchEntry(placeholder_text='Search all folders (Ctrl+F)...', width_chars=22)
        self.sentry.connect('activate', lambda e: self.start_search(e.get_text()))
        self.sentry.connect('search-changed', self._on_search_changed)
        self.sentry.connect('stop-search', lambda e: self._end_search(True, True))
        self.body_chk = Gtk.CheckButton(label='Bodies')
        self.body_chk.set_tooltip_text('Also search inside message bodies (slower)')
        hb.pack_start(self.sentry)
        hb.pack_start(self.body_chk)
        self.cols_btn = Gtk.MenuButton(label='Columns')
        self.cols_btn.set_tooltip_text('Set the width of the message list columns')
        self.cols_pop = Gtk.Popover()
        self.cols_btn.set_popover(self.cols_pop)
        hb.pack_end(self.cols_btn)
        self._make_actions()

        outer = Gtk.Paned(orientation=Gtk.Orientation.HORIZONTAL, position=int(280 * sc))
        inner = Gtk.Paned(orientation=Gtk.Orientation.VERTICAL, position=int(h0 * 0.38))
        for pn in (outer, inner):
            pn.set_wide_handle(True)                      # a visible, easy-to-grab divider
            pn.set_resize_start_child(False); pn.set_resize_end_child(True)
            pn.set_shrink_start_child(False); pn.set_shrink_end_child(True)
        ov = Gtk.Overlay()
        ov.set_child(outer)
        for edge, cursor, ha, va, w_, h_ in (
                (Gdk.SurfaceEdge.SOUTH, 's-resize', Gtk.Align.FILL, Gtk.Align.END, -1, 5),
                (Gdk.SurfaceEdge.EAST, 'e-resize', Gtk.Align.END, Gtk.Align.FILL, 5, -1),
                (Gdk.SurfaceEdge.WEST, 'w-resize', Gtk.Align.START, Gtk.Align.FILL, 5, -1),
                (Gdk.SurfaceEdge.SOUTH_EAST, 'se-resize', Gtk.Align.END, Gtk.Align.END, 18, 18),
                (Gdk.SurfaceEdge.SOUTH_WEST, 'sw-resize', Gtk.Align.START, Gtk.Align.END, 18, 18)):
            g = Gtk.Box(halign=ha, valign=va, width_request=w_, height_request=h_)
            g.set_cursor_from_name(cursor)
            gc = Gtk.GestureClick(button=1)
            gc.connect('pressed', self._edge_press, edge)
            g.add_controller(gc)
            ov.add_overlay(g)
        self.set_child(ov)

        # folders
        self.ftree = Gtk.TreeStore(str, GObject.TYPE_PYOBJECT, str)
        self.fview = Gtk.TreeView(model=self.ftree, headers_visible=False)
        self.fview.append_column(Gtk.TreeViewColumn('Folder', Gtk.CellRendererText(), text=0))
        self.fview.get_selection().connect('changed', self.on_folder)
        sw = Gtk.ScrolledWindow(child=self.fview)
        outer.set_start_child(sw)
        outer.set_end_child(inner)

        # messages: nid, date, from, subject, size, unread, size_bytes
        self.mstore = Gtk.ListStore(int, str, str, str, str, int, int, str, str, str)
        self.mview = Gtk.TreeView(model=self.mstore)
        self.cols = {}
        rp = Gtk.CellRendererPixbuf()
        ac = Gtk.TreeViewColumn('', rp, icon_name=8)
        ac.set_fixed_width(32); ac.set_resizable(False)
        ac.set_sort_column_id(8)
        self.mview.append_column(ac)
        for i, (title, col, w) in enumerate([('From', 2, 220), ('Subject', 3, 520), ('Received', 1, 160), ('Size', 4, 80)]):
            r = Gtk.CellRendererText(ellipsize=Pango.EllipsizeMode.END)
            c = Gtk.TreeViewColumn(title, r, text=col)
            c.add_attribute(r, 'weight', 5)
            c.set_resizable(True); c.set_fixed_width(w)
            self.cols[title] = c
            c.set_sort_column_id(6 if col == 4 else col)
            self.mview.append_column(c)
        pb = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=6)
        for m in ('top', 'bottom', 'start', 'end'):
            getattr(pb, 'set_margin_' + m)(10)
        pb.append(Gtk.Label(label='Column widths (pixels)', xalign=0))
        for title, c in self.cols.items():
            row = Gtk.Box(spacing=8)
            row.append(Gtk.Label(label=title, xalign=0, width_chars=10))
            sp = Gtk.SpinButton.new_with_range(30, 2000, 10)
            sp.set_value(c.get_fixed_width())
            sp.connect('value-changed', lambda b, c=c: c.set_fixed_width(int(b.get_value())))
            row.append(sp)
            pb.append(row)
        self.cols_pop.set_child(pb)
        r = Gtk.CellRendererText(ellipsize=Pango.EllipsizeMode.END)
        self.fcol = Gtk.TreeViewColumn('Folder', r, text=7)
        self.fcol.set_resizable(True); self.fcol.set_fixed_width(180); self.fcol.set_visible(False)
        self.fcol.set_sort_column_id(7)
        self.mview.append_column(self.fcol)
        self.mstore.set_sort_column_id(1, Gtk.SortType.DESCENDING)
        self.mview.get_selection().set_mode(Gtk.SelectionMode.MULTIPLE)
        self.mview.get_selection().connect('changed', self.on_message)
        self._wire_editing()
        inner.set_start_child(Gtk.ScrolledWindow(child=self.mview))

        # reader pane
        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=4)
        self.hdr = Gtk.Label(xalign=0, yalign=0, selectable=True, wrap=True, use_markup=True)
        self.hdr.set_margin_start(8); self.hdr.set_margin_top(6)
        box.append(self.hdr)
        bar = Gtk.Box(spacing=6); bar.set_margin_start(8)
        self.btn = {}
        grp = None
        for key, label in (('html', 'HTML'), ('rtf', 'Rich Text'), ('text', 'Plain Text')):
            t = Gtk.ToggleButton(label=label)
            if grp: t.set_group(grp)
            else: grp = t
            t.connect('toggled', self.on_mode, key)
            bar.append(t); self.btn[key] = t
        self.hdrbtn = Gtk.ToggleButton(label='Headers')
        self.hdrbtn.connect('toggled', lambda *_: self.show_body())
        bar.append(self.hdrbtn)
        self.allow_remote = False
        box.append(bar)
        self.attbox = Gtk.Box(spacing=6); self.attbox.set_margin_start(8)
        attsw = Gtk.ScrolledWindow(child=self.attbox, hscrollbar_policy=Gtk.PolicyType.AUTOMATIC,
                                   vscrollbar_policy=Gtk.PolicyType.NEVER, propagate_natural_height=True)
        attsw.set_hexpand(True)
        attsw.set_min_content_width(100)
        box.append(attsw)          # many attachments scroll sideways instead of widening the whole window

        self.stack = Gtk.Stack(vexpand=True, hexpand=True)
        self.tv = Gtk.TextView(editable=False, wrap_mode=Gtk.WrapMode.WORD_CHAR, monospace=True)
        self.tv.set_left_margin(8); self.tv.set_right_margin(8)
        self.stack.add_named(Gtk.ScrolledWindow(child=self.tv), 'text')
        self.web = None
        if WebKit:
            self.web = WebKit.WebView()
            st = self.web.get_settings()
            st.set_enable_javascript(False)
            st.set_auto_load_images(True)
            self.web.connect('decide-policy', self.on_policy)
            self.stack.add_named(self.web, 'web')
        box.append(self.stack)
        box.append(self.status)
        inner.set_end_child(box)

        if not WebKit:
            self.status.set_text('WebKit not found: HTML shown as text. Install gir1.2-webkit-6.0')
        kc = Gtk.EventControllerKey()
        kc.set_propagation_phase(Gtk.PropagationPhase.CAPTURE)
        kc.connect('key-pressed', self._on_win_key)
        self.add_controller(kc)
        self._set_editing(True)          # editing is on by default; the switch is a read-only lock

    # ---- file/folders ----
    def on_open(self, *_):
        self.pick_pst('Open PST file', self.open_path)

    def pick_pst(self, title, cb):
        """File chooser for a .pst. Under WSL the stock chooser lists only the Linux folders, so there we offer the Windows drives
        (/mnt/c, /mnt/f, ...) as buttons and a path box; elsewhere (Windows, native Linux) the normal chooser is used."""
        drives = []
        if sys.platform.startswith('linux') and os.path.isdir('/mnt'):
            for n in sorted(os.listdir('/mnt')):
                p = os.path.join('/mnt', n)
                if n not in ('wsl', 'wslg') and os.path.isdir(p):
                    drives.append((n.upper() + ':' if len(n) == 1 else n, p))
        def stock(folder=None):
            d = Gtk.FileDialog(title=title)
            f = Gtk.FileFilter(); f.set_name('Outlook data files'); f.add_pattern('*.pst'); f.add_pattern('*.PST')
            fl = Gio.ListStore.new(Gtk.FileFilter); fl.append(f); d.set_filters(fl)
            if folder and os.path.isdir(folder):
                d.set_initial_folder(Gio.File.new_for_path(folder))
            def done(dlg, res):
                try:
                    g = dlg.open_finish(res)
                except GLib.Error:
                    return
                cb(g.get_path())
            d.open(self, None, done)
        if not drives:
            stock(getattr(self, '_last_dir', None)); return
        w = Gtk.Window(title=title, modal=True, transient_for=self, default_width=560, resizable=True)
        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=10)
        for m in ('top', 'bottom', 'start', 'end'):
            getattr(box, 'set_margin_' + m)(14)
        box.append(Gtk.Label(label='Choose a drive (or type a path), then Browse... or Open:', xalign=0))
        row = Gtk.Box(spacing=6)
        ent = Gtk.Entry(hexpand=True, activates_default=True,
                        text=getattr(self, '_last_dir', None) or os.path.expanduser('~'))
        for label, p in drives:
            b = Gtk.Button(label=label)
            b.connect('clicked', lambda _b, p=p: ent.set_text(p))
            row.append(b)
        box.append(row)
        box.append(ent)
        btns = Gtk.Box(spacing=8, halign=Gtk.Align.END)
        cancel = Gtk.Button(label='Cancel'); browse = Gtk.Button(label='Browse...'); ok = Gtk.Button(label='Open')
        ok.add_css_class('suggested-action')
        for b in (cancel, browse, ok):
            btns.append(b)
        box.append(btns)
        w.set_child(box); w.set_default_widget(ok)
        def folder_of(t):
            t = t.strip()
            return t if os.path.isdir(t) else (os.path.dirname(t) if t else None)
        def do_browse(*_):
            fo = folder_of(ent.get_text())
            if fo:
                self._last_dir = fo
            w.close(); stock(fo)
        def do_open(*_):
            t = ent.get_text().strip()
            if os.path.isfile(t):
                self._last_dir = os.path.dirname(t)
                w.close(); cb(t)
            elif os.path.isdir(t):
                do_browse()
            else:
                self.status.set_text('No such file or folder: %s' % t)
        cancel.connect('clicked', lambda *_: w.close())
        browse.connect('clicked', do_browse)
        ok.connect('clicked', do_open)
        w.present(); ent.grab_focus(); ent.set_position(-1)

    def _opened(self, dlg, res):
        try:
            g = dlg.open_finish(res)
        except GLib.Error:
            return
        self.open_path(g.get_path())

    def _open_ctx(self, path):
        """(Re)open one PST file for reading and read its folder structure."""
        try:
            st = P.Store(path)
        except Exception as e:
            self.err('Cannot open %s: %s' % (path, e)); return False
        old = self.pst.get(path)
        if old:
            try:
                old['store'].pst.close()
            except Exception:
                pass
        sess = A.Session(path)
        try:
            info = sess.folder_info()
        except Exception as e:
            info = {}
            self.err('Cannot read folder structure for editing: %s' % e)
        self.pst[path] = {'store': st, 'sess': sess, 'info': info}
        return True

    def _activate(self, path):
        """Make `path` the PST the editing code works on (self.path / store / sess / info)."""
        c = self.pst.get(path)
        if not c:
            return False
        self.path, self.store, self.sess, self.info = path, c['store'], c['sess'], c['info']
        return True

    def _title(self):
        n = len(self.pst)
        self.set_title('OpenOutlook - ' + (os.path.basename(self.path) if n == 1 and self.path else '%d PST files open' % n if n else 'PST Viewer'))

    def open_path(self, path, select=None):
        """Open a PST (or refresh it when already open) next to the ones that are open; selects folder nid `select` of it."""
        path = os.path.abspath(path)
        if not self._open_ctx(path):
            return
        self._activate(path)
        self._title()
        self._rebuild_tree()
        self._refresh_actions()
        if select:
            it = self._find_iter(select, path=path)
            if it:
                self.fview.get_selection().select_iter(it)
                self.fview.scroll_to_cell(self.ftree.get_path(it), None, False, 0, 0)
        elif len(self.pst) > 1:
            it = self._find_iter(self.pst[path]['sess'].ipm_root, path=path)
            if it:
                self.fview.get_selection().select_iter(it)

    def _rebuild_tree(self):
        self._loading = True
        try:
            self.ftree.clear(); self.mstore.clear()
            for p in self.pst:
                self._build_tree(p)
            self.fview.expand_all()
        finally:
            self._loading = False

    def act_close_pst(self):
        f = self.cur_folder()
        model, it = self.fview.get_selection().get_selected()
        if self.busy or not it:
            self.status.set_text('Select a folder of the PST to close'); return
        path = model[it][2]
        c = self.pst.pop(path, None)
        if c:
            try:
                c['store'].pst.close()
            except Exception:
                pass
        self._end_search(True, False)
        self.clear_reader()
        if self.pst:
            self._activate(next(iter(self.pst)))
        else:
            self.path = self.store = self.sess = None
            self.info = {}
        self._title()
        self._rebuild_tree()
        self._refresh_actions()
        self.status.set_text('Closed %s' % os.path.basename(path))

    def _build_tree(self, path):
        """Like Outlook: the store's name at the top, the mailbox folders (the 'Top of Outlook data file' subtree) directly below it.
        The hidden system folders next to it (views, search roots, ...) are not shown. One top-level row per open PST."""
        c = self.pst[path]
        store, ipm = c['store'], getattr(c['sess'], 'ipm_root', None)
        top = None
        try:
            for f in store.root.subfolders():
                if f.nid == ipm:
                    top = f
                    break
        except Exception:
            top = None
        if top is None:
            self._add(None, store.root, path)
            return
        name = getattr(store, 'display_name', None) or os.path.basename(path)
        try:
            unread = top.pc.get(0x3603) if top.pc else None
        except Exception:
            unread = None
        it = self.ftree.append(None, [name + (' (%d)' % unread if unread else ''), top, path])
        for sf in top.subfolders():
            self._add(it, sf, path)

    def _find_iter(self, nid, parent=None, path=None):
        if path is None:
            path = self.path
        it = self.ftree.iter_children(parent) if parent else self.ftree.get_iter_first()
        while it:
            if self.ftree[it][1].nid == nid and self.ftree[it][2] == path:
                return it
            sub = self._find_iter(nid, it, path)
            if sub:
                return sub
            it = self.ftree.iter_next(it)
        return None

    def _add(self, parent, folder, path):
        try:
            unread = folder.pc.get(0x3603) if folder.pc else None
        except Exception:
            unread = None
        label = folder.name + (' (%d)' % unread if unread else '')
        it = self.ftree.append(parent, [label, folder, path])
        try:
            for sf in folder.subfolders():
                self._add(it, sf, path)
        except Exception as e:
            print('folder error', folder.name, e)

    def on_folder(self, sel):
        if getattr(self, '_loading', False):
            return
        if self.busy:
            return
        self._end_search(True, False)
        model, it = sel.get_selected()
        if it:
            self._activate(model[it][2])
        self.mstore.clear(); self.clear_reader()
        self._refresh_actions()
        if not it:
            return
        f = model[it][1]
        try:
            rows = f.contents()
        except Exception as e:
            self.err('Cannot read folder: %s' % e); return
        for m in rows:
            d = m.get(0x0E06) or m.get(0x0039)
            subj = m.get(0x0037, '') or ''
            if subj[:1] == '\x01' and len(subj) > 1:
                subj = subj[2:]
            sz = m.get(0x0E08, 0) or 0
            read = bool((m.get(0x0E07, 0) or 0) & 1)
            self.mstore.append([m['nid'], d.strftime('%Y-%m-%d %H:%M') if d else '',
                                m.get(0x0C1A) or m.get(0x0042) or m.get(0x0E04, '') or '', subj, human(sz),
                                400 if read else 700, sz, '',
                                'mail-attachment-symbolic' if (m.get(0x0E07, 0) or 0) & 0x10 else '', self.path])
        self.status.set_text('%s: %d messages' % (f.name, len(rows)))

    # ---- search (all folders; runs on a worker thread with its own read handle) ----
    def _edge_press(self, gesture, n, x, y, edge):
        """Resize handles along the window edges (the custom title bar leaves no frame to grab)."""
        try:
            surf = self.get_native().get_surface()
            t = gesture.get_widget().translate_coordinates(self, x, y)
            surf.begin_resize(edge, gesture.get_device(), 1, t[0] if t else x, t[1] if t else y, Gdk.CURRENT_TIME)
        except Exception as e:
            print('resize not available:', e, file=sys.stderr)

    def _on_win_key(self, ctl, keyval, keycode, state):
        if keyval == Gdk.KEY_F11:                       # full screen on / off
            if self.is_fullscreen(): self.unfullscreen()
            else: self.fullscreen()
            return True
        if keyval in (Gdk.KEY_f, Gdk.KEY_F) and state & Gdk.ModifierType.CONTROL_MASK:
            self.sentry.grab_focus(); return True
        return False

    def _on_search_changed(self, entry):
        if not entry.get_text().strip() and self.searching:
            self._end_search(False, False)
            self._restore_folder()

    def _restore_folder(self):
        sel = self.fview.get_selection()
        if sel.get_selected()[1]:
            self.on_folder(sel)
        else:
            self.mstore.clear()

    def _end_search(self, clear_entry, restore=False):
        """Stop a running search and leave search mode. restore=True re-lists the selected folder (not needed when the caller does)."""
        self._sgen += 1
        s = self._searcher
        if s:
            s.cancel()
        was = self.searching
        self.searching = False
        self.fcol.set_visible(False)
        if clear_entry and self.sentry.get_text():
            self.sentry.set_text('')
        if was and clear_entry and restore and not self.busy:
            self._restore_folder()

    def start_search(self, text):
        text = text.strip()
        if not self.pst or self.busy:
            return
        if not text:
            self._end_search(False, True); return
        q = S.Query(text)
        if q.error:
            self.status.set_text('Search: ' + q.error); return
        if q.empty():
            return
        self._sgen += 1
        if self._searcher:
            self._searcher.cancel()
        gen, paths, body = self._sgen, list(self.pst), self.body_chk.get_active()
        self.searching = True
        self.mstore.clear(); self.clear_reader()
        self.fcol.set_visible(True)
        self.status.set_text('Searching...')
        batch, count = [], [0]
        LIMIT = 2000

        def flush():
            if batch:
                items = list(batch); del batch[:]
                GLib.idle_add(self._search_add, gen, items)

        def work():
            err = None
            try:
                last = [0.0]
                import time as _t
                def prog(i, n, p):
                    if _t.time() - last[0] > 0.25:
                        last[0] = _t.time()
                        GLib.idle_add(self._search_status, gen, 'Searching %d/%d: %s  (%d found)' % (i + 1, n, p, count[0]))
                for pth in paths:
                    if gen != self._sgen or count[0] >= LIMIT:
                        break
                    sr = S.Searcher(pth)
                    self._searcher = sr
                    tag = os.path.basename(pth) + ': ' if len(paths) > 1 else ''
                    try:
                        for h in sr.search(q, body=body, include_deleted=True, progress=prog, limit=LIMIT - count[0]):
                            if gen != self._sgen:
                                break
                            h.path = pth
                            if tag:
                                h.folder_path = tag + h.folder_path
                            batch.append(h); count[0] += 1
                            if len(batch) >= 40:
                                flush()
                    finally:
                        sr.close()
            except Exception as e:
                traceback.print_exc(); err = '%s: %s' % (type(e).__name__, e)
            finally:
                flush()
            GLib.idle_add(self._search_done, gen, count[0], err, LIMIT)
        threading.Thread(target=work, daemon=True).start()

    def _search_status(self, gen, text):
        if gen == self._sgen and self.searching:
            self.status.set_text(text)
        return False

    def _search_add(self, gen, hits):
        if gen != self._sgen:
            return False
        for h in hits:
            d = h.date
            sz = h.size or 0
            self.mstore.append([h.nid, d.strftime('%Y-%m-%d %H:%M') if hasattr(d, 'strftime') else '',
                                str(h.sender or ''), h.subject or '', human(sz), 400, sz if isinstance(sz, int) else 0, h.folder_path,
                                'mail-attachment-symbolic' if getattr(h, 'att', False) else '', getattr(h, 'path', self.path)])
        return False

    def _search_done(self, gen, n, err, limit):
        if gen != self._sgen:
            return False
        self._search_finished = True
        if err:
            self.status.set_text('Search failed: ' + err)
        else:
            self.status.set_text('%d message(s) found%s' % (n, ' (first %d shown - refine the search)' % limit if n >= limit else ''))
        return False

    # ---- reader ----
    def clear_reader(self):
        self.msg = None
        self.hdr.set_markup('')
        self.tv.get_buffer().set_text('')
        while (c := self.attbox.get_first_child()):
            self.attbox.remove(c)
        if self.web: self.web.load_html('', None)

    def on_message(self, sel):
        if getattr(self, '_loading', False) or self.busy:
            return
        model, paths = sel.get_selected_rows()
        self._refresh_actions()
        if getattr(self, '_msg_timer', 0):
            GLib.source_remove(self._msg_timer); self._msg_timer = 0
        if len(paths) != 1:
            if paths:
                self.clear_reader()
                self.status.set_text('%d messages selected' % len(paths))
            return
        row = model[model.get_iter(paths[0])]
        nid, mpath = row[0], row[9]
        # load the reader after a short pause so quick Ctrl/Shift-clicks are not held up by rendering
        self._msg_timer = GLib.timeout_add(180, self._load_message, nid, mpath)

    def _load_message(self, nid, mpath=None):
        self._msg_timer = 0
        if self.busy or len(self.sel_msgs()) != 1:
            return False
        if mpath and mpath in self.pst:
            self._activate(mpath)
        self._show_message(nid)
        return False

    def _show_message(self, nid):
        try:
            m = self.store.message(nid)
            self.msg = m
            self.atts = m.attachments()
        except Exception as e:
            traceback.print_exc(); self.err('Cannot read message: %s' % e); return
        h = m.headers()
        lines = ['<b>%s</b>' % esc(h['Subject'])]
        frm = h['From'] + (' &lt;%s&gt;' % esc(h['From address']) if h['From address'] else '')
        lines.append('From: ' + (esc(h['From']) + (' &lt;%s&gt;' % esc(h['From address']) if h['From address'] else '')))
        for k in ('To', 'Cc', 'Bcc'):
            if h[k]: lines.append('%s: %s' % (k, esc(h[k])))
        d = h['Sent'] or h['Received']
        if d: lines.append('Date: %s UTC' % d)
        self.hdr.set_markup('\n'.join(lines))
        while (c := self.attbox.get_first_child()):
            self.attbox.remove(c)
        for a in self.atts:
            b = Gtk.Button(label='Save: %s (%s)' % (a.filename, human(a.size)))
            b.connect('clicked', self.on_save, a)
            self.attbox.append(b)
        av = m.available_bodies()
        for k, t in self.btn.items():
            t.set_sensitive(k in av)
        want = next((k for k in ('html', 'rtf', 'text') if k in av), None)
        self.mode = want
        if want:
            self.btn[want].set_active(True)
        self.show_body()

    def on_mode(self, btn, key):
        if btn.get_active():
            self.mode = key
            self.show_body()

    def inline_cids(self, h):
        for a in self.atts:
            if a.cid and a.data:
                mime = a.mime or 'application/octet-stream'
                uri = 'data:%s;base64,%s' % (mime, base64.b64encode(a.data).decode())
                h = h.replace('cid:' + a.cid, uri)
        return h

    def show_text(self, t):
        self.tv.get_buffer().set_text(t or '')
        self.stack.set_visible_child_name('text')

    def show_html(self, h):
        if not self.web:
            self.show_text(strip_tags(h)); return
        h = self.inline_cids(h)
        m = re.search(r'(?i)<head[^>]*>', h)
        h = (h[:m.end()] + CSP + h[m.end():]) if m else CSP + h
        self.web.load_html(h, 'about:blank')
        self.stack.set_visible_child_name('web')

    def show_body(self):
        m = self.msg
        if not m:
            return
        try:
            if self.hdrbtn.get_active():
                self.show_text(m.transport_headers() or '(no transport headers stored)'); return
            if self.mode == 'html':
                self.show_html(m.body_html() or '')
            elif self.mode == 'rtf':
                rtf = m.body_rtf() or ''
                try:
                    import pstrtf
                    self.show_html(pstrtf.rtf_to_html(rtf))
                except Exception:
                    traceback.print_exc()
                    kind, out = P.rtf_to_html_or_text(rtf)
                    self.show_html(out) if kind == 'html' else self.show_text(out)
            elif self.mode == 'text':
                self.show_text(m.body_text() or '')
            else:
                self.show_text('(message has no body)')
        except Exception as e:
            traceback.print_exc(); self.show_text('Error rendering body: %s' % e)

    def on_policy(self, web, decision, dtype):
        if dtype == WebKit.PolicyDecisionType.NAVIGATION_ACTION:
            uri = decision.get_navigation_action().get_request().get_uri()
            if uri and not uri.startswith(('about:', 'data:')):
                decision.ignore(); return True
        return False

    def on_save(self, btn, att):
        d = Gtk.FileDialog(); d.set_initial_name(att.filename)
        def done(dlg, res):
            try:
                g = dlg.save_finish(res)
            except GLib.Error:
                return
            open(g.get_path(), 'wb').write(att.data)
            self.status.set_text('Saved %s' % g.get_path())
        d.save(self, None, done)

    def err(self, text):
        self.status.set_text(text)
        print(text, file=sys.stderr)

    # =====================================================================================================
    # editing
    # =====================================================================================================
    def _make_actions(self):
        self.acts = {}
        grp = Gio.SimpleActionGroup()
        for name, fn in (('new-folder', self.act_new_folder), ('rename-folder', self.act_rename_folder),
                         ('delete-folder', self.act_delete_folder), ('move-folder', self.act_move_folder),
                         ('move-msgs', self.act_move_msgs), ('copy-msgs', self.act_copy_msgs), ('copy-other', self.act_copy_other),
                         ('delete-msgs', self.act_delete_msgs)):
            a = Gio.SimpleAction.new(name, None)
            a.connect('activate', lambda _a, _p, fn=fn: fn())
            grp.add_action(a)
            self.acts[name] = a
        self.insert_action_group('ed', grp)
        fm = Gio.Menu()
        fm.append('New subfolder...', 'ed.new-folder')
        fm.append('Rename...', 'ed.rename-folder')
        fm.append('Move to...', 'ed.move-folder')
        fm.append('Delete', 'ed.delete-folder')
        mm = Gio.Menu()
        mm.append('Move to...', 'ed.move-msgs')
        mm.append('Copy to...', 'ed.copy-msgs')
        mm.append('Copy to another PST file...', 'ed.copy-other')
        mm.append('Delete', 'ed.delete-msgs')
        self.fmenu, self.mmenu = fm, mm

    def _wire_editing(self):
        for v in (self.fview, self.mview):
            v.set_enable_search(False)       # GTK's type-ahead search popup triggers a CSS-node warning
        for view, menu in ((self.fview, 'fmenu'), (self.mview, 'mmenu')):
            g = Gtk.GestureClick(button=3)
            g.connect('pressed', self._on_rclick, view, menu)
            view.add_controller(g)
            k = Gtk.EventControllerKey()
            k.connect('key-pressed', self._on_key, view)
            view.add_controller(k)
        ds = Gtk.DragSource(actions=Gdk.DragAction.COPY | Gdk.DragAction.MOVE)
        ds.connect('prepare', self._drag_prepare, self.mview, 'msg')
        self.mview.add_controller(ds)
        ds2 = Gtk.DragSource(actions=Gdk.DragAction.MOVE)
        ds2.connect('prepare', self._drag_prepare, self.fview, 'fld')
        self.fview.add_controller(ds2)
        dt = Gtk.DropTarget.new(GObject.TYPE_STRING, Gdk.DragAction.COPY | Gdk.DragAction.MOVE)
        dt.connect('drop', self._on_drop)
        self.fview.add_controller(dt)

    # ---- helpers ----
    @staticmethod
    def _row_at(view, x, y, dest=False):
        bx, by = view.convert_widget_to_bin_window_coords(int(x), int(y))
        r = view.get_dest_row_at_pos(bx, by) if dest else view.get_path_at_pos(bx, by)
        if not r:
            return None
        if len(r) in (3, 5) and isinstance(r[0], bool):
            if not r[0]:
                return None
            r = r[1:]
        return r[0]

    def cur_folder(self):
        model, it = self.fview.get_selection().get_selected()
        return model[it][1] if it else None

    def sel_msgs(self):
        model, paths = self.mview.get_selection().get_selected_rows()
        return [model[model.get_iter(p)][0] for p in paths]

    def _ipm_set(self):
        top = getattr(self.sess, 'ipm_root', None)
        out = set()
        def walk(n):
            out.add(n)
            for c in self.info[n].children:
                walk(c)
        if top in self.info:
            walk(top)
        return out

    def _descendants(self, nid):
        out = {nid}
        for c in self.info[nid].children:
            out |= self._descendants(c)
        return out

    def _refresh_actions(self):
        ed = self.editing and bool(self.info) and not self.busy and not self.searching
        f = self.cur_folder()
        fi = self.info.get(f.nid) if f else None
        ipm = self._ipm_set() if ed else set()
        user = bool(ed and fi and not fi.protected and fi.nid in ipm)
        nmsg = len(self.sel_msgs()) if ed and f else 0
        en = {'new-folder': bool(ed and (not f or f.nid in ipm)),
              'rename-folder': user, 'move-folder': user, 'delete-folder': user,
              'move-msgs': nmsg > 0, 'copy-msgs': nmsg > 0, 'copy-other': bool(self.path and not self.busy and not self.searching and f and len(self.sel_msgs()) > 0), 'delete-msgs': nmsg > 0}
        for k, v in en.items():
            self.acts[k].set_enabled(bool(v))
        self.new_btn.set_sensitive(en['new-folder'])

    def on_edit_toggle(self, sw, state):
        if state and not self.editing:
            if not self.path:
                self.err('Open a PST first'); sw.set_active(False); return True
            self.confirm('Allow editing this file?',
                         'Changes are written straight into the PST (each one is journalled and rolled back if '
                         'interrupted). Close Outlook first and keep a backup of anything you cannot replace.',
                         'Allow editing', lambda: self._set_editing(True), lambda: sw.set_active(False))
            return True
        self._set_editing(state)
        return False

    def _set_editing(self, on):
        self.editing = on
        self.mode_lbl.set_text('EDITING' if on else 'READ-ONLY')
        if self.edit_sw.get_active() != on:
            self.edit_sw.set_state(on); self.edit_sw.set_active(on)
        else:
            self.edit_sw.set_state(on)
        self._refresh_actions()

    def confirm(self, msg, detail, ok_label, cb, cancel_cb=None):
        d = Gtk.AlertDialog()
        d.set_message(msg); d.set_detail(detail)
        d.set_buttons(['Cancel', ok_label]); d.set_cancel_button(0); d.set_default_button(0)
        def done(dlg, res):
            try:
                i = dlg.choose_finish(res)
            except GLib.Error:
                i = 0
            if i == 1:
                cb()
            elif cancel_cb:
                cancel_cb()
        d.choose(self, None, done)

    def notice(self, msg, detail=''):
        d = Gtk.AlertDialog()
        d.set_message(msg); d.set_detail(detail); d.set_buttons(['OK'])
        d.show(self)

    def ask_text(self, title, label, initial, ok_label, cb):
        w = Gtk.Window(title=title, modal=True, transient_for=self, default_width=380, resizable=False)
        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=10)
        for m in ('top', 'bottom', 'start', 'end'):
            getattr(box, 'set_margin_' + m)(14)
        box.append(Gtk.Label(label=label, xalign=0))
        ent = Gtk.Entry(text=initial, activates_default=True)
        box.append(ent)
        row = Gtk.Box(spacing=8, halign=Gtk.Align.END)
        cancel = Gtk.Button(label='Cancel'); ok = Gtk.Button(label=ok_label)
        ok.add_css_class('suggested-action')
        row.append(cancel); row.append(ok)
        box.append(row)
        w.set_child(box); w.set_default_widget(ok)
        cancel.connect('clicked', lambda *_: w.close())
        def go(*_):
            v = ent.get_text()
            w.close()
            cb(v)
        ok.connect('clicked', go)
        w.present(); ent.grab_focus(); ent.select_region(0, -1)

    def pick_folder(self, title, exclude, cb, info=None, top=None):
        w = Gtk.Window(title=title, modal=True, transient_for=self, default_width=360, default_height=460)
        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=8)
        for m in ('top', 'bottom', 'start', 'end'):
            getattr(box, 'set_margin_' + m)(10)
        ts = Gtk.TreeStore(str, int)
        info = info if info is not None else self.info
        top = top if top is not None else self.sess.ipm_root
        def add(parent, nid):
            fi = info[nid]
            it = ts.append(parent, [fi.name, nid])
            for c in fi.children:
                add(it, c)
        if top in info:
            add(None, top)
        tv = Gtk.TreeView(model=ts, headers_visible=False)
        tv.set_enable_search(False)
        tv.append_column(Gtk.TreeViewColumn('Folder', Gtk.CellRendererText(), text=0))
        tv.expand_all()
        box.append(Gtk.ScrolledWindow(child=tv, vexpand=True))
        row = Gtk.Box(spacing=8, halign=Gtk.Align.END)
        cancel = Gtk.Button(label='Cancel'); ok = Gtk.Button(label='OK', sensitive=False)
        ok.add_css_class('suggested-action')
        row.append(cancel); row.append(ok); box.append(row)
        w.set_child(box)
        def chosen():
            m, it = tv.get_selection().get_selected()
            return m[it][1] if it else None
        def changed(sel):
            n = chosen()
            ok.set_sensitive(n is not None and n not in exclude)
        tv.get_selection().connect('changed', changed)
        def go(*_):
            n = chosen()
            if n is None or n in exclude:
                return
            w.close(); cb(n)
        ok.connect('clicked', go)
        tv.connect('row-activated', go)
        cancel.connect('clicked', lambda *_: w.close())
        w.present()

    # ---- write wrapper (the write runs on a worker thread; the window stays responsive) ----
    def _write(self, fn, keep=None, then=None, also=()):
        """Run one editing operation: release the read handle, write on a worker thread, reopen, reselect, then call then(result)."""
        if self.busy:
            self.notice('Please wait', 'The previous change is still being written.')
            return None
        if keep is None:
            f = self.cur_folder(); keep = f.nid if f else None
        path = self.path
        self._end_search(True, False)
        self.busy = True
        self._refresh_actions()
        self.status.set_text('Working... (the file is being updated)')
        self.set_cursor_from_name('progress')
        for p in (path,) + tuple(also):
            c = self.pst.get(p)
            if c:
                try:
                    c['store'].pst.close()
                except Exception:
                    pass
        box = {}

        def work():
            try:
                box['res'] = fn(A.Session(path)); box['ok'] = True
            except E.EditError as e:
                box['err'] = ('Not done', str(e))
            except Exception as e:
                traceback.print_exc()
                box['err'] = ('Edit failed - the file was left as it was', '%s: %s' % (type(e).__name__, e))
            GLib.idle_add(finish)

        def finish():
            self.busy = False
            if 'err' in box:
                self.notice(*box['err'])
            for p in also:
                if p in self.pst:
                    self._open_ctx(p)
            self.open_path(path, select=keep)
            self.set_cursor_from_name(None)
            if box.get('ok') and then:
                try:
                    then(box['res'])
                except Exception:
                    traceback.print_exc()
            return False
        threading.Thread(target=work, daemon=True).start()
        return None

    # ---- folder actions ----
    def act_new_folder(self):
        f = self.cur_folder()
        parent = f.nid if f and f.nid in self._ipm_set() else self.sess.ipm_root
        pname = self.info[parent].name
        def go(name):
            name = name.strip()
            if not name:
                return
            def done(n):
                if n:
                    it = self._find_iter(n)
                    if it: self.fview.get_selection().select_iter(it)
                    self.status.set_text('Created folder "%s"' % name)
            self._write(lambda s: s.create_folder(parent, name), keep=parent, then=done)
        self.ask_text('New folder', 'New folder inside "%s":' % pname, '', 'Create', go)

    def act_rename_folder(self):
        f = self.cur_folder()
        if not f: return
        nid, old = f.nid, self.info[f.nid].name
        def go(name):
            name = name.strip()
            if not name or name == old:
                return
            self._write(lambda s: s.rename_folder(nid, name), keep=nid)
        self.ask_text('Rename folder', 'New name for "%s":' % old, old, 'Rename', go)

    def act_move_folder(self):
        f = self.cur_folder()
        if not f: return
        nid = f.nid
        excl = self._descendants(nid) | {self.info[nid].parent}
        def go(dest):
            self._write(lambda s: s.move_folder(nid, dest), keep=nid)
        self.pick_folder('Move "%s" to...' % self.info[nid].name, excl, go)

    def act_delete_folder(self):
        f = self.cur_folder()
        if not f: return
        nid = f.nid; fi = self.info[nid]
        if fi.in_deleted:
            self.confirm('Permanently delete "%s"?' % fi.name,
                         'The folder, its subfolders and every message in them will be removed from the file. '
                         'This cannot be undone.', 'Delete permanently',
                         lambda: self._write(lambda s: s.delete_folder(nid), keep=fi.parent, then=self._after_delete))
        else:
            self.confirm('Delete "%s"?' % fi.name, 'The folder will be moved to Deleted Items.', 'Delete',
                         lambda: self._write(lambda s: s.delete_folder(nid), keep=fi.parent))

    def _after_delete(self, res):
        if res and res[0] == 'purged':
            self.status.set_text('Permanently deleted %(folders)d folder(s), %(messages)d message(s)' % res[1])

    # ---- message actions ----
    def act_move_msgs(self):
        nids, f = self.sel_msgs(), self.cur_folder()
        if not nids or not f: return
        src = f.nid
        def go(dest):
            self._write(lambda s: s.move_messages(nids, dest), keep=src,
                        then=lambda n: self.status.set_text('Moved %d message(s)' % n))
        self.pick_folder('Move %d message(s) to...' % len(nids), {src}, go)

    def act_copy_msgs(self):
        nids, f = self.sel_msgs(), self.cur_folder()
        if not nids or not f: return
        src = f.nid
        def go(dest):
            self._write(lambda s: s.copy_messages(nids, dest), keep=src,
                        then=lambda n: self.status.set_text('Copied %d message(s)' % len(n)))
        self.pick_folder('Copy %d message(s) to...' % len(nids), set(), go)

    def choose_pst(self, cb):
        self.pick_pst('Copy into which PST file?', cb)

    def _after_xcopy(self, n, dpath):
        self.status.set_text('Copied %d message(s) into %s' % (n, os.path.basename(dpath)))
        self.notice('Copied %d message(s)' % n,
                    'The messages open normally in Outlook. Outlook\'s repair tool (SCANPST) may report "minor inconsistencies" for %s '
                    'because its message index is organised differently; Analyze + Repair fixes that and changes nothing else.' % os.path.basename(dpath))

    def act_copy_other(self):
        nids, f = self.sel_msgs(), self.cur_folder()
        if not nids or not f or self.busy: return
        src, spath = f.nid, self.path
        def picked(dpath):
            dpath = os.path.abspath(dpath)
            if dpath == os.path.abspath(spath):
                self.notice('Same file', 'Choose a different PST file (use "Copy to..." for folders in this file).'); return
            try:
                ds = A.Session(dpath)
                dinfo = ds.folder_info()
            except Exception as e:
                self.notice('Cannot open that file', '%s: %s' % (type(e).__name__, e)); return
            def go(dest):
                self._write(lambda s: s.copy_to_pst(nids, dpath, dest), keep=src, also=[os.path.abspath(dpath)],
                            then=lambda n: self._after_xcopy(len(n), dpath))
            self.pick_folder('Copy %d message(s) into %s...' % (len(nids), os.path.basename(dpath)), set(), go,
                             info=dinfo, top=ds.ipm_root)
        self.choose_pst(picked)

    def act_delete_msgs(self):
        nids, f = self.sel_msgs(), self.cur_folder()
        if not nids or not f: return
        src = f.nid
        perm = self.info.get(src) and self.info[src].in_deleted
        def go():
            self._write(lambda s: s.delete_messages(nids), keep=src,
                        then=lambda r: self.status.set_text('%d message(s) deleted' % sum(r)) if r else None)
        if perm:
            self.confirm('Permanently delete %d message(s)?' % len(nids),
                         'They are already in Deleted Items; this removes them from the file for good.',
                         'Delete permanently', go)
        else:
            self.confirm('Delete %d message(s)?' % len(nids), 'They will be moved to Deleted Items.', 'Delete', go)

    # ---- menus, keys, drag & drop ----
    def _on_rclick(self, gesture, n, x, y, view, menu):
        if not self.editing:
            return
        path = self._row_at(view, x, y)
        if path is not None:
            sel = view.get_selection()
            if not sel.path_is_selected(path):
                sel.unselect_all(); sel.select_path(path)
        self._refresh_actions()
        pops = self.__dict__.setdefault('_pops', {})
        pop = pops.get(menu)
        if pop is None:
            pop = Gtk.Popover()
            pop.set_parent(self)
            pop.set_has_arrow(False)
            pop.set_halign(Gtk.Align.START)
            pop.set_autohide(True)
            box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL)
            gm = getattr(self, menu)
            for i in range(gm.get_n_items()):
                label = gm.get_item_attribute_value(i, 'label', None).get_string()
                act = gm.get_item_attribute_value(i, 'action', None).get_string()
                btn = Gtk.Button(label=label, has_frame=False, action_name=act)
                btn.get_child().set_xalign(0)
                btn.connect('clicked', lambda b, p=pop: p.popdown())
                box.append(btn)
            pop.set_child(box)
            pops[menu] = pop
        t = view.translate_coordinates(self, x, y)
        wx, wy = (t[0], t[1]) if t else (x, y)
        r = Gdk.Rectangle(); r.x, r.y, r.width, r.height = int(wx), int(wy), 1, 1
        pop.set_pointing_to(r)
        pop.popup()

    def _on_key(self, ctl, keyval, keycode, state, view):
        if keyval == Gdk.KEY_Delete and self.editing:
            (self.act_delete_msgs if view is self.mview else self.act_delete_folder)()
            return True
        if keyval == Gdk.KEY_F2 and self.editing and view is self.fview:
            self.act_rename_folder(); return True
        return False

    def _drag_prepare(self, src, x, y, view, kind):
        if not self.editing or self.busy or self.searching:
            return None
        if self._row_at(view, x, y) is None:         # a press on the column header is a column move, not a message drag
            return None
        if kind == 'msg':
            ids = self.sel_msgs()
            if not ids: return None
            data = 'msg:' + ','.join(str(i) for i in ids)
        else:
            f = self.cur_folder()
            if not f or not self.acts['move-folder'].get_enabled(): return None
            data = 'fld:%d' % f.nid
        return Gdk.ContentProvider.new_for_value(data)

    def _on_drop(self, target, value, x, y):
        if self.busy or not self.editing or not isinstance(value, str) or ':' not in value:
            return False
        path = self._row_at(self.fview, x, y, dest=True)
        if path is None:
            return False
        drow = self.ftree[self.ftree.get_iter(path)]
        if drow[2] != self.path:
            self.notice('Different PST file', 'Use "Copy to another PST file..." from the message menu to copy between files.')
            return False
        dest = drow[1].nid
        if dest not in self._ipm_set():
            self.notice('Cannot drop here', 'Items can only be dropped on folders inside the mailbox tree.')
            return False
        kind, _, rest = value.partition(':')
        mods = self.get_display().get_default_seat().get_keyboard().get_modifier_state()
        copy = bool(mods & (Gdk.ModifierType.CONTROL_MASK | Gdk.ModifierType.SHIFT_MASK))   # Shift keeps the selection while dragging
        if kind == 'msg':
            nids = [int(v) for v in rest.split(',') if v]
            src = self.cur_folder().nid if self.cur_folder() else None
            if dest == src and not copy:
                return False
            def go():
                if copy:
                    self._write(lambda s: s.copy_messages(nids, dest), keep=src,
                                then=lambda r: self.status.set_text('Copied %d message(s)' % len(r)))
                else:
                    self._write(lambda s: s.move_messages(nids, dest), keep=src,
                                then=lambda r: self.status.set_text('Moved %d message(s)' % r))
            GLib.idle_add(go)
            return True
        if kind == 'fld':
            nid = int(rest)
            if nid not in self.info or dest in self._descendants(nid) or dest == self.info[nid].parent:
                return False
            GLib.idle_add(lambda: self._write(lambda s: s.move_folder(nid, dest), keep=nid) and False)
            return True
        return False


class App(Gtk.Application):
    def __init__(self):
        super().__init__(application_id='local.openoutlook', flags=Gio.ApplicationFlags.HANDLES_COMMAND_LINE | Gio.ApplicationFlags.NON_UNIQUE)

    def do_command_line(self, cl):
        args = cl.get_arguments()[1:]
        w = Win(self); w.present()
        if args:
            w.open_path(args[0])
        return 0


if __name__ == '__main__':
    sys.exit(App().run(sys.argv))
