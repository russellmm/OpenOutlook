# Session handoff (2026-09-30, later in the day; updated 2026-10-01)

Binary: publish/OpenOutlook.Desktop sha256 fe7a6c30722149935011839f7748f06903024f27142eb213075722aa30603961,
built 2026-10-01 11:22 local. 377/377 tests pass (scripts/dev-check.sh). HEAD is pushed to
github.com/russellmm/OpenOutlook main.

Shipped since the last note: archived (PST) HTML mail defaults to the snapshot reader like connected
mail; unhandled UI exceptions are now contained (see below); all eight file/folder picker call sites
report failures in the status bar instead of looking like a cancel; BUILD_STATUS/README corrected,
including the stale "PST HTML still tries WebKit first" claim; the File tab is a full Backstage view;
and folder-pane rows can be reordered (right-click Move Up/Down/Top, or drag onto a sibling) with the
arrangement persisted in folder-order.json across restarts — verified headless for roots and subfolders;
drag itself needs one real-hardware check since synthesized XDND gestures never complete under Xvfb.
And File > Options is now the classic dialog: Mail page rebuilt section-by-section from screenshots with
the Editor Options (Proofing) and Reading Pane sub-dialogs, everything persisted to options.json and proven
across a restart; the twelve unbuilt categories are named placeholders, and stored settings are consumed by
features as they land rather than all being wired at once. Theme accent is now classic Outlook blue (#0F6CBD).

## The crash-containment finding worth keeping

Avalonia 11.2 has no Application.UnhandledException event, but **Dispatcher.UIThread.UnhandledException
does catch what actually kills us**: an exception surfacing after an `await` inside an `async void`
handler is re-raised on the UI synchronization context, and it aborts with SIGABRT unless that hook sets
Handled = true. Measured against 11.2.3 under Xvfb: unhooked -> exit 134; hooked -> process alive, and a
second escape caught just as well. Exceptions thrown *synchronously* inside a routed event never reach
the hook and are swallowed silently by Avalonia (no crash, no log) -- that remains an open observability
gap, noted in BUILD_STATUS.md.

Wired in App.OnFrameworkInitializationCompleted: log via AppLog, CrashNotice (coalesced into one window
with an error count), Handled = true; cancellation and shutdown skipped; a throwing notice cannot escape
the hook. One hook covers all 22 async void handlers, so no per-handler wrappers were added.

Re-verify any time: `OO_SMOKE_CRASH_GUARD=1 scripts/headless-smoke.sh` (passes on both the debug and
published binaries). It drives OPENOUTLOOK_TEST_THROW_UNHANDLED=1, the app's opt-in seam that throws from
an async void continuation 1.5 s after startup.

## Hard constraints (unchanged, do not re-litigate)

Wayland session: Avalonia.Controls.WebView (WebKitGTK native child window) can never composite into our
window; WEBKIT_DISABLE_DMABUF_RENDERER, WEBKIT_DISABLE_COMPOSITING_MODE and GDK_BACKEND=x11 were all
ruled out on real hardware. Xvfb is pure X11 and its Avalonia clipboard serves nothing -- INVALID
evidence for the web view and for copy/paste; rendering and clipboard claims need owner confirmation.
Never launch the app outside scripts/headless-smoke.sh (a bare launch opened a window on the owner's
second monitor). Read-only against live accounts: never send, never run Junk Cleaner on real Junk, writes
only to OPENOUTLOOK-DEVTEST artifacts. Screenshots contain real mail -- delete after viewing.
Known coordinates at 1440x900: Inbox row (135,361); message rows x=400 y=412/440/468/496/524/552; reader
buttons y~495 and y~527.

## Open items, in priority order

1. UI fidelity pass (in progress): ribbon groups/labels, folder tree and reading-pane header follow
   screenshots/Outlook.jpg, and the File menu is now a real Backstage (see the 2026-09-30 BUILD_STATUS
   entry; logic in MainWindow.Backstage.cs). Next step of this pass: the Options submenu pages inside the
   backstage — classic Outlook shows General/Appearance/Mail categories on the right pane instead of the
   two dialog buttons there today. Then checks against a populated classic Outlook: message row height and
   column spacing, date-group band look, dark mode, high-DPI. Small icons (Meeting/More/Create New) are
   crude and worth redrawing. Publish before showing the user. CEF (or Chromium) with off-screen rendering remains
   the strategic reading-pane fix: it removes the Wayland child-window constraint and would restore
   character-level selection, find-in-page and real link handling. Details/costs:
   docs/reading-pane-text-selection.md.
2. Reader-gate starvation and shutdown cooperation: `_ = CloseStoresAsync()` does not wait, and a held
   `_readerGate` can block exit behind a slow render.
3. Account mutations invalidate too little: `AccountSetupClicked` bumps only `_messageVersion`, so stale
   folder lists can survive sign-in changes.
4. Make Chrome-dependent tests skip explicitly instead of returning vacuously when no browser is found;
   add a CSP nonce for the compose editor script; decompose MainWindow (~2,100 lines).
5. Small: give the crash notice an operation label (needs the 22 handlers); decide what to do about
   synchronously swallowed handler exceptions (Avalonia's internal log could be forwarded to AppLog via
   a custom ILoggerFactory -- the API name in 11.2 is not `LoggingSubsystem`, so it needs one more probe).
