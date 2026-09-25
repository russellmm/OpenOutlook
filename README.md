# OpenOutlook

An **early, incomplete development prototype** of a classic-Outlook-inspired mail client for Ubuntu, built with .NET 8 and Avalonia 11. **Not ready for daily use or release.** The current desktop can open PST archives **read-only**, browse messages, search selected-folder headers, export supported attachments, and export eligible plain-text messages to EML. Microsoft Graph and Gmail adapters, OAuth pieces, and Junk Cleaner matching have offline tests but **are not connected to live accounts**. There is no account sign-in, sending, synchronization, or PST editing in the app.

See [BUILD_STATUS.md](BUILD_STATUS.md) for exact implemented slices and release gates, [PRODUCT_REQUIREMENTS.md](PRODUCT_REQUIREMENTS.md) for approved requirements, and [DESIGN_SPEC.md](DESIGN_SPEC.md) for the design.

## Build and run

```bash
dotnet restore OpenOutlook.sln -p:NuGetAudit=false --ignore-failed-sources
dotnet test OpenOutlook.sln --no-restore
dotnet run --project src/OpenOutlook.Desktop/OpenOutlook.Desktop.csproj
```

The `NuGetAudit=false` restore option is a workaround for a local vulnerability-cache permission issue, **not** a completed security audit. Headless Xvfb in the development environment needs `-extension GLX`; details are in BUILD_STATUS.md. The project has not been packaged or validated on Ubuntu 26.04.

## Safety and privacy

- Never modify an original PST with this prototype. Supplied test PST archives and private message contents are excluded from Git and must not be uploaded.
- The desktop displays plain text rather than loading remote images or rendering message HTML. Export is limited and refuses content it cannot faithfully preserve.
- Never commit account credentials, refresh tokens, local cache, or private configuration. OAuth/keyring integration is **not complete**, and account setup remains disabled.
- Dependency vulnerability review, live provider authorization, Windows classic Outlook interoperability, safe PST edits, and full product acceptance remain outstanding.

## License

MIT; see [LICENSE](LICENSE). The PST core was contributed by the project owner for incorporation and MIT distribution.
