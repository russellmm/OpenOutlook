# OpenOutlook

An **early, incomplete development prototype** of a classic-Outlook-inspired mail client for Ubuntu, built with .NET 8 and Avalonia 11. **Not ready for daily use or release.** The current desktop can open PST archives **read-only**, browse messages, search selected-folder headers, export supported attachments, and export eligible plain-text messages to EML. Account setup has a browser sign-in flow for personal Microsoft and Google accounts. The project owner supplied the OpenOutlook Microsoft application ID and showed a successful Hotmail connection; Google sign-in remains unconfigured. The desktop restores saved Microsoft accounts on startup, lists visible Microsoft mail folders, and reads the newest 50 messages and their bodies from a selected folder through Microsoft Graph. A headless check against the owner-authorized account verified saved-token refresh, folder listing and message reading. There is no background synchronization, sending, or PST editing in the app.

See [BUILD_STATUS.md](BUILD_STATUS.md) for exact implemented slices and release gates, [PRODUCT_REQUIREMENTS.md](PRODUCT_REQUIREMENTS.md) for approved requirements, and [DESIGN_SPEC.md](DESIGN_SPEC.md) for the design.

## Build and run

```bash
dotnet restore OpenOutlook.sln -p:NuGetAudit=false --ignore-failed-sources
dotnet test OpenOutlook.sln --no-restore
dotnet run --project src/OpenOutlook.Desktop/OpenOutlook.Desktop.csproj
```

To build a self-contained Linux x64 executable:

```bash
dotnet publish src/OpenOutlook.Desktop/OpenOutlook.Desktop.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -p:NuGetAudit=false -o publish/linux-x64
./publish/linux-x64/OpenOutlook.Desktop
```

A locally built `publish/OpenOutlook-linux-x64.tar.gz` contains the executable, the owner’s public OAuth ID file, a short run note and the project license. Build outputs and local OAuth configuration are intentionally excluded from GitHub. The package launched headlessly on the development Ubuntu host, but has not been validated on a clean Ubuntu installation.

## Account setup preview

Open **Account setup** and choose Outlook.com/Hotmail or Gmail. The user-facing flow asks for no application ID, password or client secret; when that provider is configured in an OpenOutlook build, it opens the provider's sign-in page in the system browser, verifies the account identity, asks you to confirm it, and stores a refresh token in the persistent Linux keyring. It requests read-only mail access. A working, unlocked persistent default keyring is checked before the browser opens. The app supports up to two accounts per provider and four total. You can reconnect or disconnect an account locally from the same window. A saved Microsoft account appears in the main sidebar after restart. Select any visible mail folder to load its latest 50 messages, or use **Refresh folder** to reload them. Child folders are shown under their parents; hidden folders are omitted. This is an online preview, not a complete synchronized or offline mailbox.

Account labels and public application IDs persist in `$XDG_DATA_HOME/OpenOutlook/accounts.json` (normally `~/.local/share/OpenOutlook/accounts.json`), with private file permissions. Refresh tokens stay in the persistent Linux keyring, not beside the executable. Both survive rebuilding or replacing the OpenOutlook executable. The app refreshes access in memory from the saved keyring token, so a normal restart does not require browser sign-in unless the provider revokes access or the token is removed.

The OpenOutlook build owner must register one public desktop application with each provider and place its public IDs in `openoutlook-oauth.json` beside the executable, using the keys shown in `src/OpenOutlook.Desktop/openoutlook-oauth.example.json`. The owner-supplied Microsoft ID is configured in the owner’s local build, while Google is pending. A fresh source checkout has no configured provider until the build owner adds the public ID file; the example file contains the expected keys. Microsoft registration must support personal accounts, public desktop clients and the `http://localhost/callback` mobile/desktop redirect; Google registration must be a **Desktop app** OAuth client. The file contains IDs only, never client secrets or tokens. Without the provider's ID, its Sign in button stays disabled and explains why. Google projects using the Gmail read-only scope may require additional consent configuration or verification before accounts outside a test-user list can authorize. See the [Microsoft redirect guidance](https://learn.microsoft.com/en-us/entra/identity-platform/reply-url), [Google installed-app guidance](https://developers.google.com/identity/protocols/oauth2/native-app), and [Google restricted-scope guidance](https://developers.google.com/identity/protocols/oauth2/production-readiness/restricted-scope-verification).

The `NuGetAudit=false` restore option is a workaround for a local vulnerability-cache permission issue, **not** a completed security audit. Headless Xvfb in the development environment needs `-extension GLX`; details are in BUILD_STATUS.md. The local executable is a development preview, not an installer or validated release package.

## Safety and privacy

- Never modify an original PST with this prototype. Supplied test PST archives and private message contents are excluded from Git and must not be uploaded.
- The desktop displays a safe, limited rich-text preview for HTML mail using native text runs. It keeps headings, emphasis, paragraphs and lists, while blocking images, scripts, CSS, link navigation and other active content. A plain-text view is available for each HTML message; full HTML layout is still a release task. Export is limited and refuses content it cannot faithfully preserve.
- Never commit account credentials, refresh tokens, local cache, or private configuration. The owner demonstrated a live Hotmail sign-in. Saved-token refresh, folder browsing, the HTML preview and its plain-text switch passed headless checks without recording message contents.
- Dependency vulnerability review, Google provider authorization, Windows classic Outlook interoperability, safe PST edits, and full product acceptance remain outstanding.

## License

MIT; see [LICENSE](LICENSE). The PST core was contributed by the project owner for incorporation and MIT distribution.
