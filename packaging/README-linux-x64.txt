OpenOutlook development preview — Linux x64

Run from a graphical Linux desktop:
  ./OpenOutlook.Desktop

Open a PST from the File ribbon tab, or pass its path when launching:
  ./OpenOutlook.Desktop /path/to/archive.pst
Opened PST paths are saved in ~/.config/OpenOutlook/attached-psts.json (or under
$XDG_CONFIG_HOME) and restored read-only at the next launch. Detach PST removes
the saved path without deleting the PST file. Unavailable paths stay saved for retry.
Window size, position and pane/column layout persist in
~/.config/OpenOutlook/view-layout.json (or under $XDG_CONFIG_HOME).

This is a self-contained .NET 8 executable; a separate dotnet installation is not needed.
Account labels are stored in the user's OpenOutlook data directory and refresh tokens
in the persistent Linux keyring. Replacing this executable does not require signing in
again while the saved account authorization remains valid.

Select a PST folder and use Folder > Export folder to create a new directory of EML files,
including subfolders. Cancel export stops a run; failures do not leave a completed
export directory. Supported individual messages can also be exported to EML.
Select a message and use Home > Save attachment to save a supported Hotmail or PST file
attachment to a new file. Exports never edit the original PST or overwrite a target.
For Microsoft mail, Home offers New Email, Reply, Reply All, Forward, draft saving, explicit Send, read/unread, flags, Archive and Delete. Existing Hotmail sign-ins need one Account setup > select saved account > Sign in again to grant the new permissions. PST and Gmail remain read-only.
HTML mail is laid out with a bundled headless browser (the chromium folder next to the program; keep it there).
If that folder is missing, an installed Chrome or Chromium is used; otherwise a basic preview is shown. Supported
embedded and remote images load automatically.
Installing the .deb (sudo apt install ./openoutlook_0.1.0_amd64.deb) puts the program in /opt/openoutlook, adds the
openoutlook command, a menu entry and the icon, and pulls in the libraries the bundled browser needs. Sign-in tokens
are kept in the desktop keyring (libsecret: GNOME Keyring or KWallet must be running).
Connected Microsoft and Gmail accounts keep a local copy of the mailbox (PST + sync state) under ~/.local/share/OpenOutlook
unless you choose another folder in Account Settings > Data Files.
Remote images contact the sites named by the sender's message when the message opens.
Use Open in new window for a separate message reader. View original here (trusted mail)
loads the original HTML and message scripts inside OpenOutlook when you choose it;
ordinary HTML and images appear without using that choice.
The plain-text view is selectable. Links in formatted mail open the system handler; the screenshot readers offer zoom and the main reader can save a printable PDF. Open interactive message uses the system WebKitGTK 4.1 library for a selectable HTML pop-out.

Google sign-in is not configured in the owner's local build. There is no background
sync, offline mailbox, or PST editing yet. Microsoft send and write actions have synthetic tests but still need controlled live verification. This is a development preview,
not a validated release package.
