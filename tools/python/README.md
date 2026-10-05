# Python reference tools

The original Python implementation of the PST engine, used as a reference and oracle for the C library in `native/openpst`.

- `pstcore.py`, `pstltp.py`, `pstwrite.py`, `pstedit.py`, `pstops.py`, ... : read/write engine
- `pstcheck.py` / `pstfix.py` : structural checker and fixer (same rules as `opst_check` / `opst_fix`; see OpenOutlook_Design_Document.md)
- `pstdiff.py`, `cmp_nodes.py`, `cmp_bak.py` : compare two files / passes
- `pstgui.py` : the original Tk GUI (reading pane reference)
- `run_scanpst.ps1`, `run_scanpst2.ps1 -Repair` : drive Microsoft SCANPST.EXE on a throwaway copy (repair-and-diff)

Run them from this folder. Never point them at a real archive; work on copies.
