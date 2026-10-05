/* op_ltp.c - LTP layer: heap-on-node, BTH, property contexts (PC) and table contexts (TC). */
#include "op_internal.h"

/* ---- heap-on-node ------------------------------------------------------------------------------------------------ */
int op_heap_open(opst *p, const opnode *node, opheap *h) {
    memset(h, 0, sizeof *h);
    h->p = p; h->node = *node;
    int rc = op_blocks_load(p, node->bd, &h->bl);
    if (rc) return rc;
    if (h->bl.n == 0 || h->bl.b[0].n < 12 || h->bl.b[0].p[2] != 0xEC) {
        op_blocks_free(&h->bl);
        return op_err(OPST_E_FORMAT, "node 0x%x is not a heap node", node->nid);
    }
    h->client_sig = h->bl.b[0].p[3];
    h->user_root = op_u32(h->bl.b[0].p + 4);
    return 0;
}
void op_heap_close(opheap *h) {
    op_blocks_free(&h->bl);
    if (h->subs_loaded) op_subs_free(&h->subs);
    h->subs_loaded = 0;
}

int op_heap_get(const opheap *h, uint32_t hid, const uint8_t **d, size_t *n) {
    static const uint8_t empty[1] = {0};
    unsigned idx = (hid >> 5) & 0x7FF;
    size_t blk = hid >> 16;
    if (h->p->fmt == OP_FMT_UNI4K) blk >>= 3;        /* 4K files: every data block (up to 64 KB) is one heap page and takes 8 block indexes */
    if (idx == 0) { *d = empty; *n = 0; return 0; }
    if (blk >= h->bl.n) return op_err(OPST_E_FORMAT, "HID 0x%x: heap block missing", hid);
    const opbuf *b = &h->bl.b[blk];
    if (b->n < 2) return op_err(OPST_E_FORMAT, "HID 0x%x: empty heap block", hid);
    size_t pm = op_u16(b->p);
    if (pm + 4 > b->n) return op_err(OPST_E_FORMAT, "HID 0x%x: bad page map", hid);
    unsigned calloc_ = op_u16(b->p + pm);
    if (idx > calloc_ || pm + 4 + 2 * (size_t)idx + 2 > b->n) return op_err(OPST_E_FORMAT, "HID 0x%x out of range", hid);
    size_t s = op_u16(b->p + pm + 4 + 2 * (size_t)(idx - 1)), e = op_u16(b->p + pm + 4 + 2 * (size_t)idx);
    if (s > e || e > b->n) return op_err(OPST_E_FORMAT, "HID 0x%x has a bad extent", hid);
    *d = b->p + s; *n = e - s;
    return 0;
}

static int bth_rec(const opheap *h, uint32_t hid, unsigned level, unsigned cbkey, unsigned cbent, op_bth_cb cb, void *ctx) {
    const uint8_t *d; size_t n;
    int rc = op_heap_get(h, hid, &d, &n);
    if (rc) return rc;
    if (level == 0) {
        size_t sz = (size_t)cbkey + cbent;
        for (size_t i = 0; i + sz <= n; i += sz) {
            rc = cb(ctx, d + i, d + i + cbkey);
            if (rc) return rc;
        }
    } else {
        size_t sz = (size_t)cbkey + 4;
        for (size_t i = 0; i + sz <= n; i += sz) {
            rc = bth_rec(h, op_u32(d + i + cbkey), level - 1, cbkey, cbent, cb, ctx);
            if (rc) return rc;
        }
    }
    return 0;
}

int op_bth_walk(const opheap *h, uint32_t hid, unsigned *pkey, unsigned *pent, op_bth_cb cb, void *ctx) {
    const uint8_t *d; size_t n;
    int rc = op_heap_get(h, hid, &d, &n);
    if (rc) return rc;
    if (n < 8 || d[0] != 0xB5) return op_err(OPST_E_FORMAT, "bad BTH header at HID 0x%x", hid);
    unsigned cbkey = d[1], cbent = d[2], levels = d[3];
    uint32_t root = op_u32(d + 4);
    if (pkey) *pkey = cbkey;
    if (pent) *pent = cbent;
    if (cbkey == 0 || levels > 8) return op_err(OPST_E_FORMAT, "bad BTH at HID 0x%x", hid);
    if (!root) return 0;
    return bth_rec(h, root, levels, cbkey, cbent, cb, ctx);
}

const opsubs *op_heap_subs(opheap *h) {
    if (!h->subs_loaded) {
        if (op_subs_load(h->p, h->node.bs, &h->subs) != 0) { h->subs.e = NULL; h->subs.n = 0; }
        h->subs_loaded = 1;
    }
    return &h->subs;
}

int op_heap_resolve(opheap *h, uint32_t hnid, const uint8_t **d, size_t *n, void **tofree) {
    static const uint8_t empty[1] = {0};
    *tofree = NULL;
    if (hnid == 0) { *d = empty; *n = 0; return 0; }
    if ((hnid & 0x1F) == 0) return op_heap_get(h, hnid, d, n);
    const opsub *s = op_subs_find(op_heap_subs(h), hnid);
    if (!s) return op_err(OPST_E_NOTFOUND, "subnode 0x%x is missing", hnid);
    opblocks bl;
    int rc = op_blocks_load(h->p, s->bd, &bl);
    if (rc) return rc;
    uint8_t *buf; size_t len;
    rc = op_data_concat(&bl, &buf, &len);
    op_blocks_free(&bl);
    if (rc) return rc;
    *d = buf; *n = len; *tofree = buf;
    return 0;
}

/* ---- property context --------------------------------------------------------------------------------------------- */
static int is_inline_type(unsigned t) { return t == 2 || t == 3 || t == 4 || t == 0xA || t == 0xB; }

typedef struct { oppc_prop *a; size_t n, cap; } propvec;
static int pc_cb(void *ctx, const uint8_t *k, const uint8_t *v) {
    propvec *pv = (propvec *)ctx;
    if (pv->n == pv->cap) {
        size_t nc = pv->cap ? pv->cap * 2 : 64;
        oppc_prop *t = (oppc_prop *)realloc(pv->a, nc * sizeof *t);
        if (!t) return op_err(OPST_E_NOMEM, "out of memory");
        pv->a = t; pv->cap = nc;
    }
    oppc_prop *pr = &pv->a[pv->n++];
    memset(pr, 0, sizeof *pr);
    pr->pid = op_u16(k);
    pr->ptype = op_u16(v);
    pr->hnid = op_u32(v + 2);
    memcpy(pr->inl, v + 2, 4);
    return 0;
}
static int cmp_prop(const void *a, const void *b) {
    const oppc_prop *x = (const oppc_prop *)a, *y = (const oppc_prop *)b;
    return x->pid < y->pid ? -1 : x->pid > y->pid;
}

int op_pc_open(opst *p, const opnode *node, oppc *pc) {
    memset(pc, 0, sizeof *pc);
    int rc = op_heap_open(p, node, &pc->heap);
    if (rc) return rc;
    if (pc->heap.client_sig != 0xBC) {
        op_heap_close(&pc->heap);
        return op_err(OPST_E_FORMAT, "node 0x%x is not a property context (client signature 0x%x)", node->nid, pc->heap.client_sig);
    }
    propvec pv = {NULL, 0, 0};
    unsigned ck, ce;
    rc = op_bth_walk(&pc->heap, pc->heap.user_root, &ck, &ce, pc_cb, &pv);
    if (rc == 0 && (ck != 2 || ce != 6)) rc = op_err(OPST_E_FORMAT, "unexpected property BTH geometry on node 0x%x", node->nid);
    if (rc) { free(pv.a); op_heap_close(&pc->heap); return rc; }
    op_qsort(pv.a, pv.n, sizeof *pv.a, cmp_prop);
    pc->props = pv.a; pc->n = pv.n;
    return 0;
}

void op_pc_close(oppc *pc) {
    for (size_t i = 0; i < pc->n; i++) if (pc->props[i].state == 2) free((void *)pc->props[i].d);
    free(pc->props);
    pc->props = NULL; pc->n = 0;
    op_heap_close(&pc->heap);
}

oppc_prop *op_pc_find(oppc *pc, uint16_t pid) {
    size_t lo = 0, hi = pc->n;
    while (lo < hi) {
        size_t mid = (lo + hi) / 2;
        if (pc->props[mid].pid < pid) lo = mid + 1; else hi = mid;
    }
    return (lo < pc->n && pc->props[lo].pid == pid) ? &pc->props[lo] : NULL;
}

int op_pc_value(oppc *pc, oppc_prop *pr) {
    static const uint8_t empty[1] = {0};
    if (pr->state) return 0;
    if (is_inline_type(pr->ptype)) { pr->d = pr->inl; pr->n = 4; pr->state = 1; return 0; }
    const uint8_t *d; size_t n; void *tofree;
    int rc = op_heap_resolve(&pc->heap, pr->hnid, &d, &n, &tofree);
    if (rc) { pr->d = empty; pr->n = 0; pr->state = 1; return rc; }
    pr->d = d; pr->n = n; pr->state = tofree ? 2 : 1;
    return 0;
}

/* ---- table context ------------------------------------------------------------------------------------------------- */
typedef struct { uint32_t *id, *ix; size_t n, cap; unsigned ce; } rowvec;     /* ce: width of a row index (4; 2 in ANSI files) */
static int row_cb(void *ctx, const uint8_t *k, const uint8_t *v) {
    rowvec *rv = (rowvec *)ctx;
    if (rv->n == rv->cap) {
        size_t nc = rv->cap ? rv->cap * 2 : 256;
        uint32_t *a = (uint32_t *)realloc(rv->id, nc * 4), *b;
        if (!a) return op_err(OPST_E_NOMEM, "out of memory");
        rv->id = a;
        b = (uint32_t *)realloc(rv->ix, nc * 4);
        if (!b) return op_err(OPST_E_NOMEM, "out of memory");
        rv->ix = b; rv->cap = nc;
    }
    rv->id[rv->n] = op_u32(k);
    rv->ix[rv->n] = rv->ce == 2 ? op_u16(v) : op_u32(v);
    rv->n++;
    return 0;
}

int op_tc_open(opst *p, const opnode *node, optc *tc) {
    memset(tc, 0, sizeof *tc);
    tc->blkmax = p->blk_data_max ? p->blk_data_max : 8176;
    int rc = op_heap_open(p, node, &tc->heap);
    if (rc) return rc;
    if (tc->heap.client_sig != 0x7C) {
        op_heap_close(&tc->heap);
        return op_err(OPST_E_FORMAT, "node 0x%x is not a table context", node->nid);
    }
    const uint8_t *info; size_t n;
    rc = op_heap_get(&tc->heap, tc->heap.user_root, &info, &n);
    if (rc) { op_heap_close(&tc->heap); return rc; }
    if (n < 22 || info[0] != 0x7C || n < 22 + (size_t)info[1] * 8) { op_heap_close(&tc->heap); return op_err(OPST_E_FORMAT, "bad table info on node 0x%x", node->nid); }
    tc->ncols = info[1];
    for (int i = 0; i < 4; i++) tc->rgib[i] = op_u16(info + 2 + 2 * i);
    uint32_t hid_rowindex = op_u32(info + 10), hnid_rows = op_u32(info + 14);
    tc->rowsize = tc->rgib[3];
    tc->cols = (optc_col *)calloc(tc->ncols ? tc->ncols : 1, sizeof *tc->cols);
    if (!tc->cols) { op_heap_close(&tc->heap); return op_err(OPST_E_NOMEM, "out of memory"); }
    for (size_t i = 0; i < tc->ncols; i++) {
        const uint8_t *c = info + 22 + 8 * i;
        uint32_t tag = op_u32(c);
        tc->cols[i].pid = (uint16_t)(tag >> 16); tc->cols[i].ptype = (uint16_t)(tag & 0xFFFF);
        tc->cols[i].ibdata = op_u16(c + 4); tc->cols[i].cbdata = c[6]; tc->cols[i].ibit = c[7];
    }
    if (hnid_rows == 0 || hid_rowindex == 0 || tc->rowsize == 0) return 0;
    rowvec rv = {NULL, NULL, 0, 0, 4};
    unsigned ck, ce;
    {
        const uint8_t *bh; size_t bn;
        if (op_heap_get(&tc->heap, hid_rowindex, &bh, &bn) == 0 && bn >= 8 && bh[0] == 0xB5 && bh[2] == 2) rv.ce = 2;
    }
    rc = op_bth_walk(&tc->heap, hid_rowindex, &ck, &ce, row_cb, &rv);
    if (rc == 0 && (ck != 4 || (ce != 4 && ce != 2))) rc = op_err(OPST_E_FORMAT, "unexpected row index BTH geometry on node 0x%x", node->nid);
    if (rc) { free(rv.id); free(rv.ix); op_tc_close(tc); return rc; }
    tc->rowid = rv.id; tc->rowidx = rv.ix; tc->nrows = rv.n;
    if ((hnid_rows & 0x1F) == 0) {
        rc = op_heap_get(&tc->heap, hnid_rows, &tc->rowheap, &tc->rowheap_n);
        if (rc) { op_tc_close(tc); return rc; }
    } else {
        const opsub *s = op_subs_find(op_heap_subs(&tc->heap), hnid_rows);
        tc->rows_in_sub = 1;
        if (s) {
            rc = op_blocks_load(p, s->bd, &tc->rowblocks);
            if (rc) { op_tc_close(tc); return rc; }
        }
    }
    return 0;
}

void op_tc_close(optc *tc) {
    free(tc->cols); free(tc->rowid); free(tc->rowidx);
    op_blocks_free(&tc->rowblocks);
    op_heap_close(&tc->heap);
    tc->cols = NULL; tc->rowid = tc->rowidx = NULL; tc->nrows = 0;
}

const uint8_t *op_tc_row(const optc *tc, size_t i) {
    if (i >= tc->nrows) return NULL;
    uint32_t ridx = tc->rowidx[i];
    if (!tc->rows_in_sub) {
        uint64_t off = (uint64_t)ridx * tc->rowsize;
        if (off + tc->rowsize > tc->rowheap_n) return NULL;
        return tc->rowheap + off;
    }
    uint32_t rpb = tc->blkmax / tc->rowsize;
    if (rpb == 0) return NULL;
    size_t bi = ridx / rpb;
    uint64_t off = (uint64_t)(ridx % rpb) * tc->rowsize;
    if (bi >= tc->rowblocks.n || off + tc->rowsize > tc->rowblocks.b[bi].n) return NULL;
    return tc->rowblocks.b[bi].p + off;
}

int op_tc_col(const optc *tc, uint16_t pid) {
    for (size_t i = 0; i < tc->ncols; i++) if (tc->cols[i].pid == pid) return (int)i;
    return -1;
}

static int is_fixed_type(unsigned t) {
    return t == 2 || t == 3 || t == 4 || t == 5 || t == 6 || t == 7 || t == 0xA || t == 0xB || t == 0x14 || t == 0x40;
}

int op_tc_cell(optc *tc, const uint8_t *row, int col, uint16_t *ptype, const uint8_t **d, size_t *n, void **tofree) {
    *tofree = NULL;
    if (col < 0 || (size_t)col >= tc->ncols || !row) return 0;
    const optc_col *c = &tc->cols[col];
    size_t cebn = tc->rgib[3] > tc->rgib[2] ? (size_t)(tc->rgib[3] - tc->rgib[2]) : 0;
    if ((size_t)(c->ibit >> 3) >= cebn) return 0;
    if (!(row[tc->rgib[2] + (c->ibit >> 3)] & (0x80 >> (c->ibit & 7)))) return 0;
    *ptype = c->ptype;
    if (is_fixed_type(c->ptype)) {
        if ((size_t)c->ibdata + c->cbdata > tc->rowsize) return 0;
        *d = row + c->ibdata; *n = c->cbdata;
        return 1;
    }
    if ((size_t)c->ibdata + 4 > tc->rowsize) return 0;
    if (op_heap_resolve(&tc->heap, op_u32(row + c->ibdata), d, n, tofree) != 0) {
        static const uint8_t empty[1] = {0};
        *d = empty; *n = 0; *tofree = NULL;
    }
    return 1;
}

/* ---- value decoding --------------------------------------------------------------------------------------------- */
char *op_value_to_utf8(uint16_t ptype, const uint8_t *d, size_t n, unsigned cp, size_t *outlen) {
    if (ptype == 0x1F) {
        while (n >= 2 && d[n - 2] == 0 && d[n - 1] == 0) n -= 2;     /* trailing NULs are padding, not text */
        return op_utf16_to_utf8(d, n, outlen);
    }
    if (ptype == 0x1E) {
        while (n > 0 && d[n - 1] == 0) n--;
        return op_ansi_to_utf8(d, n, cp, outlen);
    }
    return NULL;
}

int64_t op_value_i64(uint16_t ptype, const uint8_t *d, size_t n, int64_t dflt) {
    switch (ptype) {
    case 0x02: if (n >= 2) return (int16_t)op_u16(d); break;
    case 0x03: case 0x0A: if (n >= 4) return (int32_t)op_u32(d); break;
    case 0x0B: if (n >= 1) return d[0] != 0; break;
    case 0x14: case 0x40: if (n >= 8) return (int64_t)op_u64(d); break;
    default: break;
    }
    return dflt;
}
