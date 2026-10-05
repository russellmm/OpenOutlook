# Connecting Microsoft (Hotmail / Outlook.com) and Gmail accounts

Works the same on Windows and Linux. Sign-in uses the system browser (OAuth with PKCE, a loopback callback on 127.0.0.1); OpenOutlook never sees your password. Refresh tokens are kept in the operating system's secret store, never in a file:

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

## Gmail
Labels appear as folders (Inbox, Starred, Important, Sent, Drafts, Spam, Trash and your own labels, with unread counts) and messages open in the reading pane with HTML. The sign-in requests the `gmail.modify` scope, which allows these actions (Home ribbon, right-click menu, or the reading-pane timer):

| Action | What it does in Gmail |
|--------|-----------------------|
| Mark read / unread | removes / adds the `UNREAD` label (an unread message you view is marked read after the Reading Pane wait time) |
| Flag / unflag | adds / removes `STARRED` |
| Archive | removes `INBOX` (the message stays in All Mail with its other labels) |
| Delete | moves to Trash (Gmail empties Trash after 30 days; permanent deletion needs a broader scope and is not offered) |
| Move to Folder... (ribbon Move or right-click) | adds the destination label and removes the current one; "Copy to Folder..." only adds the label |

Composing, replying, forwarding and saving attachments are not available yet (sending needs another scope). An account connected earlier with the read-only scope keeps working for reading; use Account setup to sign in again to enable these actions.
1. Google Cloud Console > create a project > APIs & Services > Library > enable the **Gmail API**.
2. OAuth consent screen: User type *External*; add the scope `.../auth/gmail.modify`; while the app is in *Testing*, add your own Gmail address under *Test users* (refresh tokens of testing apps expire after 7 days; publish the app, or keep re-connecting, for longer use).
3. Credentials > Create credentials > OAuth client ID > Application type **Desktop app**.
4. Put the client id in `googleClientId` and the client secret in `googleClientSecret` (Google requires the secret on the token request for desktop clients; for installed apps it is not confidential).
5. In the app: File > Info > Add Account > Gmail > Connect.

## What is tested
- Windows Credential Manager store: real round trips (short and multi-chunk tokens, replace, delete) under a throwaway prefix.
- The loopback callback listener, the PKCE exchange and refresh requests, the Google client secret (sent only to Google), the Gmail reader and its changes (exact requests, batching, refused-scope error) against a fake server, and in the headless UI the Gmail folders plus mark read / unread, flag, archive and delete against a stateful fake Gmail.
- The Microsoft authorization request built from the configured client id was accepted by Microsoft's login endpoint on Windows. Completing a sign-in needs a person at the browser and has not been done by the automated tests.
