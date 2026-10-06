# Hotmail mirror: ScanPST root cause confirmed (2026-10-06)

`rmarrash_hm.pst` now returns **NO_ERRORS**, with 19 folders and 1,202 items, after three repair passes. The supplied `rmarrash_hm_fix.pst` also returns NO_ERRORS and was not repaired or modified. All repairs disabled ScanPST backups. Original and intermediate PSTs, logs, experiment scripts, hashes and JSON diffs are kept under the git-ignored `.local/scanpst-2026-10-06/` directory. No message bodies or recipient addresses are included here.

## Cause of the original minor verdict

The contents-table row and the message property context contain identical long `PidTagDisplayTo` (0x0E04) text, but refer to **different data trees**. For this value, ScanPST expects the row to share the message property's data tree. Value equality and individually correct reference counts are insufficient.

| Reference | Original | Supplied repaired file |
|---|---|---|
| Message NID 0x2052C4, DisplayTo subnode 0x13FFF | BID 0xCDB2 | BID 0xCDB2 |
| Contents table 0x806E, cell subnode 0x1B7FF | BID 0x1D092 | BID 0xCDB3 (same BBT key as 0xCDB2) |
| Shared tree BBT reference count | Separate trees, each cRef=2 | One tree, cRef=3 |
| DisplayTo value | 9,564 bytes / 4,782 UTF-16 code units | Identical |
| Row 0x2052C4, PidTagLtpRowVer (0x67F3) | 1,884 | 6 |

BID bit 0 must be masked when comparing storage identity. The shared XBLOCK has two leaves of 8,176 and 1,388 bytes. Only its top block receives the extra reference; the child blocks still have one parent and cRef=2.

`pstdiff.py` compares reconstructed node data and subnode contents. It consequently reports only the row-version change and hides the important transition from independent storage to shared storage. Neither file has added/removed nodes or changed message text. The raw comparison of the supplied pair finds 1,378 differing bytes in their common range and a 507,904-byte size increase; most physical changes are bookkeeping, tree rewrites and allocation maps.

## Controlled experiments

Each test was scanned independently with Office ScanPST 16.0.17932.21000, using copies. All verdicts retained 19 folders and 1,202 items.

| Experiment | ScanPST verdict |
|---|---|
| Untouched original | MINOR |
| Original with only row version changed from 1,884 to 6 (and block CRC corrected) | MINOR |
| Supplied clean file with only row version restored to 1,884 | NO_ERRORS |
| Original with the table's large-value subnode referencing the message's data tree, correct reference counts, and independent tree released; row version unchanged | NO_ERRORS |
| Supplied clean file with the table value cloned into an independent tree again; row version unchanged | MINOR |

The last two tests retain identical logical node bytes. Both pass the reference checker with zero orphans, mismatched counts or missing blocks. This establishes storage sharing as the cause in this archive, and rules out the row-version value as the cause. The low row version is a side effect of ScanPST replacing the row.

The existing notes' 1,024-character threshold remains useful evidence from earlier synthetic tests, but this investigation does **not** establish a universal storage rule for every property, size or heap/subnode combination. In particular, blindly sharing arbitrary equal values, truncating recipients, or forcing every long value into subnodes is not justified by this test.

## Repair sequence on rmarrash_hm.pst

| Input snapshot | Scan result | Repair result |
|---|---|---|
| pass00, 158,737,408 bytes | MINOR, no flagged log lines | Shares DisplayTo data tree, changes row version; output pass01 is 158,991,360 bytes |
| pass01 | ERRORS: AMap at 158,737,408 has csFree 7, expected 255 | No logical node changes; output pass02 is 159,245,312 bytes |
| pass02 | ERRORS: AMap at 158,991,360 has csFree 120, expected 255 | No logical node changes; output pass03 remains 159,245,312 bytes |
| pass03 | **NO_ERRORS** | No repair needed |

The allocation-map findings arose **after** ScanPST's repairs, consistent with design rule 20. They were not findings on the original file. Pass01-to-pass02 changes 34 bytes in the common range plus one appended allocation section; pass02-to-pass03 changes 22 bytes and does not grow the file.

Original SHA-256: `e47a5187c1f4ea074ccda535d0f7f8bc7ef30fc60b4f77a203f644ae36819cd2`.

Final repaired SHA-256: `699b72cfa2d56bc2640fd51dbb87a13f66ae580610377651a89bd61f383e34c9`.

Supplied fixed-file SHA-256, unchanged: `9c5351e70dbb87dd680b9b5ff8213050139b9494b0008c5e05e04e66045357ca`.

## Writer changes indicated by the evidence

This investigation repairs the requested archive and adds diagnostic tooling; it does **not** change the native engine yet.

`op_import.c` builds the message's external property value, then supplies the same text as a separate row value. `tc_build_ex` and `tcbig_put` in `op_edit2.c` allocate independent storage for large table cells. `ed_store_tc` rebuilds all large cell subnodes, so fixing only the first import would be insufficient: later table rewrites could undo the sharing.

The engine correction should preserve a matching message property's external data-tree reference when serializing its large contents-table cell, increment the top block's reference count, and release any obsolete independent tree. It must preserve ownership through table rewrites, copy/move, deletion, repair, journal rollback and reopening. The isolated Python experiment demonstrates that the existing storage engine can represent the correct relationship without changing the mail text or row version.

Before merging that correction, test import and subsequent row rewrites around the reported 1,023/1,024-character boundary, the heap-allocation threshold, and the multi-block boundary, on both Windows and Linux. ScanPST should validate the resulting synthetic files as well as a repaired copy of this archive. The checker/fixer should eventually detect this semantic relationship; the ordinary block-reference checker correctly considers both separate and shared representations structurally valid.

## Repeatable tools

`tools/python/run_scanpst2.ps1` now starts hidden, targets only its own process with Win32 messages, and does not use mouse, keyboard, clipboard or foreground focus. `-Repair` clicks an enabled backup checkbox off, verifies it is unchecked, waits for "Repair complete", and acknowledges OK before the next file. If repair completion cannot be confirmed, it leaves that process running and stops the batch rather than killing an active writer. Dialog text matching is English-language specific. A repair result is not a clean-scan verdict: always run a separate verification scan.

```powershell
& tools/python/run_scanpst2.ps1 -Files @('experiment.pst') -Repair -Out .local/verdicts.txt
& tools/python/run_scanpst2.ps1 -Files @('experiment.pst') -Out .local/verdicts.txt
python tools/python/pststoragediff.py before.pst after.pst --binary
```

The new read-only `pststoragediff.py` reports logical streams, table-cell changes, block-sharing relationships and optional raw-byte counts. It emits sizes, hashes and numeric properties rather than message text. Its results were checked against the real-file diff, an identity comparison and both sharing-isolation experiments. It supports the Unicode formats handled by `pstcore.PST`.

Microsoft also documents silent command-line scans, which worked here for analysis, but the UI-message driver was used for repairs to explicitly verify the no-backup setting and retain the exact MINOR / ERRORS / NO_ERRORS verdict: [ScanPST multiple passes and command-line options](https://learn.microsoft.com/en-us/microsoft-365-apps/outlook/data-files/scanpst-exe-runs-multiple-passes).
