# PST Editing — design & phased plan (started 2026-10-01)

Goal: move messages between folders, delete (Deleted Items + purge), persistent flags — on real
archives, without ever risking data loss. Owner archives: rmarrash_1.pst (909MB), _2 (779KB, the
disposable test fixture — never write to 1/3 in tests), _3 (2.6GB).

## SHIPPED STATE (2026-10-02) - complete feature matrix

All editing happens inside Editing Mode (archive-root context menu). Every session: .bak backup
first, writable open, edits applied live, Finish = full block-CRC verification or auto-restore;
window close commits-or-rolls-back. Nothing is ever freed; the file never shrinks; orphaned data is
spec-legal garbage (docs above explain why this is the safe posture).

| Capability | Entry points | Engine | Commit |
|---|---|---|---|
| Read/unread + flag edits | ribbon, shortcuts, reading-pane auto-mark | dual-copy PR_MESSAGE_FLAGS + item props + unread badge, dry-run validated | 57fa0ee / 42dd8cf |
| Delete -> Deleted Items | Delete key, ribbon Delete (classic Outlook semantics) | MoveMessage re-link into the store's own trash | 74d5182 |
| Permanent delete | Shift+Delete anywhere; plain Delete inside Deleted Items; confirmation dialog | TCROWID unlink; matrix slots + PC/subnodes orphaned | fadc84f / 74d5182 |
| Purge (Empty Deleted Items) | Deleted Items folder context menu, enabled only while editing | per-message validated unlink under the reader gate | cd604ac |
| Move between folders | drag rows onto any visible tree folder; message right-click -> "Move to Folder..." dialog (system folders filtered with the tree's own presentation rule) | full re-link: dest row from source cells, heap rebuild copy-on-write, BTH regrow, matrix extend/reuse, atomic NBT flip, source unlink, nidParent repoint | d0b39b7 / 9e97c4e |
| Integrity seal | Finish Editing Mode / window close | VerifyIntegrity: every BT page CRC + every block trailer (sig, payload CRC, bid echo) | 57fa0ee |
| Pre/post damage triage | opt-in OPENOUTLOOK_BASELINE_PST test | same verifier, read-only; baselines captured for all three owner archives | 74e837a |

Live headless proofs: every row of this table was driven through the real app on fixture copies
(screenshots verified), including restart persistence and rollback-observed-working-for-real.
Tests: 399/399 with and without the fixture; rmarrash_1/2/3 md5-identical throughout; published
binary smoke-tested (see BUILD_STATUS.md for sha).

### Deliberate scope decisions (v1)
- Unicode PST only; ANSI archives stay read-only forever. OST out of scope.
- No block allocator growth path and no B/NBT page splits: allocation uses existing free slots
  (probed: thousands available in every real archive); full trees refuse with a clear message.
- Nothing is freed or compacted; scanpst/Outlook cleanup reclaims orphans eventually.
- SMQ search-update queues not maintained (both reference writers omit them; scanpst advisory only).
- Folder create/rename/delete (Phase E) NOT implemented - see plan below, unchanged.

### Outstanding follow-ups
1. Hardware check on real Windows Outlook: tolerance of the inert zeroed TCROWID trailing slot left
   by deletes (worst known case: scanpst advisory or phantom row; our reader skips zero keys).
4. Matrix addressing subtlety RESOLVED (2026-10-03): fragmented matrices tile rows by logical 8176-byte
   blocks with inter-fragment padding, so true capacity < totalLen/rowSize; the reader's dual formula
   (flat when length%rowSize==0, tiled otherwise) is now mirrored exactly in MoveMessage: reuse only at
   formula-consistent offsets validated by dry-run chain walk; full fragmented matrices grow by extending
   the LAST fragment to its tile capacity (<=8176, Outlook's observed max across 397,786 blocks in real
   files) and swapping that one BREF entry before the visibility flip. All three owner archives now pass
   399/395->399 including sequential-move round-trips into full fragmented-matrix folders.
2. Editing rmarrash_1.pst creates a ~909 MB .bak beside it - intentional; manual cleanup per session.
3. Phase E (folder create/rename/delete) remains planned, untouched.

## Verified inventory of what already exists (read from source, 2026-10-01)

| Capability | State | Where |
|---|---|---|
| Header parse ANSI+Unicode(UMPM 64-bit) | done | `Ndb.ReadHeader` |
| NBT + BBT walk (BRef map, block table) | done, in-memory dicts `_nbt/_bbt` | `Ndb.WalkBt` |
| LTP heaps (LTCU/LTPU), subnodes, BTH enumeration | done | `HeapOnNode`, `WalkBthLevel` |
| Folder tree load, contents per folder | done | `PstStore.LoadFolders` |
| Crypt (NCD/LZFK cyclic keys) + CRC32 | done | `PstCrypto` |
| **Same-size external block rewrite** incl. re-encrypt + trailer rebuild (size/sig/CRC/bid) | done | `Ndb.RewriteExternalBlock` → `WriteBlockBytes` |
| **NBT page entry rewrite** with BT-page CRC recompute | done | `Ndb.RewriteNidParent` |
| In-heap fixed uint32 property patch, persisted through to disk | done — flows into RewriteExternalBlock | `HeapOnNode.TryPatchFixedUInt32` → `TryPatchHidBytes` |
| `SetReadState` / `SetFlagged` | **work when writable** (persist flags) but folder UnreadCount not updated | `PstStore` 357-378 |
| `MoveMessage` | **WRONG semantics** — only rewrites NID parent; row stays in source Contents BTree. Must be replaced, currently unused by UI | `PstStore` 380-388 |
| Writable open path | exists (`PstStore.Open(path, writable:true)` → `FileAccess.ReadWrite`, FileShare.Read) but Desktop always passes false (MainWindow.axaml.cs:441) | |
| Integrity verifier | none | to build |
| Backup/rollback wrapper | none | to build |

BT pages are read unencrypted (per spec — only data blocks carry cyclic-key encoding); BT page CRC
recompute pattern already demonstrated in RewriteNidParent (unicode crc at 500).

## Safety architecture (non-negotiable)

1. **Backup-first**: any write session starts by copying the archive to `<path>.bak-<utcstamp>`
   (hardlink-free full copy; skip only if user opts out for huge files, with explicit warning).
2. **Post-write verification**: `Ndb.VerifyIntegrity()` — re-walk NBT+BBT, verify every BT page CRC
   and every external block trailer sig+CRC; run after each mutating operation batch. On failure:
   auto-restore from backup + surface exact error.
3. **No half-moves**: an op = one batch (row delete + row insert + count updates); if any step fails,
   restore backup. v1 does this by re-verifying and restoring rather than a journal; journal is later work.
4. Tests write ONLY to temp copies of rmarrash_2.pst. Owner's big archives are never written by tests.
5. Editing mode is explicit per-archive UI state (never implicit), status bar says EDITING.

## Phases

### A — make the existing primitives trustworthy + flags UX (this round)
- `Ndb.VerifyIntegrity()` (full-file CRC pass; ~seconds on 909MB).
- `SetReadState`/`SetFlagged`: also update folder Info node counts (UnreadCount 0x6D05 via same
  fixed-patch path; ContentCount unchanged for flags). Failure to patch counts is non-fatal (badge can
  drift) but logged.
- `PstEditSession` helper: backup → writable open → ops → verify → (restore|keep); removes .bak on success? NO — keep latest .bak until next session, user-visible.
- Desktop: right-click archive root "Editing mode" toggle; when on, store reopens writable; Unread/Read
  ribbon + list shortcuts call SetReadState/SetFlagged and drop the local read-state.json override for
  that item (native wins once writable).
- Tests on rmarrash_2.pst copy: flag persists across reopen; integrity passes; counts move.

### B — Contents-BTree row DELETE (enables real delete)
Row = record in folder's "Message Contents" BTH leaf (key=RID, value=itemID NID + per-row LTP heap).
Delete steps: remove record from leaf BT page (memmove shift or page free-list), cEnt--, recompute CRC;
delete the row's LTP heap node? v1: **orphan** it (Outlook tolerates orphaned blocks; scanpst reclaims) —
document bloat tradeoff. Update folder ContentCount/UnreadCount. Attachment BTrees + per-FID Message
Properties subnode under FAIPSubtree: v1 leave attached to item (they're keyed by FID=itemID; orphans).
Risk: page free-list vs shift choice — research brief decides.

### C — row INSERT (enables real move) (SHIPPED - see matrix above)
Insert = new RID in destination Contents BTH (RIDLAST++ on the BTree? research: per-BTree next-RID
from max RID + 1 with collision care), record {RID, itemID NID} + copy/clone of source row LTP heap.
Space problem: leaf page must have room → reuse page free space (Outlook's ibFreelistStart) else split
(new BT page = file grow + BBAT append + BRef map growth — the heavy NDB work). v1 strategy per
research brief; fallback "destination folder page full, cannot move" honest error.
Also on move: rewrite PR_FOLDERS_PATH/PR_PARENT_DISPLAY(_W) in the item's props (variable-size — may
need local-props stream append or accept stale value? research), keep ConversationIndex unchanged.

### D — delete UX + purge (SHIPPED - see matrix above)
Delete key / ribbon Delete → move row to Deleted Items folder (B+C). "Empty Deleted Items" → batch B.
Recycle-bin semantics: restore = reverse move.

### E — folders & polish
Create/rename/delete folder (Folder Information Tree LTP edits + Children BTree rows — same machinery),
rename via fixed/variable patch, OST out of scope forever, ANSI (97-2002) write support explicitly
out of scope (read-only remains).

## Research outcomes (MS-PST rev 11.2 + two Outlook-verified writers: imap2pst, PST-Builder)

- Move is RE-LINK, not copy (§2.6.3.2.8): same message NID everywhere; delete source Contents-TC
  row, insert destination TC row with a fresh RowID, set nidParent on the message NBT entry (we
  already have that primitive), update folder PCs. No PR_Folder_Path to patch - it does not exist;
  conversation topic/index live in the PC and travel untouched.
- There is NO persisted RID counter (TCINFO has none). Spec says any unique RowID works, but our
  reader resolves rows as byNid[row.RowId] and real Outlook files set dwRowID == message NID - so for
  MOVES the inserted destination row MUST carry dwRowID = message.Nid (worker-confirmed empirically);
  max+1 would break GetMessages. Fresh inserts of new messages (later phases) need their own NIDs.
- Counts (ContentCount/UnreadCount) are CALCULATED properties; cached copies are nice-to-have,
  Outlook recomputes. Our badge updates stay best-effort.
- GC/space reuse is never required: append-only + orphaning is spec-legal and exactly what both
  verified writers ship. "Clean" = trees resolve, sigs/CRCs correct, AMaps truthful, EOF on span -
  NOT zero orphans. v1 never frees anything.
- Corrected earlier speculation: "FAIPSubtree / 0x0190/0x1D0" exist in no public revision; the real
  per-folder message-local properties are Contents-TC columns (which is exactly why flag writes had
  to update both the item PC and the TC row - confirmed empirically in Phase A).
- Landmines for the allocator work (Phase C): pages must sit at 512-multiple ib (Outlook fail-fast
  0x80040813); BBT/NBT page trailers carry a COUNTER bid while AMap/PMap carry absolute ib; EOF must
  land on an AMap span boundary (253,952 B); fAMapValid=INVALID before alloc work, 0x02 last; header
  CRC pair + dwUnique bump on every header write.

### Phase B design decision (delete without an allocator)
Deleting a row = remove its TCROWID record from the Row-ID BTH (byte shift within the HN block +
count fix, same-size rewrite - no allocation). The matrix record itself is left as unreferenced
garbage: every surviving TCROWID still points at its own dwRowIndex slot, so readers cannot tell.
(Doing half of "shift matrix + fixup indices" IS corruption - landmine #8; doing neither consistently
is legal orphaning.) Message PC/recipients/attachments subnodes orphan too - the same state Outlook
itself produces for deleted mail until a manual cleanup.

## Status log
- 2026-10-01: inventory done. Research brief received and incorporated (above).
- 2026-10-01 Phase A SHIPPED (57fa0ee core + Editing Mode UI same day): PstEditSession
  backup/verify/rollback, VerifyIntegrity full-CRC pass, dual-copy flag writes with dry-run
  validation, Editing Mode context menu on archive roots, ribbon Unread/Read + shortcuts intercepted
  for PST rows (native when editing, sidecar overlay otherwise), reading-pane auto-mark writes
  natively in editing mode, shutdown finalizes sessions. Live-proven headless end-to-end: turn on ->
  mark unread via ribbon -> row bolds -> finish editing ("verified against every block CRC") ->
  fresh restart -> still bold; .bak kept.
- Next: Phase B (TCROWID unlink = real delete), then Phase C (append allocator + TC insert = move).
- 2026-10-01 pre-edit baselines (read-only): rmarrash_1.pst and rmarrash_3.pst both pass full
  VerifyIntegrity with 0 problems (2.6 GB in 4.8 s). Negative control proven: flipping a byte inside
  an allocated block is reported by bid; flips in unallocated dead space are correctly ignored.
  Harness: ArchiveIntegrityBaselineTests (opt-in OPENOUTLOOK_BASELINE_PST). Any future post-edit
  verification failure is therefore attributable to the edit, not pre-existing damage.

## Phase C allocator specification (append-only; no page splits in v1)

Every mutation that needs NEW space (TC row matrix extension, new subnode NIDs for moved items)
goes through ONE primitive: `Ndb.AllocateBlock(ushort payloadSize) -> Bid`, append-only.

Preconditions checked before ANY allocation in a session (fail closed, nothing written):
1. Header re-read; fAMapValid == 0x02 (VALID). If INVALID, refuse editing until Outlook rebuilds it.
2. Allocation must skip reserved page positions: DList@0x4200, AMap every 253,952 B from 0x4400,
   PMap every 2,031,616 B from 0x4600 (bid%4==3 slots are pages - never allocate those).
3. Enough free leaf room in BBT and NBT for the WHOLE batch counted up front (a BT leaf with
   cEnt < cEntMax has room; each move needs ~2-6 entries). No room -> refuse with a clear error.
   NO page allocation, NO splits, NO root growth in v1.

AllocateBlock steps (payload <= 8176 B):
a. bid = header.bidNextB (bid%4 != 3; pages are never allocated by us).
b. Prefer an already-free slot from the AMap bitmaps below ibAMapLast+span (2041 free slots exist in
   a typical real file - growth is rare). Only when none fit: extend at EOF rounded up to 64, and
   grow the file in whole spans from the grid that starts at 0x4400, writing the new AMap page.
c. Write payload with crypt encoding for that bid; trailer {cb, sig(ib,bid), crc(data+trailer with
   crc zeroed), bid}. cRef = 2 (BBT entry itself + one holder).
d. Insert BBT entry {bid, ib, cb, cRef} into a pre-validated leaf page: shift entries right, bump
   cEnt @488, recompute page CRC with crc field zeroed, keep bid sort order.
e. Set the AMap bit for every 64-byte slot consumed; decrement cbAMapFree.

Session bookkeeping (PstEditSession.Commit order): fAMapValid=INVALID before first alloc -> all
writes -> ibFileEof/ibAMapLast/bidNextB/bidNextP updated -> rgnid[32] raised for new NID indexes ->
header dwCRCPartial(471 B @8) + dwCRCFull(516 B @8) recomputed, dwUnique++ -> fAMapValid=0x02 LAST.
A crash before the last step leaves INVALID = Outlook's safe slow rebuild, never silent collisions.

Move op composition (§2.6.3.2.8 re-link semantics):
1. dry-run validate everything (source row unlinkable, dest leaf room, NBT parent writable);
2. insert destination TC row: new RowID = max(dest RowIDs)+1; copy the "Copied?=Y" template columns
   from the message PC into a fresh matrix record (append at matrix end; allocate + extend hnidRows
   chain if the last block lacks room); TCROWID insert into dest BTH (shift-in within HN block;
   same no-split guard on LTP BTH leaves);
3. delete source row (Phase B unlink primitive);
4. RewriteNidParent(message NID -> destination folder NID) [existing primitive];
5. best-effort count updates both folders; 6. Commit = full VerifyIntegrity as today.
SMQ SUD append deliberately skipped (both reference writers ship without it; scanpst advisory only).

UI wiring after that: drag message rows onto folder tree items (same-store targets), all through the
same validate-then-write discipline.

## UI shipped on top of phases A/B (commits 23afa62, cd604ac)
- Delete for archives: ribbon Delete + shortcut -> confirmation dialog -> Phase B unlink per selected
  row -> list reloads from file. Read-only archives get an Editing Mode pointer instead of the old
  mailbox popup. Live-proven end to end incl. finish-editing verification and restart persistence
  (deleted message stays gone in a fresh profile). Quitting mid-edit auto-restored a deleted message
  byte-for-byte - the rollback design observed working for real.
- Empty Deleted Items (purge): context menu on writable archives' Deleted Items folder, dynamically
  enabled only while editing, gated by reader lock + confirmation; same validated delete per message.
- Smoke script gained key:<xdotool-key> steps.

### AMap empirical validation (2026-10-01, read-only probe of rmarrash_2.pst)
- fAMapValid == 0x02 on the real archive: Phase C precondition holds; no rebuild path needed for v1.
- AMap page = ONE 512-byte page per span: bitmap bytes 0..495 (3968 slots x 64 B), trailer with
  ptype byte at 496 == 0x84, CRC at 500, bid at 504 == ABSOLUTE ib of the page (confirms the
  counter-vs-absolute landmine empirically; BBT/NBT use the same footer positions with a counter bid).
- Span grid starts at 0x4400, NOT at 0: EOF lands on 0x4400 + n*253952 (779264 = 0x4400 + 3 spans);
  the "EOF on span boundary" rule is relative to that grid.
- Arithmetic closes exactly: allocated(9863) + free(2041) slots == pages x 3968, and free bytes match
  header cbAMapFree (130,624 B) to the byte -> AMaps truthful, model correct.
- Consequence: most edits allocate inside EXISTING spans using already-free slots - no file growth,
  no new AMap pages, in the common case.

Process note: an earlier copy of this spec was lost when tracked files were reset while it sat
uncommitted - research notes get committed the round they are written from now on.

## Phase B SHIPPED (delete via TCROWID unlink)
- TryUnlinkBthRecord: shift-down inside the leaf NOD's byte range + zeroed trailing slot, persisted
  through the same-size RewriteExternalBlock. This parser (and libpff) derive BT record counts from
  the allocated item length - there is no stored per-node count to decrement; the shift keeps
  length == count x recSize exactly. cbKey!=4, internal-block, and size-mismatch shapes refuse.
- GetMessages fidelity change (required): folders WITH a readable Contents table now show exactly
  their rows - Outlook's own rule. The old NBT-union fallback would have resurrected every deleted
  message (nodes stay parented until some future GC). NBT scan remains the fallback only for
  folders with no readable table.
- PstStore.DeleteMessage: dry-run validated before any write; orphans matrix slot + PC/subnodes by
  design; best-effort ContentCount/UnreadCount patch.
- Pending hardware check (documented, low risk): Outlook's tolerance of the inert key-0 trailing
  slot in a Row-ID BTH leaf. Our reader skips zero keys; worst known case is a scanpst advisory or
  phantom row on real Outlook - verify when the owner next opens an edited archive.

### BBT/NBT leaf free-room probe (all three real archives, read-only)
Free entry slots in BT leaves (capacity 488/cbEnt minus used; no-split design needs these):
- rmarrash_2.pst: BBT 78 free (16 leaves/242 entries), NBT 39 free (7 leaves/66 entries)
- rmarrash_1.pst: BBT 26,258 free (7,532 leaves/124,382), NBT 237 free (210 leaves/2,913)
- rmarrash_3.pst: BBT 191,994 free (33,181 leaves/471,626), NBT 1,522 free (1,012 leaves/13,658)
Conclusion: the Phase C no-split guard will not refuse ordinary move/delete batches on any of the
owner's archives - every tree has headroom for thousands of new entries. Page header layout used by
the probe matches our reader: cEnt byte @488, cbEnt byte @490, cLevel byte @491 (unicode).

## Phase C SHIPPED (move via re-link) + classic delete semantics
- MoveMessage (d0b39b7, reviewed line-by-line in the main thread): dest row from source cells,
  variable payloads re-appended as fresh heap items, Row-ID BTH grows by new leaf item + BTINFO
  repoint (no in-item growth), matrix extend-or-reuse, atomic NBT bidData flip to the rebuilt heap
  (template heaps shared cRef<=6 are never patched in place), source unlink, nidParent repoint.
  dwRowID = message Nid per reader convention.
- Allocator as specced: BBT-authoritative occupancy (real files have live blocks with unset AMap
  bits - bitmap best-effort), reserved-page skip, rightmost-leaf BBT append with hard no-split
  guard, fAMapValid INVALID->VALID bracketing, header CRC pair + dwUnique per write, file never
  grows in v1.
- UI (74d5182): Delete = move to Deleted Items (visible there immediately); Shift+Delete or deleting
  inside Deleted Items = permanent with confirmation. Live-proven full story headless incl. finish-
  editing CRC verification after move+purge sequences.
- History note: cd604ac accidentally included worker WIP via staged-index sweep; history rewritten
  honestly (d892b0d + 9b87538). Lesson: git checkout <tree> -- paths STAGES; verify staged set before
  every commit.
- 9e97c4e SHIPPED move UI: drag-and-drop onto tree folders (XDND cannot be exercised under Xvfb -
  no window manager - so the same engine is also exposed as the right-click "Move to Folder..."
  dialog, which IS headless-provable and was: select -> menu -> pick Deleted Items -> Move ->
  "Moved 1 message to Deleted Items" live. Dialog filters MAPI system folders using the folder
  pane's own VisibleRoots presentation rule. Smoke script gained drag:<x1,y1>x2,y2> for desktop use.
- f88827c docs + published binary refreshed (sha in BUILD_STATUS.md); goal closed with the full
  matrix above. Remaining follow-ups are listed once, at the top, under Outstanding follow-ups.

## Folder creation - implementation blueprint (grounded in reader code, 2026-10-03)

**STATUS: SHIPPED (this section is now the as-built record).** Right-click any archive folder ->
New Folder... -> name dialog -> spec-shaped descriptor + cloned empty contents table -> NBT
rightmost append -> parent Subfolders flag -> full verification; live-proven end-to-end (created
"ZZ Test Folder" under treasurydirect with zero editing-mode steps, status "created - verified",
persists across fresh-profile reopen with zero integrity problems).
Reader requirements (LoadFolders, PstStore.cs ~L940): a folder IS just an NBT entry with
Nid.Type==NormalFolder whose dataBid resolves to an LTP heap readable by PropertyContext.Read;
name=PR_DISPLAY_NAME(0x3001), counts from Pid.ContentCount/ContentUnread, HasSubfolders from
Pid.Subfolders. No hierarchy-table row is needed for OUR reader (Outlook tolerates absent rows
the same way it tolerates our other orphans; scanpst advisory).

PropertyContext.Read = WalkBth(heap.UserRoot) over 8-byte records {key u32=propId, type u16,
hnid u32}; fixed <=4-byte values live IN the hnid field (ResolveValue). Variable props are items.

CreateFolder(parentNid, name) plan:
1. newIdx = (max NID index across _ndb.Nodes)+1; folderNid=newIdx<<5|NormalFolder;
   contentsNid=newIdx<<5|ContentsTable(0x0E). Global-max keeps BTree key order rightward.
2. Folder LTP heap, built fresh via the same HNHDR/alloc-map layout as RebuildBlockWithAppends:
   items = [BTH header (shape per WalkBth parse: verify [1] type byte for property contexts!),
   leaf with records 0x3001->nameItem(PT_UNICODE), 0x3613->"IPF.Note", fixed props inline:
   ContentCount=0, ContentUnread=0, Subfolders=0 (+ Pid values the reader reads)], name item utf16.
   HNHDR.userRoot = BTH header hid. Write via AllocateAndWrite (encrypt:true like other heaps).
3. Contents table heap: CLONE an existing EMPTY leaf folder's contents heap block when available
   (byte-identical structure incl. rgtc columns, hnidRows=0; internal hids are heap-local so clones
   stay consistent); else build minimal TCINFO+BTINFO+empty BTH per TableContext.Load parse rules
   (MoveMessage already supports moving INTO hnidRows==0 tables - it mints the matrix subnode).
4. NBT inserts x2: rightmost-leaf append into NBT pages (entries {key8,nid,data,sub,parent};
   parent=folderNid for contents entry, =parentNid for folder entry). Leaf free slots exist (probe:
   r1 237, r2 39, r3 1522). Append = write entry at cEnt*stride in the 512B page, cEnt++ (byte@488),
   recompute page CRC, RewriteInternalBlockSameSize. If rightmost leaf full -> refuse v1 (no splits).
   VERIFY descent routing for max keys: Ndb.TryGetNode/Nodes walk behavior - test point-lookup of
   the new NIDs after insert; if internal btKey semantics need a touch-up on the path, mirror what
   real files show (folder NIDs increase monotonically in owner archives).
5. Parent's Subfolders prop: set via PropertyContext.TryPatchFixedUInt32 on parent heap (pattern
   already used for unread badges).
6. UI: folder-tree context menu "New Folder..." dialog -> store.CreateFolder -> refresh; always-
   editable session machinery unchanged (EnsureWritableStore + VerifyOperationAsync per op).
Tests: create -> reopen sees it; move message into new folder -> reopen keeps it (exercises the
hnidRows==0 mint path); create x N until leaf-full refusal; integrity 0 problems; all archives.
Open question to resolve first: exact WalkBth header byte expectations for property-context BTHs
(Read BTHHDR parse in HeapOnNode.WalkBth before building item [0]).

## Owner field report 2026-10-02 (v2 binary) - diagnosis + plans
1. MOVE fails on real archives: engine probe over ALL folder pairs of an rmarrash_1 copy shows moves
   succeed until a DESTINATION table's last heap block fills; RebuildBlockWithAppends then returns
   null -> "shape this editor will not guess" refusal (writes nothing; status shows the reason).
   FIX PLAN (next round): when tail rebuild fails on a multi-block (BREF) destination heap, build a
   continuation block (items + fresh allocation map, no HNHDR; hid BlockIndex = new ordinal - the
   reader already walks multi-block heaps this way), allocate it in the same batch, and grow the
   BREF payload by one 8-byte bid entry. BREF lives in an internal page whose item cannot grow
   in place -> rewrite that internal block via RebuildBlockWithAppends(hasHnhdr:false)-style item
   replacement (append enlarged BREF item, repoint node dataBid) BEFORE the atomic visibility flip.
2. "Item deleted from source but never appears" - NOT reproduced engine-side (refusals write
   nothing; every successful pair round-tripped with integrity 0). If it recurs after fix 1 lands,
   capture ~/.local/share/OpenOutlook/logs/openoutlook.log immediately after the failed move.
3. COPY to folder (owner-requested safety path): CopyMessage = MoveOrCopy(deleteSource:false) -
   allocate a fresh NID handle for the copy {same bidData/bidSub as source, parent=dest} via
   AppendNbtEntry (sharing data bids is native PST semantics, cRef>1); write the destination row
   with the COPY's nid at TCROWID.dwId; skip src unlink, skip RewriteNidParent on the original,
   adjust only destination counts. UI: "Copy to Folder..." beside Move. Then move = copy + verify
   copy visible + unlink source (make MoveMessage itself follow this order so a failed move can
   never lose the original).
4. SHIPPED: New Folder... now also on the archive ROOT node (NewFolderAtRootAsync, parent=store.Root;
   live-proven headless "created - verified"). Create-failure visibility: refusals land in the status
   bar verbatim ("no empty message table to model a new folder on", duplicate names, etc.).
