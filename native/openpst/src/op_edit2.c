/* op_edit2.c - table contexts (parse / build), property contexts and node-level load / store (port of pstedit.TC / Editor and
 * pstfolders.pc_*). */
#include "op_wr.h"

#define NOMEM op_err(OPST_E_NOMEM, "out of memory")
#define BAD(...) op_err(OPST_E_FORMAT, __VA_ARGS__)

int tc_row_pidvals(const tctx *t, size_t row, pcprops *out);
int tc_add_by_pid(tctx *t, uint32_t rowid, const pcprops *vals);

static int fixed_size(unsigned pt) {
    switch (pt) {
    case 0x0002: return 2;
    case 0x0003: case 0x0004: case 0x000A: return 4;
    case 0x0005: case 0x0006: case 0x0007: case 0x0014: case 0x0040: return 8;
    case 0x000B: return 1;
    default: return 0;
    }
}

/* ---- parse ------------------------------------------------------------------------------------------------------------------------ */
typedef struct { uint32_t idx, id; } rowidx;
typedef struct { rowidx *v; size_t n, cap; } rowidxs;

static int collect_index(void *ctx, const uint8_t *k, const uint8_t *v) {
    rowidxs *r = (rowidxs *)ctx;
    if (r->n == r->cap) {
        size_t nc = r->cap ? r->cap * 2 : 256;
        rowidx *t = (rowidx *)realloc(r->v, nc * sizeof *t);
        if (!t) return OPST_E_NOMEM;
        r->v = t; r->cap = nc;
    }
    r->v[r->n].id = op_u32(k); r->v[r->n].idx = op_u32(v); r->n++;
    return 0;
}
static int cmp_rowidx(const void *a, const void *b) {
    uint32_t x = ((const rowidx *)a)->idx, y = ((const rowidx *)b)->idx;
    return x < y ? -1 : x > y;
}

static const wsub *find_wsub(const wsubs *s, uint32_t nid) {
    for (size_t i = 0; i < s->n; i++) if (s->e[i].nid == nid) return &s->e[i];
    return NULL;
}

/* concatenated data of a subnode */
static int sub_concat(opw *w, const wsubs *subs, uint32_t nid, bbuf *out) {
    const wsub *s = find_wsub(subs, nid);
    if (!s) return op_err(OPST_E_FORMAT, "subnode 0x%x missing", nid);
    hblocks hb;
    int rc = opw_leaf_blocks(w, s->bd, &hb);
    if (rc) return rc;
    for (size_t i = 0; i < hb.n && !rc; i++) rc = bb_put(out, hb.b[i].p, hb.b[i].n) ? OPST_E_NOMEM : 0;
    hb_free(&hb);
    return rc;
}

static int tc_parse(opw *w, const hblocks *hp, const wsubs *subs, tctx *tc) {
    memset(tc, 0, sizeof *tc);
    if (!heap_is_heap(hp)) return BAD("not a heap-on-node");
    if (heap_client(hp) != 0x7C) return BAD("not a table context");
    const uint8_t *info; size_t in;
    int rc = heap_get(hp, heap_root(hp), &info, &in);
    if (rc) return rc;
    if (in < 22) return BAD("table context header is truncated");
    unsigned ccols = info[1];
    if (in < 22 + 8 * (size_t)ccols) return BAD("table context column list is truncated");
    for (int i = 0; i < 4; i++) tc->rgib[i] = (uint16_t)op_u16(info + 2 + 2 * i);
    uint32_t hid_ri = op_u32(info + 10), hnid_rows = op_u32(info + 14);
    tc->cols = (tcol *)calloc(ccols ? ccols : 1, sizeof *tc->cols);
    if (!tc->cols) return NOMEM;
    tc->ncols = ccols;
    for (unsigned i = 0; i < ccols; i++) {
        uint32_t tag = op_u32(info + 22 + 8 * i);
        tc->cols[i].pid = (uint16_t)(tag >> 16); tc->cols[i].ptype = (uint16_t)(tag & 0xFFFF);
        tc->cols[i].ibd = (uint16_t)op_u16(info + 22 + 8 * i + 4);
        tc->cols[i].cbd = info[22 + 8 * i + 6]; tc->cols[i].ibit = info[22 + 8 * i + 7];
    }
    if (hnid_rows == 0) return 0;
    unsigned rs = tc->rgib[3];
    if (rs == 0 || tc->rgib[2] > rs) return BAD("bad table context row layout");

    rowidxs idx = {0, 0, 0};
    rc = heap_bth_walk(hp, hid_ri, NULL, NULL, collect_index, &idx);
    if (rc) { free(idx.v); return rc; }
    op_qsort(idx.v, idx.n, sizeof *idx.v, cmp_rowidx);

    const uint8_t *matrix = NULL; size_t matrix_n = 0;
    hblocks rowblocks = {0, 0};
    unsigned rpb = OP_BLOCKMAX / rs;
    if ((hnid_rows & 0x1F) == 0) {
        rc = heap_get(hp, hnid_rows, &matrix, &matrix_n);
    } else {
        tc->rows_nid = hnid_rows;
        const wsub *s = find_wsub(subs, hnid_rows);
        if (!s) rc = op_err(OPST_E_FORMAT, "subnode 0x%x missing", hnid_rows);
        else rc = opw_leaf_blocks(w, s->bd, &rowblocks);
    }
    if (rc) { free(idx.v); return rc; }
    size_t cbebytes = (size_t)tc->rgib[3] - tc->rgib[2];
    for (size_t k = 0; k < idx.n && !rc; k++) {
        const uint8_t *row = NULL;
        size_t i = idx.v[k].idx;
        if (matrix) { if ((i + 1) * rs <= matrix_n) row = matrix + i * rs; }
        else {
            size_t bi = i / rpb, ri = i % rpb;
            if (bi < rowblocks.n && (ri + 1) * rs <= rowblocks.b[bi].n) row = rowblocks.b[bi].p + ri * rs;
        }
        if (!row) { rc = BAD("row %zu missing from row matrix", i); break; }
        rc = tc_add_row(tc, idx.v[k].id);
        if (rc) break;
        size_t r = tc->nrows - 1;
        const uint8_t *ceb = row + tc->rgib[2];
        for (size_t c = 0; c < tc->ncols && !rc; c++) {
            tcol *col = &tc->cols[c];
            if ((size_t)(col->ibit >> 3) >= cbebytes || !(ceb[col->ibit >> 3] & (0x80 >> (col->ibit & 7)))) continue;
            if (fixed_size(col->ptype)) {
                if ((size_t)col->ibd + col->cbd > rs) { rc = BAD("column outside the row"); break; }
                rc = tc_set_cell(tc, r, (int)c, row + col->ibd, col->cbd);
            } else {
                if ((size_t)col->ibd + 4 > rs) { rc = BAD("column outside the row"); break; }
                uint32_t h = op_u32(row + col->ibd);
                if (h == 0) rc = tc_set_cell(tc, r, (int)c, (const uint8_t *)"", 0);
                else if ((h & 0x1F) == 0) {
                    const uint8_t *d; size_t n;
                    rc = heap_get(hp, h, &d, &n);
                    if (!rc) rc = tc_set_cell(tc, r, (int)c, d, n);
                } else {
                    bbuf data = {0};
                    rc = sub_concat(w, subs, h, &data);
                    if (!rc) rc = tc_set_cell(tc, r, (int)c, data.p, data.n);
                    bb_free(&data);
                }
                (void)0;
            }
            if (!rc) tc->rows[r].present[c] = 1;
        }
    }
    hb_free(&rowblocks);
    free(idx.v);
    if (rc) tc_free(tc);
    return rc;
}

int ed_load_tc(opw *w, uint32_t nid, tctx *out) {
    nbt_e e;
    memset(out, 0, sizeof *out);                  /* always safe to tc_free(), also after an error */
    int rc = opw_node(w, nid, &e);
    if (rc == OPST_E_NOTFOUND) return op_err(OPST_E_NOTFOUND, "node 0x%x not found", nid);
    if (rc) return rc;
    wsubs subs;
    rc = opw_subnodes(w, e.bs, &subs);
    if (rc) return rc;
    hblocks hp;
    rc = opw_leaf_blocks(w, e.bd, &hp);
    if (!rc) { rc = tc_parse(w, &hp, &subs, out); hb_free(&hp); }
    wsubs_free(&subs);
    return rc;
}

/* ---- build --------------------------------------------------------------------------------------------------------------------- */
typedef struct { uint32_t id; uint32_t pos; uint8_t k[4], v[4]; } recsort;
static int cmp_recsort(const void *a, const void *b) {
    uint32_t x = ((const recsort *)a)->id, y = ((const recsort *)b)->id;
    return x < y ? -1 : x > y;
}

void tcbig_free(tcbig *b, size_t n) { for (size_t i = 0; i < n; i++) free(b[i].p); free(b); }

int tcbig_put(opw *w, const tcbig *b, size_t n, wsub *out) {
    for (size_t i = 0; i < n; i++) {
        size_t nb = (b[i].n + OP_BLOCKMAX - 1) / OP_BLOCKMAX;
        opbuf *bl = (opbuf *)calloc(nb ? nb : 1, sizeof *bl);
        if (!bl) return OPST_E_NOMEM;
        for (size_t k = 0; k < nb; k++) { bl[k].p = b[i].p + k * OP_BLOCKMAX; bl[k].n = b[i].n - k * OP_BLOCKMAX < OP_BLOCKMAX ? b[i].n - k * OP_BLOCKMAX : OP_BLOCKMAX; }
        hblocks hb = {bl, nb};
        uint64_t top = 0;
        int rc = opw_put_blocks(w, &hb, &top);
        free(bl);
        if (rc) return rc;
        out[i].nid = b[i].nid; out[i].bd = top; out[i].bs = 0;
    }
    return 0;
}

int tc_build(const tctx *t, hblocks *heap, hblocks *rowblocks, uint32_t *rows_nid) { return tc_build_ex(t, NULL, heap, rowblocks, rows_nid, NULL, NULL); }

int tc_build_ex(const tctx *t, opw *w, hblocks *heap, hblocks *rowblocks, uint32_t *rows_nid, tcbig **bigs, size_t *nbigs) {
    heap->b = NULL; heap->n = 0;
    if (bigs) *bigs = NULL;
    if (nbigs) *nbigs = 0;
    size_t capbig = 0;
    rowblocks->b = NULL; rowblocks->n = 0;
    *rows_nid = 0;
    unsigned rs = t->rgib[3];
    hbuild hb;
    int rc = hbuild_init(&hb, 0x7C);
    if (rc) return rc;
    uint32_t h_ri = 0, h_info = 0;
    rc = hbuild_alloc(&hb, NULL, 8, &h_ri);
    if (!rc) rc = hbuild_alloc(&hb, NULL, 22 + 8 * t->ncols, &h_info);
    uint8_t *rowbuf = NULL;
    size_t rowbytes = (size_t)t->nrows * rs;
    if (!rc && t->nrows) {
        rowbuf = (uint8_t *)calloc(1, rowbytes);
        if (!rowbuf) rc = OPST_E_NOMEM;
    }
    size_t cebn = (size_t)t->rgib[3] - t->rgib[2];
    for (size_t r = 0; r < t->nrows && !rc; r++) {
        uint8_t *row = rowbuf + r * rs;
        uint8_t *ceb = row + t->rgib[2];
        for (size_t c = 0; c < t->ncols && !rc; c++) {
            if (!t->rows[r].present[c]) continue;
            const tcol *col = &t->cols[c];
            const bbuf *v = &t->rows[r].cell[c];
            if ((size_t)(col->ibit >> 3) < cebn) ceb[col->ibit >> 3] |= (uint8_t)(0x80 >> (col->ibit & 7));
            if (fixed_size(col->ptype)) {
                size_t m = v->n < col->cbd ? v->n : col->cbd;
                if (m) memcpy(row + col->ibd, v->p, m);
            } else if (v->n > OP_MAXALLOC && w && bigs && nbigs) {            /* too large for the heap: a subnode of the table node */
                uint32_t idx = op_u32(w->hdr + 44 + 4 * 0x1F) + 1;
                wr32(w->hdr + 44 + 4 * 0x1F, idx);
                if (*nbigs == capbig) {
                    capbig = capbig ? capbig * 2 : 4;
                    tcbig *nb = (tcbig *)realloc(*bigs, capbig * sizeof *nb);
                    if (!nb) { rc = OPST_E_NOMEM; break; }
                    *bigs = nb;
                }
                tcbig *bg = &(*bigs)[(*nbigs)++];
                bg->nid = (idx << 5) | 0x1F; bg->n = v->n;
                bg->p = (uint8_t *)malloc(v->n);
                if (!bg->p) { rc = OPST_E_NOMEM; break; }
                memcpy(bg->p, v->p, v->n);
                wr32(row + col->ibd, bg->nid);
            } else if (v->n) {
                uint32_t hid;
                rc = hbuild_alloc(&hb, v->p, v->n, &hid);
                if (!rc) wr32(row + col->ibd, hid);
            }
        }
    }
    uint32_t hnid_rows = 0;
    int use_sub = 0;
    if (!rc && t->nrows) {
        if (rowbytes <= OP_MAXALLOC) rc = hbuild_alloc(&hb, rowbuf, rowbytes, &hnid_rows);
        else {
            use_sub = 1;
            hnid_rows = t->rows_nid ? t->rows_nid : 0x3F;
            unsigned rpb = OP_BLOCKMAX / rs;
            size_t nb = (t->nrows + rpb - 1) / rpb;
            for (size_t i = 0; i < nb && !rc; i++) {
                size_t first = i * rpb, m = t->nrows - first < rpb ? t->nrows - first : rpb;
                size_t len = (i + 1 < nb) ? OP_BLOCKMAX : m * rs;           /* every non-final row block is padded to a full block */
                uint8_t *blk = (uint8_t *)calloc(1, len ? len : 1);
                if (!blk) { rc = OPST_E_NOMEM; break; }
                memcpy(blk, rowbuf + first * rs, m * rs);
                opbuf *tb = (opbuf *)realloc(rowblocks->b, (rowblocks->n + 1) * sizeof *tb);
                if (!tb) { free(blk); rc = OPST_E_NOMEM; break; }
                rowblocks->b = tb;
                rowblocks->b[rowblocks->n].p = blk; rowblocks->b[rowblocks->n].n = len; rowblocks->n++;
            }
        }
    }
    free(rowbuf);
    recsort *rs_ = NULL;
    kv *recs = NULL;
    if (!rc && t->nrows) {
        rs_ = (recsort *)malloc(t->nrows * sizeof *rs_);
        recs = (kv *)malloc(t->nrows * sizeof *recs);
        if (!rs_ || !recs) rc = OPST_E_NOMEM;
        for (size_t i = 0; i < t->nrows && !rc; i++) { rs_[i].id = t->rows[i].rowid; rs_[i].pos = (uint32_t)i; }
        if (!rc) {
            op_qsort(rs_, t->nrows, sizeof *rs_, cmp_recsort);        /* ties keep no particular order; duplicates are an error anyway */
            for (size_t i = 0; i < t->nrows; i++) {
                if (i && rs_[i].id == rs_[i - 1].id) { rc = op_err(OPST_E_ARG, "duplicate row ids"); break; }
                wr32(rs_[i].k, rs_[i].id); wr32(rs_[i].v, rs_[i].pos);
                recs[i].k = rs_[i].k; recs[i].v = rs_[i].v;
            }
        }
    }
    if (!rc) rc = build_bth(&hb, 4, 4, recs, t->nrows, h_ri, NULL);
    free(rs_); free(recs);
    if (!rc) {
        bbuf info = {0};
        bb_u8(&info, 0x7C); bb_u8(&info, (unsigned)t->ncols);
        for (int i = 0; i < 4; i++) bb_u16(&info, t->rgib[i]);
        bb_u32(&info, h_ri); bb_u32(&info, hnid_rows); bb_u32(&info, 0);
        for (size_t c = 0; c < t->ncols; c++) {
            bb_u32(&info, ((uint32_t)t->cols[c].pid << 16) | t->cols[c].ptype);
            bb_u16(&info, t->cols[c].ibd); bb_u8(&info, t->cols[c].cbd); bb_u8(&info, t->cols[c].ibit);
        }
        if (info.bad) rc = OPST_E_NOMEM;
        if (!rc) rc = hbuild_set(&hb, h_info, info.p, info.n);
        bb_free(&info);
    }
    if (!rc) rc = hbuild_finalize(&hb, h_info, heap);
    hbuild_free(&hb);
    if (rc) { hb_free(rowblocks); hb_free(heap); if (bigs) { tcbig_free(*bigs, nbigs ? *nbigs : 0); *bigs = NULL; if (nbigs) *nbigs = 0; } return rc; }
    *rows_nid = use_sub ? hnid_rows : 0;
    return 0;
}

/* Editor.store_tc: new blocks for the table, old ones released; unchanged subnodes are kept (reference counted) */
static int cmp_wsub_nid(const void *a, const void *b) { uint32_t x = ((const wsub *)a)->nid, y = ((const wsub *)b)->nid; return x < y ? -1 : x > y; }

int ed_store_tc(opw *w, uint32_t nid, tctx *tc) {
    nbt_e e;
    int rc = opw_node(w, nid, &e);
    if (rc) return rc;
    hblocks heap, rows;
    uint32_t rows_nid;
    tcbig *bigs = NULL;
    size_t nbig = 0;
    rc = tc_build_ex(tc, w, &heap, &rows, &rows_nid, &bigs, &nbig);
    if (rc) return rc;
    /* every subnode of a table node is its row matrix or a large cell value, and all of them are rebuilt with the table */
    wsub *ns = (wsub *)calloc(nbig + 2, sizeof *ns);
    size_t nn = 0;
    uint64_t new_bd = 0, new_bs = 0;
    if (!ns) rc = OPST_E_NOMEM;
    if (!rc) rc = opw_put_blocks(w, &heap, &new_bd);
    if (!rc && rows.n) {
        uint64_t top;
        rc = opw_put_blocks(w, &rows, &top);
        if (!rc) { ns[nn].nid = rows_nid; ns[nn].bd = top; ns[nn].bs = 0; nn++; }
    }
    if (!rc && nbig) { rc = tcbig_put(w, bigs, nbig, ns + nn); nn += nbig; }
    if (!rc) op_qsort(ns, nn, sizeof *ns, cmp_wsub_nid);
    if (!rc) rc = opw_put_subnodes(w, ns, nn, &new_bs);
    if (!rc) rc = opw_node_put(w, nid, new_bd, new_bs, e.parent);
    if (!rc) rc = opw_release(w, e.bd);
    if (!rc && e.bs) rc = opw_release(w, e.bs);
    if (!rc) tc->rows_nid = rows_nid;
    free(ns);
    tcbig_free(bigs, nbig);
    hb_free(&heap); hb_free(&rows);
    return rc;
}

/* ---- property contexts -------------------------------------------------------------------------------------------------------- */
typedef struct { pcprops *out; int oom; } pcctx;
typedef struct { const hblocks *hp; pcprops *out; int err; int lenient; } pcw;

static int pcprops_cb(void *ctx, const uint8_t *k, const uint8_t *v) {
    pcw *c = (pcw *)ctx;
    unsigned pid = op_u16(k), pt = op_u16(v);
    uint32_t val = op_u32(v + 2);
    int fs = fixed_size(pt);
    int rc;
    if (fs && fs <= 4) {
        uint8_t b[4];
        wr32(b, val);
        return pcprops_set(c->out, pid, pt, b, (size_t)fs);
    }
    if (val == 0) return pcprops_set(c->out, pid, pt, (const uint8_t *)"", 0);
    if (val & 0x1F) {
        if (c->lenient == 2) {                                   /* keep the reference: pc_build writes the subnode NID back unchanged */
            rc = pcprops_set(c->out, pid, pt, (const uint8_t *)"", 0);
            if (!rc) pcprops_find(c->out, pid)->ext_nid = val;
            return rc;
        }
        return c->lenient ? 0 : op_err(OPST_E_UNSUPPORTED, "property 0x%04x is stored in a subnode (unsupported)", pid);
    }
    const uint8_t *d; size_t n;
    rc = heap_get(c->hp, val, &d, &n);
    if (rc) return rc;
    return pcprops_set(c->out, pid, pt, d, n);
}

int pcprops_get_ex(opw *w, uint32_t nid, pcprops *out, int lenient) {
    memset(out, 0, sizeof *out);
    nbt_e e;
    int rc = opw_node(w, nid, &e);
    if (rc == OPST_E_NOTFOUND) return op_err(OPST_E_NOTFOUND, "node 0x%x not found", nid);
    if (rc) return rc;
    hblocks hp;
    rc = opw_leaf_blocks(w, e.bd, &hp);
    if (rc) return rc;
    if (!heap_is_heap(&hp) || heap_client(&hp) != 0xBC) { hb_free(&hp); return op_err(OPST_E_FORMAT, "node 0x%x is not a property context", nid); }
    pcw c = {&hp, out, 0, lenient};
    rc = heap_bth_walk(&hp, heap_root(&hp), NULL, NULL, pcprops_cb, &c);
    hb_free(&hp);
    if (rc) pcprops_free(out);
    return rc;
}

int pcprops_get(opw *w, uint32_t nid, pcprops *out) { return pcprops_get_ex(w, nid, out, 0); }

static int cmp_pcprop(const void *a, const void *b) {
    unsigned x = ((const pcprop *)a)->pid, y = ((const pcprop *)b)->pid;
    return x < y ? -1 : x > y;
}

int pc_build(const pcprops *props, hblocks *out) {
    size_t n = props->n;
    out->b = NULL; out->n = 0;
    if (n == 0 || n * 8 > OP_MAXALLOC) return op_err(OPST_E_ARG, "property count out of range");
    if (n > OP_MAXALLOC * 9 / 10 / 8) return op_err(OPST_E_UNSUPPORTED, "too many properties for a single-leaf property context");
    pcprop *sorted = (pcprop *)malloc(n * sizeof *sorted);
    if (!sorted) return NOMEM;
    memcpy(sorted, props->p, n * sizeof *sorted);
    op_qsort(sorted, n, sizeof *sorted, cmp_pcprop);
    hbuild hb;
    int rc = hbuild_init(&hb, 0xBC);
    uint32_t hh = 0;
    uint8_t (*keys)[2] = (uint8_t (*)[2])malloc(n * 2);
    uint8_t (*vals)[6] = (uint8_t (*)[6])malloc(n * 6);
    kv *recs = (kv *)malloc(n * sizeof *recs);
    size_t *var = (size_t *)malloc(n * sizeof *var);
    size_t nvar = 0;
    if (!keys || !vals || !recs || !var) rc = OPST_E_NOMEM;
    if (!rc) rc = hbuild_alloc(&hb, NULL, 8, &hh);
    uint32_t nxt = 3;                       /* heap item after the header (1) and the single BTH leaf (2) */
    for (size_t i = 0; i < n && !rc; i++) {
        const pcprop *p = &sorted[i];
        uint32_t val;
        int fs = fixed_size(p->ptype);
        if (fs && fs <= 4) { uint8_t b[4] = {0, 0, 0, 0}; if (p->v.n) memcpy(b, p->v.p, p->v.n < 4 ? p->v.n : 4); val = op_u32(b); }
        else if (p->ext_nid) val = p->ext_nid;                 /* stored in a subnode */
        else if (p->v.n == 0) val = 0;
        else { val = nxt << 5; nxt++; var[nvar++] = i; }
        wr16(keys[i], p->pid);
        wr16(vals[i], p->ptype); wr32(vals[i] + 2, val);
        recs[i].k = keys[i]; recs[i].v = vals[i];
    }
    if (!rc) rc = build_bth(&hb, 2, 6, recs, n, hh, NULL);
    int patched = 0;
    for (size_t j = 0; j < nvar && !rc; j++) {
        uint32_t got, want = op_u32(vals[var[j]] + 2);
        rc = hbuild_alloc(&hb, sorted[var[j]].v.p, sorted[var[j]].v.n, &got);
        if (!rc && got != want) { wr32(vals[var[j]] + 2, got); patched = 1; }          /* the heap spilled into another block */
    }
    if (!rc && patched) rc = hbuild_patch_bth_leaf(&hb, 0x40, recs, n, 2, 6);
    if (!rc) rc = hbuild_finalize(&hb, hh, out);
    hbuild_free(&hb);
    free(sorted); free(keys); free(vals); free(recs); free(var);
    return rc;
}

int pc_store(opw *w, uint32_t nid, const pcprops *p) {
    nbt_e e;
    int rc = opw_node(w, nid, &e);
    if (rc) return rc;
    hblocks hb;
    rc = pc_build(p, &hb);
    if (rc) return rc;
    uint64_t new_bd;
    rc = opw_put_blocks(w, &hb, &new_bd);
    hb_free(&hb);
    if (rc) return rc;
    if (e.bs) rc = opw_add_ref(w, e.bs);
    if (!rc) rc = opw_node_put(w, nid, new_bd, e.bs, e.parent);
    if (!rc) rc = opw_release(w, e.bd);
    if (!rc && e.bs) rc = opw_release(w, e.bs);
    return rc;
}
