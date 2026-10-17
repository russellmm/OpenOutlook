# Calendar reminders - plan (2026-10-16; steps 1 to 6 built the same day, see `docs/calendar.md`)

> Built: everything below except the taskbar flash and reminder sound, re-checks on wake / network change (the 5-minute poll covers them), and the
> Reminder box on *editing* an event (events cannot be edited yet). The "today" paragraph describes the state before the work.

Owner request: OpenOutlook should pop up calendar reminders like Outlook's "Reminder(s)" window. **Reminders that came due while OpenOutlook
was closed (for example a week away) must still show up when it starts; once dismissed they must never show again.** Today the appointment
window's Reminder box is a placeholder ("Reminder: planned feature", `CalendarEditorWindow.cs`) and nothing pops up.

Scope: Microsoft (Hotmail / Outlook.com) calendars only, like the rest of the calendar (`docs/calendar.md`). Gmail calendars are not supported yet.
Like Outlook, reminders appear only while OpenOutlook is running.

## 1. Source of truth: Microsoft Graph's reminder list

- `GET /me/reminderView(startDateTime=...,endDateTime=...)` returns, for every event occurrence whose **reminder time** falls in the range and
  that has **not been dismissed** (and is not snoozed past now): `eventId`, `changeKey`, `eventSubject`, `eventStartTime`, `eventEndTime`,
  `eventLocation`, `reminderFireTime`, `eventWebLink`. Recurring series come back per occurrence. This is the list Outlook itself works from.
- `POST /me/events/{eventId}/dismissReminder` - dismissed everywhere (Outlook, phone, web).
- `POST /me/events/{eventId}/snoozeReminder` with `{"newReminderTime": {"dateTime": "...", "timeZone": "UTC"}}`.
- Needs `Calendars.ReadWrite` (the owner's account already has it, `accounts.json`; `GraphCalendarClient` already checks the scope).

Using the server's list (not our own "has it popped up?" bookkeeping) is what makes both owner requirements hold across machines and programs:
a reminder missed while OpenOutlook was closed is still in the list; a reminder dismissed in OpenOutlook, Outlook or on the phone is gone from it.

## 2. Look-back window ("I have not opened OpenOutlook for a week")

- At start-up and on every check, ask for `startDateTime = now - LookBack`, `endDateTime = now + 1 day`.
- `LookBack` default **30 days** (Options > Calendar > "Show reminders that came due up to [30] days ago"; 1-90). Anything older is ignored, so a
  long absence does not flood the window with ancient meetings. (Outlook itself shows every undismissed past reminder; 30 days is a cap against
  years of never-dismissed items, and it is a setting.)
- Show a reminder when `reminderFireTime <= now`. Keep the not-yet-due ones in memory and arm a timer for the next fire time, so a reminder
  appears on its minute, not on the next poll.
- Poll every **5 minutes** (and right after start-up, after waking from sleep, after network comes back, after an event is created / edited in
  OpenOutlook) - catches events added on the phone. Cheap: one Graph call per account.

## 3. Never again once dismissed (also offline / before the server confirms)

- Dismiss: call `dismissReminder`, and at once record the reminder locally in `reminders-state.json` (config folder):
  key = `accountId + eventId + reminderFireTime` (the fire time keeps a later occurrence / re-scheduled event from being hidden), value =
  `dismissed` + time + `pendingSync` flag. The local record hides it immediately and across restarts even if the call failed or the computer is
  offline; a pending record is sent again on the next poll until Graph accepts it (404 = the event was deleted: drop the record).
- Snooze: `snoozeReminder` + local record `snoozedUntil` (shown again at that time even before the next poll; cleared when Graph's list agrees).
- If the event is changed so the reminder fires at a new time, the key changes and the reminder shows again - same as Outlook.
- Housekeeping: drop local records older than LookBack + 7 days (the file stays small, bounded like the junk log: at most 2,000 entries).

## 4. The Reminders window (like Outlook's)

- Title "N Reminder(s)", bell icon; header with the selected item's subject, start date/time and location; list of all due reminders with
  "N minutes / hours / days overdue" or "in N minutes", newest due first; buttons **Dismiss**, **Dismiss All**, snooze choice
  (5, 10, 15 minutes, 1, 2, 4 hours, 1 day, 1 week; default 5 minutes) and **Snooze**; double-click / Enter opens the event
  (`CalendarEditorWindow` or the calendar on that day).
- One window for all accounts; it stays on top (`Topmost`) but does not steal focus while typing; it reappears when a new reminder comes due;
  closing it with X = hide until the next due reminder (Outlook behaviour), not dismiss.
- Taskbar flash on Windows; a sound only if Options > Mail > "Play a sound" style option is on (`OptionsSettings.PlaySound` exists).
- "Dismiss All" with several items asks "Dismiss all N reminders?" like Outlook.

## 5. The Reminder box in the appointment window

- Make `_reminder` real: None, 0, 5, 10, 15 (default), 30 minutes, 1, 2, 3, 4, 8 hours, 0.5, 1, 2 days, 1 week -> `isReminderOn` /
  `reminderMinutesBeforeStart` on create (`GraphCalendarClient` create payload) and on edit; show the event's current value when opening one.

## 6. Code layout

| Piece | Where | Notes |
|---|---|---|
| `GraphReminderClient` (`ReminderViewAsync`, `DismissAsync`, `SnoozeAsync`) | `src/OpenOutlook.Providers.Microsoft` | Same safety as the other Graph clients: verified account, no redirects, safe pagination (`@odata.nextLink` only to graph.microsoft.com), bounded page size, errors without tokens. |
| `ReminderSchedule` (pure) | `src/OpenOutlook.Desktop` | Input: server list + local state + now + LookBack. Output: due list (sorted), next fire time, pending syncs. All the "when does it show" rules live here. |
| `ReminderStateStore` | `src/OpenOutlook.Desktop` | `reminders-state.json`, atomic writes, corrupt file -> empty (never crashes start-up), bounded. |
| `RemindersWindow` | `src/OpenOutlook.Desktop` | The UI in section 4; built in code like `ArchiveRepairWindow`. |
| `MainWindow.Reminders.cs` | `src/OpenOutlook.Desktop` | Scheduler: start-up check, 5-minute poll, next-fire timer, wake / network events, wiring to the window. |
| Options | `OptionsSettings.ReminderLookBackDays` (30), `ShowReminders` (true) | Options > Calendar. |

## 7. Tests (each one a row in `docs/EDGE_CASES.md`)

- `ReminderScheduleTests`: a reminder that fired 6 days ago while closed shows at start; one older than LookBack does not; one due in 10
  minutes is not shown yet and sets the next fire time; dismissed locally -> hidden even if the server still lists it (offline); dismissed and
  confirmed -> gone; snoozed -> hidden until its time, then shown; an event rescheduled (new fire time) shows again; a recurring series' next
  occurrence shows although an earlier one was dismissed; sorting and "overdue" text; time zones (UTC from Graph, local display; the
  2026-10-15 Hotmail time-shift lesson).
- `GraphReminderClientTests`: reminderView URL and parsing (UTC times, missing fields), pagination guard, wrong account refused, dismiss /
  snooze bodies, 404 on dismiss = deleted event, throttling (429) retried later, token never in errors.
- `ReminderStateStoreTests`: round trip, corrupt / oversized file, cap and age pruning, pending-sync retry.
- Headless: the window lists two reminders with overdue text, Dismiss removes one and records it, Dismiss All asks first, Snooze hides until
  the chosen time; the appointment window's Reminder box writes `reminderMinutesBeforeStart`.
- Manual (owner): create an event with a 5-minute reminder in OpenOutlook -> pops up on time in OpenOutlook and in Outlook; dismiss in
  OpenOutlook -> gone in Outlook / phone; close OpenOutlook, let a reminder pass, reopen -> it shows as overdue; dismiss -> never again.

## 8. Order of work

1. `GraphReminderClient` + tests.  2. `ReminderSchedule` + `ReminderStateStore` + tests.  3. Scheduler in the main window (log lines:
"reminders: N due, next at ..."), no UI yet - check against the owner's real calendar in the log viewer.  4. `RemindersWindow` + headless tests.
5. Appointment window Reminder box.  6. Options.  7. Docs (`calendar.md`, `FEATURES.md`, `EDGE_CASES.md`, handoff), builds for Windows and Linux.
