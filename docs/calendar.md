# Hotmail / Outlook.com Calendar

OpenOutlook's Calendar uses the connected **personal Microsoft account** already used for mail. It does not create a second login. Gmail, iCloud, local PST calendars, and Microsoft 365 organization accounts are outside this integration.

## Available now

- Calendar destination in the left navigation, with a Home/View ribbon and a sidebar containing two mini months and the account's calendar list.
- Month, Week, Work Week, and Day views. Day and week views have timed columns; Today, previous/next, mini-month date selection, view switching, and Refresh navigate the schedule.
- Calendar and event loading from Microsoft Graph for one selected calendar at a time. Dates with loaded activity are bold in the mini months.
- **New Appointment** and **New Meeting** in an editable calendar. The composer accepts subject, dates and times, all-day, location, and notes; meetings also require attendee email addresses. It validates the time range and local daylight-saving gaps before saving. Meeting creation asks Graph to send invitations.
- A saved event appears in the current view immediately. If a save cannot be confirmed, the user can retry with the same Graph transaction ID or dismiss the retry.
- **Open, change and delete events** (2026-10-17): double-click an event in any view. It opens in the appointment window with its title, times, all-day, location, notes, attendees, reminder, Show As, Private and importance; **Save & Close** updates it (Graph PATCH), **Delete** removes it. For one occurrence of a recurring series only that occurrence changes (the window says so). A meeting someone else organized opens read-only; Delete still removes it from your calendar. The notes are sent only when they were changed (a formatted body is not replaced by plain text) and the attendees only for your own meetings.
- **The appointment window looks like Outlook's**: date boxes "Sat 10/17/2026" with a calendar button (month with Today), time boxes with Outlook's half-hour list (the end list shows the length: "12:00 PM (2 hours)"), typed dates and times ("10/20", "8p", "20:30"); the end moves with the start; Actions (Delete, Calendar, Forward), Show As with Outlook's colour squares, Reminder, Recurrence, Categorize and the Private / High / Low Importance toggles (Show As, Private and importance are saved).
- **Reminders** (2026-10-16, plan in [reminders-plan.md](reminders-plan.md)). The appointment window's Reminder box is real: None, 0 / 5 / 10 / 15 (default) / 30 minutes, 1 / 2 / 3 / 4 / 8 hours, 0.5 / 1 / 2 days, 1 week; it is sent with the new event (`isReminderOn`, `reminderMinutesBeforeStart`). At start, after an event is saved here, and every minute (Options > Calendar, 1 to 60) OpenOutlook reads Graph's `reminderView` for every Microsoft account with calendar access, over the look-back (30 days by default) plus the next day, and a timer fires at the next reminder time in between. Due reminders open an Outlook-like **Reminders** window: the selected item's subject, start and location; each reminder with "Due in" ("6 hours overdue", "in 10 minutes"); **Dismiss**, **Dismiss All** (asks first), **Open Item** (the calendar on that day) and **Snooze** (5 minutes to 1 week). Closing it with X only hides it until the next reminder comes due.
  - A reminder that came due while OpenOutlook was closed still shows, up to the look-back; older ones do not.
  - Dismiss and Snooze go to Graph, so they also apply in Outlook and on the phone (and the other way round). They are first saved in `reminders-state.json` beside the other settings, so a dismissed reminder never shows again, also offline or before Graph confirms; unconfirmed changes are sent again on each check. If the event is moved, its reminder time changes and it shows again, as in Outlook.
  - A newly due reminder plays Windows' Calendar Reminder sound (Linux: the desktop's alarm sound via canberra-gtk-play) and flashes the taskbar button on Windows; Options > Calendar > **Play reminder sound** (on).
  - File > Options > Calendar: **Show reminders** (on), the look-back in days (1 to 90, default 30) and how often to check for new or changed reminders (1 to 60 minutes, default 1; a reminder already known pops up on time regardless).

The owner created an appointment in OpenOutlook on Windows and confirmed that it appeared in Outlook on **2026-10-09**. This verifies one live Microsoft calendar write. Meeting invitations and Linux calendar use have not been verified against live accounts.

## Account and permission behavior

File > Info > Account Settings and Calendar read the same `ConnectedAccountRegistry`. Calendar filters that list to personal Microsoft accounts; File > Info also lists Gmail. Selecting a Microsoft account in File > Info selects that identity in Calendar. The refresh token for that identity is held by Windows Credential Manager or the Linux keyring, while `accounts.json` stores only the account label, public identity, and requested scopes.

New Microsoft sign-ins request `Calendars.ReadWrite` alongside the existing mail and contact scopes. An older mail login needs **Repair…** in File > Info > Account Settings > Email, or **Repair selected account sign-in** in Calendar. Finish the browser approval and OpenOutlook's account identity confirmation, then wait for the Accounts window to report **connected with calendar access**. Calendar enables creation only after the saved account records the scope and an editable calendar loads. A disabled New Appointment or New Meeting button has a tooltip explaining its current prerequisite.

The earlier permission check on the owner's Windows profile returned HTTP 403 while its saved account still lacked calendar scope. The saved account was updated afterward, and the owner verified creation in Outlook. That earlier 403 is not the current calendar state.

## Current limits

- Only one calendar is displayed at a time. Side-by-side and overlay views, editing a whole recurring series, recurrence itself, categories, a cross-app day preview, offline calendar data, and scheduling tools are pending.
- Several ribbon buttons are visible as disabled placeholders, including Schedule View, Open Calendar, Calendar Groups, Side by Side, Overlay, and Time Scale.
- Calendar availability and scope are checked for the selected account. A real calendar write has been confirmed on Windows; meeting sending and Linux need live verification.

Implementation: `CalendarPage.cs` and `CalendarPage.Layout.cs` build the UI, `CalendarEditorWindow.cs` creates event drafts, and `GraphCalendarClient.cs` lists calendars, loads occurrences, and creates events through Microsoft Graph. Reminders: `GraphReminderClient.cs` (reminderView, dismiss, snooze), `Reminders.cs` (`ReminderSchedule`: what is due; `ReminderStateStore`), `MainWindow.Reminders.cs` (scheduler) and `RemindersWindow.cs`. Focused Graph unit tests and Avalonia headless UI tests cover the feature without using a private account.
