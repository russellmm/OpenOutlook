# Session handoff (2026-09-30)

Binary: publish/OpenOutlook.Desktop sha f5b905eed98dbea0. HEAD 7efdb6b, pushed to
github.com/russellmm/OpenOutlook main. 356/356 tests pass (scripts/dev-check.sh).

Shipped this session: text selection over the snapshot reader (TextSelectionGeometry +
HtmlPageView, geometry from Chrome), Open in browser (BrowserDocumentWriter, 0700 dir /
0600 file / prunes prior messages), ComposeHtml sanitizer profile, 64MP decoded-pixel
budget, CSS url() ordering fix, AppLog + AppDomain/TaskScheduler handlers, CrashNotice
(built, NOT yet wired), SafePick (report callback not yet supplied at the 8 sites),
PstException in the EML-export filter, fail-closed dialog navigation guard, PST HTML now
defaults to snapshot. Selection + clipboard confirmed working on real hardware by owner.

Hard constraints: Wayland session, so Avalonia.Controls.WebView (WebKitGTK native child
window) can never composite into our window; env vars WEBKIT_DISABLE_DMABUF_RENDERER,
WEBKIT_DISABLE_COMPOSITING_MODE, GDK_BACKEND=x11 all ruled out on real hardware. Xvfb is
pure X11 and its Avalonia clipboard serves nothing - it is INVALID evidence for both the
web view and the clipboard. Never launch the app outside scripts/headless-smoke.sh (it
opened a window on the owner's second monitor once). Read-only against live accounts;
never send, never run Junk Cleaner on real Junk. Details: docs/reading-pane-text-selection.md

Open items, in priority order:
1. BUILD_STATUS.md still lists the PST-HTML gap as open - strike it (fixed in 7efdb6b).
2. Wire CrashNotice into the 19 async void handlers in MainWindow (22 app-wide) via a
   SafeUi wrapper; Avalonia 11.2 has no Application.UnhandledException hook.
3. Supply SafePick's report callback so a failed portal reports instead of looking like
   cancel.
4. Then new features. CEF with off-screen rendering remains the strategic reading-pane fix.
