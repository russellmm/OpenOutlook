# Junk Cleaner: native integration plan

Source app: `F:\Claude\OutlookJunkCleaner` (WPF tray app, drives classic Outlook over COM). Goal: the same behaviour built into OpenOutlook, for every connected account, with no Outlook dependency, on Windows and Linux.

## 1. What the old app does (to keep)
- Matches keywords (case-insensitive substring) against the visible From line: sender name, sender address, SMTP address, "sent on behalf" name/address, and the `From:` transport header.
- Extra rules, each off by default: delete High-importance junk; delete junk with no To; delete junk "on behalf of".
- Only the default Junk folder. Deleted items go to Deleted Items (not permanently removed).
- Manual "Clean Junk Folder Now" (with confirmation) and optional "Always clean" every 1-60 minutes, silent with a notification when something was deleted.
- Keyword list editor; legacy config at `%LOCALAPPDATA%\OutlookJunkCleaner\config.json`.
- Dropped: COM adapter, tray icon, start with Windows, "start in tray" (OpenOutlook is the app).

## 2. What already exists in OpenOutlook
- `src/OpenOutlook.JunkCleaner`: `JunkMatcher.MatchReasons` (port of the matching rules, with reasons), `JunkRuleOptions`, per-account `JunkCleanerAccountSettings` (opt-in, keywords, interval, rules), `JunkCleanerSettingsStore`, and `PreviewLegacyConfig` / `ImportForAccount` for the old config.
- Ribbon "Junk" button (`PreviewJunkImportButton`) that only previews a legacy import; "Remove Junk" is a placeholder.
- Mirror engine with per-account Junk folder in the PST copy, server delete/move push (`MirrorPush`), Graph and Gmail writers.

## 3. Design
**Where to clean.** Clean on the server through the existing writers so every client sees the result, then let the mirror pull reflect it. Do not edit the PST directly (the mirror push would fight it).
- Microsoft: list the `junkemail` folder with the extra fields (`from`, `sender`, `importance`, `toRecipients`, `internetMessageHeaders` for `From:` only when needed), match, then `MoveAsync` to `deleteditems`.
- Gmail: list the `SPAM` label, fetch metadata (From/To headers, Importance/X-Priority), match, then move to Trash (modify labels). Permanent delete is not possible with current scopes and is not wanted.
- PST data files (opened archives): manual run only, using the native engine's move to Deleted Items; never scheduled.

**Provider-neutral interface.** `IJunkSource` in `OpenOutlook.JunkCleaner`: `ListJunkAsync()` returning `JunkCandidate(id, subject, FromFields, importance?, hasTo?)`, and `MoveToDeletedAsync(ids)`. Implementations: `GraphJunkSource`, `GmailJunkSource`, `PstJunkSource`. A `JunkCleanerRunner` does scan, match, optional confirmation callback, delete, and returns `JunkRunResult` (scanned, matched with reasons, deleted, failures). Unknown fields are "unknown, not a match" (already the matcher's rule).

**Safety rules.**
- Per-account opt-in (already modelled); default off.
- Manual run shows a preview list (subject, sender, reason) with checkboxes before deleting; scheduled runs delete silently but write a log.
- Move to Deleted Items/Trash only; keep a "last run" log of what moved (account, subject, sender, reason, time) so mistakes are recoverable.
- Cap per run (for example 500) to limit damage from a bad keyword; keyword shorter than 3 characters needs a confirm in the editor.
- Skip when offline or signed out; never prompt a sign-in from the background.

**Scheduling.** Reuse the mirror scheduler pattern in `MainWindow.Mirror.cs`: a timer per account at its interval, run after a mirror sync completes so the Junk list is current. Setting `OPENOUTLOOK_NO_MIRROR=1` also disables it in tests.

## 4. UI
- Account Settings gets a "Junk Cleaner" tab next to Email / Data Files (same themed look): account selector, enable toggle, keyword list with add/remove/paste-many, the three rule checkboxes, interval, "Clean now…" and "Import from OutlookJunkCleaner…" (uses the existing preview/import).
- Ribbon Junk group: "Remove Junk" becomes "Clean Junk Now" for the selected account; the old Junk-import preview button moves into the settings tab.
- Status bar: after a scheduled run, brief "Junk Cleaner removed 4 messages" text; the preview/last-run log is reachable from the tab.
- Optional later: "Always delete mail from this sender" in the message context menu, adding the sender to the keyword list.

## 5. Phases
1. **Core (small).** `IJunkSource`, `JunkCleanerRunner`, `JunkRunResult`, run log; unit tests with a fake source covering each rule, unknown fields, cap, and failure isolation.
2. **Microsoft source.** `GraphJunkSource` + test with a fake HTTP handler; verify live on the Hotmail account via MailSmoke (add a `junkclean` step, dry run first).
3. **UI.** Junk Cleaner tab in Account Settings, ribbon button, preview dialog with checkboxes, headless tests and screenshot.
4. **Scheduler.** Per-account timer, status-bar message, log file; settings persisted through `JunkCleanerSettingsStore`.
5. **Gmail source.** `GmailJunkSource` (SPAM label, metadata fetch, move to Trash), live check on the Gmail account.
6. **PST source** (manual only) and legacy import wired to the tab; docs and handoff update.

Phases 1-2 are the smallest useful step: after them a manual dry run against Hotmail works from MailSmoke. Each phase ends with tests, publish, push.

## 6. Open questions
1. Should Gmail "Spam" be cleaned too (same rules), or Microsoft only at first?
2. Scheduled runs: silent delete (as the old app does) or always a small notification with an Undo/log link?
3. Per-run cap: is 500 messages a sensible limit?
4. Should the keyword list be shared across accounts or per account (the settings model is per account; the old app had one list)?

## 7. Decisions and progress (2026-10-05)
- Decisions: Microsoft (Hotmail) only for now; keywords and rules per account; scheduled runs delete silently; cap 500 moves per run.
- Found: `GraphJunkMailReader` (lists Junk Email) and `MicrosoftJunkPreviewMatcher` (matching, tested) already existed.
- Done: `MicrosoftJunkCleanerRunner` (scan, move to Deleted Items with per-message failure isolation, cap, stops on sign-in errors) + 5 tests.
- Next: Junk Cleaner tab in Account Settings, ribbon "Clean Junk Now" with preview, scheduler + last-run log, live dry run on Hotmail.

## 8. Implemented (2026-10-05)
- Account Settings > Junk Cleaner tab (`AccountSettingsWindow.Junk.cs`): per-account on/off, automatic cleaning + interval, keywords, the three rules, Clean now, import of the old config.json, log of removed mail.
- Ribbon Junk group: "Clean Junk" runs a manual clean with a tick-box preview (`JunkPreviewDialog`); a "turned off / no keywords" account opens the tab instead.
- Scheduler (`MainWindow.Junk.cs`, 1-minute timer, per-account interval, silent): moved mail is logged to `junk-cleaner.log` beside `mirror-settings.json`; the mailbox copy re-syncs afterwards. Disabled by `OPENOUTLOOK_NO_MIRROR=1`.
- `JunkCleanerAccountSettings.AutoClean` added; importing the old config turns the cleaner on but leaves automatic cleaning as it was.
- Live dry run (`dotnet run --project tools/MailSmoke -- junkscan`) on Hotmail: 299 in Junk, 282 match the old config (all three rules on). It found that `GraphJunkMailReader` rejected Graph's real next-page links; fixed (path shape check).
- Not built: Gmail, PST sources, "always delete this sender" menu item.
