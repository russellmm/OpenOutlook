# Junk: Not Junk, Block Sender and Junk Email Options - plan (2026-10-16, not started)

Owner request (with screenshots of classic Outlook): the message right-click **Junk** menu (Block Sender, Never Block Sender, Never Block
Sender's Domain, Never Block this Group or Mailing List, Not Junk, Junk E-mail Options...) and the **Junk Email Options** dialog (tabs Options,
Safe Senders, Safe Recipients, Blocked Senders, International). Microsoft (Hotmail / Outlook.com) accounts first.

## 1. Where Outlook keeps the lists, and what we can reach

- The owner's assumption is right: the lists are stored **on Microsoft's server**, in the mailbox's junk email rule. Outlook desktop,
  Outlook.com on the web (Settings > Mail > Junk email) and the phone app all read and change the same lists, and the server applies them when
  mail arrives.
- **Microsoft Graph (what OpenOutlook uses) cannot read or change these lists.** There is no endpoint for them (Microsoft Q&A: "not exposed via
  the Graph API"). What Graph has:
  - moving a message (v1.0): "Not Junk" = move from Junk Email to Inbox works today;
  - `reportMessage` (beta only): report a message as `junk`, `notJunk` or `phish`, optionally moving it. It "improves mail filtering";
    Microsoft does not say whether it adds the sender to Safe / Blocked Senders. To find out, run it once on a real junk message, with the
    owner's OK, and then look at Outlook's lists;
  - the older `markAsJunk` / `markAsNotJunk`, which did add / remove the sender on the Blocked Senders list, are deprecated and stopped
    working on 2025-12-30.
- Other routes are out: EWS is being retired for Exchange Online (and needs work / school accounts); MAPI over HTTP, what Outlook desktop
  itself speaks, is a very large protocol to implement for one dialog.

## 2. Proposal: OpenOutlook's own lists, filled from Outlook once, applied by OpenOutlook

- **Junk Email Options window** per account with the same five tabs and buttons (Add, Edit, Remove, Import from File, Export to File).
  The lists are kept in `junk-lists-<account>.json` beside the other settings.
- **Import from File / Export to File** use Outlook's own format (a plain text file, one address or `@domain` per line). So the owner exports
  each list from Outlook today, imports it once, and keeps both programs in step by exporting / importing again when wanted.
- **Applied by OpenOutlook** (only while it runs, like the Junk Cleaner):
  - Safe Senders / Safe Recipients: never removed by the Junk Cleaner; mail from them found in Junk Email is moved back to the Inbox.
  - Blocked Senders: mail from them is moved from the Inbox to Junk Email (when the mailbox copy sees new mail).
  - "Also trust email from my Contacts": uses the People (contacts) list we already read.
  - International > Blocked Top-Level Domain List (`.ru`, `.cn`, ...): treated like blocked senders. Blocked Encodings: later.
- **Options tab**: Microsoft's own filter level (No Automatic Filtering / Low / High / Safe Lists Only) is a server setting Graph cannot read
  or set; the tab says so and points to Outlook.com > Settings > Junk email, plus the Junk Cleaner settings we already have. "Permanently
  delete" stays off (the house rule: everything goes to Deleted Items).
- **Right-click > Junk** on a Microsoft message, as in Outlook:
  - Block Sender: add the sender to Blocked Senders, move the message to Junk Email (and report it as junk, if the live test shows it helps);
  - Never Block Sender / Never Block Sender's Domain (@domain) / Never Block this Group or Mailing List (the To address, to Safe
    Recipients): add to the list;
  - Not Junk (in Junk Email): move to the Inbox and, like Outlook, ask "Always trust email from <address>" (ticked by default) to add it to
    Safe Senders;
  - Junk E-mail Options...: the window above.
- Not synced back to Outlook: changing a list in OpenOutlook does not change Outlook's list (and the other way round) until exported / imported.
  If the live `reportMessage` test shows Microsoft does update its lists, Block Sender / Not Junk also report, and the server lists get
  those additions too.

## 3. Order of work

1. `JunkLists` (pure: match sender / recipients / domain / TLD, normalise, Outlook text import / export, bounded) + store + tests.
2. Junk Email Options window (five tabs) + headless tests; reached from right-click and from the Junk Cleaner settings.
3. Right-click Junk submenu + Not Junk move + "Always trust" prompt + headless tests.
4. Apply the lists: Junk Cleaner skips safe; rescue safe mail from Junk; move blocked mail out of the Inbox; logged like the Junk Cleaner.
5. With the owner's OK: one live `reportMessage` test, then decide whether Block Sender / Not Junk also report.
6. Docs (`FEATURES.md`, `EDGE_CASES.md`, handoff), both builds.
