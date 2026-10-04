/* op_test.c - internal test hooks used by the command line tool (not part of the public API). */
#include "op_wr.h"

typedef struct { uint32_t *nid; size_t n, cap; } nidlist;

static int walk_folders(opst *p, uint32_t folder, nidlist *out, int depth) {
    static const unsigned types[3] = {0x0D, 0x0E, 0x0F};
    uint32_t base = folder & ~0x1Fu;
    for (int i = 0; i < 3; i++) {
        if (op_nbt_find(p, base | types[i])) {
            if (out->n == out->cap) {
                size_t nc = out->cap ? out->cap * 2 : 64;
                uint32_t *t = (uint32_t *)realloc(out->nid, nc * sizeof *t);
                if (!t) return OPST_E_NOMEM;
                out->nid = t; out->cap = nc;
            }
            out->nid[out->n++] = base | types[i];
        }
    }
    if (depth > 64) return 0;
    opst_folder_info *kids; size_t nk;
    int rc = opst_folder_children(p, folder, &kids, &nk);
    if (rc) return 0;
    for (size_t i = 0; i < nk && !rc; i++) rc = walk_folders(p, kids[i].nid, out, depth + 1);
    opst_free_folders(kids);
    return rc;
}

/* loads and stores every folder table (like pstedit.py rebuild all) in one transaction */
int op_test_rebuild(opst *p, int *count) {
    nidlist l = {0, 0, 0};
    int rc = walk_folders(p, OP_NID_ROOT, &l, 0);
    if (rc) { free(l.nid); return rc; }
    opw *w = NULL;
    rc = op_txn_begin(p, &w);
    for (size_t i = 0; i < l.n && !rc; i++) {
        tctx tc;
        rc = ed_load_tc(w, l.nid[i], &tc);
        if (!rc) { rc = ed_store_tc(w, l.nid[i], &tc); tc_free(&tc); }
    }
    if (!rc) rc = op_txn_commit(p);
    else op_txn_abort(p);
    if (count) *count = (int)l.n;
    free(l.nid);
    return rc;
}

/* structural validation of the current file (needs a writable handle) */
int op_test_validate(opst *p, char **report, size_t *nproblems) {
    opw *w = NULL;
    int rc = op_txn_begin(p, &w);
    if (rc) return rc;
    bbuf b = {0};
    rc = opw_validate(w, &b, nproblems);
    op_txn_abort(p);
    if (rc) { bb_free(&b); return rc; }
    bb_u8(&b, 0);
    *report = (char *)b.p;
    return 0;
}
