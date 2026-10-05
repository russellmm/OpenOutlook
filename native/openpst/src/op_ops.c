/* op_ops.c - message operations (move / copy / delete / purge), folder counters, the ID map (node 0xC01) and Outlook's message index
 * (node 0xE01).  Port of pstops.py and pstidmap.py. All functions work inside one writer transaction (ops_begin ... ops_commit). */
#include "op_wr.h"

#define NOMEM op_err(OPST_E_NOMEM, "out of memory")

/* ---- zlib compatible CRC-32 (used for the synthetic message index bucket keys) ----------------------------------------------- */
static uint32_t zcrc32(const uint8_t *p, size_t n) {
    static uint32_t tab[256];
    static int ready;
    if (!ready) {
        for (uint32_t i = 0; i < 256; i++) {
            uint32_t c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) ? (c >> 1) ^ 0xEDB88320u : c >> 1;
            tab[i] = c;
        }
        ready = 1;
    }
    uint32_t c = 0xFFFFFFFFu;
    for (size_t i = 0; i < n; i++) c = tab[(c ^ p[i]) & 0xFF] ^ (c >> 8);
    return c ^ 0xFFFFFFFFu;
}

/* ---- ID map ---------------------------------------------------------------------------------------------------------------------- */
typedef struct { uint8_t k[16]; uint32_t nid; } idrec;
struct idmap { int present; idrec *r; size_t n, cap; int dirty; };

static int idrec_cmp(const void *a, const void *b) { return memcmp(((const idrec *)a)->k, ((const idrec *)b)->k, 16); }

typedef struct { idmap *m; } idctx;
static int idmap_cb(void *ctx, const uint8_t *k, const uint8_t *v) {
    idmap *m = ((idctx *)ctx)->m;
    if (m->n == m->cap) {
        size_t nc = m->cap ? m->cap * 2 : 1024;
        idrec *t = (idrec *)realloc(m->r, nc * sizeof *t);
        if (!t) return OPST_E_NOMEM;
        m->r = t; m->cap = nc;
    }
    memcpy(m->r[m->n].k, k, 16);
    m->r[m->n].nid = op_u32(v);
    m->n++;
    return 0;
}

idmap *ops_idmap(ops *o, int *rcp) {
    int rc = 0;
    if (o->im_loaded) { if (rcp) *rcp = 0; return o->im; }
    idmap *m = (idmap *)calloc(1, sizeof *m);
    if (!m) { if (rcp) *rcp = OPST_E_NOMEM; return NULL; }
    nbt_e e;
    rc = opw_node(o->w, 0xC01, &e);
    if (rc == OPST_E_NOTFOUND) rc = 0;
    else if (!rc) {
        hblocks hp;
        rc = opw_leaf_blocks(o->w, e.bd, &hp);
        if (!rc) {
            const uint8_t *d; size_t n;
            rc = heap_get(&hp, heap_root(&hp), &d, &n);
            if (!rc && n >= 4) {
                idctx c = {m};
                rc = heap_bth_walk(&hp, op_u32(d), NULL, NULL, idmap_cb, &c);
                if (!rc) { op_qsort(m->r, m->n, sizeof *m->r, idrec_cmp); m->present = 1; }
            } else if (!rc) rc = op_err(OPST_E_FORMAT, "ID map root item is too short");
            hb_free(&hp);
        }
    }
    if (rc) { free(m->r); free(m); if (rcp) *rcp = rc; return NULL; }
    o->im = m; o->im_loaded = 1;
    if (rcp) *rcp = 0;
    return m;
}

int idmap_present(idmap *m) { return m && m->present; }
size_t idmap_count(idmap *m) { return m ? m->n : 0; }
int idmap_get(idmap *m, size_t i, uint8_t guid[16], uint32_t *nid) {
    if (!m || i >= m->n) return 0;
    memcpy(guid, m->r[i].k, 16); *nid = m->r[i].nid;
    return 1;
}
int idmap_lookup(idmap *m, const uint8_t guid[16], uint32_t *nid) {
    if (!m || !m->n) return 0;
    idrec key;
    memcpy(key.k, guid, 16);
    idrec *r = (idrec *)bsearch(&key, m->r, m->n, sizeof *m->r, idrec_cmp);
    if (!r) return 0;
    *nid = r->nid;
    return 1;
}
void idmap_free(idmap *m) { if (m) { free(m->r); free(m); } }

int idmap_add(idmap *m, const uint8_t guid[16], uint32_t nid) {
    if (!m->present) return 0;
    idrec key;
    memcpy(key.k, guid, 16);
    idrec *r = m->n ? (idrec *)bsearch(&key, m->r, m->n, sizeof *m->r, idrec_cmp) : NULL;
    if (r) { r->nid = nid; m->dirty = 1; return 0; }
    if (m->n == m->cap) {
        size_t nc = m->cap ? m->cap * 2 : 1024;
        idrec *t = (idrec *)realloc(m->r, nc * sizeof *t);
        if (!t) return NOMEM;
        m->r = t; m->cap = nc;
    }
    size_t lo = 0, hi = m->n;
    while (lo < hi) { size_t mid = (lo + hi) / 2; if (memcmp(m->r[mid].k, guid, 16) < 0) lo = mid + 1; else hi = mid; }
    memmove(m->r + lo + 1, m->r + lo, (m->n - lo) * sizeof *m->r);
    memcpy(m->r[lo].k, guid, 16); m->r[lo].nid = nid;
    m->n++;
    m->dirty = 1;
    return 0;
}

/* SCANPST convention for a deleted object: keep the record, set its NID to 0 */
int idmap_zero_nid(idmap *m, uint32_t nid) {
    if (!m || !m->present) return 0;
    int n = 0;
    for (size_t i = 0; i < m->n; i++) if (m->r[i].nid == nid) { m->r[i].nid = 0; n++; }
    if (n) m->dirty = 1;
    return n;
}

int idmap_flush(ops *o) {
    idmap *m = o->im;
    if (!m || !m->present || !m->dirty) return 0;
    opw *w = o->w;
    nbt_e e;
    int rc = opw_node(w, 0xC01, &e);
    if (rc) return rc;
    hbuild hb;
    rc = hbuild_init(&hb, 0x9C);
    if (rc) return rc;
    uint32_t h0 = 0, hh = 0;
    rc = hbuild_alloc(&hb, NULL, 4, &h0);
    kv *recs = (kv *)malloc((m->n ? m->n : 1) * sizeof *recs);
    uint8_t (*vals)[4] = (uint8_t (*)[4])malloc((m->n ? m->n : 1) * 4);
    if (!recs || !vals) rc = OPST_E_NOMEM;
    for (size_t i = 0; i < m->n && !rc; i++) { wr32(vals[i], m->r[i].nid); recs[i].k = m->r[i].k; recs[i].v = vals[i]; }
    if (!rc) rc = build_bth(&hb, 16, 4, recs, m->n, 0, &hh);
    if (!rc) { uint8_t b[4]; wr32(b, hh); rc = hbuild_set(&hb, h0, b, 4); }
    hblocks blocks = {0, 0};
    if (!rc) rc = hbuild_finalize(&hb, h0, &blocks);
    hbuild_free(&hb);
    free(recs); free(vals);
    if (rc) return rc;
    uint64_t new_bd;
    rc = opw_put_blocks(w, &blocks, &new_bd);
    hb_free(&blocks);
    if (rc) return rc;
    if (e.bs) rc = opw_add_ref(w, e.bs);
    if (!rc) rc = opw_node_put(w, 0xC01, new_bd, e.bs, e.parent);
    if (!rc) rc = opw_release(w, e.bd);
    if (!rc && e.bs) rc = opw_release(w, e.bs);
    if (!rc) m->dirty = 0;
    return rc;
}

/* ---- ops lifecycle --------------------------------------------------------------------------------------------------------------- */
int ops_begin(opst *p, ops *o) {
    memset(o, 0, sizeof *o);
    o->p = p;
    int rc = op_txn_begin(p, &o->w);
    o->deleted = p->deleted;
    return rc;
}
int ops_commit(ops *o) {
    int rc = idmap_flush(o);
    if (rc) { ops_abort(o); return rc; }
    rc = op_txn_commit(o->p);
    idmap_free(o->im);
    o->im = NULL; o->im_loaded = 0;
    return rc;
}
void ops_abort(ops *o) {
    op_txn_abort(o->p);
    idmap_free(o->im);
    o->im = NULL; o->im_loaded = 0;
}

int ops_begin_ro(opst *p, ops *o) {
    memset(o, 0, sizeof *o);
    o->p = p;
    int rc = op_ro_begin(p, &o->w);
    o->deleted = p->deleted;
    return rc;
}
void ops_end_ro(ops *o) {
    op_ro_end(o->w);
    o->w = NULL;
    idmap_free(o->im);
    o->im = NULL; o->im_loaded = 0;
}

/* ---- counters --------------------------------------------------------------------------------------------------------------------- */
uint32_t ops_next_row_ver(ops *o) {
    o->w->unique = (uint32_t)(o->w->unique + 1u);
    return o->w->unique;
}

int ops_pc_add(ops *o, uint32_t nid, unsigned pid, int delta, int *found) {
    opw *w = o->w;
    *found = 0;
    nbt_e e;
    int rc = opw_node(w, nid, &e);
    if (rc == OPST_E_NOTFOUND) return op_err(OPST_E_NOTFOUND, "node 0x%x not found", nid);
    if (rc) return rc;
    hblocks hp;
    rc = opw_leaf_blocks(w, e.bd, &hp);
    if (rc) return rc;
    if (!heap_is_heap(&hp)) { hb_free(&hp); return op_err(OPST_E_FORMAT, "not a heap-on-node"); }
    uint8_t key[2];
    wr16(key, pid);
    size_t blk, off;
    if (!heap_find_record(&hp, heap_root(&hp), key, &blk, &off)) { hb_free(&hp); return 0; }
    uint8_t *b = hp.b[blk].p;
    if (op_u16(b + off) != 0x0003) { hb_free(&hp); return op_err(OPST_E_FORMAT, "property 0x%04x of node 0x%x is not an int32", pid, nid); }
    int32_t v = (int32_t)op_u32(b + off + 2);
    int64_t nv = (int64_t)v + delta;
    wr32(b + off + 2, (uint32_t)(nv < 0 ? 0 : nv));
    uint64_t new_bd;
    rc = opw_put_blocks(w, &hp, &new_bd);
    hb_free(&hp);
    if (rc) return rc;
    rc = opw_node_put(w, nid, new_bd, e.bs, e.parent);
    if (!rc) rc = opw_release(w, e.bd);
    if (!rc) *found = 1;
    return rc;
}

int ops_hier_add(ops *o, uint32_t folder, int d_cnt, int d_unread) {
    opw *w = o->w;
    nbt_e fe;
    int rc = opw_node(w, folder, &fe);
    if (rc) return rc == OPST_E_NOTFOUND ? op_err(OPST_E_NOTFOUND, "folder 0x%x not found", folder) : rc;
    uint32_t hn = (fe.parent & ~0x1Fu) | 0x0D;
    nbt_e he;
    rc = opw_node(w, hn, &he);
    if (rc == OPST_E_NOTFOUND) return 0;
    if (rc) return rc;
    tctx tc;
    rc = ed_load_tc(w, hn, &tc);
    if (rc) return rc;
    int i = tc_find(&tc, folder);
    if (i >= 0) {
        int changed = 0;
        static const unsigned pids[2] = {0x3602, 0x3603};
        int deltas[2] = {d_cnt, d_unread};
        for (int k = 0; k < 2 && !rc; k++) {
            int ci = tc_col(&tc, pids[k]);
            if (deltas[k] && ci >= 0 && tc.rows[i].present[ci] && tc.rows[i].cell[ci].n >= 4) {
                int64_t v = (int32_t)op_u32(tc.rows[i].cell[ci].p) + (int64_t)deltas[k];
                uint8_t b[4];
                wr32(b, (uint32_t)(v < 0 ? 0 : v));
                rc = tc_set_cell(&tc, (size_t)i, ci, b, 4);
                changed = 1;
            }
        }
        if (!rc && changed) {
            int vi = tc_col(&tc, 0x67F3);
            if (vi >= 0 && tc.rows[i].present[vi]) {
                uint8_t b[4];
                wr32(b, ops_next_row_ver(o));
                rc = tc_set_cell(&tc, (size_t)i, vi, b, 4);
            }
            if (!rc) rc = ed_store_tc(w, hn, &tc);
        }
    }
    tc_free(&tc);
    return rc;
}

int ops_counts(ops *o, uint32_t folder, int d_cnt, int d_unread) {
    int found, rc = 0;
    if (d_cnt) rc = ops_pc_add(o, folder, 0x3602, d_cnt, &found);
    if (!rc && d_unread) rc = ops_pc_add(o, folder, 0x3603, d_unread, &found);
    if (!rc) rc = ops_hier_add(o, folder, d_cnt, d_unread);
    return rc;
}

int ops_unread(const pcprops *vals) {
    for (size_t i = 0; i < vals->n; i++) {
        if (vals->p[i].pid == 0x0E07) return vals->p[i].v.n >= 4 && !((int32_t)op_u32(vals->p[i].v.p) & 1);
    }
    return 0;
}

/* ---- grouping ---------------------------------------------------------------------------------------------------------------------- */
typedef struct { uint32_t parent; uint32_t *nids; size_t n, cap; } mgroup;
typedef struct { mgroup *g; size_t n; } mgroups;
static void mgroups_free(mgroups *m) { for (size_t i = 0; i < m->n; i++) free(m->g[i].nids); free(m->g); memset(m, 0, sizeof *m); }

static int group_msgs(ops *o, const uint32_t *nids, size_t n, mgroups *out) {
    memset(out, 0, sizeof *out);
    out->g = (mgroup *)calloc(n ? n : 1, sizeof *out->g);
    if (!out->g) return NOMEM;
    for (size_t i = 0; i < n; i++) {
        nbt_e e;
        int rc = opw_node(o->w, nids[i], &e);
        if (rc == OPST_E_NOTFOUND) { mgroups_free(out); return op_err(OPST_E_NOTFOUND, "message 0x%x not found", nids[i]); }
        if (rc) { mgroups_free(out); return rc; }
        if ((nids[i] & 0x1F) != 4) { mgroups_free(out); return op_err(OPST_E_ARG, "0x%x is not a normal message", nids[i]); }
        size_t k;
        for (k = 0; k < out->n; k++) if (out->g[k].parent == e.parent) break;
        if (k == out->n) { out->g[k].parent = e.parent; out->n++; }
        mgroup *g = &out->g[k];
        if (g->n == g->cap) {
            size_t nc = g->cap ? g->cap * 2 : 8;
            uint32_t *t = (uint32_t *)realloc(g->nids, nc * sizeof *t);
            if (!t) { mgroups_free(out); return NOMEM; }
            g->nids = t; g->cap = nc;
        }
        g->nids[g->n++] = nids[i];
    }
    return 0;
}

/* ---- move ---------------------------------------------------------------------------------------------------------------------------- */
int ops_move_msgs(ops *o, const uint32_t *nids, size_t n, uint32_t dest, size_t *moved) {
    opw *w = o->w;
    mgroups gs;
    if (moved) *moved = 0;
    int rc = group_msgs(o, nids, n, &gs);
    if (rc) return rc;
    uint32_t dtc_nid = (dest & ~0x1Fu) | 0x0E;
    tctx dtc;
    rc = ed_load_tc(w, dtc_nid, &dtc);
    if (rc) { mgroups_free(&gs); return rc; }
    int d_cnt = 0, d_unread = 0;
    for (size_t gi = 0; gi < gs.n && !rc; gi++) {
        uint32_t src = gs.g[gi].parent;
        if (src == dest) continue;
        uint32_t stc_nid = (src & ~0x1Fu) | 0x0E;
        tctx stc;
        rc = ed_load_tc(w, stc_nid, &stc);
        if (rc) break;
        int s_unread = 0;
        for (size_t k = 0; k < gs.g[gi].n && !rc; k++) {
            uint32_t m = gs.g[gi].nids[k];
            int row = tc_find(&stc, m);
            if (row < 0) { rc = op_err(OPST_E_NOTFOUND, "row 0x%x not in table", m); break; }
            pcprops vals;
            rc = tc_row_pidvals(&stc, (size_t)row, &vals);
            if (rc) break;
            rc = tc_remove(&stc, m);
            if (!rc) rc = tc_add_by_pid(&dtc, m, &vals);
            nbt_e e = {0};
            if (!rc) rc = opw_node(w, m, &e);
            if (!rc) rc = opw_node_put(w, m, e.bd, e.bs, dest);
            if (!rc) { int u = ops_unread(&vals); s_unread += u; d_unread += u; d_cnt++; }
            pcprops_free(&vals);
        }
        if (!rc) rc = ed_store_tc(w, stc_nid, &stc);
        if (!rc) rc = ops_counts(o, src, -(int)gs.g[gi].n, -s_unread);
        tc_free(&stc);
    }
    if (!rc && d_cnt) {
        rc = ed_store_tc(w, dtc_nid, &dtc);
        if (!rc) rc = ops_counts(o, dest, d_cnt, d_unread);
    }
    tc_free(&dtc);
    mgroups_free(&gs);
    if (!rc && moved) *moved = (size_t)d_cnt;
    return rc;
}

/* ---- read / flag state ------------------------------------------------------------------------------------------------------------- */
/* read-modify-write of an int32 property of a node's property context, in place: new = (old & andmask) | ormask.
   *found = 0 when the property is absent (nothing changed). */
static int ops_pc_rmw(ops *o, uint32_t nid, unsigned pid, uint32_t andmask, uint32_t ormask, int create, uint32_t *oldv, uint32_t *newv, int *found) {
    opw *w = o->w;
    *found = 0;
    nbt_e e;
    int rc = opw_node(w, nid, &e);
    if (rc == OPST_E_NOTFOUND) return op_err(OPST_E_NOTFOUND, "node 0x%x not found", nid);
    if (rc) return rc;
    hblocks hp;
    rc = opw_leaf_blocks(w, e.bd, &hp);
    if (rc) return rc;
    if (!heap_is_heap(&hp)) { hb_free(&hp); return op_err(OPST_E_FORMAT, "not a heap-on-node"); }
    uint8_t key[2];
    wr16(key, pid);
    size_t blk, off;
    uint32_t ov = 0, nv = 0;
    if (!heap_find_record(&hp, heap_root(&hp), key, &blk, &off)) {
        if (!create) { hb_free(&hp); return 0; }
        /* the property is absent: insert a 6-byte int32 record into the (single-level) property BTH, keeping the keys sorted */
        const uint8_t *hd, *ld; size_t hn, ln;
        rc = heap_get(&hp, heap_root(&hp), &hd, &hn);
        if (rc) { hb_free(&hp); return rc; }
        if (hn < 8 || hd[0] != 0xB5 || hd[1] != 2 || hd[2] != 6 || hd[3] != 0) { hb_free(&hp); return op_err(OPST_E_UNSUPPORTED, "property context of node 0x%x has a layout that cannot take a new property in place", nid); }
        uint32_t leaf = op_u32(hd + 4);
        rc = heap_get(&hp, leaf, &ld, &ln);
        if (rc) { hb_free(&hp); return rc; }
        size_t nrec = ln / 8, at = 0;
        uint8_t *nb = (uint8_t *)malloc(ln + 8);
        if (!nb) { hb_free(&hp); return OPST_E_NOMEM; }
        while (at < nrec && op_u16(ld + at * 8) < pid) at++;
        memcpy(nb, ld, at * 8);
        wr16(nb + at * 8, pid); wr16(nb + at * 8 + 2, 3); wr32(nb + at * 8 + 4, ormask);
        memcpy(nb + (at + 1) * 8, ld + at * 8, (nrec - at) * 8);
        rc = heap_grow_item(&hp, leaf, nb, ln + 8);
        free(nb);
        if (rc == 0) { hb_free(&hp); return op_err(OPST_E_UNSUPPORTED, "no room in the property context of node 0x%x for a new property", nid); }
        if (rc < 0) { hb_free(&hp); return rc; }
        ov = 0; nv = ormask;
        *oldv = ov; *newv = nv; *found = 1;
        goto store;
    }
    uint8_t *b = hp.b[blk].p;
    if (op_u16(b + off) != 0x0003) { hb_free(&hp); return op_err(OPST_E_FORMAT, "property 0x%04x of node 0x%x is not an int32", pid, nid); }
    ov = op_u32(b + off + 2); nv = (ov & andmask) | ormask;
    *oldv = ov; *newv = nv; *found = 1;
    if (nv == ov) { hb_free(&hp); return 0; }
    wr32(b + off + 2, nv);
store:
    uint64_t new_bd;
    rc = opw_put_blocks(w, &hp, &new_bd);
    hb_free(&hp);
    if (rc) return rc;
    rc = opw_node_put(w, nid, new_bd, e.bs, e.parent);
    if (!rc) rc = opw_release(w, e.bd);
    return rc;
}

/* read: -1 unchanged, 0 unread, 1 read.  flag: -1 unchanged, else PidTagFlagStatus (0 none, 1 complete, 2 flagged).
   Updates the message, its contents-table row and the folder's unread count.  A message without PidTagFlagStatus gets the
   property inserted when a flag is set (clearing a flag on such a message is a no-op). */
int ops_set_msg_state(ops *o, const uint32_t *nids, size_t n, int read, int flag) {
    opw *w = o->w;
    if (read < -1 || read > 1 || flag < -1 || flag > 2) return op_err(OPST_E_ARG, "bad read/flag value");
    mgroups gs;
    int rc = group_msgs(o, nids, n, &gs);
    if (rc) return rc;
    for (size_t gi = 0; gi < gs.n && !rc; gi++) {
        uint32_t folder = gs.g[gi].parent;
        uint32_t tc_nid = (folder & ~0x1Fu) | 0x0E;
        tctx tc;
        rc = ed_load_tc(w, tc_nid, &tc);
        if (rc) break;
        int d_unread = 0, changed = 0;
        int cf = tc_col(&tc, 0x0E07), cs = tc_col(&tc, 0x1090), cv = tc_col(&tc, 0x67F3);
        for (size_t k = 0; k < gs.g[gi].n && !rc; k++) {
            uint32_t m = gs.g[gi].nids[k];
            uint32_t of = 0, nf = 0, os = 0, ns = 0;
            int ff = 1, fs = 1, touched = 0;
            if (read >= 0) {
                rc = ops_pc_rmw(o, m, 0x0E07, read ? 0xFFFFFFFFu : ~1u, read ? 1u : 0u, 0, &of, &nf, &ff);
                if (!rc && !ff) rc = op_err(OPST_E_FORMAT, "message 0x%x has no flags property", m);
                if (!rc && of != nf) { touched = 1; d_unread += (nf & 1) ? -1 : 1; }
            }
            if (!rc && flag >= 0) {
                rc = ops_pc_rmw(o, m, 0x1090, 0u, (uint32_t)flag, flag != 0, &os, &ns, &fs);
                if (!rc && !fs && flag != 0) rc = op_err(OPST_E_UNSUPPORTED, "message 0x%x has no flag status property", m);
                if (!rc && fs && os != ns) touched = 1;
            }
            if (rc || !touched) continue;
            int row = tc_find(&tc, m);
            if (row >= 0) {
                uint8_t b[4];
                if (read >= 0 && cf >= 0 && tc.rows[row].present[cf]) { wr32(b, nf); rc = tc_set_cell(&tc, (size_t)row, cf, b, 4); }
                if (!rc && flag >= 0 && fs && cs >= 0) { wr32(b, ns); rc = tc_set_cell(&tc, (size_t)row, cs, b, 4); }
                if (!rc && cv >= 0 && tc.rows[row].present[cv]) { wr32(b, ops_next_row_ver(o)); rc = tc_set_cell(&tc, (size_t)row, cv, b, 4); }
                changed = 1;
            }
        }
        if (!rc && changed) rc = ed_store_tc(w, tc_nid, &tc);
        if (!rc && d_unread) rc = ops_counts(o, folder, 0, d_unread);
        tc_free(&tc);
    }
    mgroups_free(&gs);
    return rc;
}

/* ---- copy ----------------------------------------------------------------------------------------------------------------------------- */
/* a copy must not carry the original's identity: new PidTagSearchKey (0x300B), change key (0x65E2) and the matching predecessor list (0x65E3) */
static int fresh_slot(hblocks *hp, unsigned pid, size_t size, size_t *blk, size_t *s) {
    uint8_t key[2];
    wr16(key, pid);
    size_t b, off;
    if (!heap_find_record(hp, heap_root(hp), key, &b, &off)) return 0;
    uint32_t hid = op_u32(hp->b[b].p + off + 2);
    if (hid == 0 || (hid & 0x1F)) return 0;
    size_t bb, ss, ee;
    if (heap_loc(hp, hid, &bb, &ss, &ee) || ee - ss != size) return 0;
    *blk = bb; *s = ss;
    return 1;
}
static void fresh_keys(hblocks *blocks, void *ctx) {
    (void)ctx;
    if (!heap_is_heap(blocks) || heap_client(blocks) != 0xBC) return;
    size_t b, s;
    if (fresh_slot(blocks, 0x300B, 16, &b, &s)) op_random(blocks->b[b].p + s, 16);
    if (fresh_slot(blocks, 0x65E2, 22, &b, &s)) {
        uint8_t old[22], nw[22], r2[2];
        memcpy(old, blocks->b[b].p + s, 22);
        op_random(r2, 2);
        uint64_t cnt = 0;
        for (int i = 16; i < 22; i++) cnt = (cnt << 8) | old[i];
        cnt += 1 + (((uint64_t)r2[0] << 8) | r2[1]);
        memcpy(nw, old, 16);
        for (int i = 5; i >= 0; i--) { nw[16 + i] = (uint8_t)(cnt & 0xFF); cnt >>= 8; }
        memcpy(blocks->b[b].p + s, nw, 22);
        size_t b2, s2;
        if (fresh_slot(blocks, 0x65E3, 23, &b2, &s2) && memcmp(blocks->b[b2].p + s2 + 1, old, 22) == 0) memcpy(blocks->b[b2].p + s2 + 1, nw, 22);
    }
}

int ops_copy_msgs(ops *o, const uint32_t *nids, size_t n, uint32_t dest, int share, int ids, int fresh, uint32_t *new_nids) {
    opw *w = o->w;
    mgroups gs;
    int rc = group_msgs(o, nids, n, &gs);
    if (rc) return rc;
    uint32_t dtc_nid = (dest & ~0x1Fu) | 0x0E;
    tctx dtc;
    rc = ed_load_tc(w, dtc_nid, &dtc);
    if (rc) { mgroups_free(&gs); return rc; }
    int cnt = 0, unread = 0;
    nidpair *pairs = (nidpair *)malloc((n ? n : 1) * sizeof *pairs);
    if (!pairs) rc = OPST_E_NOMEM;
    size_t np = 0;
    for (size_t gi = 0; gi < gs.n && !rc; gi++) {
        uint32_t src = gs.g[gi].parent;
        tctx stc;
        rc = ed_load_tc(w, (src & ~0x1Fu) | 0x0E, &stc);
        if (rc) break;
        for (size_t k = 0; k < gs.g[gi].n && !rc; k++) {
            uint32_t m = gs.g[gi].nids[k];
            int row = tc_find(&stc, m);
            if (row < 0) { rc = op_err(OPST_E_NOTFOUND, "row 0x%x not in table", m); break; }
            pcprops vals;
            rc = tc_row_pidvals(&stc, (size_t)row, &vals);
            if (rc) break;
            uint32_t idx = op_u32(w->hdr + 44 + 4 * 4) + 1;
            wr32(w->hdr + 44 + 4 * 4, idx);
            uint32_t nn = (idx << 5) | 4;
            uint8_t b4[4];
            wr32(b4, nn);
            rc = pcprops_set(&vals, 0x67F2, 3, b4, 4);
            if (!rc && pcprops_find(&vals, 0x67F3)) { wr32(b4, ops_next_row_ver(o)); rc = pcprops_set(&vals, 0x67F3, 3, b4, 4); }
            nbt_e e = {0};
            if (!rc) rc = opw_node(w, m, &e);
            uint64_t nbd = 0, nbs = 0;
            if (!rc) {
                if (share) {
                    rc = opw_add_ref(w, e.bd);
                    if (!rc && e.bs) rc = opw_add_ref(w, e.bs);
                    nbd = e.bd; nbs = e.bs;
                } else rc = opw_clone_node(w, e.bd, e.bs, fresh ? fresh_keys : NULL, NULL, &nbd, &nbs);
            }
            if (!rc) rc = opw_node_put(w, nn, nbd, nbs, dest);
            if (!rc) {
                idmap *im = NULL;
                if (ids && pcprops_find(&vals, 0x0E30)) im = ops_idmap(o, &rc);
                if (!rc && im && idmap_present(im)) {
                    uint8_t g[16];
                    rc = op_random(g, 16);
                    if (!rc) rc = pcprops_set(&vals, 0x0E30, 0x0102, g, 16);
                    if (!rc) rc = idmap_add(im, g, nn);
                    if (!rc && pcprops_find(&vals, 0x0E33)) {
                        uint8_t b8[8];
                        wr64(b8, w->bid_next_b);
                        rc = pcprops_set(&vals, 0x0E33, 0x0014, b8, 8);
                    }
                } else if (!rc) {
                    pcprops_del(&vals, 0x0E30); pcprops_del(&vals, 0x0E33); pcprops_del(&vals, 0x0E34);
                }
            }
            if (!rc) rc = tc_add_by_pid(&dtc, nn, &vals);
            if (!rc) {
                cnt++; unread += ops_unread(&vals);
                if (new_nids) new_nids[cnt - 1] = nn;
                pairs[np].nid = m; pairs[np].parent = nn; np++;
            }
            pcprops_free(&vals);
        }
        tc_free(&stc);
    }
    if (!rc) rc = ed_store_tc(w, dtc_nid, &dtc);
    if (!rc) rc = ops_counts(o, dest, cnt, unread);
    if (!rc) {
        /* attach every copy to the bucket of its own conversation key; copies without a conversation index follow their source's bucket */
        keyednid *keyed = (keyednid *)malloc((np ? np : 1) * sizeof *keyed);
        nidpair *rest = (nidpair *)malloc((np ? np : 1) * sizeof *rest);
        size_t nk = 0, nr = 0;
        if (!keyed || !rest) rc = OPST_E_NOMEM;
        for (size_t i = 0; i < np && !rc; i++) {
            uint32_t key;
            if (msg_conv_key(w, pairs[i].parent, &key)) { keyed[nk].key = key; keyed[nk].nid = pairs[i].parent; nk++; }
            else rest[nr++] = pairs[i];
        }
        if (!rc) rc = ops_note_max_message_nid(o, rest, nr, NULL, 0, keyed, nk);
        free(keyed); free(rest);
    }
    tc_free(&dtc);
    free(pairs);
    mgroups_free(&gs);
    return rc;
}

/* ---- message index (node 0xE01) ----------------------------------------------------------------------------------------------------- */
typedef struct { uint32_t hid; uint8_t *d; size_t n; } bucket;

typedef struct { bucket *b; size_t n, cap; hblocks *hp; } bucketlist;
static int bucket_cb(void *ctx, const uint8_t *k, const uint8_t *v) {
    (void)k;
    bucketlist *bl = (bucketlist *)ctx;
    uint32_t hid = op_u32(v);
    const uint8_t *d; size_t n;
    if (heap_get(bl->hp, hid, &d, &n)) return 0;                       /* unreadable bucket: skipped, as in Python */
    if (bl->n == bl->cap) {
        size_t nc = bl->cap ? bl->cap * 2 : 256;
        bucket *t = (bucket *)realloc(bl->b, nc * sizeof *t);
        if (!t) return OPST_E_NOMEM;
        bl->b = t; bl->cap = nc;
    }
    bl->b[bl->n].hid = hid;
    bl->b[bl->n].d = (uint8_t *)malloc(n ? n : 1);
    if (!bl->b[bl->n].d) return OPST_E_NOMEM;
    memcpy(bl->b[bl->n].d, d, n);
    bl->b[bl->n].n = n;
    bl->n++;
    return 0;
}
static void buckets_free(bucketlist *bl) { for (size_t i = 0; i < bl->n; i++) free(bl->b[i].d); free(bl->b); memset(bl, 0, sizeof *bl); }

typedef struct { uint32_t top; } topctx;
static int top_cb(void *ctx, const uint8_t *e) {
    topctx *t = (topctx *)ctx;
    uint32_t nid = (uint32_t)(op_u64(e) & 0xFFFFFFFFu);
    if ((nid & 0x1F) == 4 && nid > t->top) t->top = nid;
    return 0;
}

typedef struct { uint32_t *k; size_t n, cap; } keyset;
static int keys_cb(void *ctx, const uint8_t *k, const uint8_t *v) {
    (void)v;
    keyset *s = (keyset *)ctx;
    if (s->n == s->cap) {
        size_t nc = s->cap ? s->cap * 2 : 256;
        uint32_t *t = (uint32_t *)realloc(s->k, nc * sizeof *t);
        if (!t) return OPST_E_NOMEM;
        s->k = t; s->cap = nc;
    }
    s->k[s->n++] = op_u32(k);
    return 0;
}
static int keyset_has(const keyset *s, uint32_t k) { for (size_t i = 0; i < s->n; i++) if (s->k[i] == k) return 1; return 0; }

/* Key of a message's bucket in the message index (node 0xE01), found with SCANPST as an oracle (14,578 of 14,582 real keys, 167 of 170 probes):
   0xFFFF0000 xor (xor over the 16 bytes of the conversation GUID, byte j shifted left by 15 - j); the GUID is bytes 6..22 of PidTagConversationIndex */
static uint32_t conv_key_of_guid(const uint8_t *g) {
    uint32_t h = 0;
    for (int j = 0; j < 16; j++) h ^= (uint32_t)g[j] << (15 - j);
    return 0xFFFF0000u ^ h;
}

int msg_conv_key(opw *w, uint32_t nid, uint32_t *key) {
    nbt_e e;
    *key = 0;
    if (opw_node(w, nid, &e) != 0) return 0;
    hblocks hp;
    if (opw_leaf_blocks(w, e.bd, &hp) != 0) return 0;
    int ok = 0;
    if (heap_is_heap(&hp) && heap_client(&hp) == 0xBC) {
        uint8_t k2[2];
        wr16(k2, 0x71);
        size_t blk, off;
        if (heap_find_record(&hp, heap_root(&hp), k2, &blk, &off)) {
            uint32_t hid = op_u32(hp.b[blk].p + off + 2);
            const uint8_t *d; size_t n;
            if (hid && !(hid & 0x1F) && heap_get(&hp, hid, &d, &n) == 0 && n >= 22) { *key = conv_key_of_guid(d + 6); ok = 1; }
        }
    }
    hb_free(&hp);
    return ok;
}

typedef struct { uint32_t key, hid; } kmap_e;
typedef struct { kmap_e *m; size_t n, cap; } keymaps;
static int keymap_add(keymaps *k, uint32_t key, uint32_t hid) {
    if (k->n == k->cap) {
        size_t nc = k->cap ? k->cap * 2 : 256;
        kmap_e *t = (kmap_e *)realloc(k->m, nc * sizeof *t);
        if (!t) return OPST_E_NOMEM;
        k->m = t; k->cap = nc;
    }
    k->m[k->n].key = key; k->m[k->n].hid = hid; k->n++;
    return 0;
}
static int keymap_cb(void *ctx, const uint8_t *k, const uint8_t *v) { return keymap_add((keymaps *)ctx, op_u32(k), op_u32(v)); }

int ops_note_max_message_nid(ops *o, const nidpair *pairs, size_t npairs, const idxgroup *groups, size_t ngroups, const keyednid *keyed, size_t nkeyed) {
    opw *w = o->w;
    nbt_e e;
    int rc = opw_node(w, 0xE01, &e);
    if (rc == OPST_E_NOTFOUND) return 0;
    if (rc) return rc;
    hblocks hp;
    rc = opw_leaf_blocks(w, e.bd, &hp);
    if (rc) return rc;
    if (!heap_is_heap(&hp) || heap_client(&hp) != 0xCC) { hb_free(&hp); return 0; }
    int changed = 0;
    size_t blk, s, ee;
    rc = heap_loc(&hp, heap_root(&hp), &blk, &s, &ee);
    if (rc) { hb_free(&hp); return rc; }
    uint32_t cur = op_u32(hp.b[blk].p + s + 8);
    topctx tc = {0};
    rc = bt_items(w, 1, top_cb, &tc);
    uint32_t top = tc.top ? tc.top : cur;
    if (!rc && top > cur) { wr32(hp.b[blk].p + s + 8, top); changed = 1; }
    if (!rc && npairs) {
        bucketlist bl = {0, 0, 0, &hp};
        rc = heap_bth_walk(&hp, 0x40, NULL, NULL, bucket_cb, &bl);
        if (rc) { buckets_free(&bl); rc = 0; bl.n = 0; }            /* the BTH could not be read: nothing to attach to (as in Python) */
        for (size_t pi = 0; pi < npairs && !rc; pi++) {
            uint32_t srcn = pairs[pi].nid, newn = pairs[pi].parent;
            size_t cnt0 = bl.n;
            for (size_t bi = 0; bi < cnt0 && !rc; bi++) {
                bucket *bk = &bl.b[bi];
                if (!bk->d) continue;                                  /* deleted from the list */
                int has_new = 0, has_src = 0;
                for (size_t q = 0; q + 4 <= bk->n; q += 4) { uint32_t v = op_u32(bk->d + q); if (v == newn) has_new = 1; if (v == srcn) has_src = 1; }
                if (has_new) break;
                if (!has_src) continue;
                uint8_t *nl = (uint8_t *)malloc(bk->n + 4);
                if (!nl) { rc = OPST_E_NOMEM; break; }
                memcpy(nl, bk->d, bk->n);
                wr32(nl + bk->n, newn);
                size_t nlen = bk->n + 4;
                int g = heap_grow_item(&hp, bk->hid, nl, nlen);
                if (g < 0) { free(nl); rc = g; break; }
                if (g == 1) { free(bk->d); bk->d = nl; bk->n = nlen; changed = 1; }
                else {                                                  /* no room in its block: move the list to the end of the heap */
                    uint32_t nh;
                    rc = heap_append_item(&hp, nl, nlen, &nh);
                    if (rc) { free(nl); break; }
                    if (!heap_bth_repoint(&hp, 0x40, bk->hid, nh)) { free(nl); rc = op_err(OPST_E_FORMAT, "message index: bucket 0x%x not found in its BTH", bk->hid); break; }
                    free(bk->d); bk->d = NULL;                          /* del lists[hid] */
                    if (bl.n == bl.cap) {
                        size_t nc = bl.cap ? bl.cap * 2 : 256;
                        bucket *t = (bucket *)realloc(bl.b, nc * sizeof *t);
                        if (!t) { free(nl); rc = OPST_E_NOMEM; break; }
                        bl.b = t; bl.cap = nc;
                        bk = &bl.b[bi];
                    }
                    bl.b[bl.n].hid = nh; bl.b[bl.n].d = nl; bl.b[bl.n].n = nlen; bl.n++;   /* lists[nh] = newlst */
                    changed = 1;
                }
                break;
            }
        }
        buckets_free(&bl);
    }
    if (!rc && nkeyed) {
        keymaps km = {0, 0, 0};
        rc = heap_bth_walk(&hp, 0x40, NULL, NULL, keymap_cb, &km);
        for (size_t ki = 0; ki < nkeyed && !rc; ki++) {
            uint32_t key = keyed[ki].key, newn = keyed[ki].nid;
            size_t at;
            for (at = 0; at < km.n; at++) if (km.m[at].key == key) break;
            if (at == km.n) {                                    /* no bucket with this key yet: a new one holding just this message */
                uint8_t b4[4];
                uint32_t lh;
                wr32(b4, newn);
                rc = heap_append_item(&hp, b4, 4, &lh);
                if (!rc) rc = heap_bth_insert(&hp, key, lh);
                if (!rc) rc = keymap_add(&km, key, lh);
                changed = 1;
                continue;
            }
            uint32_t hid = km.m[at].hid;
            const uint8_t *lst; size_t ln;
            rc = heap_get(&hp, hid, &lst, &ln);
            if (rc) break;
            int has = 0;
            for (size_t q = 0; q + 4 <= ln; q += 4) if (op_u32(lst + q) == newn) has = 1;
            if (has) continue;
            uint8_t *nl = (uint8_t *)malloc(ln + 4);
            if (!nl) { rc = OPST_E_NOMEM; break; }
            memcpy(nl, lst, ln);
            wr32(nl + ln, newn);
            int g = heap_grow_item(&hp, hid, nl, ln + 4);
            if (g < 0) rc = g;
            else if (g == 1) changed = 1;
            else {                                               /* no room in its block: move the list to the end of the heap */
                uint32_t nh;
                rc = heap_append_item(&hp, nl, ln + 4, &nh);
                if (!rc && !heap_bth_repoint(&hp, 0x40, hid, nh)) rc = op_err(OPST_E_FORMAT, "message index: bucket 0x%x not found in its BTH", hid);
                if (!rc) { km.m[at].hid = nh; changed = 1; }
            }
            free(nl);
        }
        free(km.m);
    }
    if (!rc && ngroups) {
        keyset used = {0, 0, 0};
        rc = heap_bth_walk(&hp, 0x40, NULL, NULL, keys_cb, &used);
        for (size_t gi = 0; gi < ngroups && !rc; gi++) {
            uint8_t *low = (uint8_t *)malloc(groups[gi].topic_len ? groups[gi].topic_len : 1);
            if (!low) { rc = OPST_E_NOMEM; break; }
            for (size_t i = 0; i < groups[gi].topic_len; i++) { uint8_t c = groups[gi].topic[i]; low[i] = (c >= 'A' && c <= 'Z') ? (uint8_t)(c + 32) : c; }
            uint32_t key = 0xFF800000u | (zcrc32(low, groups[gi].topic_len) & 0x7FFFFF);
            free(low);
            while (keyset_has(&used, key)) key = 0xFF800000u | ((key + 1) & 0x7FFFFF);
            if (used.n == used.cap) {
                size_t nc = used.cap ? used.cap * 2 : 256;
                uint32_t *t = (uint32_t *)realloc(used.k, nc * sizeof *t);
                if (!t) { rc = OPST_E_NOMEM; break; }
                used.k = t; used.cap = nc;
            }
            used.k[used.n++] = key;
            uint8_t *lst = (uint8_t *)malloc(groups[gi].n * 4 + 1);
            if (!lst) { rc = OPST_E_NOMEM; break; }
            for (size_t i = 0; i < groups[gi].n; i++) wr32(lst + i * 4, groups[gi].nids[i]);
            uint32_t lh;
            rc = heap_append_item(&hp, lst, groups[gi].n * 4, &lh);
            free(lst);
            if (!rc) rc = heap_bth_insert(&hp, key, lh);
            changed = 1;
        }
        free(used.k);
    }
    if (rc || !changed) { hb_free(&hp); return rc; }
    uint64_t new_bd;
    rc = opw_put_blocks(w, &hp, &new_bd);
    hb_free(&hp);
    if (rc) return rc;
    rc = opw_node_put(w, 0xE01, new_bd, e.bs, e.parent);
    if (!rc) rc = opw_release(w, e.bd);
    return rc;
}

int ops_bump_hwm(ops *o, const uint32_t *nids, size_t n) {
    uint8_t *h = o->w->hdr;
    for (size_t i = 0; i < n; i++) {
        unsigned t = nids[i] & 0x1F;
        uint32_t idx = nids[i] >> 5;
        if (t == 5 || t == 0x1F) {
            if (idx > op_u32(h + 44 + 4 * t)) wr32(h + 44 + 4 * t, idx);
        }
    }
    return 0;
}

/* ---- purge / delete ----------------------------------------------------------------------------------------------------------------- */
int ops_purge_msgs(ops *o, const uint32_t *nids, size_t n) {
    opw *w = o->w;
    mgroups gs;
    int rc = group_msgs(o, nids, n, &gs);
    if (rc) return rc;
    int rcim = 0;
    idmap *im = ops_idmap(o, &rcim);
    if (rcim) { mgroups_free(&gs); return rcim; }
    for (size_t gi = 0; gi < gs.n && !rc; gi++) {
        uint32_t src = gs.g[gi].parent;
        uint32_t stc_nid = (src & ~0x1Fu) | 0x0E;
        tctx stc;
        rc = ed_load_tc(w, stc_nid, &stc);
        if (rc) break;
        int unread = 0;
        for (size_t k = 0; k < gs.g[gi].n && !rc; k++) {
            uint32_t m = gs.g[gi].nids[k];
            idmap_zero_nid(im, m);
            int row = tc_find(&stc, m);
            if (row >= 0) {
                pcprops vals;
                rc = tc_row_pidvals(&stc, (size_t)row, &vals);
                if (rc) break;
                unread += ops_unread(&vals);
                pcprops_free(&vals);
                rc = tc_remove(&stc, m);
                if (rc) break;
            }
            nbt_e e = {0};
            rc = opw_node(w, m, &e);
            if (!rc) rc = opw_node_del(w, m);
            if (!rc) rc = opw_release(w, e.bd);
            if (!rc && e.bs) rc = opw_release(w, e.bs);
        }
        if (!rc) rc = ed_store_tc(w, stc_nid, &stc);
        if (!rc) rc = ops_counts(o, src, -(int)gs.g[gi].n, -unread);
        tc_free(&stc);
    }
    mgroups_free(&gs);
    return rc;
}

int ops_delete_msgs(ops *o, const uint32_t *nids, size_t n, size_t *moved, size_t *purged) {
    if (!o->deleted) return op_err(OPST_E_FORMAT, "Deleted Items folder not found in the store");
    uint32_t *other = (uint32_t *)malloc((n ? n : 1) * sizeof *other), *indel = (uint32_t *)malloc((n ? n : 1) * sizeof *indel);
    if (!other || !indel) { free(other); free(indel); return NOMEM; }
    size_t no = 0, nd = 0;
    int rc = 0;
    for (size_t i = 0; i < n && !rc; i++) {
        nbt_e e;
        int r = opw_node(o->w, nids[i], &e);
        if (r == 0 && e.parent == o->deleted) indel[nd++] = nids[i];
        else other[no++] = nids[i];
    }
    if (moved) *moved = 0;
    if (purged) *purged = 0;
    if (no) { size_t m; rc = ops_move_msgs(o, other, no, o->deleted, &m); if (moved) *moved = m; }
    if (!rc && nd) { rc = ops_purge_msgs(o, indel, nd); if (!rc && purged) *purged = nd; }
    free(other); free(indel);
    return rc;
}
