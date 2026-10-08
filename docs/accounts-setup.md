# Connecting Microsoft (Hotmail / Outlook.com) and Gmail accounts

Works the same on Windows and Linux (Ubuntu; under WSL see "Linux and WSL" at the end). Sign-in uses the system browser (OAuth with PKCE, a loopback callback on 127.0.0.1); OpenOutlook never sees your password. Refresh tokens are kept in the operating system's secret store, never in a file:

| System | Where tokens are stored |
|--------|-------------------------|
| Windows | Windows Credential Manager (`OpenOutlook/<provider>/<account>/<n>` entries, per user) |
| Linux | Secret Service keyring through libsecret (needs a persistent, unlocked default keyring) |
| other | sign-in is refused rather than falling back to a file |

The app needs a public OAuth client id for each provider. They are read from `openoutlook-oauth.json` next to the executable (a template is `openoutlook-oauth.example.json`; the real file is git-ignored):

```json
{
  "microsoftClientId": "<application (client) id>",
  "googleClientId": "<client id>.apps.googleusercontent.com",
  "googleClientSecret": "<client secret>"
}
```

## Microsoft (Hotmail, Outlook.com, Live)
1. Azure portal > App registrations > New registration. Supported account types: *Personal Microsoft accounts only*.
2. Authentication > Add a platform > *Mobile and desktop applications*; add the redirect URI `http://127.0.0.1` (the app uses a random loopback port; Microsoft matches the loopback host without the port). Allow public client flows.
3. API permissions (delegated, Microsoft Graph): `User.Read`, `offline_access`, `Mail.ReadWrite`, `Mail.Send`, `Contacts.ReadWrite`.
4. Copy the *Application (client) ID* into `microsoftClientId`.
5. In the app: File > Info > Add Account > Microsoft > Connect. The browser opens; sign in and approve; return to the app and confirm the account.

## Hotmail / Outlook.com mail actions
Mark read/unread, flag, archive, delete, and **Move to Folder / Copy to Folder** (ribbon Move, right-click menu, or drag the messages onto a folder of the same account; right-drag offers Move Here / Copy Here). The picker looks like Outlook's Move Items window and its New... button creates a folder. These need the account to have been connected with mail write permission; otherwise the app asks you to sign in again.

## Gmail
Labels appear as folders (Inbox, Starred, Important, Sent, Drafts, Spam, Trash and your own labels, with unread counts) and messages open in the reading pane with HTML. The sign-in requests the `gmail.modify` and `gmail.compose` scopes, which allow these actions (Home ribbon, right-click menu, or the reading-pane timer):

| Action | What it does in Gmail |
|--------|-----------------------|
| Mark read / unread | removes / adds the `UNREAD` label (an unread message you view is marked read after the Reading Pane wait time) |
| Flag / unflag | adds / removes `STARRED` |
| Archive | removes `INBOX` (the message stays in All Mail with its other labels) |
| Delete | moves to Trash (Gmail empties Trash after 30 days; permanent deletion needs a broader scope and is not offered) |
| Move to Folder... (ribbon Move, right-click, or drag onto a folder) | adds the destination label and removes the current one; "Add Label..." / Copy Here only adds the label; copying to Trash is a move |

New message, Reply, Reply All, Forward (with the original attachments) and saving attachments work for Gmail; the compose window has a From list of every connected account (a saved draft stays with its account). Editing a saved Gmail draft is not available yet. An account connected earlier without the `gmail.compose` scope keeps working for reading and the actions above; use Account setup to sign in again to enable sending.

Setting up the Google side (once):

1. Google Cloud Console > create a project > APIs & Services > Library > enable the **Gmail API**.
2. OAuth consent screen: User type *External*; add the scopes `.../auth/gmail.modify` and `.../auth/gmail.compose`; while the app is in *Testing*, add your own Gmail address under *Test users* (refresh tokens of testing apps expire after 7 days; publish the app, or keep re-connecting, for longer use).
3. Credentials > Create credentials > OAuth client ID > Application type **Desktop app**.
4. Put the client id in `googleClientId` and the client secret in `googleClientSecret` (Google requires the secret on the token request for desktop clients; for installed apps it is not confidential).
5. In the app: File > Info > Add Account > Gmail > Connect.

## What is tested
- Windows Credential Manager store: real round trips (short and multi-chunk tokens, replace, delete) under a throwaway prefix.
- The loopback callback listener, the PKCE exchange and refresh requests, the Google client secret (sent only to Google), the Gmail reader and its changes (exact requests, batching, refused-scope error) against a fake server, and in the headless UI the Gmail folders plus mark read / unread, flag, archive and delete against a stateful fake Gmail.
- The Microsoft authorization request built from the configured client id was accepted by Microsoft's login endpoint on Windows. Completing a sign-in needs a person at the browser and has not been done by the automated tests.

## Account Settings (File > Info > Account Settings)
The Account Settings button opens a menu like Outlook's (Account Settings…, Account Name and Sync Settings, and four items still marked "To be implemented"). **Account Settings…** is a dialog with three tabs (Email, Data Files, Junk Cleaner):
- **Email**: the connected accounts (New… connects one, Repair… signs in again, Remove disconnects it from this computer, Set as Default chooses the default sender of new messages). Change… and the arrows are marked "To be implemented".
- **Data Files**: the local mailbox copy of each account (a PST, default `%LOCALAPPDATA%\OpenOutlook\Mail` or `~/.local/share/openoutlook/mail`) and every Outlook data file you opened. New… creates a new, empty PST (save dialog; the new file is added to the folder list), Add… opens a PST, Settings… shows details (for a mailbox copy: how much mail it keeps, the largest attachment, Sync now, Change location…), Remove closes a file (never deletes it), Open File Location… shows its folder. Gmail accounts have a copy as well (one folder per label). A mailbox copy is stored in `%LOCALAPPDATA%\OpenOutlook\Mail` (Windows) or `~/.local/share/openoutlook/mail` (Linux) unless you change the location, and it is not shown as a second mailbox in the folder list.
- **Junk Cleaner** (Microsoft accounts): per account on/off, keywords (one per line), the rules (high importance, no To address, "on behalf of", flagged), automatic cleaning with an interval, **Clean now…** (shows a list with tick boxes before anything moves to Deleted Items), **Import from OutlookJunkCleaner…** and the log of what was removed. The ribbon's Junk group has **Clean Junk** for the same preview.
- **Moving mail between stores**: in the message list, right-click > **Move to Folder…** or **Copy to Folder…** (or drag onto a folder; right-drag asks Move Here / Copy Here). The picker lists every open data file and every connected mailbox, so mail can go from a PST to Hotmail or Gmail, from a mailbox into a PST, and between mailboxes. Gmail needs the `gmail.modify` scope to receive mail; a Microsoft account needs mail write access (sign in again if it says so).
- **Add by path…** (Data Files tab): type or paste a path such as `X:\email\archive.pst` or `\\server\share\archive.pst` when the file dialog does not list a network drive.

## Linux and WSL
- Tokens need a **persistent, unlocked default keyring** (GNOME Keyring or KWallet). Under WSL: `sudo apt install gnome-keyring dbus-user-session libsecret-tools`; the first use opens "Choose password for new keyring" (`scripts/wsl-create-keyring.sh` triggers it); `scripts/wsl-keyring-diag.sh` shows the state.
- Sign-in pages open in the system browser. The `.deb` launcher points `BROWSER` at the Windows browser when it runs under WSL.
- Windows network drives are available to WSL only after they are mounted (`/etc/fstab` drvfs lines such as `X: /mnt/x drvfs defaults,nofail,uid=1000,gid=1000 0 0`); the file dialog lists `/mnt/<letter>` drives in its sidebar.
