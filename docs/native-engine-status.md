# Native PST engine: what was built and how it was validated

Branch `native-engine-phase0`, merged to `main`. The app (Avalonia, .NET 8) now reads and writes Outlook Unicode PST files through **OpenPST**, a from-scratch C library vendored in `native/openpst/`. The original managed engine was removed. Reference docs: `Ctools.MD` (C modules), `PythonTools.MD` (Python reference tools), `OpenOutlook_Design_Document.md` (every file-format rule found, with how), `OpenOutlook_Avalonia_Integration_Plan.md` (phases, decisions).

## Phases delivered

| Phase | What |
|-------|------|
| 0 | C source vendored, CMake + scripts (`scripts/build-native.*`), `OpenOutlook.PstNative` binding, `IPstEngine` seam |
| 1 | Native read engine: Unicode-512, ANSI, 4K-page (OST, zlib-compressed blocks), cyclic encryption |
| 2 | Always-edit mode: native writer, journal + `.lck`, write-behind, read-only fallback only when locked; `.bak` is opt-in |
| 3 | Import (EML files / folders, drag and drop), cross-archive copy and move, folder ops |
| 4 | Reading-pane options (space-bar paging, portrait full-screen, always-preview), Options backup toggle, Check and Repair window |
| after | Headless UI tests, soak harness, SCANPST-driven corrections (below) |

Other app work: reading pane with HTML / Rich Text / Plain Text / Headers views; embedded web view is removed at startup on Linux when WebKitGTK is missing (no crash dialog).

## How correctness is judged

Microsoft's SCANPST.EXE is the oracle. Method: repair a throwaway copy repeatedly, diff the nodes between passes (`tools/python/pstdiff.py`, `run_scanpst2.ps1 -Repair`), turn each difference into a rule, a check and (where possible) a fixer rule. Files can need more than three passes.

## Checks (`opst_check`, Python `pstcheck.py`)
refs, nids, tables, idmap, xblocks, rowvers, subnodes, rowcells (incl. row completeness and conversation id), amap, folders.

## Fixer rules (`opst_fix`, Python `pstfix.py`)

| Rule | Fixes |
|------|-------|
| R1 | rows without ID cells (+ ID map record); root-table normal folders included |
| R2 | ID-map records of deleted nodes |
| R3 | messages missing from the message index |
| R4 | duplicate / too-high row versions |
| R5 | header NID counters too low (every node type) |
| R6 | rows without the row-only cells 0x0E17 / 0x3013 |
| R7 | blocks not marked allocated in the AMaps; cbAMapFree recomputed |
| R8 | incomplete folders (tables, parent row); hierarchy rows that do not mirror their folder |
| R9 | orphan blocks; wrong BBT reference counts |
| R10 | contents rows vs message: size, flags, delivery time, missing cells, conversation id (C library only) |

Results on the owner's original files: `rmarrash_2.pst` 15 SCANPST-flagged items -> NO_ERRORS in one fixer pass; `rmarrash_1.pst` 215 flagged -> 0 (minor only before R10 was complete).

## Engine defects found by testing, and fixed
Found by the soak harness and SCANPST probing (rules 27-35 in the design document): rows moved/copied/imported into tables with more columns were incomplete; message size was estimated (~350 bytes short) instead of measured; conversation index header held the wrong time bits; random conversation id instead of MD5(upper(topic)); empty display-cc written; message-index second bucket key missing; AMap free-space double counting in the writers; folders created by early code lacked tables.

## Test tooling

| Tool | Purpose |
|------|---------|
| `tools/PstSoak` | Seeded random operations (read/flag, move, copy, delete, import, folder create/delete, cross-archive copy, reopen) on a **clean** copy; checker after every operation; refuses a non-clean start; `--keep` the result for SCANPST |
| `tools/PstProbe` | Imports messages that differ in one field each, so SCANPST's rewrites can be attributed to a field |
| `tests/OpenOutlook.HeadlessTests` | Avalonia.Headless drives the real window (open, read, space-bar, portrait, repair window) and saves screenshots; needs `OPENOUTLOOK_TEST_PST` |
| `native/openpst/tests` | `test_write` (damage hooks reproduce each historical defect), `test_basic`, `test_formats` |
| `tools/python` | Python reference engine, checker, fixer, SCANPST driver |
| `scripts/wsl-*.sh` | Build and test on Linux in WSL; the app also starts under Xvfb |

Run a soak: `PstSoak clean.pst --second clean2.pst --seed 1 --ops 300 --keep out.pst`, then scan `out.pst` with SCANPST. Both inputs must be SCANPST-clean first.

## Status
- 8 soak seeds (300-400 operations): no checker findings; SCANPST NO_ERRORS on every result.
- Tests: `test_write` 168 checks pass; .NET 417 pass, 6 known Windows-only failures (5 unix-permission cache tests, 1 loopback socket test); 4 headless UI tests pass.

## Known limits / next
- Writing is Unicode-512 only; ANSI and 4K files open read-only.
- Topic upper-casing for the conversation id covers ASCII, Latin-1, Latin Extended-A, Greek, Cyrillic; other scripts untested against SCANPST.
- Old imports keep their old conversation index (the fixer repairs rows, not already-written indexes).
- R10 and the amap/folders checks are not in the Python fixer/checker for every case.
- Soak does not yet cover folder rename/move.
- The 3 tables of `test-archive.pst` (early-writer damage) are not rebuilt by any rule.
- Roadmap (plan section 11): more ribbon features, Graph write, 1 GB perf pass, fuzzing/ASAN CI, packaging.
