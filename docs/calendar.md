# Hotmail / Outlook.com Calendar

OpenOutlook's Calendar uses the connected **personal Microsoft account** already used for mail. It does not create a second login. Gmail, iCloud, local PST calendars, and Microsoft 365 organization accounts are outside this integration.

## Available now

- Calendar destination in the left navigation, with a Home/View ribbon and a sidebar containing two mini months and the account's calendar list.
- Month, Week, Work Week, and Day views. Day and week views have timed columns; Today, previous/next, mini-month date selection, view switching, and Refresh navigate the schedule.
- Calendar and event loading from Microsoft Graph for one selected calendar at a time. Dates with loaded activity are bold in the mini months.
- **New Appointment** and **New Meeting** in an editable calendar. The composer accepts subject, dates and times, all-day, location, and notes; meetings also require attendee email addresses. It validates the time range and local daylight-saving gaps before saving. Meeting creation asks Graph to send invitations.
- A saved event appears in the current view immediately. If a save cannot be confirmed, the user can retry with the same Graph transaction ID or dismiss the retry.

The owner created an appointment in OpenOutlook on Windows and confirmed that it appeared in Outlook on **2026-10-09**. This verifies one live Microsoft calendar write. Meeting invitations and Linux calendar use have not been verified against live accounts.

## Account and permission behavior

File > Info > Account Settings and Calendar read the same `ConnectedAccountRegistry`. Calendar filters that list to personal Microsoft accounts; File > Info also lists Gmail. Selecting a Microsoft account in File > Info selects that identity in Calendar. The refresh token for that identity is held by Windows Credential Manager or the Linux keyring, while `accounts.json` stores only the account label, public identity, and requested scopes.

New Microsoft sign-ins request `Calendars.ReadWrite` alongside the existing mail and contact scopes. An older mail login needs **Repair…** in File > Info > Account Settings > Email, or **Repair selected account sign-in** in Calendar. Finish the browser approval and OpenOutlook's account identity confirmation, then wait for the Accounts window to report **connected with calendar access**. Calendar enables creation only after the saved account records the scope and an editable calendar loads. A disabled New Appointment or New Meeting button has a tooltip explaining its current prerequisite.

The earlier permission check on the owner's Windows profile returned HTTP 403 while its saved account still lacked calendar scope. The saved account was updated afterward, and the owner verified creation in Outlook. That earlier 403 is not the current calendar state.

## Current limits

- Only one calendar is displayed at a time. Side-by-side and overlay views, event details and editing, a cross-app day preview, offline calendar data, and scheduling tools are pending.
- Several ribbon buttons are visible as disabled placeholders, including Schedule View, Open Calendar, Calendar Groups, Side by Side, Overlay, and Time Scale.
- Calendar availability and scope are checked for the selected account. A real calendar write has been confirmed on Windows; meeting sending and Linux need live verification.

Implementation: `CalendarPage.cs` and `CalendarPage.Layout.cs` build the UI, `CalendarEditorWindow.cs` creates event drafts, and `GraphCalendarClient.cs` lists calendars, loads occurrences, and creates events through Microsoft Graph. Focused Graph unit tests and Avalonia headless UI tests cover the feature without using a private account.
