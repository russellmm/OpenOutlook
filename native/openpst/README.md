# OpenPST (C library)

A from-scratch C11 library for Outlook **Unicode PST** files (MS-PST, wVer 23), written from the specification
(`ms-pst.pdf`, v20250218). No third-party code, no dependencies beyond the C runtime (POSIX `iconv` is used for rare
non-Western ANSI code pages on Linux; Windows uses `MultiByteToWideChar`). Builds on Linux and Windows
(GCC, Clang, MSVC, MinGW). MIT licensed.

**Status (v0.3.0): reader, search, RTF/HTML conversion and the in-place writer** (create / rename / move / delete folders,
copy / move / delete messages, copy between files with named-property translation, repair of the "minor inconsistencies"
SCANPST reports, scan-style checks). The writer is a port of the Python implementation (`pstwrite.py`, `pstedit.py`,
`pstops.py`, `pstfolders.py`, `pstidmap.py`, `pstnpm.py`, `pstxcopy.py`, `pstfix.py`, `pstcheck.py`) and produces
**byte-identical files** (see "Writer" below). Files are edited in place, never rewritten; **close Outlook first and work on
copies of anything you care about.**

## Verified

* Output of the C reader compared with the Python reference reader (`tests/pydump.py`) over every folder and message
  (flags, dates, sizes, subject, sender, text/HTML/RTF body lengths, recipients, attachment names and sizes):
  `rmarrash_2.pst`, `test_X2_fix.pst`, `rmarrash_1_clean.pst` (2,793 messages) and `rmarrash_3.pst` (13,410 messages)
  - **no differences**. A full dump of the 2.6 GB file takes about 18 s, of the 910 MB file about 2 s.
* GCC `-Wall -Wextra` clean, AddressSanitizer + UBSan clean, unit tests (`tests/test_basic.c`).
* ABI through the shared library with Python `ctypes` (`tests/test_ctypes.py`) - the same calls a P/Invoke wrapper makes.
* Windows: built with MSVC 2022 x64 (`/W4`, no warnings) and run natively; the dump of `rmarrash_1_clean.pst` is
  byte-identical to the Python reader's. Also built with GCC 15 + ASAN/UBSAN under WSL.
* C1b (search, RTF -> HTML/text) against the Python code, over the same two real mailboxes:
  * RTF -> HTML and RTF -> text for all 6,869 RTF messages (2,447 + 4,422): identical to `pstrtf.rtf_to_html` /
    `pstcore.rtf_to_html_or_text` except where the C code is deliberately better (emoji written as two `\uN`
    surrogate escapes are joined instead of becoming `??`; `font-family:'Name'` uses single quotes - the Python
    version put double quotes inside a double quoted attribute, now fixed there too; e-mail addresses in `<...>` survive
    in the plain text of RTF messages).
  * `opst_search` vs `pstsearch.Searcher`: the same hits (compared as sets) for 20 queries covering every operator, with
    and without body search (`tests/pysearch.py`).
  * `tests/fuzz_rtf.c` (damaged RTF/HTML bodies, 16,000 runs, under ASAN/UBSAN): no memory errors.

## Writer (v0.3)

    opst *p;
    opst_open("copy_of_mail.pst", OPST_OPEN_WRITE, &p);          /* rolls back a leftover journal first */
    uint32_t f;
    opst_folder_create(p, opst_ipm_root(p), "Receipts 2026", NULL, &f);
    opst_msgs_copy(p, nids, n, f, new_nids);                     /* or opst_msgs_move / _delete / _purge */
    opst_folder_delete(p, f, &permanent, &stats);                /* Outlook semantics: Deleted Items first, then for good */
    opst_msgs_copy_to(src, nids, n, p, f, new_nids);             /* from ANOTHER file: named properties translated */
    opst_fix(p, 1, &report);                                     /* apply the known repairs */
    opst_check(p, &chk, text, sizeof text);                      /* scan-style checks, read only */

* **Every call is one atomic transaction.** Changes are collected in memory; before the first byte of the file changes the
  original bytes of every region to be overwritten are saved to `<file>.journal` (flushed). Commit order: journal, header with
  fAMapValid = 0, data / pages / allocation maps, final header, flush, delete journal. A crash at any point is rolled back the
  next time the file is opened for writing - verified by simulated crashes at four points of the commit (also after the file had
  grown by 176 MB: size and bytes are restored exactly). The journal format is the Python writer's, so either can recover the other's.
* Pages / blocks are updated in place; B-tree pages split and merge, the allocation maps (AMap / PMap / FMap / FPMap / DList) are
  kept consistent and the file grows by whole 16-section steps (about 4 MB) when it is full (limit 16 GB).
* Refused operations change nothing: a read-only handle (`OPST_E_STATE`), special folders (`OPST_E_REFUSED`: root, Top of data file,
  Inbox, Deleted Items, ... by the ids recorded in the store and by English name), a folder into its own subfolder, duplicate or invalid names.
* `opst_journal_pending()` tells a *reader* that an interrupted write left a journal (the file may be half written).

### Verified (writer)

* **Byte-for-byte equal to the Python writer.** Test harness (`tests/pyops.py` runs the Python side): the same command list is run
  with the C tool on one copy and with Python on another, and the files are compared after every step. With `OPST_TEST_RANDOM=1` both
  sides draw the same "random" ids, so even copies (new ids) compare. Results, all identical: rebuild of every folder table
  of `rmarrash_1_clean.pst` (60 tables); 15 steps on `rmarrash_2.pst` (copy, move, delete, purge, create / rename / move /
  delete / purge folder); 13 steps on `rmarrash_1_clean.pst` (868 MB, 300-message copy, 40-message moves, deletes, folders);
  cross-file copies from `rmarrash_1_clean.pst` and `rmarrash_3.pst` (named properties, attachments, name map rebuilt); a
  file-growing copy of 8 messages of 15-24 MB (175 MB added; 3,000-block data trees); the fixer on a deliberately damaged
  file (`tests/pydamage.py`: all five rules, counts and bytes equal).
* The same step lists run on **Linux (GCC 15, ASAN + UBSAN)** produce files identical to the Windows (MSVC) ones.
* `tests/test_write.c`: 85 checks on a copy (read-only refusal, protection, name rules, folder and message operations, counts
  and read-back, crash recovery at four points, check results). `opst_check` reports the same findings as `pstcheck.py`.
* MSVC `/W4`, GCC `-Wall -Wextra`: no warnings (also for the single-file build).

### Differences from the Python code (fixes found while porting)

* `pstnpm.py` computed the bucket key of string names with `zlib.crc32`; Outlook uses the PST CRC (no inversion) - rebuilt buckets
  now equal the stored ones (212 buckets, 157 string names checked). **Fixed in `pstnpm.py` too** (backup `pstnpm.py.bak_c2`):
  before, a cross-file copy rewrote every string-name record under the wrong key.
* New named property ids are assigned in ascending source-id order (Python used set order) - `pstxcopy.py` patched likewise.
* A name map whose values no longer fit in the first heap block (many new names) used to fail ("heap id mismatch"); the C code lets
  the heap use further blocks.
* `pstcheck nids` guessed that a header counter was NID-shaped when its low bits equalled the type (false alarms, e.g. folder index
  0x1000d); both checkers now read it as a raw index (`pstcheck.py` patched, backup `.bak_c2`).
* Row versions needing renewal are written in ascending table order (`pstfix.py`, was set order).

## Build

CMake (recommended):

    cmake -S . -B build -DCMAKE_BUILD_TYPE=Release
    cmake --build build
    ctest --test-dir build             # unit tests;  OPST_TEST_PST=some.pst adds file based checks
    ./build/openpst some.pst tree      # command line tool (read commands: info tree list show body search att check verify dump)
    # write commands (use a COPY): wmove wcopy wdel wpurge fcreate frename fmove fdelete fpurge wxcopy wfix [--apply]

Tests: `OPST_TEST_PST=small.pst ctest --test-dir build` also runs the write tests on a copy of that file.
Options: `-DOPST_BUILD_SHARED=OFF`, `-DOPST_BUILD_TOOLS=OFF`, `-DOPST_BUILD_TESTS=OFF`, `-DOPST_SANITIZE=ON`.
Outputs: `libopenpst.a` (static), `libopenpst.so` / `openpst.dll` (shared, only the `opst_*` functions are exported), `openpst` (CLI).

Single file instead (copy two files into any project):

    python3 tools/amalgamate.py        # writes dist/openpst.c and dist/openpst.h
    cc -std=c11 -O2 -c dist/openpst.c

For a DLL/shared object from the amalgamation add `-DOPST_SHARED -DOPST_BUILD`.

## Search and text conversion (v0.2)

    opst_search_opts o = {0};  o.size = sizeof o;  o.flags = OPST_SEARCH_BODY;  o.limit = 2000;
    opst_hit *hits; size_t n;
    opst_search(p, "from:anna \"final invoice\" -draft has:attachment after:2024-01-31", &o, &hits, &n);
    for (size_t i = 0; i < n; i++) printf("%s: %s\n", hits[i].folder_path, hits[i].subject);
    opst_free_hits(hits);

Query syntax (case- and accent-insensitive, all terms must match): words, `"exact phrases"`, `-word`,
`from:` `to:` `cc:` `subject:` `body:`, `has:attachment`, `is:unread` / `is:read`, `after:YYYY-MM-DD`, `before:YYYY-MM-DD`,
`folder:NAME`. Row level fields come from the contents tables, so a search of the 2.6 GB / 13,000 message file takes
0.2 s without and about 1 s with message bodies. `opts` may be `NULL`; it can restrict the folders, skip Deleted Items,
set a limit, report progress, and be cancelled from another thread (`cancel` points to an int32 the other thread sets).

    opst_msg_text(m, &len)      plain text of a message (text body, else HTML or RTF with the formatting removed)
    opst_msg_html(m, &len)      HTML for display (the HTML body, else the RTF body converted)
    opst_rtf_to_html / opst_rtf_to_text / opst_html_to_text     the converters, usable without a PST file

The RTF converter keeps bold/italic/underline/strike, super/subscript, font names and sizes, colours, alignment,
paragraphs, tables, hyperlinks, embedded PNG/JPEG pictures, code pages and `\uN`; RTF that encapsulates HTML
(`\fromhtml1`) is de-encapsulated instead. Unknown groups are skipped, so one odd group never loses the body.

## Using it from C

    #include "openpst.h"
    opst *p;
    if (opst_open("mail.pst", OPST_OPEN_READONLY, &p)) { fprintf(stderr, "%s\n", opst_last_error()); return 1; }
    opst_folder_info *kids; size_t n;
    opst_folder_children(p, opst_ipm_root(p), &kids, &n);        /* Inbox, Sent Items, ... sorted by name */
    for (size_t i = 0; i < n; i++) {
        opst_msg_row *rows; size_t nr;
        opst_messages(p, kids[i].nid, &rows, &nr);               /* fast: reads the folder's contents table */
        for (size_t k = 0; k < nr; k++) {
            opst_msg *m;
            opst_msg_open(p, rows[k].nid, &m);
            size_t len; const char *html = opst_msg_body(m, OPST_BODY_HTML, &len);   /* UTF-8, owned by m */
            opst_msg_close(m);
        }
        opst_free_messages(rows);
    }
    opst_free_folders(kids);
    opst_close(p);

Rules: opaque handles; all strings UTF-8; functions return `OPST_OK` (0) or a negative `OPST_E_*` code and
`opst_last_error()` gives a message (per thread); arrays are released with the matching `opst_free_*`; strings
returned by `opst_msg_*` belong to the message handle and stay valid until `opst_msg_close`; times are FILETIME
(`opst_filetime_to_unix`, `opst_filetime_to_iso`); a handle must not be used from two threads at once (separate
handles are independent). Close messages before closing the file.

## Using it from C# (.NET 8 / Avalonia)

`bindings/csharp/OpenPst.cs` is a ready-made P/Invoke wrapper (`PstFile`, `PstMessage`, plain records for folders,
rows, recipients and attachments). Put `openpst.dll` / `libopenpst.so` next to the executable:

    using var pst = new OpenPst.PstFile(path);
    foreach (var f in pst.Children(pst.IpmRoot)) Console.WriteLine($"{f.Name} ({f.ContentCount})");
    using var msg = pst.OpenMessage(nid);
    var html = msg.Body(OpenPst.PstBody.Html) ?? msg.Body(OpenPst.PstBody.Text);

The wrapper compiles with the .NET 8 SDK and is exercised against the MSVC-built `openpst.dll` (folders, messages, bodies,
recipients, attachments, `Search`, `Text()`, `Html()`, `PstText`). Search from C#:

    var hits = pst.Search("from:anna invoice", PstSearchFlags.Body, limit: 2000, cancel: token);
    string html = msg.Html() ?? msg.Text() ?? "";      // RTF-only messages are converted

## Notes on behaviour (same as the Python reader)

* Subject: Outlook's `\x01` prefix control characters are removed. Sender in message lists falls back
  PidTagSenderName -> PidTagSentRepresentingName -> display-to.
* Trailing NUL padding is trimmed from strings.
* RTF is returned decompressed as bytes (7-bit RTF with `\'xx` escapes); HTML as UTF-8 (converted from the message code page).
* Not supported (clean error code): ANSI PST (wVer < 23), 4K-page PST (wVer >= 36), cyclic encryption.
* Embedded-message attachments (method 5) are returned as their raw object bytes, not unpacked.

## Layout

    include/openpst.h        public API
    src/                     op_platform (IO, errors) | op_util (text, CRC, LZFu, lists, time) | op_ndb (blocks, B-trees)
                             | op_ltp (heap, BTH, property/table contexts) | op_rtf (RTF/HTML conversion)
                             | op_search (query parser, Unicode folding, search) | op_msg (folders, messages, public API)
                             writer: op_wr (transactions, journal, allocation maps, B-trees) | op_edit, op_edit2 (heap / table /
                             property context building, node editing) | op_ops (messages, ID map, message index) | op_folders
                             | op_npm (name-to-id map) | op_xcopy (copy between files) | op_fix (repair) | op_check | op_api_w (public API)
                             op_foldtab.inc / op_htmlent.inc: generated tables (tools/gen_foldtab.py, tools/gen_htmlent.py)
    tools/openpst_cli.c      CLI + example;   tools/amalgamate.py   single-file builder
    tests/                   test_basic.c, test_write.c, test_ctypes.py, fuzz_rtf.c; pydump.py, pyrtf.py, pysearch.py, pyops.py, pydamage.py (Python references)
    bindings/csharp/         OpenPst.cs
    dist/                    generated single-file build

## Next stages

A C GUI if wanted ("library first, GUI later"). Open items: subnode trees deeper than one SLBLOCK, property contexts with more than ~400
properties, files above 16 GB. (The key of Outlook's message-index buckets is solved: `0xFFFF0000 xor` the GUID bytes of the conversation
index, byte j shifted left by 15 - j; files written by the C tool analyze as "no errors" in SCANPST.)
