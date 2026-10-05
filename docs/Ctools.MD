# C tools and libraries reference

This document describes the OpenPST C implementation in `native/openpst`: all 20 library source modules, its public/internal headers, two command-line tools, and four C test programs. It also maps their responsibilities to the Python tools described in `PythonTools.MD`.

The C modules compile together into one library. They are not separate programs corresponding one-for-one to the Python scripts. Descriptions below follow the current source and CMake configuration; no libraries, tools, or tests were executed to prepare this reference.

## Library outputs and build

OpenPST is a C11 library with a public `opst_*` API. The current declared version is `0.3.0`. CMake builds these targets:

| Target | Purpose and output |
| --- | --- |
| `openpst_static` | Static library containing all engine modules. On Windows, `openpst_static.lib`; on Unix-like systems, `libopenpst.a`. Used by the CLI and C tests. |
| `openpst` | Shared library exposing the public API: `openpst.dll` on Windows, `libopenpst.so` on Linux, or the platform's shared-library equivalent. Windows also produces a DLL import library. |
| `openpst_cli` | Command-line program with output name `openpst` (`openpst.exe` on Windows). |
| `inspect_msg` | Developer program for inspecting internal message/table structures. |
| `test_basic`, `test_formats`, `test_write` | Test executables registered with CTest as `basic`, `formats`, and `write`. |
| `fuzz_rtf` | Converter robustness test executable, run manually rather than registered with CTest. |

Example build commands, run from the repository root:

```text
cmake -S native/openpst -B native/openpst/build -DCMAKE_BUILD_TYPE=Release
cmake --build native/openpst/build --config Release
ctest --test-dir native/openpst/build -C Release --output-on-failure
```

With a multi-configuration generator such as Visual Studio, executables normally appear in the configuration subdirectory, for example `native/openpst/build/Release/openpst.exe`. Other generators commonly put them directly in the build directory. Commands below use `openpst` as shorthand for that executable's path.

CMake options: `OPST_BUILD_SHARED`, `OPST_BUILD_TOOLS`, and `OPST_BUILD_TESTS` default to `ON`; `OPST_SANITIZE` defaults to `OFF` and enables AddressSanitizer/UndefinedBehaviorSanitizer with GCC/Clang. The engine uses the C runtime and platform APIs; POSIX code-page conversion uses `iconv`, while Windows uses `MultiByteToWideChar`. It does not require Python at runtime.

## Format support and file effects

The current reader in `op_ndb.c` accepts ANSI versions 14/15, Unicode files with 512-byte pages (versions 23 through 35), and Unicode files with 4K pages (versions 36 and above). It handles encryption methods 0 (none), 1 (permutation), and 2 (cyclic), and decompresses zlib/deflate data blocks in 4K files. These capabilities are newer than the format restrictions stated in the existing README and some header comments.

Editing is limited to Unicode files with 512-byte pages. The current writable-open guard checks the page format rather than requiring exactly version 23. Cross-file copying also requires a Unicode 512-byte-page source; ANSI and 4K sources can be viewed but cannot currently be copied through this writer API.

`OPST_OPEN_READONLY` does not recover or alter a journal. `OPST_OPEN_WRITE` rolls back a leftover `<file>.journal` before loading the file. `opst_journal_pending()` reports an interrupted write to a reader, and `opst_recovered()` reports recovery on a writable handle. Close Outlook and use a copy for editing commands.

Ordinary public edits buffer changes and commit them through a recovery journal. Cross-file copying commits the copy first and then invokes the fixer in a separate transaction. Growth occurs in batches of 16 allocation sections, approximately 4 MB, with a configured limit of 65,536 sections (approximately 16 GB).

Structural checking has format limits: `opst_check()` reports that its checks are unavailable for ANSI/4K files and returns no findings; `opst_verify()` only reports loaded B-tree page CRC mismatches for those formats. A zero-finding result for these formats does not mean the full Unicode integrity checks ran.

## Public API and headers

### `include/openpst.h`

Public API shared by C/C++, the CLI, Python `ctypes`, and the C# P/Invoke wrapper. Defines opaque file/message handles, error codes, open modes, result structs, search options, import structs, repair/check reports, and exported `opst_*` functions.

| API group | Main operations |
| --- | --- |
| File access | `opst_open`, `opst_close`, store/root information, journal status, version/error reporting. |
| Folders | Find folders by path, enumerate children, retrieve metadata, and create/rename/move/delete/purge folders. |
| Messages | Enumerate contents rows, open a message, read properties and bodies, recipients, attachment metadata, and attachment bytes. |
| Text conversion | `opst_rtf_to_html`, `opst_rtf_to_text`, `opst_html_to_text`, plus message-level text/HTML fallback rendering. |
| Search | `opst_search` with body search, Deleted Items exclusion, folder restrictions, result limits, progress callbacks, and cancellation. |
| Message edits | Move/copy/delete/purge messages, set read/flag state, and copy messages between PSTs. |
| Import | Import one message or a batch from plain fields, recipients, and attachment bytes. |
| Integrity | `opst_verify`, `opst_check`, and report-only or applying `opst_fix`. |
| Utilities | FILETIME conversion and matching memory-release functions. |

Strings are UTF-8. Status-returning operations use `OPST_OK` or negative `OPST_E_*` codes; getters and memory-release functions have their own return types. Readable error text is available through `opst_last_error()`. Release returned arrays with the matching `opst_free_*` function and standalone converter output with `opst_free()`. Message-owned properties/bodies/attachment bytes stay valid until their message handle is closed. Close messages before closing their PST, and do not use one handle concurrently from multiple threads.

### `src/op_internal.h`

Internal declarations for platform I/O, errors, text utilities, format layouts, nodes/blocks, heaps, property/table readers, and the private contents of file/message handles. Includes byte-order helpers and compiler/platform compatibility definitions. It is an implementation header, not the supported consumer API.

### `src/op_wr.h`

Internal writer declarations and limits. Defines transaction buffers, allocation maps, B-tree entries, heap/table/property builders, operation contexts, ID/name maps, and writer helpers used across the editing modules. Corresponds to shared internals of `pstwrite.py`, `pstedit.py`, and related Python editing modules.

## Reading, searching, and rendering modules

### `src/op_platform.c`

Provides portable file open/read/write/flush/truncate operations, file existence/removal helpers, per-thread error messages, platform code-page conversion, path comparison, and random bytes. Handles Windows UTF-8 paths through platform conversion and provides deterministic randomness when `OPST_TEST_RANDOM=1` is enabled for tests.

Python counterpart: `pio.py` and platform/error/random helpers spread across the Python engine. File mutations occur when its write helpers are called by the writer.

### `src/op_util.c`

Shared PST CRC implementation, UTF-16/code-page text decoding, text comparison/folding helpers, compressed RTF (LZFu/MELA) decoding, result-list allocation, and FILETIME formatting/conversion. Supports the reader and converters without editing PST data.

Python counterpart: utility functions in `pstcore.py`, plus common text/list handling performed by Python itself.

### `src/op_ndb.c`

Node Database storage reader: parses file headers, loads NBT/BBT trees, reads data blocks, handles data/subnode trees, decrypts blocks, and exposes file open/close and low-level verification. Implements the ANSI, Unicode 512-page, and Unicode 4K-page layouts and dispatches compressed 4K blocks to the inflate module. Coordinates journal recovery when a file is opened for writing.

Python counterpart: `pstcore.PST` and `pstcore.Node`, plus much of the header inspection in `pstprobe.py`. Public entry points include `opst_open`, `opst_close`, and `opst_verify`.

### `src/op_inflate.c`

Small, bounds-checked RFC 1950/1951 zlib/deflate decoder for compressed 4K-page PST/OST data blocks. Supports stored and Huffman-coded deflate blocks without linking an external zlib library. Internal entry point: `op_zinflate`.

Python counterpart: no direct replacement in the original Python PST reader, which rejects 4K-page files.

### `src/op_ltp.c`

Lists/Tables/Properties reader layer. Opens heap-on-node data, walks B-trees on heaps, resolves heap/subnode values, and parses property contexts and table contexts into data used by the messaging layer. It reads existing structures; the writer's corresponding builders live in `op_edit.c` and `op_edit2.c`.

Python counterpart: `pstcore.Heap`, `pstcore.PC`, and `pstcore.TC`. It is not a standalone equivalent of the `pstltp.py` scan program.

### `src/op_msg.c`

Messaging layer and public read API. Implements folder lookup/enumeration, contents rows, message properties and bodies, recipients, attachments, and result cleanup. Removes Outlook subject-prefix control characters and supplies text/HTML display fallbacks. Results are exposed through `opst_folder_*`, `opst_messages`, `opst_msg_*`, and attachment APIs.

Python counterpart: `pstcore.Store`, `Folder`, `Message`, and `Attachment`, with body-display helpers used by the Python GUI/search code.

### `src/op_rtf.c`

RTF-to-HTML, RTF-to-text, and HTML-to-text conversion. Handles common formatting, fonts/colors, alignment, tables, hyperlinks, embedded PNG/JPEG pictures, Unicode/code-page escapes, and encapsulated HTML. The converters can be called without opening a PST and return allocated UTF-8 output.

Python counterpart: `pstrtf.py`, `pstcore.rtf_to_html_or_text`, and HTML stripping in `pstsearch.py`. Public functions: `opst_rtf_to_html`, `opst_rtf_to_text`, and `opst_html_to_text`.

### `src/op_search.c`

Query parser and search engine. Performs case- and accent-insensitive matching on folder contents rows and opens messages when body matching is required. Supports words, phrases, exclusions, field restrictions (`from:`, `to:`, `cc:`, `subject:`, `body:`), attachments, read state, sent/received date filters, and folder filters. Search options support progress, cancellation, folder selection, limits, and skipping Deleted Items.

Python counterpart: `pstsearch.py`. Public entry point: `opst_search`; matching uses the generated Unicode folding table.

## Writing and repair modules

### `src/op_wr.c`

Storage writer: buffered transaction overlays, journal creation/recovery, checksums/signatures, allocation/free-space maps, block allocation/reference counts, data trees, NBT/BBT editing, file growth, commit/abort, and writer structural validation. Writes original bytes to the recovery journal before modifying the PST and reloads the reader after successful commits.

Python counterpart: `pstwrite.py`; file growth also covers the storage behavior used by `pstgrow.py`. It is an internal engine module with no separate executable or public grow command.

### `src/op_edit.c`

Writer-side heap reading/editing/building and B-tree-on-heap construction. Locates and grows heap items, appends allocations, inserts/repoints B-tree records, builds multiblock heaps, and loads/stores data-block and subnode structures through the writer transaction.

Python counterpart: `pstedit.Heap`, `HeapBuilder`, `build_bth`, and heap-edit helpers from `pstops.py`/`pstfolders.py`.

### `src/op_edit2.c`

Writer-side table and property contexts. Parses/rebuilds tables, maps row cells to property IDs, builds property contexts, and loads/stores node table/property data. Used by folder/message edits, named-property maps, import, and repairs.

Python counterpart: `pstedit.TC`, `pstedit.Editor`, and property-context helpers from `pstfolders.py`.

### `src/op_ops.c`

Message operations and shared editing metadata. Moves/copies/deletes/purges messages, changes read/flag state, updates folder counts and contents rows, maintains the object-ID map (`0xC01`), and manages message-index (`0xE01`) and node-counter metadata. Same-file copies share reference-counted blocks. Operations execute inside a transaction supplied by the public API layer.

Python counterpart: `pstops.py` and `pstidmap.py`. Read/flag state changes are additional public functionality beyond the documented Python CLI.

### `src/op_folders.c`

Creates, renames, moves, deletes, and purges folders, including their tables and hierarchy metadata. Protects special folders using recorded IDs and names, checks name/move restrictions, and supplies folder reconstruction/hierarchy synchronization helpers for the fixer/checker.

Python counterpart: `pstfolders.py` and folder-protection rules from `pstactions.py`.

### `src/op_api_w.c`

Public write facade. Requires writable handles, applies operation guards, starts a transaction, invokes the relevant editing implementation, and commits or aborts it. Exposes folder edits, message edits/read-state changes, single/batch import, folder protection queries, and journal status getters.

Python counterpart: `pstactions.Session`. Public cross-file copy and repair functions are implemented in `op_xcopy.c` and `op_fix.c`, respectively.

### `src/op_npm.c`

Reads and rebuilds the Name-to-ID map (`0x61`) for named properties. Resolves numeric/string names and GUID identities, allocates destination property IDs, and saves property streams and hash buckets using the PST CRC convention. Used when copying messages between stores.

Python counterpart: `pstnpm.py`. Internal map operations participate in the caller's transaction; there is no separate name-map CLI command.

### `src/op_xcopy.c`

Copies messages between different PSTs. Clones independent destination blocks, preserves recipient/attachment subnodes, translates named property IDs through both stores' maps, drops unresolved named properties, renews IDs/row versions, and updates destination counts/counters. Commits the copy and then runs a separate fixer pass for destination consistency and indexing.

Python counterpart: `pstxcopy.py`. Public entry point: `opst_msgs_copy_to`; CLI command: `wxcopy`. The source can be a read-only handle and is not edited by the copy.

### `src/op_import.c`

Builds new messages from plain UTF-8 fields rather than cloning an existing PST message. Constructs the message property context, bodies, recipient table, attachment table/nodes, large-value subnodes, contents row, object IDs, and message-index metadata. Supports batched imports with one contents-table rewrite and transaction.

Python counterpart: no direct tool in the original directory. Public entry points, wrapped by `op_api_w.c`: `opst_msg_import` and `opst_msgs_import`. Callers parse EML, Graph, or other input formats themselves and supply the import structs; this module does not fetch mail or parse EML files. Batch payloads remain in memory until commit.

### `src/op_fix.c`

Reports or applies nine known repair categories through `opst_fix`:

| Rule | Repair |
| --- | --- |
| R1 | Add missing row ID cells and object-ID map records. |
| R2 | Zero ID-map NIDs referring to deleted nodes. |
| R3 | Add missing message-index entries and update highest-message metadata. |
| R4 | Renew duplicate/high row versions and raise the version counter. |
| R5 | Raise insufficient header node-ID counters. |
| R6 | Add missing message-row status and per-row key cells. |
| R7 | Correct allocation-map markings and header free-space totals. |
| R8 | Complete folder tables/hierarchy membership and synchronize hierarchy rows with folder properties. |
| R9 | Remove orphan blocks and correct BBT reference counts. |

Python counterpart: `pstfix.py`, with additional C repair coverage. `apply=0` reports through a read-only operation context; `apply=1` requires a writable handle and commits repairs. Restricted to Unicode 512-byte-page files. The fixer handles known rules, not every condition Microsoft SCANPST can detect.

### `src/op_check.c`

Read-only integrity checks through `opst_check`. Examines block references/orphans, node-ID counters, folder/message counts and parent links, row-versus-message properties, ID mappings, data-tree lengths/fullness, row versions, attachment sizes/local IDs, message-row status/key cells, allocation maps, and folder completeness. Returns categorized counts plus optional report text.

Python counterpart: `pstcheck.py`. Uses a read-only writer view without committing or recovering journals. The CLI's `check` command runs the complete set rather than accepting individual Python-style check names. Checks are unavailable for ANSI/4K files, as described above.

### `src/op_test.c`

Internal developer hooks, included in the library source list but outside the public API. `op_test_rebuild` loads/rebuilds all reachable folder hierarchy/contents/associated-information tables in one transaction. `op_test_validate` starts a writer view, runs storage validation, and aborts the view without committing changes.

Python counterpart: `pstedit.py rebuild all` and `PSTWriter.validate()` used by `pstselftest.py`. Accessible through the statically linked CLI commands `rebuild` and `wvalidate`; both commands open the file writable and can recover a journal.

## Generated/include data

| File | Purpose |
| --- | --- |
| `src/op_foldtab.inc` | Unicode normalization/case/accent folding lookup data used by `op_search.c`; generated by `tools/gen_foldtab.py`. |
| `src/op_htmlent.inc` | HTML entity decoding lookup data used by `op_rtf.c`; generated by `tools/gen_htmlent.py`. |
| `src/op_mpbb.inc` | Permutation/cyclic cipher tables used by `op_ndb.c`. |

These are compiled-in tables, not executables. `tools/amalgamate.py` can generate `dist/openpst.c` and `dist/openpst.h` for embedding the engine as a single C source/header pair. The generated source contains the same modules and tables; it is not another library implementation. Generation writes output files and was not run for this documentation task.

## Command-line tools

### `tools/openpst_cli.c` — `openpst`

Main command-line front end and API usage example. Folder arguments accept a NID or a path, first resolved below the top-of-store folder and then from the root. NIDs accept decimal/hexadecimal syntax. Message-edit commands accept comma-separated NID lists, with a current CLI capacity of 4,096 entries.

Read/report commands:

| Command | Purpose and output |
| --- | --- |
| `openpst FILE.pst info` | Store name and root/IPM-root/Deleted Items IDs. |
| `openpst FILE.pst tree` | Folder hierarchy, IDs, message counts, and unread counts. |
| `openpst FILE.pst list FOLDER` | Message IDs, dates, attachment indicators, senders, and subjects. |
| `openpst FILE.pst show NID [text|html|rtf]` | Selected headers, the requested stored body, and attachment metadata; defaults to text. |
| `openpst FILE.pst body NID text|html` | Display body with HTML/RTF conversion fallbacks. |
| `openpst FILE.pst search "QUERY" [--body] [--limit N] [--no-deleted]` | Search results; no default limit. |
| `openpst FILE.pst searchdump "QUERY" [--body] [--limit N] [--no-deleted]` | Search results as TSV for comparison. |
| `openpst FILE.pst check` | Full supported scan-style integrity report; exits nonzero when findings exist. |
| `openpst FILE.pst verify` | Low-level CRC/structure report; exits nonzero when findings exist. |
| `openpst FILE.pst dump` | Folder/message/recipient/attachment summary data as TSV, used for Python/C comparisons. |
| `openpst FILE.pst att NID INDEX OUTFILE` | Save one attachment, creating/replacing the output file without editing the PST. |
| `openpst FILE.pst rtf2html BODY.rtf` | Convert an RTF file to HTML on stdout; use `-` for standard input. |
| `openpst FILE.pst rtfall OUTPUT_DIRECTORY` | Export converted HTML/text for RTF messages as `<NID>.html` and `<NID>.txt`; the output directory must already exist. |

All commands above open the PST read-only. Even `rtf2html` currently requires a PST filename because the CLI opens it before dispatching commands; the converter API itself needs no PST.

Editing/developer commands:

```text
openpst FILE.pst wmove NID[,NID...] DEST_FOLDER
openpst FILE.pst wcopy NID[,NID...] DEST_FOLDER
openpst FILE.pst wdel NID[,NID...]
openpst FILE.pst wpurge NID[,NID...]
openpst FILE.pst fcreate PARENT_FOLDER "New name" [CONTAINERCLASS]
openpst FILE.pst frename FOLDER "New name"
openpst FILE.pst fmove FOLDER DEST_PARENT
openpst FILE.pst fdelete FOLDER
openpst FILE.pst fpurge FOLDER
openpst DEST.pst wxcopy SOURCE.pst NID[,NID...] DEST_FOLDER
openpst FILE.pst wfix [--apply]
openpst FILE.pst rebuild
openpst FILE.pst wvalidate
```

`wmove`/`wcopy` move/copy messages; `wdel` moves them to Deleted Items or purges messages already there; `wpurge` always removes them permanently. Folder commands follow the equivalent semantics, with purge removing the subtree and its messages. `fcreate` defaults to container class `IPF.Note`. `wxcopy` copies source message IDs into an existing destination folder; it does not create the destination folder.

`wfix` reports unless `--apply` is supplied. `rebuild` rewrites all reachable folder tables. `wvalidate` reports writer structural validation and aborts its transaction. Every command in this second group opens the target writable, so even report-only `wfix` and `wvalidate` can recover an existing journal. The basic usage banner omits several implemented commands; the lists above follow the dispatch code.

The library also supports message read/flag changes and single/batch import, but this CLI currently has no commands for them.

### `tools/inspect_msg.c` — `inspect_msg`

Read-only developer inspector: `inspect_msg FILE.pst NID`. Prints node/block/parent IDs, property types and storage locations, subnodes, recipient/attachment tables, and attachment property contexts. Given a folder table NID, it instead reports table columns, row size/offsets, and sample row IDs. Set the `INSPECT_ROW` environment variable to a row ID to print that row's cells.

Python counterpart: portions of `pstcli.py props`, `pstdebug.py`, `pstdebug2.py`, and fixed-layout comparison probes. It reports one file's structures rather than comparing two files.

## C test programs

### `tests/test_basic.c` — `test_basic`

Self-checks for time/text conversion, Unicode folding, CRC-related utilities/compressed RTF, HTML/RTF rendering, result allocation, and argument/error handling. Setting `OPST_TEST_PST` additionally exercises read-only file opening, folders/messages, search, body conversion, and verification against that file. No PST edits. Registered with CTest as `basic`.

### `tests/test_formats.c` — `test_formats`

In-memory test vectors for permutation/cyclic block encryption and zlib/deflate decoding. Checks independent known outputs, round trips, decoding modes, short/truncated inputs, output bounds, and corrupted-input robustness. Does not require or edit a PST. Registered with CTest as `formats`.

### `tests/test_write.c` — `test_write`

Write API integration tests using a copy of the real PST supplied through `OPST_TEST_PST`. Exercises read-only refusals, folder protection/name rules, message/folder edits, read/flag state, import with recipients/bodies/attachments, checker/fixer behavior, and simulated crash/journal recovery. Skips when the environment variable is unset.

Creates/overwrites `opst_write_test.pst` and additional `opst_write_test2.pst` fixtures in the working directory, then removes generated fixtures/journals during cleanup. The original supplied file is read, not edited. Registered with CTest as `write`; related Python predecessor: transaction/stress checks in `pstselftest.py`.

### `tests/fuzz_rtf.c` — `fuzz_rtf`

Read-only converter robustness harness: `fuzz_rtf FILE.pst [MUTATIONS_PER_BODY] [MAX_BODIES]`. Extracts real message bodies, mutates input in memory, and runs text/HTML converters to exercise malformed RTF/HTML handling. Defaults to 20 mutations per body and 300 bodies. Intended for use with sanitizer builds; prints body/run counts. Built by CMake but not registered with CTest.

The native test directory also retains Python comparison/reference harnesses (`pydump.py`, `pyrtf.py`, `pysearch.py`, `pyops.py`, `pydamage.py`, and `test_ctypes.py`). They support validation of the C engine and are not separate C libraries.

## Feature definitions

This catalog describes the library's public capabilities and supporting engine features, including additions with no Python equivalent. CLI-only export/inspection/test features are defined in the command and test sections above. The nine individual repair features are defined in the R1–R9 table.

### File access, compatibility, and diagnostics

| Feature | What it does | Access and file effects |
| --- | --- | --- |
| Read-only access | Opens an existing archive for browsing, searching, extracting data, and supported checks. Does not roll back an interrupted write. | `opst_open(..., OPST_OPEN_READONLY, ...)`; `opst_close` releases the handle. |
| Writable access | Opens an existing editable archive and enables write operations. Recovers a pending journal before reading the archive. It does not create an empty PST. | `opst_open(..., OPST_OPEN_WRITE, ...)`; opening can change the PST through recovery. |
| Store and structural-folder identification | Returns the store display name and IDs for the hidden root, visible top-of-store/IPM root, and Deleted Items. Lets callers locate the browsing/editing starting points without assuming names. | `opst_display_name`, `opst_root_folder`, `opst_ipm_root`, `opst_deleted_items`; read only. |
| ANSI reading | Decodes older version-14/15 files with 32-bit storage pointers and their header/tree layouts. | Selected automatically by `op_ndb.c`; viewing only, with limited integrity verification. |
| Unicode 512-page reading/editing | Reads the conventional Unicode layout and supplies the storage format supported by all write operations. | Selected automatically for versions 23–35; reading alone makes no changes. |
| Unicode 4K-page reading | Reads larger-page Unicode archives and their larger/compressed data blocks. | Selected automatically for versions 36 and above; viewing only, with limited integrity verification. |
| Block encryption/decryption | Converts permutation- or cyclic-encoded data to readable bytes; encodes written data using the archive's existing encryption mode. Cyclic encoding uses the block ID as part of its key. | Internal `op_crypt_block`; mode 0 leaves bytes unchanged. Decoding does not edit the file. |
| Deflate decoding | Expands zlib-wrapped compressed data blocks so the normal property/message reader can consume them. | Internal `op_zinflate` in `op_inflate.c`; no PST writes. This differs from Outlook's compressed-RTF decoder. |
| Version/error reporting | Identifies the library version and provides a readable explanation of the most recent error on the current thread. Negative error codes distinguish I/O, format, unsupported data, missing objects, arguments, state, memory, and refused operations. | `opst_version`, `opst_last_error`; no PST writes. |
| Journal detection and recovery reporting | Tells a reader whether an interrupted-write journal exists and tells a writable caller whether recovery happened during open. | `opst_journal_pending`, `opst_recovered`; these getters only report status. |
| Low-level verification | Checks the supported format's header/tree/block integrity, CRCs, and block structure. For ANSI/4K, it currently reports only loaded tree-page CRC mismatches. | `opst_verify`; read only. Finding counts are separate from the function's execution status. |
| Full structural checks | Detects relationships that simple checksums cannot establish: missing/orphan blocks, wrong counts/parents, invalid ID/version metadata, attachment sizes, maps, and incomplete folders. | `opst_check`; read only and restricted to the Unicode 512-page layout. |
| Repair preview/application | Counts known R1–R9 issues, or fixes them in a writer transaction. Preview lets the caller show the prospective repairs before applying them. | `opst_fix(p, 0, ...)` reports; `opst_fix(p, 1, ...)` writes. |

### Browsing and message content

| Feature | What it does | Access and file effects |
| --- | --- | --- |
| Folder enumeration | Lists a folder's immediate children, sorted case-insensitively by name. A caller can recurse to build the complete tree. | `opst_folder_children`; read only. |
| Folder metadata | Returns name, parent ID, message/unread counts, and has-subfolders state for one folder. | `opst_folder_info_get`; read only. Counts come from stored metadata. |
| Folder-path lookup | Resolves slash-separated folder names below a caller-selected starting folder; a zero starting ID means the hidden root. | `opst_folder_find`; read only. |
| Fast message listing | Reads a folder's contents table rather than opening each full message. Returns IDs, subjects, sender/recipient display fields, flags, sent/received dates, size, importance, class/topic, attachment indication, and flag status. | `opst_messages`; read only. `OPST_MSGFLAG_READ`, `UNSENT`, and `HASATTACH` describe the corresponding stored flag bits. |
| Message handles | Opens one message's property/subnode data and owns cached content until closed. | `opst_msg_open`, `opst_msg_close`; read only. |
| Raw property access | Retrieves a property by numeric MAPI ID, preserving its MAPI type, byte length, and raw value for specialized consumers. | `opst_msg_prop`; read only. |
| String/integer property access | Decodes a string property to UTF-8, or reads an integer/bool/FILETIME property with a fallback value. Includes headers, addresses, subject, topic, class, dates, and other stored properties. | `opst_msg_str`, `opst_msg_i64`; read only. Subject decoding removes Outlook's leading prefix controls. |
| Body availability | Reports which of text, HTML, and RTF are actually stored, so callers can offer available viewing modes. | `opst_msg_bodies`; returns a bit mask, without synthesizing bodies. |
| Stored body extraction | Returns text/HTML as UTF-8 and RTF as decompressed RTF bytes. Requests a specific stored body without the display fallback sequence. | `opst_msg_body`; read only. Compressed RTF is decoded through the LZFu/MELA helper. |
| Display/indexing text | Chooses an existing plain-text body, otherwise strips HTML, otherwise extracts text from RTF. Useful for previews, indexing, and text viewing. | `opst_msg_text`; read only; can return no body. |
| Display HTML | Returns stored HTML, otherwise converts RTF while preserving formatting. Does not synthesize HTML from a text-only body. | `opst_msg_html`; read only; can return no body. |
| Recipient extraction | Returns recipients' display names, addresses, and To/Cc/Bcc type from the recipient table. | `opst_msg_recipients`; read only. |
| Attachment enumeration | Lists filename, MIME type, Content-ID, size, storage method, hidden status, and extraction index. Content-ID/hidden metadata lets an application identify inline pictures. | `opst_msg_attachments`; read only. |
| Attachment extraction | Resolves an attachment index to its raw payload bytes for saving or display. Embedded-message/object payloads are raw data rather than a separately unpacked message API. | `opst_attachment_data`; read only. Saving to disk is the caller's responsibility or the CLI's `att` command. |
| Standalone RTF-to-HTML | Builds an HTML page from RTF, preserving supported formatting, tables, links, and embedded pictures; de-encapsulates original HTML when present. | `opst_rtf_to_html`; no PST required or edited. |
| Standalone RTF-to-text | Removes RTF formatting while retaining readable content, including text from encapsulated HTML. | `opst_rtf_to_text`; no PST required or edited. |
| Standalone HTML-to-text | Removes HTML markup, converts tag boundaries to spacing, and decodes HTML entities to readable text. | `opst_html_to_text`; no PST required or edited. |

### Search features

All search features use `opst_search` and return message/folder IDs, folder paths, subjects, sender/recipient display fields, dates, flags, sizes, and attachment indicators. They do not change PST data.

| Feature | What it does |
| --- | --- |
| Case/accent-insensitive matching | Uses Unicode folding so spelling case and combining accents do not prevent a match. |
| Words, phrases, and exclusions | Requires all positive terms/quoted phrases to match and excludes a candidate when a negative term such as `-draft` matches. An empty query finds nothing. |
| Field filters | `from:`, `to:`, `cc:`, `subject:`, and `body:` confine a term to that field; ordinary terms search subject/sender/recipient fields. |
| Body search | `OPST_SEARCH_BODY` lets ordinary terms also match message text; explicit `body:` terms request body matching themselves. Bodies require opening candidate messages. |
| Attachment/read-state filters | `has:attachment` selects messages with attachments; `is:read` and `is:unread` select the stored read state. |
| Date filters | `after:YYYY-MM-DD` applies an inclusive lower date bound; `before:` applies an exclusive upper bound. Uses sent date, falling back to received date. Invalid dates return an argument error. |
| Folder filtering | `folder:TEXT` matches text within the folder path. The options struct can further restrict search to specific folder IDs. |
| Deleted Items exclusion | `OPST_SEARCH_SKIP_DELETED` omits Deleted Items and its descendants; otherwise they are eligible. |
| Result limits/order | `opts.limit` stops after that many hits; zero means unlimited. Results follow depth-first folder order, sorted folder names, then contents-table order. |
| Progress notifications | An optional callback reports the current folder index, total folder count, and path before each folder is searched. |
| Cancellation | A caller-supplied cancellation flag can stop the search; results already found are returned. This is separate from permission to use one PST handle concurrently. |

### Folder and message editing

All edits below require a writable handle. Ordinary operations commit through the journal as one transaction; cross-file copying has the separate copy/fixer sequence described above.

| Feature | What it does | Public access |
| --- | --- | --- |
| Special-folder protection | Identifies structural/default folders that cannot be renamed, moved, or deleted. Write guards also reject invalid names, conflicting names, and moves into a folder's own descendants. | `opst_folder_is_protected` reports protection; folder write APIs apply guards. |
| Folder creation | Adds a named child folder and its storage tables/hierarchy row. Accepts a container class, defaulting to `IPF.Note`; returns its new ID. | `opst_folder_create`. |
| Folder rename | Changes a permitted folder's display name and corresponding hierarchy data. | `opst_folder_rename`. |
| Folder move | Changes a permitted folder's parent and synchronizes old/new hierarchy metadata. | `opst_folder_move`. |
| Folder deletion | Moves a folder to Deleted Items on first deletion. If it is already inside Deleted Items, permanently removes it and its subtree; reports the mode and removed folder/message counts. | `opst_folder_delete`. |
| Folder purge | Permanently removes a permitted folder subtree and its messages regardless of its current location. | `opst_folder_purge`. |
| Message move | Moves supplied message IDs to a destination folder and updates contents rows, parents, and counts. | `opst_msgs_move`. |
| Same-file message copy | Creates new message IDs/rows in the destination while sharing reference-counted data blocks. Returns the new IDs when requested. | `opst_msgs_copy`. |
| Message deletion | Moves supplied messages to Deleted Items; messages already there are permanently removed. Reports separate moved/purged counts. | `opst_msgs_delete`. |
| Message purge | Permanently removes supplied messages from their current folders and releases their storage references. | `opst_msgs_purge`. |
| Read/unread updates | Changes each selected message's read flag and corresponding contents row, keeping folder unread totals consistent. `read=-1` leaves read state unchanged, `0` marks unread, and `1` marks read. | `opst_msgs_set_state`; no corresponding command in the current CLI. |
| Follow-up flag updates | Sets `PidTagFlagStatus`: `0` none, `1` complete, or `2` flagged; `flag=-1` leaves it unchanged. Can be combined with a read-state change in the same transaction. This updates status, not a reminder/due-date scheduling system. | `opst_msgs_set_state`; no corresponding command in the current CLI. |
| Cross-file message copy | Creates independent destination storage, keeps recipients/attachments, translates store-specific named properties, assigns fresh destination IDs/versions, and performs destination repair/index maintenance. | `opst_msgs_copy_to`; source/destination must be different archives, with a supported source format. |
| Named-property translation | Resolves property IDs above `0x7FFF` by GUID/name identity, reuses or adds destination identities, and remaps copied values rather than assuming IDs mean the same thing in both files. Unresolvable source identities are dropped. | Internal `op_npm.c`/`op_xcopy.c`, automatically used by cross-file copy. |

### Import and storage services

| Feature | What it does | Access and file effects |
| --- | --- | --- |
| Single-message import | Builds a new PST message from caller-supplied subject, sender, text/HTML bodies, headers, Message-ID, dates, importance, read state, recipients, and attachments, then files it into a folder and returns its ID. Class defaults to `IPM.Note`; omitted sent/received dates use the documented current/sent defaults. | `opst_msg_import`; writes. No original Python tool equivalent. |
| Batch import | Imports an array of messages into one folder, with one transaction and one contents-table rewrite. Returns an ID for each message; avoids the repeated table work of individual calls. | `opst_msgs_import`; writes. Keep batches bounded because payloads remain in memory until commit. |
| Recipient construction | Creates recipient rows and address-related properties from name, SMTP address, and To/Cc/Bcc type. | Part of plain-field import; writes in the import transaction. |
| Attachment/inline-image construction | Builds by-value attachments from filename, bytes, MIME type, optional Content-ID, hidden status, and modified time. Allows HTML bodies to refer to imported inline images through their Content-IDs. | Part of plain-field import; writes in the import transaction. |
| Large-value storage | Moves large bodies/property values into subnodes/data trees when they exceed heap-item limits, so large payloads can be imported and copied. | Internal builders; writes only within the enclosing edit/import transaction. |
| Object IDs and row versions | Generates/maintains object-ID map records and row versions used to relate folders/messages to their table rows. Deleted-object mappings are retained with zero NIDs. | Internal `op_ops.c`, used by edits and repairs. |
| Message-index/counter maintenance | Keeps Outlook's message index, highest-message metadata, and node-ID counters consistent as messages are created/copied/imported or repaired. | Internal operation/import/fixer modules; writes within their transactions. |
| Atomic write/rollback | Buffers changes, saves overwritten bytes to a journal, writes the updated storage/header, flushes, and removes the journal after success. Aborts discard pending changes; interrupted committed writes can be rolled back on the next writable open. | Internal writer, automatically used by public edits. |
| Allocation/reference management | Allocates/reuses blocks, updates B-trees and free-space maps, increments references to shared storage, and frees blocks when no longer required. | Internal `op_wr.c`; writes at commit. |
| Automatic file growth | Adds whole allocation sections when current free space cannot satisfy an allocation, preserving format-specific allocation-map pages. | Internal writer; increases file size at commit, subject to its configured size limit. |
| Time conversion | Converts stored 100-nanosecond Windows FILETIME values to Unix seconds or UTC ISO text for application display. | `opst_filetime_to_unix`, `opst_filetime_to_iso`; no PST writes. |
| Result-memory cleanup | Frees owned folder/message-row/recipient/attachment/search arrays and standalone converter buffers. Prevents consumers from needing to know the library's allocation layout. | `opst_free_folders`, `opst_free_messages`, `opst_free_recipients`, `opst_free_attachments`, `opst_free_hits`, `opst_free`; no PST writes. |
| Application-language integration | Exposes opaque handles and plain fixed-width structs so native code, Python `ctypes`, and .NET P/Invoke can call the same engine. | Public header/shared library; the existing C# wrapper uses these functions. |
| Portable/single-file embedding | Builds on Windows/POSIX through the platform layer, or packages engine source/header into an amalgamation for embedding in another project. | CMake or `tools/amalgamate.py`; building/generation creates outputs but does not edit PSTs. |

## Python-to-C replacement map

| Python tool/module | C replacement or closest equivalent |
| --- | --- |
| `pstcore.py` | `op_ndb.c`, `op_ltp.c`, `op_msg.c`, `op_util.c`, and `op_platform.c`. |
| `pstcore2.py` | CRC/cipher helper responsibilities appear in `op_util.c`, `op_ndb.c`, and `op_mpbb.inc`; it has no separate C module. |
| `pio.py` | `op_platform.c`. |
| `pstcli.py` | `openpst` read commands; `inspect_msg` supplies lower-level property inspection. Attachment export uses `att` for one attachment at a time. |
| `pstsearch.py` | `op_search.c`, `opst_search`, and CLI `search`/`searchdump`. |
| `pstrtf.py` | `op_rtf.c` converter APIs and CLI `rtf2html`/`rtfall`. |
| `pstwrite.py` | `op_wr.c` and writer declarations in `op_wr.h`. |
| `pstedit.py` | `op_edit.c`, `op_edit2.c`; CLI `rebuild` covers rebuilding all folder tables. |
| `pstops.py` | `op_ops.c`/`op_api_w.c`; CLI `wmove`, `wcopy`, `wdel`, `wpurge`. |
| `pstfolders.py` | `op_folders.c`/`op_api_w.c`; CLI folder commands. |
| `pstactions.py` | Public write API in `op_api_w.c`, plus cross-file/repair APIs in their own modules. |
| `pstidmap.py` | ID-map implementation inside `op_ops.c`. |
| `pstnpm.py` | `op_npm.c`. |
| `pstxcopy.py` | `op_xcopy.c`, `opst_msgs_copy_to`, and CLI `wxcopy`. |
| `pstfix.py` | `op_fix.c`, `opst_fix`, and CLI `wfix`. |
| `pstcheck.py` | `op_check.c`, `opst_check`, and CLI `check`. |
| `pstgrow.py` | Internal writer growth in `op_wr.c`; no standalone grow CLI. |
| `pstselftest.py` | `test_basic`, `test_write`, and writer validation hooks; no identical synthetic-test CLI. |
| `pstltp.py` | Reader/writer heap parsing and validation supply related checks; no dedicated heap scan/calibration CLI. |
| `pstprobe.py` | Header/format parsing in `op_ndb.c`; no equivalent detailed header-probe CLI. |
| `pstdebug.py`, `pstdebug2.py` | Error reporting, `inspect_msg`, and validation provide related diagnostics; no exact traceback/cell-patching equivalent. |
| `pstdiff.py`, `cmp_bak.py`, `cmp_nodes.py`, `c01.py`, `probe_maps.py`, `mkvariants.py` | No direct C CLI equivalents. The native library provides storage/inspection building blocks; the Python scripts remain specialized comparison/investigation utilities. |
| `pstgui.py` | No C GUI program. OpenOutlook's application consumes the library through the C# wrapper in `src/OpenOutlook.PstNative/OpenPst.cs`. |

Additional native functionality beyond this replacement map includes plain-field message import, read/flag updates, deflate decoding, and ANSI/4K/cyclic-format reading. The C# wrapper and application are consumers of OpenPST, not additional C engine modules.
