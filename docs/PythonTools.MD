# Python tools reference

This document covers all 29 Python files in `tools/python`. These are the original Python PST implementation and its supporting utilities, used as a reference for the C library in `native/openpst`. Some files are command-line programs; others are libraries imported by those programs.

Descriptions are based on the current source code. The scripts were read, not executed, to prepare this reference.

## Running the tools

Run the example commands from `tools/python`, with Python 3. Replace example filenames, folder paths, and node IDs with your own values. `NID` means node identifier; commands accepting NIDs generally allow hexadecimal values such as `0x2000e4`.

Most tools use only the Python standard library and neighboring modules. `pstgui.py` additionally requires PyGObject and GTK 4; WebKit 6 is optional for HTML rendering.

The main reader supports Unicode PST files with versions 23 through 35 and encryption modes 0 (none) and 1 (permutation). It rejects ANSI PSTs, versions 36 and above, and cyclic encryption. The writer and low-level checker require Unicode version 23.

For tools that write PST data, close Outlook and work on a copy. `PSTWriter` automatically rolls back a leftover `<file>.journal` when opened. Consequently, utilities using that writer can modify a PST during recovery even when their normal operation only reports information. This applies to `pstdiff.py`, the fixed-file comparison helpers, `pstedit.py dump`, `pstnpm.py` diagnostics, `pstfix.py` report mode, some `pstcheck.py` checks, and folder inspection through `pstactions.py` or the GUI. Cross-file copying also opens its source through the writer.

## Browsing, searching, and rendering

### `pstcli.py`

Command-line front end for the read-only PST reader. Prints file information, verifies blocks and message parsing, displays the folder tree, lists messages, shows message headers and bodies, and dumps raw MAPI properties. Its `save` command exports attachments to a directory; it does not edit the PST.

Supported commands:

```text
python pstcli.py FILE.pst info
python pstcli.py FILE.pst verify
python pstcli.py FILE.pst tree
python pstcli.py FILE.pst list "Top of Personal Folders/Inbox"
python pstcli.py FILE.pst show NID [--body auto|html|rtf|text]
python pstcli.py FILE.pst props NID
python pstcli.py FILE.pst save NID OUTPUT_DIRECTORY
```

### `pstgui.py`

GTK 4 desktop PST viewer and editor. Displays folders, messages, headers, text/HTML/RTF bodies, and attachments; supports searching and saving attachments. Enabling **Allow editing** exposes folder creation, rename, move, and delete, message move/copy/delete, and copying messages into another PST. Drag-and-drop supports moving folders and messages, with Ctrl used to copy messages. Reads through `pstcore.py`, searches through `pstsearch.py`, and edits through `pstactions.py`.

Usage: `python pstgui.py [FILE.pst]`. Requires PyGObject/GTK 4; WebKit 6 enables the embedded HTML viewer. The current implementation uses GTK, despite the older README describing it as Tk. Folder inspection uses the writer-backed session layer and has the journal-recovery behavior described above.

### `pstsearch.py`

Read-only, headless message search engine used by the GUI and available from the command line. Searches subjects, senders, and recipients; optionally searches bodies. Matching is case- and accent-insensitive. Supports phrases, excluded terms, field-specific terms (`from:`, `to:`, `cc:`, `subject:`, `body:`), attachment/read-state filters, dates, and folder filters. The library supports cancellation and progress callbacks.

Usage: `python pstsearch.py FILE.pst "QUERY" [--body] [--folder NAME] [--limit N] [--deleted]`. The CLI defaults to 50 results. In the current code, Deleted Items are included regardless of `--deleted`; library callers can explicitly exclude them using `include_deleted=False`.

### `pstrtf.py`

Converts uncompressed Outlook RTF body text into a complete HTML page. Preserves common text formatting, fonts, colors, alignment, lists, tables, hyperlinks, and embedded PNG/JPEG pictures. Handles code pages, Unicode escapes, and RTF containing encapsulated HTML. This is a renderer, not a PST editor or compressed-RTF decoder.

Usage: `python pstrtf.py BODY.rtf` prints HTML to standard output; without a filename it reads standard input. Library entry point: `rtf_to_html(rtf_text)`. Saving the output creates an HTML file without changing the input.

## Editing and repair commands

### `pstops.py`

Performs message operations within one PST: move, copy, delete, and permanent deletion. Copies within the same file share reference-counted blocks. Maintains contents tables, folder counts, object IDs, and related message metadata through the editor/writer layers. Commits changes to the supplied PST.

```text
python pstops.py FILE.pst move NID[,NID...] "Folder/Path" [--verify]
python pstops.py FILE.pst copy NID[,NID...] "Folder/Path" [--verify]
python pstops.py FILE.pst delete NID[,NID...] [--verify]
python pstops.py FILE.pst purge NID[,NID...] [--verify]
```

`delete` moves messages to Deleted Items, or permanently removes messages already there. `purge` permanently removes them wherever they are. `--verify` runs structural validation after committing. Search folders and search-update queues are not updated by these operations.

### `pstfolders.py`

Creates, renames, moves, and deletes folders in a PST, updating hierarchy tables and related folder metadata. Protects structural/default folders. Writes the supplied PST; search folders and search-update queues are not updated.

```text
python pstfolders.py FILE.pst create "Parent/Path" "New name" [CONTAINERCLASS] [--verify]
python pstfolders.py FILE.pst rename "Folder/Path" "New name" [--verify]
python pstfolders.py FILE.pst move "Folder/Path" "New/Parent/Path" [--verify]
python pstfolders.py FILE.pst delete "Folder/Path" [--verify]
python pstfolders.py FILE.pst purge "Folder/Path" [--verify]
```

New folders default to container class `IPF.Note`; an empty parent path selects the root-level parent. `delete` moves a folder to Deleted Items, or permanently removes it when already inside Deleted Items. `purge` permanently removes the folder subtree and its messages. `--verify` validates after committing.

### `pstxcopy.py`

Copies messages from a folder in one PST into a folder in another. Clones message data and recipient/attachment subnodes into independent destination blocks, translates named property IDs between stores, adds missing property names, and updates destination rows, counts, IDs, row versions, and message-index metadata. Named properties whose identities cannot be resolved in the source are dropped. Source and destination must be different files.

Usage: `python pstxcopy.py SOURCE.pst DEST.pst --from "Inbox" --to "Imported" [--count N] [--create]`. Folder paths match case-insensitively and may omit the top-of-store folder. `--count` limits the copied messages; `--create` creates a missing destination folder directly under the destination's top-of-store folder.

The copy is committed to the destination, followed by a separate fixer pass/commit if needed. Destination-folder creation is also a separate operation. The source is normally only inspected, but its writer-backed open can recover a leftover journal. The library function `copy_messages(...)` returns the new destination NIDs.

### `pstfix.py`

Reports or repairs known structural inconsistencies identified through comparisons with Microsoft SCANPST repairs. Handles missing row ID cells and ID-map records, stale ID mappings, missing message-index entries, duplicate row versions, allocation-map/free-space inconsistencies, incomplete folder tables or hierarchy membership, and missing message-row status/key cells.

Usage: `python pstfix.py FILE.pst` reports issues; `python pstfix.py FILE.pst --apply` applies supported fixes in a journaled commit and then checks again. Report mode does not commit fixes but uses the writer and can recover a journal. Apply mode refuses to start when a journal already exists. Finding no known issues is not a guarantee that SCANPST will find none.

### `pstgrow.py`

Appends a specified number of empty allocation sections to a PST for testing or preallocation, then commits the enlarged file. Each section adds 253,952 bytes. The writer also grows files automatically when allocation space runs out.

Usage: `python pstgrow.py FILE.pst N`. Writes the supplied PST and prints the old/new section counts and resulting size.

### `pstedit.py`

Shared implementation of heap-on-node readers/builders, B-trees on heaps, table-context parsing/rebuilding, and node-level editing. `Editor` connects those structures to a `PSTWriter` transaction and handles data blocks, subnodes, and table storage.

Its CLI can dump folder hierarchy/contents/associated-information tables to a text file or rebuild those tables in a PST:

```text
python pstedit.py dump FILE.pst OUTPUT.txt
python pstedit.py rebuild FILE.pst all
python pstedit.py rebuild FILE.pst FOLDERNAME
```

`dump` writes the text output and opens the PST through the writer. `rebuild` writes the PST and validates after committing; a folder-name selector matches leaf names case-insensitively, so multiple folders with that name can be selected.

## Validation and diagnostics

### `pstcheck.py`

Collection of SCANPST-style structural checks. Reports problems without deliberately committing repairs. Usage: `python pstcheck.py CHECK FILE.pst`.

| Check | What it examines |
| --- | --- |
| `refs` | Missing referenced blocks, orphan blocks, and BBT reference counts. |
| `amap` | Whether BBT blocks are marked allocated and header free-space totals agree with allocation maps. |
| `folders` | Required folder tables and membership in the parent's hierarchy table. |
| `nids` | Header node-ID counters against the highest IDs in use. |
| `tables` | Folder message/unread counts, parent links, and contents-table rows versus message properties. |
| `idmap` | Row IDs versus the object-ID map, including stale records pointing to missing nodes. |
| `xblocks` | Data-tree block fullness and total-length consistency. |
| `rowvers` | Store-wide row-version uniqueness and the header's version counter. |
| `subnodes` | By-value attachment size properties and local attachment/LTP node-ID counters. |
| `rowcells` | Missing status/key values in contents rows when the corresponding columns exist. |
| `all` | Runs all the checks above. |

`folders`, `idmap`, and `rowvers` use `PSTWriter`, so those checks and `all` can recover a journal. The CLI prints its findings but does not aggregate them into a failing exit status.

### `pstltp.py`

Read-only structural scan of heap-on-node data in top-level nodes and subnodes. Checks heap allocation/page maps, property-context B-trees, table metadata, and heap-resident row-matrix lengths. Prints heap/client counts, multiblock statistics, and calibration comparisons for stored heap fill levels. Returns a nonzero exit status when examined heaps have problems.

Usage: `python pstltp.py FILE.pst`. Uses `pstcheck.py` for read-only storage access and `pstedit.py` for heap parsing.

### `pstprobe.py`

Small read-only header probe. Prints the PST signature, version, encryption mode, declared versus actual file size, and Unicode NBT/BBT root pointers. Includes basic ANSI header offsets for inspection, without implementing an ANSI reader.

Usage: `python pstprobe.py FILE.pst`.

### `probe_maps.py`

Read-only allocation-map investigation utility. Locates AMap, PMap, FMap, and FPMap pages, reports their positions and spacing, calculates each AMap's longest free run, and compares FMap bytes with candidate AMap alignments. Useful for reverse-engineering free-space-map layout.

Usage: `python probe_maps.py FILE.pst`.

### `pstdebug.py`

Read-only parser debugging helper. Walks folders and messages, exercises headers, body availability, recipients, and attachments, and prints full tracebacks for up to four distinct failures. Adds table or node structure details to help locate the failing data.

Usage: `python pstdebug.py FILE.pst`.

### `pstdebug2.py`

Read-only diagnostic for invalid table-context heap references. Temporarily patches the in-process `pstcore.TC` row parser to detect certain bad heap IDs in variable-length cells. Prints up to six detailed reports identifying the table node, property/type, offsets, heap size, and row bytes. The patch does not alter the module file.

Usage: `python pstdebug2.py FILE.pst`.

### `pstdiff.py`

Compares two PSTs at the logical node level. Reports added/removed nodes and changes to decoded node data, subnode contents, and parent IDs. Intended for studying Outlook edits or SCANPST repairs without treating every physical block relocation as a content change.

Usage: `python pstdiff.py BEFORE.pst AFTER.pst`. Prints its report and does not commit edits, but opens both files through `PSTWriter`, with the journal-recovery caveat above. Its subnode comparison explicitly handles leaf subnode blocks; deeper subnode indexes receive a marker rather than a complete traversal.

### `cmp_bak.py`

Read-only low-level comparison of two supplied PSTs, labeled as backup and repaired versions in its output. Prints byte differences in the first 564 header bytes, NBT/BBT entry counts, added/removed IDs, and changed node/block entries, with limits on displayed examples.

Usage: `python cmp_bak.py BEFORE.pst AFTER.pst`.

### `cmp_nodes.py`

Investigation script fixed to `test_E.bak` and `test_E.pst` in the working directory. Compares table nodes `0x802d`, `0x806d`, and `0x828d` by row/property value, column layout, and row offsets; also compares raw node `0xc01` data and reports sample differing byte offsets.

Usage: `python cmp_nodes.py`. Takes no configurable file arguments. Uses the writer/editor for inspection, so journal recovery is possible.

### `c01.py`

Investigation script fixed to `test_E.bak` and `test_E.pst`. Compares B-tree records in node `0xc01`, reports keys added/removed/changed by repair, and checks whether repaired keys match property `0x0E30` values in hierarchy table `0x802d`.

Usage: `python c01.py`. Takes no configurable file arguments and uses writer-backed inspection. Unlike `pstidmap.py`, it directly interprets the heap root as the B-tree header, making it a specific layout probe rather than the general ID-map API.

## Test and variant-generation utilities

### `pstselftest.py`

Writer validation and stress-test suite. Supports these modes:

| Mode | Behavior and file effects |
| --- | --- |
| `synth` | Creates/overwrites `/tmp/synth.pst`, inserts and deletes synthetic data/nodes to exercise tree splits, and simulates interrupted writes to check byte-for-byte journal recovery. Uses a hard-coded Unix-style temporary path. |
| `check FILE.pst` | Read-only checks of allocation-map bit order, block signatures, tree structure, and header/free-space facts. Refuses to touch a file with an existing journal. |
| `tx FILE.pst` | Commits a test node/data tree, reopens and validates, then removes it and commits again. Writes the supplied PST. |
| `stress FILE.pst` | Inserts 300 large test nodes, commits and validates, then removes them and validates again. Writes the supplied PST. |

Usage: `python pstselftest.py MODE [FILE.pst]`. The write tests exercise storage transactions; they do not promise that the file ends byte-identical to its initial state.

### `mkvariants.py`

Fixed-fixture bisect helper for isolating differences between Python-generated PST edits and Outlook-generated edits. Copies `rmarrash_2.pst` into `test_Z0.pst`, creates `OutlookDest`, copies message `0x2000e4`, and generates variants by transplanting selected nodes from `test_P1.pst`.

Usage: `python mkvariants.py`. Requires those named fixtures in the working directory. Creates/overwrites `test_Z0.pst`, `test_ZA.pst`, `test_ZB.pst`, `test_ZD.pst`, `test_ZE.pst`, and `test_ZF.pst`, and removes existing journals for generated targets. It is a development experiment with fixed filenames and node IDs.

## Shared engine and support modules

### `pstcore.py`

Main read-only PST engine, implemented without third-party PST libraries. Provides memory-mapped file access, node/block B-trees, data and subnode traversal, heap/property/table contexts, MAPI value decoding, and store/folder/message/attachment objects. Also supplies block verification, FILETIME conversion, compressed RTF decompression, and body conversion helpers. Imported by the CLI, GUI, search engine, and diagnostics; it has no standalone CLI.

### `pstcore2.py`

Small standalone helper containing a permutation byte table named `MPBB_I` and a PST CRC implementation. It does not implement the PST reader, folders, or messages and has no CLI. Its byte table differs from `pstcore.py`'s table; none of the other Python files in this directory currently imports it.

### `pstwrite.py`

Incremental, in-place storage writer for Unicode version-23 PSTs. Provides `Journal`, `PSTWriter`, and `BTree` for buffered transactions, crash recovery, checksums/signatures, block allocation and reference counting, data trees, NBT/BBT updates, allocation/free-space maps, file growth, and structural validation. Commits write a recovery journal before replacing file data.

Library module with no standalone CLI. Opening `PSTWriter(path)` can recover a journal immediately. Allocation grows the file automatically in batches of 16 sections; the current implementation caps growth at 65,536 sections, approximately 16 GB. Its header comment saying growth is unimplemented is older than the current code.

### `pstactions.py`

GUI-independent editing facade. `Session` provides folder inspection/protection metadata and methods to create/rename/move/delete folders, move/copy/delete/purge messages, and copy messages into another PST. Ordinary edits open the appropriate operation layer, execute the change, commit, and close; errors discard pending writes. Cross-file copying delegates to `pstxcopy.py` and its separate fixer pass.

Library module with no standalone CLI. `folder_info()` is an inspection method but opens the writer-backed folder layer, so journal recovery can occur.

### `pstidmap.py`

Maintains node `0xC01`, the lookup from 16-byte object IDs to folder/message NIDs. `IdMap` reads existing records, adds IDs, generates new IDs, and follows SCANPST's deletion convention by retaining a record with its NID set to zero. `flush()` rebuilds the map inside the caller's writer transaction. Library module with no standalone CLI.

### `pstnpm.py`

Reads and updates the Name-to-ID map in node `0x61`. `NameMap` resolves store-specific named property IDs to GUID/name identities, looks up or adds identities, and rebuilds the property-context streams and hash buckets when saved. Used to translate named properties during cross-file copying; saving participates in the caller's transaction.

Usage: `python pstnpm.py FILE.pst` runs a diagnostic self-test of named-property hash-bucket placement and sample lookups. It prints property/GUID/bucket statistics and fails when records are in the wrong buckets. The diagnostic uses writer-backed inspection and can recover a journal.

### `pio.py`

Windows portability shim. Importing it adds `os.pread`/`os.pwrite` fallbacks when missing, using seek/read/write operations, and wraps `os.open` to add `O_BINARY` on platforms that expose it. Changes the `os` module in the running process, not source files. On platforms already providing positional I/O and not needing binary flags, it makes no changes. No standalone CLI.

## Scope

The two PowerShell SCANPST automation scripts and `README.md` in `tools/python` are not Python tools and are outside this inventory. Python scripts under `native/openpst/tools` and `native/openpst/tests` belong to the native library's separate tooling and test directories and are also outside this inventory.
