# People (Hotmail / Outlook.com contacts)

OpenOutlook's People section shows and edits the contacts of a connected **personal Microsoft account** through Microsoft Graph, with the same sign-in as mail and calendar. Gmail contacts, PST Contacts folders and Microsoft 365 organization accounts are not part of it yet.

## Available now (phase 1, 2026-10-15)

- **People on the navigation rail** opens the section; Mail and Calendar switch back.
- **My Contacts** lists the account's default Contacts folder and its contact folders (Graph `/me/contacts` and `/me/contactFolders`). With more than one Microsoft account an account picker appears above the folders.
- **Contact list** sorted the way Outlook files people (File as, else "Last, First", else the name), with round avatars: the contact's picture when it has one (loaded in the background, four at a time, first 300 of a folder), otherwise coloured initials. The line under the name is the first email address or phone number. **Items: N** counts what is shown.
- **A-Z jump index** (123, a-z): selects the first person filed under that letter, or the next letter that has one.
- **Details card**: picture, name, job title and company, Email / Meeting / Edit buttons, every email, work / home / mobile phone, work / home / other address, web page, IM, birthday and notes (all selectable for copying).
- **Ribbon (Home)**: New Contact, Delete, Email (new message to the contact from that account), Meeting (the appointment window in meeting mode with the contact invited, saved in the default calendar), People and List views, Search People (filters name, company, job title, email and phone as you type), Refresh. **View** has People / List as well. Every other ribbon button is visible with the tooltip **"Planned feature"** and says so in the status bar when clicked.
- **Contact window** (double-click a contact, Enter, Edit, or New Contact), laid out like Outlook's General page: Full Name (split into title, first, middle, last and suffix on save, "Last, First" accepted), Company, Job title, File as (Outlook's five choices), Email / Email 2 / Email 3 with Display as, Web page address, IM address, four phone rows with a type picker (Business, Business 2, Home, Home 2, Mobile; fax, pager and the other Outlook types are listed but disabled with "Planned feature": Graph has no field for them), the address box with Business / Home / Other (parsed into street, city, state, postal code and country), picture (JPEG or PNG up to 4 MB, uploaded with the contact), a live business-card preview, and Notes. **Save & Close**, **Save & New** and **Delete** write to the account; fields the form does not show (birthday, extra IM addresses) are kept.
- An account whose saved sign-in has no `Contacts.ReadWrite` is asked to **Repair** its sign-in; nothing is sent to Graph until it has.

## Not yet

- Contact groups (New Contact Group, Groups), Business Card / Card / Phone views, Move, Mail Merge, Forward / Share contacts, Categorize / Follow Up / Private, Address Book, Details / Certificates / All Fields pages, Check Full Name, Map It, the mailing-address flag, fax and other phone types.
- Phase 2: keep the contacts in the account's mailbox copy (PST Contacts folder) for offline use and Outlook. Phase 3: autocomplete in To / Cc / Bcc and an address book picker. Phase 4: groups and Gmail contacts (Google People API).

## Implementation

`GraphContactsClient` (`src/OpenOutlook.Providers.Microsoft`): folders, contacts (paged, 5,000 per folder at most), create / update / delete, photo get / set; the same safety rules as the calendar client (no redirects, graph.microsoft.com/v1.0/me only, size limits, the token checked against the account). `PeoplePage.cs` (page and ribbon), `ContactEditorWindow.cs` (contact window), `ContactFormat.cs` (name / address / sort / initials rules), `MainWindow.People.cs` (rail, Email and Meeting). Tests: `GraphContactsClientTests`, `ContactFormatTests`, `PeoplePageHeadlessTests`, `PeopleRailOpensContactsAndSwitchesWithCalendarAndMail`.
