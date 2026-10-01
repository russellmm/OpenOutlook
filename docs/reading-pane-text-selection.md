# Reading pane: HTML rendering, text selection, and why an embedded browser does not work here

## The constraint that decides everything

OpenOutlook renders received HTML mail two ways:

1. **Snapshot reader** (default for connected mail). Headless Chrome lays the document out and
   screenshots it; Avalonia displays the PNG tiles. Layout fidelity is excellent because the pixels
   are produced by a real browser engine. Links work as invisible buttons placed at coordinates
   Chrome reported.
2. **Interactive reader** (`Avalonia.Controls.WebView`, WebKitGTK). Real HTML in an embedded widget,
   so selection and copy are native.

Option 2 does not work on the owner's desktop, and the reason is a display-protocol limitation rather
than a bug we can fix:

- The session is **Wayland** (`XDG_SESSION_TYPE=wayland`).
- `Avalonia.Controls.WebView` on Linux embeds a GTK3 + WebKit2GTK widget as a **native child window**.
- Wayland has no equivalent of X11 `XReparentWindow`: one client cannot let a second client paint into
  a hole in its surface.

Consequences, all observed: the web view loads the document, its scripts run (a probe inside the page
reports content present, and Ctrl+A/Ctrl+C copy thousands of characters), yet nothing ever composites
into the window. The user sees the last thing Avalonia drew -- stale snapshot pixels -- so the pane
looks frozen. Every in-app check reports success, so there is no failure to detect and fall back on.

Ruled out by experiment on the real machine: `WEBKIT_DISABLE_DMABUF_RENDERER=1`,
`WEBKIT_DISABLE_COMPOSITING_MODE=1`, and `GDK_BACKEND=x11` all produce the same frozen pane. This is
not a compositing-mode problem.

**Testing note that matters for future changes:** headless Xvfb is pure X11, where reparenting works.
Interactive mode renders perfectly there and copy works. **An Xvfb run proves nothing about this
failure** and must not be used as evidence that the embedded reader is safe to default on.

## What ships instead

### 1. Text selection over the snapshot (`TextSelectionGeometry`, `HtmlPageView`)

Chrome already knows where every piece of text is. During the same render pass that produces the
tiles, it reports leaf-block rectangles and their text (`BrowserHtmlRenderer.RenderDocumentAsync`).
`HtmlPageView` turns those rectangles into a selection: press inside a block to select it, drag to
extend across every block the drag sweeps, `Ctrl+A` for all blocks on the tile, `Ctrl+C` to copy. The
copied text is ordered top-to-bottom then left-to-right from the coordinates, so table cells read
correctly regardless of document order, and overlapping blocks resolve to the smallest so a bold word
inside a paragraph can be picked alone.

Selection is computed from rectangles rather than delegated to Avalonia's `SelectableTextBlock`:
Avalonia does not hit-test invisible text drawn over the picture, so a drag started there could never
begin (verified -- clicks passed straight through to the layer beneath). Everything here is drawn by
Avalonia, so it behaves identically on X11, XWayland and Wayland.

### 2. Open in browser (`BrowserDocumentWriter`)

The same sanitised document the snapshot reader renders is written to `0700` scratch directory as a
`0600` file and opened in the system browser, giving real selection, find-in-page and printing.
Scripts stay disabled and remote images stay blocked -- only the renderer changes. Earlier message
copies are deleted on each open so reading mail does not leave a growing archive of other people's
correspondence on disk.

## Known limitations of the current approach

- **Granularity is the text block.** A drag selects whole blocks, not arbitrary character ranges.
  Mapping substrings of one text node to the several line boxes it spans needs font metrics we do not
  share with Chrome.
- **`Ctrl+A` covers the visible tile**, not tiles scrolled out of view on very long messages.
- **Links win over text.** Link buttons handle their own clicks first, so words inside a link cannot
  be selected by pressing on them; drag across surrounding blocks instead.
- **Block-level selection is as good as it gets without Chrome's font metrics**, and `Ctrl+A` covers
  the visible tile rather than the whole message on very long mail.

Clipboard copy was confirmed working by the owner on real hardware. Note for future harness work:
under Xvfb, Avalonia's clipboard reports success but serves nothing -- even an immediate
`GetTextAsync` read-back returns null -- while WebKitGTK's copy in the same environment reaches other
clients normally. **Xvfb is not a valid test surface for the Avalonia clipboard**, exactly as it is
not a valid test surface for the embedded web view.

## Future work: embed Chromium with off-screen rendering (CEF)

The strategic fix for an interactive reading pane under Wayland is **Chromium Embedded Framework with
off-screen rendering** (`CefGlue` or equivalent .NET bindings). CEF renders into a bitmap that *our*
code composites, so there is no child window to reparent and the Wayland constraint disappears.
Selection, copy, context menus and find-in-page are handled inside Chromium and simply appear in the
pixels we draw -- the same reason the snapshot reader already looks right.

Why this is the recommended direction:

- It is the architecture every client with a flawless reading pane uses in substance: Thunderbird
  ships Gecko; Apple Mail embeds WebKit; new Outlook for Windows moved to Chromium (WebView2).
  Nobody reimplements HTML layout, and nobody screenshots it either.
- Our snapshot reader already trusts Chrome for layout. CEF keeps that engine and adds interactivity,
  instead of switching to the one engine (WebKitGTK) that fails here.

Costs to weigh before starting:

- Roughly 150-250 MB of shipped dependency, plus a multi-process model to supervise and shut down.
- OSR means we own input forwarding (mouse, keyboard, scroll, IME) and cursor/selection painting.
- Security work is required to keep the current guarantees: no scripting for untrusted mail, request
  interception to block remote loads until the user allows them, and a sandbox policy per message.

Interim step if CEF is deferred: replace the remaining WebKit dependency entirely (remove the
interactive reader) so there is one rendering path whose behaviour is known on every display server,
rather than a control that silently does nothing for Wayland users.
