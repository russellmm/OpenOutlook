# OpenOutlook / OpenPST - Design Document

Status: 4 October 2026. Covers the Python implementation (`F:\Claude\*.py`), the C library OpenPST v0.3.0 (`F:\Claude\openpst`), the
packaging, the verification method and everything learned about the Outlook PST format on the way.
Companion documents: `OpenOutlook_Session_Handoff_rev4.md` (rules 1-11, stages D-F, GUI), `OpenOutlook_Session_Handoff_rev5.md` (rules 12-16, the copy investigation, `pstfix`, big-file tests),
`OpenOutlook_Session_Handoff_rev6.md` (rules 17-18, stage G), `OpenOutlook_Local_Session_Handoff.md` (rev7) and `OpenOutlook_Local_Session_Handoff_rev8.md` (C port, what happened when), `openpst\README.md` (API reference), `ms-pst.pdf` (the specification, MS-PST v20250218).

---------------------------------------------------------------------------------------------------------------------------------------

## 1. Purpose and scope

**OpenOutlook** is a from-scratch email client for Ubuntu whose core is a utility that reads **and writes** Outlook 2024 *classic* Unicode
PST files (wVer 23, 512-byte pages) **in place** - never rewriting the whole file. Everything is written from the specification; no third-party PST code.

Goals
* Read any Unicode PST: folders, messages, bodies (text / HTML / compressed RTF), recipients, attachments, search.
* Edit a PST in place so that Outlook and Microsoft's own repair tool (SCANPST) accept the result: create / rename / move / delete folders,
  copy / move / delete messages, copy messages between files, repair the small inconsistencies other tools leave behind.
* Be crash-safe: an interrupted write must never leave a damaged file.
* Offer the engine to other programs: a C library with a P/Invoke-friendly API (used by the user's C# / .NET 8 / Avalonia app
  `github.com/russellmm/OpenOutlook`) and a GTK4 desktop app for Linux (Python).

Non-goals / limits
* ANSI PSTs (wVer < 23) and 4K-page PSTs (wVer >= 36); encryption other than "none" and "permute" (method 1); files above ~16 GB.
* Editing message *contents* (flags, bodies); sending/receiving mail; search folders and Outlook's search-update queues (Outlook rebuilds them).
* Subnode trees deeper than one SLBLOCK, and property contexts with more than ~400 properties, are refused with a clear error.

Standing safety rules (kept throughout)
* Never touch a PST the user relies on: only copies (`rmarrash_1_clean.pst` copies, the small `rmarrash_2.pst`, copies of `rmarrash_3.pst`).
* SCANPST: Analyze only on anything valuable; Repair only on throwaway copies (done to study what it changes). Outlook must be closed while writing.

---------------------------------------------------------------------------------------------------------------------------------------

## 2. System overview

```
                 +-------------------------------+        +-------------------------------+
                 |  pstgui.py  (GTK4 desktop app)|        |  Avalonia app (C#, .NET 8)    |
                 +---------------+---------------+        +---------------+---------------+
                                 | Python modules                         | P/Invoke (OpenPst.cs)
   +-----------------------------v------------------+     +---------------v---------------+
   | pstactions / pstops / pstfolders / pstxcopy /  |     |  OpenPST C library (openpst.h)|
   | pstfix / pstcheck / pstsearch / pstrtf         |     |  reader, search, RTF, writer  |
   | pstedit / pstwrite / pstidmap / pstnpm / pstcore/pio|  | (same on-disk behaviour)      |
   +------------------------+-----------------------+     +---------------+---------------+
                            |                                             |
                            +---------------------+-----------------------+
                                                  v
                                         Unicode PST file (+ <file>.journal while writing)
```

Two implementations of the same engine exist on purpose:
* **Python** (complete, battle-tested with SCANPST on real mailboxes) is the reference and powers the GTK app and the `.deb`.
* **C** (OpenPST) is the portable engine: Linux and Windows, no dependencies, P/Invoke-friendly. Its writer is a faithful port of the Python writer and is
  verified to produce **byte-identical files** (section 10).

Test data (all in `F:\Claude`): `rmarrash_1_clean.pst` (868 MB, 2,793 messages, SCANPST clean), `rmarrash_2.pst` (1.2 MB, 10 messages), `rmarrash_3.pst`
(2.6 GB, 13,410 messages). Store name of the user's mailbox: "rmarrash_hotmail2".

---------------------------------------------------------------------------------------------------------------------------------------

## 3. The PST format as used here (MS-PST layers)

The file is modelled in three layers, and the code mirrors them.

### 3.1 NDB - Node Database layer
* **Header** (564 bytes): `dwMagic "!BDN"`, wVer (23 = Unicode), `bidNextP` (offset 32), `dwUnique` (40), `rgnid[32]` NID counters (offset 44 + 4 x type), `ibFileEof`
  (184), `ibAMapLast` (192), `cbAMapFree` (200), `cbPMapFree` (208, deprecated, ignored), root BREFs of the node B-tree (216) and block B-tree (232),
  `fAMapValid` (248; 2 = valid), the FMap bytes (256..383, one per AMap section for the first 128 sections), `bCryptMethod` (0x201), `bidNextB` (516),
  and two CRCs (offsets 4 and 524).
* **Pages** are 512 bytes: 496 bytes body + 16-byte trailer (ptype, ptype again, wSig, dwCRC, bid). Types: NBT 0x81, BBT 0x80, AMap 0x84, PMap 0x83,
  FMap 0x82, FPMap 0x85, DList 0x86. `wSig = ((ib ^ bid) >> 16 ^ (ib ^ bid)) & 0xFFFF` (zero for map pages). The **PST CRC** is CRC-32 (poly 0xEDB88320)
  **without** initial/final inversion - *not* zlib.crc32 (a bug found in `pstnpm.py`, section 9).
* **B-trees**: leaf entries of 32 bytes (NBT: nid, bidData, bidSub, nidParent) or 24 bytes (BBT: bid, ib, cb, cRef); index entries of 24 bytes (key, bid, ib);
  at most 15 / 20 entries per page. Pages are updated in place and split when full.
* **Blocks**: data + padding + 16-byte trailer (cb, wSig, dwCRC, bid), 64-byte aligned, max 8176 data bytes. Internal blocks (bid bit 1): XBLOCK /
  XXBLOCK (data trees for data > 8176 bytes; every non-final child must be exactly 8176 bytes and cbTotal must equal the sum), SLBLOCK (sub-node tree).
  Encryption method 1 permutes the bytes of external data blocks (`mpbb` tables).
* **Allocation**: the file is a grid of **sections** of 253,952 bytes starting at 0x4400; each begins with an AMap page (496 bytes of bitmap = 3,968 slots of 64 bytes,
  1 = allocated). Every 8th section also holds a PMap page (kept all-ones; deprecated), sections >= 128 hold FMap pages every 496 sections, and FPMap pages appear
  from section 8192. The header FMap bytes hold the longest free run of each section (capped at 255). `cbAMapFree` = total free bytes. The DList page at 0x4200 is an
  optional density list (we keep it valid but empty).
* **Reference counts**: each BBT entry's cRef counts the node/parent references plus one; shared blocks are ref-counted, orphans and mismatches are SCANPST errors.

### 3.2 LTP - Lists, Tables, Properties
* **Heap-on-node (HN)**: a node's data blocks hold a heap: page-map at the end of each block (`cAlloc`, `cFree`, offsets), HID = (block << 16) | (item << 5), block 0 header
  (client signature, user root, 8 fill-level nibbles), fill-level pages every 128 blocks (bitmap block header of 66 bytes at block 8, 136, ...). Max allocation 3,580 bytes.
* **BTH** (B-tree-on-heap): header item + leaf/index nodes inside the heap. Property contexts use key 2 / value 6, table row index key 4 / value 4, the ID map key 16 / value 4,
  the message index key 4 / value 4.
* **Property context (PC, client 0xBC)**: BTH pid -> (type, value); fixed values <= 4 bytes inline, the rest as HID (or sub-node NID).
* **Table context (TC, client 0x7C)**: column descriptors, row-index BTH, row matrix either in the heap (<= 3,580 bytes) or in a sub-node (rows packed 8176 / rowsize per block,
  every non-final block padded to a full 8176 bytes).

### 3.3 Messaging layer
* NIDs: type in the low 5 bits (2 normal folder, 4 normal message, 8 associated message, 0xD/0xE/0xF hierarchy / contents / FAI tables of folder index,
  5 attachment, 0x1F stream); folder tables share the folder's index. Special nodes: 0x21 message store (PC), 0x61 name-to-ID map, 0x122 root folder,
  0x692 recipient table, 0x671 attachment table, 0xC01 the **ID map**, 0xE01 the **message index**.
* A **message** is a PC node with sub-nodes (recipient table, attachment table, attachment PCs, large streams). A **folder** is a PC (properties 0x3001 name, 0x3602 / 0x3603
  counts, 0x360A has-subfolders, ...) plus three TC nodes. Counts live in three places that must agree: the folder PC, the parent's hierarchy-table row, the contents table.
* Special folders are recorded in the store PC (0x35E0-0x35E7 entry IDs) and in the root / IPM-root PCs (0x36D0-0x36D7); the wastebasket is 0x35E3.
* Compressed RTF (LZFu, MS-OXRTFCP): initial dictionary is **207** bytes (an off-by-three here silently garbled every RTF body once).

### 3.4 Format rules learned from SCANPST (numbered as in the handoffs: rules 1-11 rev4, 12-16 rev5, 17-18 rev6; 19-20 added with the C port; 21-26 found while building the message importer, 2026-10)
Every rule was found by giving SCANPST a file, reading its log, and - where the log was silent - **repairing a throwaway copy and diffing** (section 11).

| # | Rule |
|---|---|
| 1 | **Parent B-tree key = the child page's first key (`btkeyMin`)** at all times; the writer updates parent keys whenever they differ, not only when smaller. Violations make SCANPST rebuild the tree and report orphan / unallocated blocks. |
| 2 | **Header NID counters** `rgnid[type]` for types 0x0D, 0x0E, 0x0F equal the folder index (`create` raises them). The checker no longer exempts them (types 6, 7, 0x10 hold NID-shaped values and are skipped). |
| 3 | **ID map, node 0xC01** (heap, client 0x9C): item 0x20 holds the HID of a BTH (key = 16-byte ID, value = 4-byte NID, keys sorted bytewise). Every folder and message row carries 0x0E30 (16-byte ID), 0x0E33 (change number, int64) and 0x0E34 (replica blob `01000000 + 16-byte GUID + 01000000`). New objects get a record; a purged object **keeps its record with NID 0** (SCANPST's own convention). Message PCs do not carry these properties. |
| 4 | **Folder create**: index = rgnid[2]+1; folder PC (0x3001, 0x3602, 0x3603, 0x360A, 0x3613 class, 0x6635, 0x6636); empty 0x0D/0x0E/0x0F tables share the folder's index (an empty template table block is shared by `add_ref`); hierarchy row in the parent with 0x0E30/0E33/0E34; parent's has-subfolders set. |
| 5 | **Folder purge** removes the node, its three tables, all messages and subfolders, and also hidden stale children found through an NBT parent map; otherwise orphan nodes remain. |
| 6 | **Delete semantics** (messages and folders): first delete moves to Deleted Items (Outlook-style "Name (2)" on a name clash); deleting inside Deleted Items is permanent. |
| 7 | **Protected folders**: root, the store's 0x35E0-0x35E7 entry IDs, default-folder IDs 0x36D0-0x36D7, direct children of the root, plus (GUI layer) well-known English names directly under the IPM top folder. |
| 8 | **File growth layout**: file size is exactly `0x4400 + n * 253,952`. New section: AMap; PMap (ptype 0x83, all 0xFF) when `s % 8 == 0`; FMap (0x82, at +1024) when `s >= 128 and (s-128) % 496 == 0`; FPMap (0x85, at +1024) when `s >= 8192 and (s-8192) % 31744 == 0`. FMap byte = min(255, longest free run). 16 sections per growth, 65,536 maximum (16 GB). Growth is journal-safe (truncate to the exact grid before data writes; a grow-only transaction still commits). Tested to 4.24 GB (synthetic) and on the real 2.6 GB file. |
| 9 | **Outlook-origin errors**: after Outlook edits a file SCANPST can show minor errors (stale FMap bytes, orphan blocks); pre-Outlook copies scan clean, so those are Outlook's, not the writer's. |
| 10 | Message copy: independent cloned blocks (shared, ref-counted blocks passed SCANPST in small tests but Outlook never shares blocks). |
| 11 | **Data-tree blocks must be full**: in an XBLOCK every non-final child is exactly 8,176 bytes and cbTotal equals the sum of the children (SCANPST: "Middle page not full" / "XBLOCK has invalid cbTotal"). A table whose row matrix lives in a sub-node packs `8176 // rowsize` rows per block and pads every non-final block with zeros (rowsize 126: 64 rows = 8,064 B, padded). Found by moving messages out of a 1,296-row folder. |
| 12 | **Contents-table rows need ID cells** 0x0E30 / 0E33 / 0E34 and an ID-map record. Outlook itself leaves them off for a copy and for a folder it creates, SCANPST then says "minor" and its repair adds them; **we write them** so copies scan clean. |
| 13 | **PidTagLtpRowVer (0x67F3) is unique across the whole store** (all hierarchy and contents tables) and below the header's `dwUnique` (offset 40). Outlook draws values from a store-wide counter; we bump `w.unique` and use it (the header stores `unique + 1` on commit); the destination folder's hierarchy row version is bumped whenever its counts change. Every clean file had zero duplicates, every "minor" file had some - but the rule alone was not sufficient (rule 12 was also needed). |
| 14 | **Node 0xE01 is Outlook's message index** (heap, client 0xCC): root item offset 8 = highest message NID; item 0x40 = BTH (key 4, value 4): key = a 32-bit value >= 0xFF800000, value = HID of a list of message NIDs. Related messages share a bucket (1,928 buckets for 2,793 messages). A copy was originally appended to its source's bucket; the real rule is the conversation key of 3.5 (which gives the same result for copies of indexed messages). |
| 15 | **Growing a heap list**: a heap block is padded to 8176 and often has only a few spare bytes. Growth first tries in place (shift later items, rewrite the page map, recompute the fill-level nibble: block 0 header offset 8 for blocks < 8, else the page header of the 128-block group); if the block is full the list is **moved to a new item at the end of the heap** (existing HIDs never change), the BTH leaf value is repointed, the old item stays unreferenced. A non-final heap block is padded to 8176; a new block at index 8 + 128k would need a 66-byte header (not supported, error). Invisible on a small file, showed up with 5 copies on the big one. |
| 16 | **Other nodes Outlook touches (observed, not replicated, not needed by SCANPST)**: 0xEE1 + 0xF01 (28-byte per-object records and a counter), 0x61 (Outlook added 43 name entries when it copied), 0x261 and 0x80027 (search update queue); Outlook's copy gets property 0x8078 (message size +16) and keeps the original's search key and change key. Opening a file in Outlook rewrites contents-table columns, so Outlook-vs-baseline diffs are noisy: use an open-only control. |
| 17 | **Header NID high-water marks**: offset 44 + 4 x type holds the highest index in use for that type; it must cover every node **and every sub-node tree** (types 5 and 31 included). SCANPST: "Header NID high-water mark has incorrect value (type=5, read=400, calculated=628)". Fixed by raising the counters for every cloned sub-node (`bump_hwm`) and fixer rule R5. |
| 18 | **Message-index buckets for messages without a relative**: SCANPST creates a new bucket (key >= 0xFF800000) for each message that is not indexed; the key is per conversation (3.5). Our earlier CRC-of-topic keys were accepted but not recognised: SCANPST kept them and added its own bucket per message (every copied message in two buckets) = "minor". |
| 19 | **Contents rows must equal their message**, including cells of **named properties**, which carry the *destination's* property ids after a cross-file copy: dropping them gives "Contents Table ..., row doesn't match sub-object" (found on a 1.1 GB copy by repair-and-diff: SCANPST adds the missing named cell). |
| 20 | **SCANPST's own repair** appends a section per pass and writes the FMap byte of the new section wrong ("AMap page <@old EOF> has csFree of 128, but should have 255"), fixing it on the next pass - hence "3-4 attempts". Not a defect of the written file; it only occurs when SCANPST has something to repair. |
| 21 | **PidTagAttachSize (0x0E20) of a by-value attachment equals the sum of the sizes of all its property values exactly** (strings/binaries by their byte length, inline fixed values by their own size: bool 1, int32 4, ...; 0x0E20 itself counts 4; the data counts once wherever it is stored). Verified on 4,053 Outlook-written attachments (0 exceptions). A padded size gives "Attachment (nid=...) missing or invalid PR_ATTACH_SIZE" and, as a consequence, "Attachment table / Contents Table ... row doesn't match sub-object". Embedded messages and OLE objects (method 5/6) add storage that is not a property value; the rule does not apply to them. Checks: `subnodes`. |
| 22 | **Local NIDs inside a message come from the header counters.** Attachment nodes (type 5) and large-value subnodes (type 0x1F) must be allocated from `rgnid[5]` / `rgnid[0x1F]` (and the counter advanced), or SCANPST reports "Header NID high-water mark has incorrect value (type=5, read=400, calculated=800)". This is rule 17 applied to *new* sub-nodes. The fixed table nids 0x671 / 0x692 are not counted. Checks: `subnodes`; fixer R5. |
| 23 | **PidTagMessageSize (0xE08) is compared with the real size**: it must include the property values, the attachments' sizes **and the recipient and attachment tables** (Outlook's own value for a real message = property sum + attachments + table data within ~2%). Small messages whose size omitted the tables (~30% low) were rejected as "Contents Table ... row doesn't match sub-object" while large ones (~5% low) passed - the tolerance is a ratio, exact value unknown. Not machine-checked (heuristic); the importer computes it as above. |
| 24 | **Contents rows carry row-only cells Outlook writes for every message**: 0x0E17 (message status, int32 0) and 0x3013 (a 16-byte per-row GUID, version 4). When missing, SCANPST's repair adds them. SCANPST regenerates the 0x3013 *value* on every repair (deterministic per NID, not derivable) - a benign difference. Checks: `rowcells`; fixer R6. |
| 25 | **Files that keep an ID map (node 0xC01)** need, for every new message, an ID-map record plus the row cells 0x0E30 (16-byte id), 0x0E33 (int64 change number) and 0x0E34 (24 bytes: 01 00 00 00, GUID, 01 00 00 00); files without an ID map do not carry them. (Same as rule 3, applied to imported messages.) |
| 26 | **A file can need more than 3 SCANPST repair passes.** Imported-file test: pass 1-3 still listed items (and the old damage of the base file took until pass 4 to reach NO_ERRORS); pass 5 was identical to pass 4. Always compare consecutive passes (repair-and-diff) until two passes produce no difference. |

Silent verdicts: SCANPST's "Only minor inconsistencies" has no log line and a greyed-out Details button - the only way to learn what it dislikes is repair a copy and diff.
A clean verdict on moves and folder operations does not prove the copy / create paths: every new message NID touches the message index (0xE01), the ID map (0xC01), a row version and the ID cells.

### 3.5 The message index (node 0xE01) and its bucket key - solved
Node 0xE01 is a heap (client 0xCC): root item holds the highest message NID (offset 8); item 0x40 is a BTH key(4) -> HID of a list of message NIDs ("bucket"). Every
message must be in exactly one bucket. The key was unknown for the first half of the project (a CRC-based guess left SCANPST reporting "minor inconsistencies"). Found with
SCANPST as an *oracle*: copies carry chosen conversation GUIDs, one repair pass assigns each its real key.

    key = 0xFFFF0000  xor  ( xor over j = 0..15 of ( GUID[j] << (15 - j) ) )
    GUID = bytes 6..22 of PidTagConversationIndex (0x71); the 6-byte header is ignored

It reproduces 14,578 of 14,582 keys of two real mailboxes (the 4 others are probably threads whose index changed after indexing) and 167 of 170 probes. Different threads may share a key and
then share a bucket (no probing). A new message joins the bucket with its key or a new bucket is created (sorted BTH insert with node split, index keys, root growth).
Messages without a conversation index fall back to "next to the best-matching message of the same topic" (rare).

---------------------------------------------------------------------------------------------------------------------------------------

## 4. Python implementation (`F:\Claude`)

| Module | Role |
|---|---|
| `pio.py` | portable `pread` / `pwrite` / `O_BINARY` for Windows |
| `pstcore.py` | **read-only engine**: PST, NBT/BBT, blocks, heap/BTH/PC/TC, Store/Folder/Message, LZFu, CRC, mpbb tables |
| `pstrtf.py` | RTF -> HTML (fonts, colours, tables, links, pictures, `\fromhtml1`); `pstcore.rtf_to_html_or_text` for plain text |
| `pstsearch.py` | query language, folder walk, row-level and body search |
| `pstwrite.py` | **writer core**: transactions, journal, AMap/PMap/FMap/DList, allocation, block add/release, B-tree put/delete/split, validate |
| `pstedit.py` | heap / BTH / table-context builders, `Editor` (clone nodes, store tables, ref counting) |
| `pstidmap.py` | ID map (0xC01) |
| `pstnpm.py` | name-to-ID map (0x61): read, lookup, add, save |
| `pstops.py` | message ops (move / copy / delete / purge), counters, message index maintenance, NID high-water marks |
| `pstfolders.py` | folder ops, protection of special folders, property-context rebuild |
| `pstxcopy.py` | copy between PST files (named-property translation) |
| `pstfix.py` | the repair rules R1-R5 |
| `pstcheck.py` | scan-style checks (refs, nids, tables, idmap, xblocks, rowvers) |
| `pstltp.py`, `pstdebug*.py`, `probe_maps.py` | early LTP reader / debugging helpers for the allocation maps and heaps |
| `pstcli.py` | command-line reader (`list`, `verify`, ...) |
| `pstgrow.py` | `pstgrow.py FILE N` appends N empty sections (test tool for growth) |
| `pstdiff.py` | node-level diff of two PST files (added / removed / changed data length, sub-node set, parent): the main tool of the repair-and-diff method |
| `mkvariants.py`, `patch*.py`, `cmp_nodes.py`, `cmp_bak.py` | bisect helper and applied (anchored, idempotent) patch scripts, kept as method references |
| `test_edge.py` | big-file stress: copy N, move N, move a folder of N, copy GROW more (file growth), delete tree twice, then checks |
| `pstactions.py` | GUI-free session layer: every public method is one atomic journalled write |
| `pstgui.py` | GTK4 application (`.deb` v0.8.5): tree, message list, search, drag/drop, copy to another PST, worker-thread writes |
| `test_gui.py`, `test_edge.py`, `pstselftest.py` | headless GUI test (43 checks), edge cases, writer self test |
| `build_deb.sh` | builds `openoutlook_VERSION_all.deb` (modules in `/usr/lib/openoutlook`, launchers `openoutlook`, `-fix`, `-search`, `-check`, `-xcopy`) |
| `run_scanpst.ps1`, `run_scanpst2.ps1` | scripted SCANPST (section 11) |

---------------------------------------------------------------------------------------------------------------------------------------

## 5. C library OpenPST v0.3.0 (`F:\Claude\openpst`)

C11, no third-party dependencies, no VLAs or GNU extensions (MSVC-compatible), UTF-8 strings, FILETIME times, explicit little-endian readers, `pread` / `ReadFile+OVERLAPPED`
file access. Opaque handles, plain structs, negative `OPST_E_*` codes plus thread-local `opst_last_error()`, arrays released with `opst_free_*`, no required callbacks.
Error codes: IO -1, FORMAT -2, UNSUPPORTED -3, NOTFOUND -4, NOMEM -5, ARG -6, STATE -7 (e.g. read-only handle), REFUSED -8 (safety rule).

### 5.1 Source map (`src\`)
| File | Contents |
|---|---|
| `op_platform.c` | errors, file IO incl. write / flush / truncate / journal helpers, OS code pages, random (test mode `OPST_TEST_RANDOM=1`), path identity |
| `op_util.c` | CRC, UTF-8/16 conversion, case folding, LZFu, result lists, time |
| `op_ndb.c`, `op_ltp.c`, `op_msg.c` | reader: NDB, heap/BTH/PC/TC, folders/messages/recipients/attachments, public read API, `opst_verify` |
| `op_rtf.c` | RTF -> HTML / plain text, HTML -> text (generated entity table `op_htmlent.inc`) |
| `op_search.c` | query parser, Unicode folding (generated `op_foldtab.inc`), search |
| `op_wr.c` (+ `op_wr.h`) | writer core: overlay, journal, allocation maps, grow, B-trees, blocks, commit, validate |
| `op_edit.c`, `op_edit2.c` | heap editing / building, BTH builder, table contexts, property contexts, node cloning, load/store of tables |
| `op_ops.c` | ID map, message ops, counters, message index, NID marks |
| `op_folders.c` | folder ops and protection |
| `op_npm.c`, `op_xcopy.c`, `op_fix.c`, `op_check.c` | name map, cross-file copy, repair rules, checks |
| `op_api_w.c` | public write API wrappers (one transaction per call) |
| `op_test.c` | internal test hooks used by the CLI |
Tools: `tools\openpst_cli.c` (CLI), `amalgamate.py` (single-file build `dist\openpst.c`), `gen_foldtab.py`, `gen_htmlent.py`. Bindings: `bindings\csharp\OpenPst.cs`.

### 5.2 Public API (`include\openpst.h`)
* **Open / info**: `opst_open(path, OPST_OPEN_READONLY | OPST_OPEN_WRITE)`, `opst_close`, `opst_display_name`, `opst_root_folder`, `opst_ipm_root`, `opst_deleted_items`,
  `opst_recovered`, `opst_journal_pending`.
* **Read**: `opst_folder_children`, `opst_folder_info_get`, `opst_folder_find`, `opst_messages` (contents-table rows), `opst_msg_open/close/str/prop/i64/body/bodies/recipients/attachments`, `opst_attachment_data`,
  `opst_msg_text`, `opst_msg_html`.
* **Conversion**: `opst_rtf_to_html`, `opst_rtf_to_text`, `opst_html_to_text`.
* **Search**: `opst_search(query, opts{flags, limit, folders, cancel flag, progress}, hits)`, `opst_free_hits`.
* **Write** (each call = one atomic transaction): `opst_folder_create/rename/move/delete/purge`, `opst_folder_is_protected`, `opst_msgs_move/copy/delete/purge`, `opst_msgs_copy_to` (other file).
* **Maintenance**: `opst_fix(apply, report)` (rules R1-R5), `opst_check(report, text)`, `opst_verify` (CRC / trailer scan).
* **C#**: `PstFile(path, write)`, `Children`, `Messages`, `OpenMessage`, `Search`, `CreateFolder`, ... `CopyMessagesTo`, `Fix`, `Check`, `PstMessage.Text()/Html()/Body()`, `PstText.*`.

---------------------------------------------------------------------------------------------------------------------------------------

## 6. Writer design (identical in Python and C)

### 6.1 Transaction model
All changes of one public call are collected in an in-memory **overlay** (C: 64-byte slots in a hash table; Python: pending list + page index). Reads see the overlay,
so every operation can read what it just wrote. Nothing reaches the file until commit.

Commit order (each step flushed):
1. reconcile (mark every block / page referenced by the trees as allocated), write dirty AMap pages, FMap bytes and pages, DList;
2. `cbAMapFree` update; **journal** `<file>.journal` written: magic `PSTJRNL1`, original EOF, then (offset, length, original bytes) for the header and for every region about to be overwritten;
3. header written with `fAMapValid = 0`;
4. file extended to the exact section grid if it grew; all pending slots written;
5. final header (valid = 2, `dwUnique + 1`, new EOF / roots / counters, both CRCs); 6. journal deleted.

Recovery (on `OPST_OPEN_WRITE`, or Python `PSTWriter`): a leftover journal is applied in reverse, the file truncated to its original size, flushed, the journal removed. Verified by simulated crashes at four
points of the commit, including after a 176 MB growth (size and bytes restored exactly), and across implementations (a C journal is recovered by Python). A read-only open of a file with a journal reports
`opst_journal_pending`. A real failure during commit triggers the same rollback immediately.

### 6.2 Allocation
`alloc(size, align)`: sections are searched **from the last to the first**, first fit with alignment (64 bytes for blocks, 512 for pages); a per-section upper bound of the longest free run avoids rescans.
When nothing fits the file grows by 16 sections (about 4 MB) - AMap, PMap (every 8th), FMap (>= section 128 every 496), FPMap (>= 8192 every 31,744) pages are created. Limit 65,536 sections (16 GB).
Free updates the AMap and `cbAMapFree`. B-tree pages split (new page bid from `bidNextP`) and merge/remove when empty exactly as the Python code does - the allocation order matters for byte identity.

### 6.3 Blocks and ref counts
`add_block` writes data + trailer (encrypting for method 1), enters the BBT with cRef 2. Data > 8176 bytes become XBLOCK (level 1, <= 1021 children) / XXBLOCK (level 2) trees with padded non-final blocks.
`release` decrements; at cRef 2 the block is freed with its children (data-tree children, sub-node tree entries). Unchanged sub-nodes of a rebuilt node are kept by `add_ref`.

### 6.4 Node-level editing
Tables are loaded into an in-memory model (columns, rows with per-column presence and bytes), edited, and **rebuilt** (new heap, row index BTH, row matrix) and stored; the old blocks are released.
Single properties of heap-stored PCs (folder counters) are patched in place; grown heap items shift later items inside their block and update page map and fill levels without changing any HID;
new items are appended without moving existing ones. Property contexts of folders are rebuilt from a property map.

### 6.5 Operations
* **Message move**: remove the row from the source contents table, add it to the destination (same cell values), NBT parent update, counters in three places, row version renewed.
* **Message copy**: new NID from the header counter, **cloned** blocks (optionally fresh search/change keys), new row with ID cells (0x0E30 random 16 bytes, 0x0E33, replica blob 0x0E34) and an ID-map
  record, unique row version, message index attached by the conversation key (3.5).
* **Delete**: first delete moves to Deleted Items; deleting inside Deleted Items (or `purge`) removes the node, releases blocks, zeroes the ID-map record, updates counters.
* **Folders**: create (new folder PC, three tables copied empty from a sibling/parent, hierarchy row with ID cells, has-subfolders update, header counters), rename (PC + parent row), move (parent rows, NBT parent,
  has-subfolders of old and new parent), delete (Deleted Items, name disambiguation "Name (2)"), purge (recursive, including hidden children listed only in the NBT).
  **Protected**: root, the store's special-folder entry IDs, everything directly below the root, and (GUI rule) the standard English folder names directly below Top of data file.
* **Cross-file copy** (`pstxcopy` / `xc_copy`): for each message clone the node and sub-nodes into the destination; translate named property ids (0x8000+) through both name-to-ID maps in ascending order, adding missing names;
  properties whose name is unknown are dropped; the property contexts of the message **and of every attachment** are re-keyed; the contents row keeps its named cells under the destination's ids; the name map is rebuilt;
  header marks for cloned sub-nodes are raised.
* **Name-to-ID map**: bucket count 251, GUID / entry / string streams, bucket records keyed by **PST CRC of the UTF-16 name** (numeric names by id), bucket = (key ^ wGuid) % buckets; streams > 3,580 bytes go to sub-nodes;
  values may spill into further heap blocks.

### 6.6 Fixer rules (`pstfix` / `opst_fix`)
R1 rows without ID cells -> add IDs + ID-map records;  R2 ID-map records of deleted nodes -> NID 0;  R3 messages missing from the message index -> attach by conversation key, raise the highest-NID field;
R4 duplicate / too-high row versions -> new store-wide unique values, `dwUnique` raised;  R5 header NID high-water marks of types 5 / 31 raised to cover every node and sub-node;
R6 contents rows without the row-only cells 0x0E17 / 0x3013 -> cells added (rules 24; C `opst_fix` and Python `pstfix.py` both).
Report-only mode works on a read-only handle; "no known issues" is not "SCANPST finds nothing".

### 6.7 Checks (`pstcheck` / `opst_check`)
refs (BBT ref counts, orphans, missing blocks), nids (header counters; read as raw indexes), tables (folder counts, unread, has-subfolders, parent links, contents rows vs messages, unlisted messages), idmap,
xblocks, rowvers, **subnodes** (attachment sizes, local nid counters; rules 21-22) and **rowcells** (rule 24). The C check reports the same findings as the Python one on all test files.
The two new checks were validated on Outlook-written archives (rmarrash_1: 1,415 by-value attachments, rmarrash_3: 2,638 - zero findings) and have negative tests: the importer carries test hooks (`OPST_TEST_BAD_ATTSIZE`, `OPST_TEST_BAD_NIDCOUNTER`, `OPST_TEST_NO_ROWCELLS`) that reproduce the three original defects, and `test_write` asserts the checker flags each (and that the fixer repairs the row cells).

---------------------------------------------------------------------------------------------------------------------------------------

## 7. Reader, search and RTF details
* **Reader**: whole NBT / BBT loaded into sorted arrays at open (full dump of the 2.6 GB file about 18 s, of the 910 MB file about 2 s); lazy value resolution; contents tables give message lists without opening messages.
  Subject prefix control characters (`\x01` + one character) are stripped; sender in lists falls back 0x0C1A -> 0x0042 -> display-to.
* **Search**: case- and accent-insensitive via a generated table equal to `casefold(strip_combining(NFKD(c)))` (7,828 entries, Unicode 16). Terms: words, `"phrases"`, `-word`, `from: to: cc: subject: body:`,
  `has:attachment`, `is:unread|read`, `after:`/`before:` (sent date, else received), `folder:`. Row-level fields from contents tables (13,000 messages: 0.2 s; with bodies about 1 s); cancel flag and progress callback.
* **RTF**: tokenizer -> group tree; styles (bold, italic, underline, strike, super/sub, size, colour, highlight, font), alignment, paragraphs, tables, hyperlinks (`HYPERLINK` fields), PNG/JPEG pictures as data URIs
  (including `\*\shppict`), code pages for `\'hh`, `\uN` with surrogate pairs and fallback skipping, symbol charset read as 1252; `\fromhtml1` is de-encapsulated instead.
  Plain text of RTF keeps `<address>` text (the old Python search stripped it as if it were HTML).

---------------------------------------------------------------------------------------------------------------------------------------

## 8. Environment and tooling

| Item | How |
|---|---|
| MSVC 2022 x64 | `vcvars64.bat`, then VS's bundled cmake + ninja; build dir `openpst\build-msvc`; `/W4` clean |
| WSL Ubuntu | gcc 15, cmake; `-DOPST_SANITIZE=ON` for ASAN + UBSAN; Python GUI test under `xvfb-run` |
| .NET SDK 8/9/10 | scratch project compiles `OpenPst.cs` |
| Tests | `OPST_TEST_PST=small.pst ctest`: `test_basic` (161 checks on a real file), `test_write` (85 checks on a copy), `fuzz_rtf` (16,000 damaged RTF/HTML bodies), Python `test_gui.py` (43) |
| Single file | `python tools\amalgamate.py` -> `dist\openpst.c` (+ `openpst.h`); compiles with MSVC and GCC without warnings |

---------------------------------------------------------------------------------------------------------------------------------------

## 9. Findings and corrections made along the way
* **Compressed RTF dictionary** was 204 bytes, must be 207 (every compressed body was garbage).
* **`pstnpm.py` CRC**: used `zlib.crc32` for string-name bucket keys; Outlook uses the PST CRC (157/157 stored keys match). Earlier cross-file copies had wrongly keyed string-name records.
* **`pstrtf.py`**: `font-family:"X"` inside a double-quoted attribute broke fonts; now single quotes.
* **Set-order dependence** removed from `pstxcopy.py` (named-id assignment) and `pstfix.py` (row-version renewal) so results are deterministic.
* **`pstcheck nids`** guessed NID-shaped counters (`v >> 5`) and raised false alarms; raw index now.
* **Message-index key** solved (3.5): replaces the CRC-of-topic guess; removes SCANPST's "minor inconsistencies" after copies.
* **Named cells of copied rows** must be re-keyed, not dropped (found by repairing a 1.1 GB copy and diffing the rows): removes SCANPST's "row doesn't match sub-object".
* **SCANPST's own behaviour**: when it repairs it appends a section per pass and writes that section's FMap byte wrong ("csFree of 128, should have 255"), fixing it on the next pass - hence the usual
  "3-4 attempts". Not caused by our allocation (cbAMapFree, AMaps vs referenced blocks and DList were checked). Files written by the tool no longer need repair, so it does not occur.
* Portability traps found while porting: C evaluates operands of `a() | b()` in unspecified order (MSVC right to left) - side-effecting calls are sequenced; `qsort(NULL, 0, ...)` is flagged by UBSAN.

---------------------------------------------------------------------------------------------------------------------------------------

## 10. Verification method (why the C writer can be trusted)
1. **Byte identity with the reference.** The same command list is run with the C CLI on one copy and with the Python writer (`tests\pyops.py`) on another; the files are compared after every step.
   With `OPST_TEST_RANDOM=1` both sides draw the same "random" ids (xorshift32 stream), so even copies compare. Because allocation order, page splits, heap layout, ids and CRCs must all match, this is a very
   sharp test.
2. Coverage of the identical comparisons: rebuild of all 60 folder tables of the 868 MB file; 15 steps on the 1.2 MB file; 13 steps on the 868 MB file (300-message copy, 40-message moves, deletes, folders);
   cross-file copies (named properties, attachments, name-map rebuild); a file-growing copy of 8 messages of 15-24 MB (175 MB added, 3,000-block data trees); the fixer on a deliberately damaged file
   (`tests\pydamage.py`: all five rules); folder create / rename / move / delete / purge.
3. The same lists on Linux (GCC, ASAN + UBSAN) give files identical to the Windows ones.
4. Crash recovery at four commit points, with growth; cross-implementation journal recovery.
5. **SCANPST** (section 11): analyze results of files written by the C tool - small and 1.1 GB - are "No errors".
6. Reader: dumps of all folders and messages identical to the Python reader (2,793 and 13,410 messages); RTF: all 6,869 RTF messages identical to Python except deliberate improvements; search: 20 queries identical hit sets.

---------------------------------------------------------------------------------------------------------------------------------------

## 11. SCANPST tooling and the oracle method
* SCANPST.EXE (Office 16) has no command line; its window is driven. `run_scanpst.ps1` (UI Automation) no longer finds the controls (they are unpatterned Win32 panes); **`run_scanpst2.ps1`** sends Win32 messages:
  sets the path (`WM_SETTEXT`), `BM_CLICK` Start, polls the dialog text, records verdict (NO_ERRORS / MINOR / ERRORS), folder and item counts and the log; `-Repair` (throwaway copies only) unticks "Make backup"
  and presses Repair. Results append to `scan_results.txt`.
* **Repair-and-diff** studies what SCANPST changes: repair a copy, then diff nodes (`pstdiff.py`), tables (`rowdiff`), headers and allocation maps (`tests\e01_research`).
* **Oracle probing** (message-index key): `wxcopy` one message 170 times, patch GUID bytes in message and contents row, one repair pass, read assigned keys back (`probe_*.py`).

---------------------------------------------------------------------------------------------------------------------------------------

## 12. History, GUI design and the test ledger

### 12.1 Stages
| Stage | Content | State |
|---|---|---|
| A-C | storage layer, heap/BTH/TC builders, message operations | done |
| D | folder create / rename / move / delete / purge | done, SCANPST clean |
| E | file growth incl. files over 2 GB (4.24 GB synthetic, 2.6 GB real) | done, SCANPST clean |
| F | GUI editing (`pstgui.py` + `pstactions.py`) | done; GUI-edited files scan clean |
| G | search, Rich Text, cross-PST copy, worker-thread writes, `.deb` | done (`.deb` 0.8.5) |
| C1 / C1b | C reader; search and RTF -> HTML in C | done, identical to Python |
| C2 | C writer, fixer, checker, cross-file copy | done, byte-identical to Python |
| H | message-index key solved (oracle method), named row cells | done, SCANPST "No errors" |

### 12.2 GUI (GTK4, `pstgui.py`)
* Editing is on by default (header switch "Editing" is a read-only lock). Each action closes the read handle, writes through `pstactions` **on a worker thread** (busy flag disables edits and selection loading),
  reopens and reselects. Right-click menus (folders: New subfolder, Rename, Move to, Delete; messages: Move to, Copy to, Copy to another PST, Delete), Delete key, F2, drag and drop of messages and folders,
  multi-select; **copy by drag = hold Shift (or Ctrl) when releasing**, plain drag moves.
* Header bar search box (Enter runs, Esc returns, Ctrl+F focuses) with a "Bodies" check box; searches run on a thread with their own read handle (max 2,000 rows); results show a Folder column.
* Folder tree looks like Outlook (store name, then "Top of Outlook data file"; hidden system folders not listed); message list columns attachment icon, From (0x0C1A, else 0x0042, else To), Subject,
  Received, Size, user-draggable; window and text scaled 1.5x (`OPENOUTLOOK_SCALE=1` restores).
* Rich Text mail is converted with `pstrtf` (HTML mail needs `gir1.2-webkit-6.0`). Several PSTs can be open side by side.
* WSLg notes: `GDK_BACKEND=x11 GSK_RENDERER=cairo LIBGL_ALWAYS_SOFTWARE=1`; `NON_UNIQUE` application flag; the reader loads after a 180 ms pause so quick clicks are not swallowed; `set_enable_search(False)`
  on both lists avoids a Gtk-CRITICAL; tree-view columns cannot be dragged in GTK4 `Gtk.TreeView` (reorderable columns removed; a "Columns" popover sets widths).
* Protected-folder names are English only; the GUI was tested on WSL and via MSYS2 GTK4 on Windows (no WebKit there).

### 12.3 Writer performance (Python)
A copy cost about 1 s per message on a 900 MB file and grew with batch size. Fixes (identical results): `alloc` caches per section the free-slot count (later the exact longest free run), skips full or too-fragmented
sections and drops the entry whenever a section changes; `read()` indexes pending writes per 4 KB page (was a quadratic rescan). 100 copies on the 900 MB file: 63 s -> 5 s. `test_edge.py` on the 2.46 GB file
(N=300, GROW=1500): copy 300 = 64 s, move 300 = 45 s, move a folder of 300 = 25 s, copy 1,500 = 233 s (file 2,514 -> 2,580 MB), delete + purge 600 = 94 s, `pstcheck` 58 s; SCANPST no errors (47 folders, 14,610 items).
The C writer does the same work in well under a second per operation on these files.

### 12.4 Test ledger (SCANPST Analyze unless stated)
| File | What | Verdict |
|---|---|---|
| test_F | folder ops incl. a repaired regression case | no errors (24 folders, 2,690 items) |
| test_G | grown 24 sections | no errors |
| test_H | 2.6 GB copy, grown 16 sections + ops | no errors (47 folders, 13,398 items) |
| test_I | copy of rmarrash_1_clean edited through the GUI | no errors |
| test_J | headless GUI test, moved messages out of the 1,296-row Sent folder | errors -> rule 11, fixed |
| test_P0 / P2 / P1 | Outlook-made experiments (open only / + folder / + copy) | P1 (Outlook's own copy) minor; its SCANPST repair grew 1.29 -> 1.80 MB |
| test_W0-W2 / W3-W6 | create, move, delete clean; copies minor | led to rules 12-15 |
| test_W7 / W8 | unique row versions + bucket update, no ID cells | minor |
| test_Y1 / Y2 | small, folder + 1 copy with ID cells (Y2 with fresh keys) | no errors |
| test_W9 / W10 | big, 5 copies: only the first fitted in the bucket / after the heap-append fix | minor (repaired copy differs only in 0xE01) / no errors |
| test_K | headless GUI test (25 checks) | no errors; Outlook shows the copies correctly (user-verified on W10, Y1) |
| fx_P1, fx_Y1, fx_W9 | `pstfix` results (Outlook copy, Outlook permanent delete, big file message index) | no errors; node-for-node / bucket-for-bucket equal to SCANPST's own repair |
| test_C0 / C1 | C writer: untouched control / cross copy + copies + move + delete + folder + fix | no errors / minor (CRC index keys) |
| test_C3 / C4 | same after the conversation-key fix; 150-message cross copy from the 2.6 GB file | no errors |
| test_C5 / C6 | 1.1 GB: 100 copies, moves, deletes, folder ops, 70 cross-file copies incl. 10 of 15-24 MB, growth, fix | errors (26 rows, rule 19) / no errors |
Outlook-side findings: Outlook opens files with our copies and shows them correctly; after Outlook permanently deletes a message whose ID record **we** created, SCANPST says "minor" (dangling ID-map NID, fixer R2 /
SCANPST zero it). Outlook removes a permanently deleted NID from its 0xE01 bucket; our purge leaves stale entries (SCANPST tolerates them; removing them is optional polish).

## 13. Open items and ideas
1. Wire the C library into the Avalonia app (`bindings\csharp\OpenPst.cs` is ready: read, search, convert, edit); decide GUI strategy (C GUI is optional - "library first, GUI later").
2. Subnode trees deeper than one SLBLOCK; property contexts above ~400 properties; files above 16 GB; PMap / FPMap beyond the first sections (rarely exercised).
3. The three unexplained oracle probes (GUIDs `00 00 02 ...`, `13 00 ...`, `14 00 ...` returned key 0xFFFFFFFF); irrelevant for real GUIDs.
4. `bin-win\` still holds the v0.1.0 llvm-mingw binaries; MSVC builds need the VC runtime on the target (consider `/MT` for distribution).
5. Optional: SCANPST Analyze on every future change that touches the writer (`run_scanpst2.ps1 file.pst`).
6. Untested (from the Python handoffs): Outlook opening a file after the GUI edited it; a file over 2 GB after Outlook has grown it; FPMap pages beyond the first (section 8192; the second would be at 39,936);
   search folders and the search-update queues (untouched; Outlook's rebuild unproven); localised protected-folder names; native Windows GUI run.
7. Optional polish: remove a permanently deleted NID from its 0xE01 bucket; a GUI action 'Make this file SCANPST-clean' (run `opst_fix` after the user has been in Outlook).
8. Working style that worked with this user: short results-first answers, exact commands, complete anchored patches, honest statements of what is untested, evidence the user can paste back.

## 14. Later work (2026-10): formats, importer, integration
- **Formats beyond Unicode-512 (reader only):** ANSI (wVer 14/15: 32-bit BIDs/IBs, 496-byte B-tree pages with the CRC over 500 bytes, 12/16-byte tree entries, 12-byte block trailers, SLBLOCK without padding, 2-byte table row indexes) and Unicode-4K (wVer 36/37, Outlook .ost: 4096-byte pages with 16-bit counts at 4056 and a 24-byte trailer, CRC over 4072; blocks aligned to 512 with a 24-byte trailer whose bytes 18-19 hold the uncompressed size - **zlib-compressed blocks** when it differs from the stored size; heap pages up to 64 KB, each taking 8 HID block indexes). The MS-PST document (2025-02) does not describe 4K files; the layout was reverse-engineered from two real OST caches and validated by page CRCs. Cyclic encryption (method 2) implemented from MS-PST 5.2. Writing is Unicode-512 only; other formats open read-only.
- **Importer (`opst_msg_import`, `op_import.c`):** builds a message from plain fields; rules 21-26 above came from SCANPST repair-and-diff of imported files (final state: imported nodes identical across 5 repair passes).
- **Speed:** the allocation-map reconcile runs once per open file; read/flag changes are write-behind in the C# engine.
- **Managed engine retired:** the Avalonia app uses OpenPST only (see OpenOutlook_Avalonia_Integration_Plan.md).

## 15. Rules found by the soak harness and SCANPST probing (2026-10-05)
These came from `tools/PstSoak` (random operations on a clean copy, checker after every step, SCANPST on the result) and `tools/PstProbe` (messages that differ in one field, to attribute what SCANPST rewrites). Numbering continues the rule table in 3.4.

| # | Rule |
|---|------|
| 27 | **Allocation maps must agree with the block tree.** Every BBT block and tree page is marked allocated in the AMaps, and the header's cbAMapFree equals the maps' free space (SCANPST: "Block is not allocated in AMAP!", "Computed cbAMapFree of X, but header has Y"). A writer that marks blocks it finds unmarked must recompute the free total from the maps, not subtract again. |
| 28 | **Folders are complete.** Every folder has hierarchy / contents / FAI tables (nid types 0xD / 0xE / 0xF with the folder's index), a row in its parent's hierarchy table, and that row mirrors the folder's properties (name, counts, class); has-subfolders is the truth about the children. The header counters of the table types cover the folder's index. |
| 29 | **No orphan blocks, correct reference counts.** A BBT entry nothing references is removed (SCANPST "Couldn't find BBT entry in the RBT"); cRef = references + 1. |
| 30 | **A contents row is complete and equals its message.** For every column of the table the message has a (non-empty) value for, the row carries it; a row has a cell exactly when the message has the property (empty strings are no cell, a message never has an empty 0x0E03). Size (0x0E08), flags (0x0E07) and delivery time (0x0E06) equal the message's. Rows moved or copied into a table with more columns must be completed from the message. |
| 31 | **PidTagMessageSize is the stored size**: the sum of the stored block sizes (cb) of the message's data tree and subnode tree, minus 40 bytes (measured on two messages; estimating it from property sizes is ~350 bytes short). |
| 32 | **Message index (0xE01) keys.** Each message is in the bucket of the conversation-index GUID and in the bucket of its row's 0x3013, both through the same fold (0xFFFF0000 xor sum of byte j << (15 - j)). For messages Outlook wrote, 0x3013 equals the conversation-index GUID (bytes 6..22), so it is one bucket. |
| 33 | **0x3013 (conversation id) of an imported message = MD5 over the UTF-16LE bytes of the upper-cased conversation topic** (topic = subject without RE:/FW:). Verified by probing: it depends only on the topic, not on sender, body, times, recipients, message id, read flag or the message's own conversation index. The importer uses this value as the conversation-index GUID too, so 0x3013, the index GUID and both bucket keys agree and SCANPST changes nothing. |
| 34 | **Conversation index header** = 0x01 followed by the top 40 bits of the FILETIME (FILETIME >> 24), then the 16-byte GUID. |
| 35 | **Root hierarchy table 0x12D**: its normal-folder rows carry the ID cells (SCANPST adds them once the ID map is in use); search-folder rows do not. The replica blob (0x0E34) comes from any row or, when none has one, from the message store's own 0x0E34. |
| 36 | **Creating a PST from nothing** (`opst_create`, `op_create.c`). Skeleton: header (wVer 23, wVerClient 19, permutation encryption, rgnid all 0x400 except type 3 = 0x4000, type 4 = 0x10000 so the first message is NID 0x200024, type 8 = 0x8000), DList page at 0x4200 (flags 1, no entries), AMap at 0x4400, PMap at 0x4600 (0xFF), empty NBT and BBT leaf pages; the file is exactly one section (271,360 bytes), as Outlook writes. Then one transaction builds: store 0x21 (record key, 0x0E34 replica blob, 0x35DF = 0xC9, entry ids 0x35E0 / 0x35E3 / 0x35E6 / 0x35E7 for Top / Deleted Items / Common Views / Search Root, 0x66FA, 0x66FC, 0x67FF), name map 0x61 (bucket count 251, three empty streams), ID map 0xC01 and message index 0xE01 (both empty BTHs), the table templates 0x60D 0x60E 0x60F 0x610 0x671 0x692 0x6B6 0x6D7 0x6F8 0x64C (outgoing queue) with Outlook's column layouts, the receive folder table 0x62B (one row: class "" delivers to the root folder 0x122), empty internal nodes 0x1E1 0x201 0x261 0xE41 0xEC1 0xF21, root 0x122, Top 0x8022, Search Root 0x8042, Deleted Items 0x8062 (below Top), IPM_COMMON_VIEWS 0x8082. SCANPST names what is missing, one log line each: without 0x62B "Receive folder table missing", without 0xEC1 / 0x1E1 "Search folder missing update queue", without 0x201 "Search activity list missing", without 0x64C "Missing the outgoing queue", without 0x6B6 / 0x6D7 / 0x6F8 "Missing template". Not needed by SCANPST and left out: Outlook'"'"'s spam search folder 0x2223 and nodes 0xEE1 / 0xF01. An empty message index BTH (root HID 0) is filled by `heap_bth_insert` creating the first leaf (the first import used to fail with "bad HID 0x0"). |
| 37 | **Fixer rule R11 (tables)** and repeat passes. (a) A contents row whose message node does not exist (row id 0 included), or whose message belongs by its node's parent to another folder, is removed; (b) a message that its own folder's table does not list gets a row (cells 0x67F2 = NID, 0x67F3 = a new row version, 0x0E08 / 0x0E07 / 0x0E06 and the other cells from the message; the id and row-only cells follow from R1 / R6); (c) the folder's content / unread counts (property context and hierarchy row) become what the table holds; (d) a contents table that cannot be read at all (here: the row matrix subnode named in the table info was missing, early-writer damage) is replaced by an empty one and its messages are listed again. SCANPST names a row added without 0x67F2 as "Row matrix ... row (0) missing PR_LTP_ROW_ID" followed by "TC missing required column" for every column. R10 also fills a missing 0x0E08 / 0x0E07 / 0x0E06 cell and any cell that is present but empty while the message has a value. `opst_fix(apply)` repeats passes (up to four more) until a dry run finds nothing, because each repair can make the next possible; the report is the first pass's. Result: `rmarrash_2.pst` and `test-archive.pst` (the early-writer damage listed as a known limit) are SCANPST NO_ERRORS and `test_write` passes 168 / 168 on both. |
| 38 | **Found by mirroring a real mailbox (1,140 messages, 19 folders)**, each checked by repairing a copy with SCANPST and diffing. (a) A message with no To recipient must not carry an empty PidTagDisplayTo (0x0E04): SCANPST deletes it (8 bytes per message); same for 0x0E03. Fixer R12 removes them from older files (the property context is rewritten with subnode references kept: `pcprops_get_ex(..., 2)`) and the row cell. (b) The contents row of a message whose subject (0x0037), sender name (0x0042) or conversation topic (0x0070) is an empty string carries **empty cells** for them (SCANPST adds exactly these: "Contents Table row doesn't match sub-object", 9 of 1,140). Every other empty string is still no cell. R10 adds them to older rows. (c) Subjects and display names are cut to 255 characters as Outlook does; the sender's name is also part of the one-off entry ids. (d) Values over 3,580 bytes: message properties go to subnodes (subject, names, display lists); a **table cell** over 3,580 bytes (a recipient list of hundreds of names) is stored in a subnode of the table node (local NID type 0x1F from the header counter), `tc_build_ex` / `tcbig_put`; previously "heap allocation of N bytes not allowed". SCANPST still rewrites such a table (it renumbers the row versions) and its own repaired result then scans with one error, so this single case stays "minor". |

Fixer rules added: R7 allocation maps (27), R8 folders and hierarchy rows (28), R9 orphan blocks / refcounts (29), R10 contents-row sync (30, 32, 33). Checks added: `amap`, `folders`, row completeness in `rowcells`.
