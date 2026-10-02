# PST Editing — design & phased plan (started 2026-10-01)

Goal: move messages between folders, delete (Deleted Items + purge), persistent flags — on real
archives, without ever risking data loss. Owner archives: rmarrash_1.pst (909MB), _2 (779KB, the
disposable test fixture — never write to 1/3 in tests), _3 (2.6GB).

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

### C — row INSERT (enables real move)
Insert = new RID in destination Contents BTH (RIDLAST++ on the BTree? research: per-BTree next-RID
from max RID + 1 with collision care), record {RID, itemID NID} + copy/clone of source row LTP heap.
Space problem: leaf page must have room → reuse page free space (Outlook's ibFreelistStart) else split
(new BT page = file grow + BBAT append + BRef map growth — the heavy NDB work). v1 strategy per
research brief; fallback "destination folder page full, cannot move" honest error.
Also on move: rewrite PR_FOLDERS_PATH/PR_PARENT_DISPLAY(_W) in the item's props (variable-size — may
need local-props stream append or accept stale value? research), keep ConversationIndex unchanged.

### D — delete UX + purge
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
- There is NO persisted RID counter (TCINFO has none) -> destination RowID = max(existing)+1.
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
