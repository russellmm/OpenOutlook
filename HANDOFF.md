# Session handoff (2026-09-30, later in the day)

Binary: publish/OpenOutlook.Desktop sha256 af35f629ddeaac2a5c204a0955974718b4ab7be6703c7e5e358787490afa662a,
built 22:30 local. 360/360 tests pass (scripts/dev-check.sh). HEAD is pushed to
github.com/russellmm/OpenOutlook main.

Shipped since the last note: archived (PST) HTML mail defaults to the snapshot reader like connected
mail; unhandled UI exceptions are now contained (see below); all eight file/folder picker call sites
report failures in the status bar instead of looking like a cancel; BUILD_STATUS/README corrected,
including the stale "PST HTML still tries WebKit first" claim.

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

1. New features -- the crash/picker queue is done. CEF (or Chromium) with off-screen rendering remains
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
