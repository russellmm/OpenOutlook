/* op_edit.c - heap-on-node reading / editing / building, BTree-on-heap builder, table contexts, property contexts and
 * node-level editing through a writer transaction (port of pstedit.py and the heap helpers of pstops.py / pstfolders.py). */
#include "op_wr.h"

#define NOMEM op_err(OPST_E_NOMEM, "out of memory")
#define BAD(...) op_err(OPST_E_FORMAT, __VA_ARGS__)

/* ---- hblocks ------------------------------------------------------------------------------------------------------------------ */
void hb_free(hblocks *h) {
    for (size_t i = 0; i < h->n; i++) free(h->b[i].p);
    free(h->b);
    h->b = NULL; h->n = 0;
}
static int hb_push(hblocks *h, uint8_t *p, size_t n) {          /* takes ownership of p */
    opbuf *t = (opbuf *)realloc(h->b, (h->n + 1) * sizeof *t);
    if (!t) { free(p); return NOMEM; }
    h->b = t;
    h->b[h->n].p = p; h->b[h->n].n = n; h->n++;
    return 0;
}
int hb_clone(const hblocks *src, hblocks *dst) {
    dst->b = NULL; dst->n = 0;
    for (size_t i = 0; i < src->n; i++) {
        uint8_t *c = (uint8_t *)malloc(src->b[i].n ? src->b[i].n : 1);
        if (!c) { hb_free(dst); return NOMEM; }
        memcpy(c, src->b[i].p, src->b[i].n);
        if (hb_push(dst, c, src->b[i].n)) { hb_free(dst); return OPST_E_NOMEM; }
    }
    return 0;
}

/* ---- heap reading --------------------------------------------------------------------------------------------------------------- */
int heap_is_heap(const hblocks *h) { return h->n && h->b[0].n >= 12 && h->b[0].p[2] == 0xEC; }
unsigned heap_client(const hblocks *h) { return h->b[0].p[3]; }
uint32_t heap_root(const hblocks *h) { return op_u32(h->b[0].p + 4); }

int heap_loc(const hblocks *h, uint32_t hid, size_t *blk, size_t *s, size_t *e) {
    uint32_t idx = (hid >> 5) & 0x7FF;
    size_t bi = hid >> 16;
    if ((hid & 0x1F) || idx == 0 || bi >= h->n) return BAD("bad HID 0x%x", hid);
    const uint8_t *b = h->b[bi].p;
    size_t bn = h->b[bi].n;
    if (bn < 2) return BAD("bad heap block");
    size_t pm = op_u16(b);
    if (pm + 4 > bn) return BAD("heap page map outside its block");
    if (idx > op_u16(b + pm)) return BAD("HID 0x%x out of range", hid);
    if (pm + 4 + 2 * (size_t)idx + 2 > bn) return BAD("heap page map outside its block");
    size_t a = op_u16(b + pm + 4 + 2 * (idx - 1)), z = op_u16(b + pm + 4 + 2 * (idx - 1) + 2);
    if (a > z || z > bn) return BAD("heap item 0x%x outside its block", hid);
    *blk = bi; *s = a; *e = z;
    return 0;
}
int heap_get(const hblocks *h, uint32_t hid, const uint8_t **d, size_t *n) {
    size_t blk, s, e;
    int rc = heap_loc(h, hid, &blk, &s, &e);
    if (rc) return rc;
    *d = h->b[blk].p + s; *n = e - s;
    return 0;
}

int heap_bth_walk(const hblocks *h, uint32_t hdr_hid, unsigned *cbk_out, unsigned *cbe_out, bth_cb cb, void *ctx);
static int bthw_rec(const hblocks *h, uint32_t x, unsigned level, unsigned cbk, unsigned cbe, bth_cb cb, void *ctx, int depth) {
    const uint8_t *d; size_t n;
    if (depth > 8) return BAD("BTH too deep");
    int rc = heap_get(h, x, &d, &n);
    if (rc) return rc;
    if (level == 0) {
        size_t sz = cbk + cbe;
        for (size_t i = 0; i < n / sz; i++) {
            rc = cb(ctx, d + i * sz, d + i * sz + cbk);
            if (rc) return rc;
        }
    } else {
        size_t sz = cbk + 4;
        for (size_t i = 0; i < n / sz; i++) {
            rc = bthw_rec(h, op_u32(d + i * sz + cbk), level - 1, cbk, cbe, cb, ctx, depth + 1);
            if (rc) return rc;
        }
    }
    return 0;
}
int heap_bth_walk(const hblocks *h, uint32_t hdr_hid, unsigned *cbk_out, unsigned *cbe_out, bth_cb cb, void *ctx) {
    const uint8_t *d; size_t n;
    int rc = heap_get(h, hdr_hid, &d, &n);
    if (rc) return rc;
    if (n < 8 || d[0] != 0xB5) return BAD("bad BTH header");
    unsigned cbk = d[1], cbe = d[2], lv = d[3];
    uint32_t root = op_u32(d + 4);
    if (cbk_out) *cbk_out = cbk;
    if (cbe_out) *cbe_out = cbe;
    if (!root || !cb) return 0;
    if (cbk + cbe == 0) return BAD("bad BTH header");
    return bthw_rec(h, root, lv, cbk, cbe, cb, ctx, 0);
}

/* locate a BTH leaf record by key: block index and offset of the value */
typedef struct { const hblocks *h; const uint8_t *key; unsigned cbk; size_t blk, off; int found; } findctx;
static int find_rec(const hblocks *h, uint32_t x, unsigned level, unsigned cbk, unsigned cbe, const uint8_t *key, size_t *blk, size_t *off, int depth) {
    size_t b, s, e;
    if (depth > 8) return 0;
    if (heap_loc(h, x, &b, &s, &e)) return 0;
    const uint8_t *d = h->b[b].p + s;
    size_t n = e - s;
    if (level == 0) {
        size_t sz = cbk + cbe;
        for (size_t i = 0; i < n / sz; i++) if (memcmp(d + i * sz, key, cbk) == 0) { *blk = b; *off = s + i * sz + cbk; return 1; }
    } else {
        size_t sz = cbk + 4;
        for (size_t i = 0; i < n / sz; i++) if (find_rec(h, op_u32(d + i * sz + cbk), level - 1, cbk, cbe, key, blk, off, depth + 1)) return 1;
    }
    return 0;
}
int heap_find_record(const hblocks *h, uint32_t bth_hid, const uint8_t *key, size_t *blk, size_t *off) {
    const uint8_t *d; size_t n;
    if (heap_get(h, bth_hid, &d, &n) || n < 8 || d[0] != 0xB5) return 0;
    uint32_t root = op_u32(d + 4);
    if (!root) return 0;
    return find_rec(h, root, d[3], d[1], d[2], key, blk, off, 0);
}

/* ---- heap editing ---------------------------------------------------------------------------------------------------------------- */
unsigned heap_fill_level(size_t free_b) {
    static const struct { size_t lim; unsigned v; } t[] = {{3584, 0}, {2560, 1}, {2048, 2}, {1792, 3}, {1536, 4}, {1280, 5}, {1024, 6}, {768, 7},
                                                        {512, 8}, {256, 9}, {128, 10}, {64, 11}, {32, 12}, {16, 13}, {8, 14}};
    for (size_t i = 0; i < sizeof t / sizeof t[0]; i++) if (free_b >= t[i].lim) return t[i].v;
    return 15;
}

static void set_fill(hblocks *h, size_t blk, unsigned fill) {
    if (blk < 8) {
        uint32_t v = op_u32(h->b[0].p + 8);
        v = (v & ~(0xFu << (4 * blk))) | ((uint32_t)fill << (4 * blk));
        wr32(h->b[0].p + 8, v);
    } else {
        size_t base = 8 + ((blk - 8) / 128) * 128, pos = blk - base, o = 2 + (pos >> 1), sh = 4 * (pos & 1);
        if (base < h->n && o < h->b[base].n) h->b[base].p[o] = (uint8_t)((h->b[base].p[o] & ~(0xF << sh)) | (fill << sh));
    }
}

/* replaces heap item `hid` by newdata (any size) inside its block, shifting later items. 1 done, 0 no room, <0 error */
int heap_grow_item(hblocks *h, uint32_t hid, const uint8_t *nd, size_t nn) {
    size_t blk, s, e;
    int rc = heap_loc(h, hid, &blk, &s, &e);
    if (rc) return rc;
    uint8_t *b = h->b[blk].p;
    size_t bn = h->b[blk].n;
    size_t idx = ((hid >> 5) & 0x7FF) - 1;
    size_t pm = op_u16(b);
    unsigned ca = op_u16(b + pm), cf = op_u16(b + pm + 2);
    if (pm + 4 + 2 * ((size_t)ca + 1) > bn) return BAD("heap page map outside its block");
    uint16_t *offs = (uint16_t *)malloc(((size_t)ca + 1) * sizeof *offs);
    if (!offs) return NOMEM;
    for (unsigned i = 0; i <= ca; i++) offs[i] = (uint16_t)op_u16(b + pm + 4 + 2 * i);
    bbuf body = {0};
    uint16_t *noffs = (uint16_t *)malloc(((size_t)ca + 1) * sizeof *noffs);
    if (!noffs) { free(offs); return NOMEM; }
    bb_put(&body, b, offs[0]);
    noffs[0] = offs[0];
    for (unsigned i = 0; i < ca; i++) {
        if (i == idx) bb_put(&body, nd, nn);
        else bb_put(&body, b + offs[i], (size_t)offs[i + 1] - offs[i]);
        noffs[i + 1] = (uint16_t)body.n;
    }
    if (body.n & 1) bb_u8(&body, 0);
    size_t newpm = body.n;
    bb_u16(&body, ca); bb_u16(&body, cf);
    for (unsigned i = 0; i <= ca; i++) bb_u16(&body, noffs[i]);
    free(offs); free(noffs);
    if (body.bad) { bb_free(&body); return NOMEM; }
    if (body.n > OP_BLOCKMAX) { bb_free(&body); return 0; }
    unsigned fill = heap_fill_level(OP_BLOCKMAX - body.n);
    wr16(body.p, (unsigned)newpm);
    if (bn == OP_BLOCKMAX) bb_zero(&body, OP_BLOCKMAX - body.n);
    if (body.bad) { bb_free(&body); return NOMEM; }
    free(h->b[blk].p);
    h->b[blk].p = body.p; h->b[blk].n = body.n;
    set_fill(h, blk, fill);
    return 1;
}

/* a new heap item at the end of the heap (last block, or a new one); no existing HID changes */
int heap_append_item(hblocks *h, const uint8_t *data, size_t n, uint32_t *hid) {
    size_t L = h->n - 1;
    uint8_t *b = h->b[L].p;
    size_t bn = h->b[L].n;
    size_t pm = op_u16(b);
    unsigned ca = op_u16(b + pm), cf = op_u16(b + pm + 2);
    if (pm + 4 + 2 * ((size_t)ca + 1) > bn) return BAD("heap page map outside its block");
    uint16_t *offs = (uint16_t *)malloc(((size_t)ca + 2) * sizeof *offs);
    if (!offs) return NOMEM;
    for (unsigned i = 0; i <= ca; i++) offs[i] = (uint16_t)op_u16(b + pm + 4 + 2 * i);
    bbuf body = {0};
    bb_put(&body, b, offs[ca]);
    bb_put(&body, data, n);
    offs[ca + 1] = (uint16_t)body.n;
    if (body.n & 1) bb_u8(&body, 0);
    size_t newpm = body.n;
    bb_u16(&body, ca + 1); bb_u16(&body, cf);
    for (unsigned i = 0; i <= ca + 1; i++) bb_u16(&body, offs[i]);
    free(offs);
    if (body.bad) { bb_free(&body); return NOMEM; }
    if (body.n <= OP_BLOCKMAX && ca + 1 <= 2046) {
        unsigned fill = heap_fill_level(OP_BLOCKMAX - body.n);
        wr16(body.p, (unsigned)newpm);
        if (bn == OP_BLOCKMAX) bb_zero(&body, OP_BLOCKMAX - body.n);
        if (body.bad) { bb_free(&body); return NOMEM; }
        free(h->b[L].p);
        h->b[L].p = body.p; h->b[L].n = body.n;
        set_fill(h, L, fill);
        *hid = (uint32_t)((L << 16) | ((size_t)(ca + 1) << 5));
        return 0;
    }
    bb_free(&body);
    size_t nb = L + 1;
    if (nb > 0xFFFF || (nb >= 8 && (nb - 8) % 128 == 0)) return op_err(OPST_E_UNSUPPORTED, "the heap needs a block kind that is not supported yet");
    if (h->b[L].n < OP_BLOCKMAX) {                         /* a non-final block must be full */
        uint8_t *t = (uint8_t *)realloc(h->b[L].p, OP_BLOCKMAX);
        if (!t) return NOMEM;
        memset(t + h->b[L].n, 0, OP_BLOCKMAX - h->b[L].n);
        h->b[L].p = t; h->b[L].n = OP_BLOCKMAX;
    }
    bbuf nbk = {0};
    bb_zero(&nbk, 2);
    bb_put(&nbk, data, n);
    if (nbk.n & 1) bb_u8(&nbk, 0);
    size_t npm = nbk.n;
    bb_u16(&nbk, 1); bb_u16(&nbk, 0); bb_u16(&nbk, 2); bb_u16(&nbk, (unsigned)(2 + n));
    if (nbk.bad) { bb_free(&nbk); return NOMEM; }
    wr16(nbk.p, (unsigned)npm);
    unsigned fill = heap_fill_level(OP_BLOCKMAX - nbk.n);
    size_t len = nbk.n;
    uint8_t *own = nbk.p;
    if (hb_push(h, own, len)) return OPST_E_NOMEM;
    set_fill(h, nb, fill);
    *hid = (uint32_t)((nb << 16) | (1u << 5));
    return 0;
}

/* in the BTH whose header is heap item `bth_hid`, change the 4-byte value old_hid to new_hid (1 = done) */
static int repoint_rec(hblocks *h, uint32_t x, unsigned level, unsigned cbk, unsigned cbe, uint32_t old_hid, uint32_t new_hid, int depth) {
    size_t b, s, e;
    if (depth > 8 || heap_loc(h, x, &b, &s, &e)) return 0;
    uint8_t *d = h->b[b].p + s;
    size_t n = e - s;
    if (level == 0) {
        size_t sz = cbk + cbe;
        for (size_t i = 0; i < n / sz; i++) {
            if (cbe == 4 && op_u32(d + i * sz + cbk) == old_hid) { wr32(d + i * sz + cbk, new_hid); return 1; }
        }
    } else {
        size_t sz = cbk + 4;
        for (size_t i = 0; i < n / sz; i++) if (repoint_rec(h, op_u32(d + i * sz + cbk), level - 1, cbk, cbe, old_hid, new_hid, depth + 1)) return 1;
    }
    return 0;
}
int heap_bth_repoint(hblocks *h, uint32_t bth_hid, uint32_t old_hid, uint32_t new_hid) {
    const uint8_t *d; size_t n;
    if (heap_get(h, bth_hid, &d, &n) || n < 8) return 0;
    uint32_t root = op_u32(d + 4);
    if (!root) return 0;
    return repoint_rec(h, root, d[3], d[1], d[2], old_hid, new_hid, 0);
}

/* sorted insert of (key -> value) into the BTH at heap item 0x40 (4-byte keys, 4-byte values, any number of index levels);
   splits a node that would exceed the maximum heap allocation, grows the root when needed. Heap ids of existing items never change. */
#define BTH_MAX_ENT 1024
typedef struct { uint32_t hid; unsigned level; uint32_t k[BTH_MAX_ENT], v[BTH_MAX_ENT]; size_t n; size_t pos; } bthnode;

static int bth_store(hblocks *h, bthnode *nd, uint32_t *rk, uint32_t *rv, size_t *rn, uint32_t k_[], uint32_t v_[], size_t n) {
    size_t parts_n = 1, split = n;
    if (n * 8 > OP_MAXALLOC) { parts_n = 2; split = n / 2; }
    uint32_t hid = nd->hid;
    *rn = 0;
    for (size_t j = 0; j < parts_n; j++) {
        size_t from = j == 0 ? 0 : split, to = j == 0 ? split : n;
        uint8_t *buf = (uint8_t *)malloc((to - from) * 8 + 1);
        if (!buf) return NOMEM;
        for (size_t i = from; i < to; i++) { wr32(buf + (i - from) * 8, k_[i]); wr32(buf + (i - from) * 8 + 4, v_[i]); }
        uint32_t nh;
        if (j == 0) {
            int rc = heap_grow_item(h, hid, buf, (to - from) * 8);
            if (rc < 0) { free(buf); return rc; }
            if (rc == 0) {
                rc = heap_append_item(h, buf, (to - from) * 8, &hid);
                if (rc) { free(buf); return rc; }
            }
            nh = hid;
        } else {
            int rc = heap_append_item(h, buf, (to - from) * 8, &nh);
            if (rc) { free(buf); return rc; }
        }
        free(buf);
        rk[*rn] = k_[from]; rv[*rn] = nh; (*rn)++;
    }
    return 0;
}

int heap_bth_insert(hblocks *h, uint32_t key, uint32_t val) {
    const uint8_t *hd; size_t hn;
    int rc = heap_get(h, 0x40, &hd, &hn);
    if (rc) return rc;
    if (hn < 8 || hd[0] != 0xB5 || hd[1] != 4 || hd[2] != 4) return BAD("message index BTH has an unexpected shape");
    unsigned lv = hd[3];
    uint32_t root = op_u32(hd + 4);
    bthnode *path = (bthnode *)calloc(lv + 1, sizeof *path);
    if (!path) return NOMEM;
    uint32_t cur = root;
    unsigned level = lv;
    size_t np = 0;
    for (;;) {
        const uint8_t *d; size_t dn;
        rc = heap_get(h, cur, &d, &dn);
        if (rc) { free(path); return rc; }
        bthnode *nd = &path[np++];
        nd->hid = cur; nd->level = level;
        nd->n = dn / 8;
        if (nd->n > BTH_MAX_ENT - 2) { free(path); return BAD("message index node too large"); }
        for (size_t i = 0; i < nd->n; i++) { nd->k[i] = op_u32(d + i * 8); nd->v[i] = op_u32(d + i * 8 + 4); }
        if (level == 0) break;
        size_t pos = 0;
        for (size_t i = 0; i < nd->n; i++) if (nd->k[i] <= key) pos = i;
        nd->pos = pos;
        cur = nd->v[pos];
        level--;
    }
    bthnode *leaf = &path[np - 1];
    size_t at = 0;
    for (size_t i = 0; i < leaf->n; i++) {
        if (leaf->k[i] == key) { free(path); return op_err(OPST_E_FORMAT, "message index key 0x%08x already present", key); }
        if (leaf->k[i] < key) at++;
    }
    memmove(leaf->k + at + 1, leaf->k + at, (leaf->n - at) * sizeof(uint32_t));
    memmove(leaf->v + at + 1, leaf->v + at, (leaf->n - at) * sizeof(uint32_t));
    leaf->k[at] = key; leaf->v[at] = val; leaf->n++;
    uint32_t rk[2], rv[2];
    size_t rn;
    rc = bth_store(h, leaf, rk, rv, &rn, leaf->k, leaf->v, leaf->n);
    for (size_t i = np - 1; i > 0 && !rc; i--) {
        bthnode *pn = &path[i - 1];
        /* replace the entry at pos by the returned ones */
        uint32_t nk[BTH_MAX_ENT], nv[BTH_MAX_ENT];
        size_t m = 0;
        for (size_t q = 0; q < pn->pos; q++) { nk[m] = pn->k[q]; nv[m] = pn->v[q]; m++; }
        for (size_t q = 0; q < rn; q++) { nk[m] = rk[q]; nv[m] = rv[q]; m++; }
        for (size_t q = pn->pos + 1; q < pn->n; q++) { nk[m] = pn->k[q]; nv[m] = pn->v[q]; m++; }
        rc = bth_store(h, pn, rk, rv, &rn, nk, nv, m);
    }
    free(path);
    if (rc) return rc;
    size_t blk, s, e;
    rc = heap_loc(h, 0x40, &blk, &s, &e);
    if (rc) return rc;
    if (rn == 1) {
        if (rv[0] != root) wr32(h->b[blk].p + s + 4, rv[0]);
    } else {
        uint8_t buf[16];
        wr32(buf, rk[0]); wr32(buf + 4, rv[0]); wr32(buf + 8, rk[1]); wr32(buf + 12, rv[1]);
        uint32_t nr;
        rc = heap_append_item(h, buf, 16, &nr);
        if (rc) return rc;
        rc = heap_loc(h, 0x40, &blk, &s, &e);
        if (rc) return rc;
        wr32(h->b[blk].p + s + 4, nr);
        h->b[blk].p[s + 3] = (uint8_t)(lv + 1);
    }
    return 0;
}

/* ---- heap builder ------------------------------------------------------------------------------------------------------------- */
int hbuild_init(hbuild *b, unsigned client) {
    memset(b, 0, sizeof *b);
    b->client = client;
    b->blk = (hbblk *)calloc(4, sizeof *b->blk);
    if (!b->blk) return NOMEM;
    b->capblk = 4; b->nblk = 1;
    return 0;
}
void hbuild_free(hbuild *b) {
    for (size_t i = 0; i < b->nblk; i++) {
        for (size_t k = 0; k < b->blk[i].n; k++) free(b->blk[i].it[k].p);
        free(b->blk[i].it);
    }
    free(b->blk);
    memset(b, 0, sizeof *b);
}
static size_t hb_hdr(size_t bi) { return bi == 0 ? 12 : (bi >= 8 && (bi - 8) % 128 == 0) ? 66 : 2; }
static int hb_fits(const hbblk *blk, size_t bi, size_t n) {
    size_t end = hb_hdr(bi) + blk->used + n;
    end += end & 1;
    return end + 4 + 2 * (blk->n + 2) <= OP_BLOCKMAX;
}
static int hb_newblock(hbuild *b) {
    if (b->nblk == b->capblk) {
        hbblk *t = (hbblk *)realloc(b->blk, b->capblk * 2 * sizeof *t);
        if (!t) return NOMEM;
        memset(t + b->capblk, 0, b->capblk * sizeof *t);
        b->blk = t; b->capblk *= 2;
    }
    b->nblk++;
    return 0;
}
int hbuild_alloc(hbuild *b, const uint8_t *data, size_t n, uint32_t *hid) {
    if (n == 0 || n > OP_MAXALLOC) return op_err(OPST_E_ARG, "heap allocation of %zu bytes not allowed", n);
    size_t bi = b->nblk - 1;
    if (!hb_fits(&b->blk[bi], bi, n)) {
        if (hb_newblock(b)) return OPST_E_NOMEM;
        bi++;
        if (bi > 0xFFFF || !hb_fits(&b->blk[bi], bi, n)) return op_err(OPST_E_UNSUPPORTED, "heap too large");
    }
    if (b->blk[bi].n >= 2046) {
        if (hb_newblock(b)) return OPST_E_NOMEM;
        bi++;
    }
    hbblk *blk = &b->blk[bi];
    if (blk->n == blk->cap) {
        size_t nc = blk->cap ? blk->cap * 2 : 16;
        hitem *t = (hitem *)realloc(blk->it, nc * sizeof *t);
        if (!t) return NOMEM;
        blk->it = t; blk->cap = nc;
    }
    uint8_t *c = (uint8_t *)calloc(1, n);
    if (!c) return NOMEM;
    if (data) memcpy(c, data, n);
    blk->it[blk->n].p = c; blk->it[blk->n].n = n;
    blk->n++;
    blk->used += n;
    *hid = (uint32_t)((bi << 16) | (blk->n << 5));
    return 0;
}
int hbuild_set(hbuild *b, uint32_t hid, const uint8_t *data, size_t n) {
    size_t bi = hid >> 16, idx = ((hid >> 5) & 0x7FF) - 1;
    if (bi >= b->nblk || idx >= b->blk[bi].n || b->blk[bi].it[idx].n != n) return op_err(OPST_E_ARG, "size changed for placeholder");
    memcpy(b->blk[bi].it[idx].p, data, n);
    return 0;
}
int hbuild_finalize(hbuild *b, uint32_t user_root, hblocks *out) {
    out->b = NULL; out->n = 0;
    unsigned *fills = (unsigned *)calloc(b->nblk, sizeof *fills);
    if (!fills) return NOMEM;
    int rc = 0;
    for (size_t bi = 0; bi < b->nblk && !rc; bi++) {
        hbblk *blk = &b->blk[bi];
        size_t hdr = hb_hdr(bi);
        bbuf body = {0};
        bb_zero(&body, hdr);
        size_t *offs = (size_t *)malloc((blk->n + 1) * sizeof *offs);
        if (!offs) { bb_free(&body); rc = OPST_E_NOMEM; break; }
        offs[0] = hdr;
        for (size_t k = 0; k < blk->n; k++) { bb_put(&body, blk->it[k].p, blk->it[k].n); offs[k + 1] = body.n; }
        if (body.n & 1) bb_u8(&body, 0);
        size_t pm = body.n;
        bb_u16(&body, (unsigned)blk->n); bb_u16(&body, 0);
        for (size_t k = 0; k <= blk->n; k++) bb_u16(&body, (unsigned)offs[k]);
        free(offs);
        if (body.bad) { bb_free(&body); rc = OPST_E_NOMEM; break; }
        fills[bi] = heap_fill_level(body.n <= OP_BLOCKMAX ? OP_BLOCKMAX - body.n : 0);
        wr16(body.p, (unsigned)pm);
        if (bi < b->nblk - 1 && body.n < OP_BLOCKMAX) bb_zero(&body, OP_BLOCKMAX - body.n);
        if (body.bad) { bb_free(&body); rc = OPST_E_NOMEM; break; }
        size_t len = body.n;
        rc = hb_push(out, body.p, len);
    }
    if (rc) { free(fills); hb_free(out); return rc; }
    uint8_t *b0 = out->b[0].p;
    b0[2] = 0xEC; b0[3] = (uint8_t)b->client;
    wr32(b0 + 4, user_root);
    uint32_t fl = 0;
    for (size_t i = 0; i < b->nblk && i < 8; i++) fl |= (uint32_t)fills[i] << (4 * i);
    wr32(b0 + 8, fl);
    for (size_t bi = 8; bi < out->n; bi += 128) {
        for (size_t pos = 0; pos < 128; pos++) {
            size_t j = bi + pos;
            if (j >= out->n) break;
            out->b[bi].p[2 + (pos >> 1)] |= (uint8_t)(fills[j] << (4 * (pos & 1)));
        }
    }
    free(fills);
    return 0;
}

int build_bth(hbuild *b, unsigned cbkey, unsigned cbent, const kv *recs, size_t n, uint32_t hdr_hid, uint32_t *out_hdr) {
    uint32_t hh = hdr_hid;
    int rc;
    if (!hh) { rc = hbuild_alloc(b, NULL, 8, &hh); if (rc) return rc; }
    uint32_t root = 0;
    unsigned levels = 0;
    if (n) {
        size_t per = (size_t)(OP_MAXALLOC * 9 / 10) / (cbkey + cbent);
        if (per < 1) per = 1;
        size_t nl = (n + per - 1) / per;
        const uint8_t **firstkey = (const uint8_t **)malloc(nl * sizeof *firstkey);
        uint32_t *hids = (uint32_t *)malloc(nl * sizeof *hids);
        if (!firstkey || !hids) { free(firstkey); free(hids); return NOMEM; }
        for (size_t i = 0; i < n; i += per) {
            size_t m = n - i < per ? n - i : per;
            uint8_t *buf = (uint8_t *)malloc(m * (cbkey + cbent));
            if (!buf) { free(firstkey); free(hids); return NOMEM; }
            for (size_t k = 0; k < m; k++) {
                memcpy(buf + k * (cbkey + cbent), recs[i + k].k, cbkey);
                memcpy(buf + k * (cbkey + cbent) + cbkey, recs[i + k].v, cbent);
            }
            rc = hbuild_alloc(b, buf, m * (cbkey + cbent), &hids[i / per]);
            free(buf);
            if (rc) { free(firstkey); free(hids); return rc; }
            firstkey[i / per] = recs[i].k;
        }
        size_t perI = (size_t)(OP_MAXALLOC * 9 / 10) / (cbkey + 4);
        if (perI < 1) perI = 1;
        while (nl > 1) {
            size_t nn = (nl + perI - 1) / perI;
            const uint8_t **fk2 = (const uint8_t **)malloc(nn * sizeof *fk2);
            uint32_t *h2 = (uint32_t *)malloc(nn * sizeof *h2);
            if (!fk2 || !h2) { free(fk2); free(h2); free(firstkey); free(hids); return NOMEM; }
            for (size_t i = 0; i < nl; i += perI) {
                size_t m = nl - i < perI ? nl - i : perI;
                uint8_t *buf = (uint8_t *)malloc(m * (cbkey + 4));
                if (!buf) { free(fk2); free(h2); free(firstkey); free(hids); return NOMEM; }
                for (size_t k = 0; k < m; k++) {
                    memcpy(buf + k * (cbkey + 4), firstkey[i + k], cbkey);
                    wr32(buf + k * (cbkey + 4) + cbkey, hids[i + k]);
                }
                rc = hbuild_alloc(b, buf, m * (cbkey + 4), &h2[i / perI]);
                free(buf);
                if (rc) { free(fk2); free(h2); free(firstkey); free(hids); return rc; }
                fk2[i / perI] = firstkey[i];
            }
            free(firstkey); free(hids);
            firstkey = fk2; hids = h2; nl = nn;
            levels++;
        }
        root = hids[0];
        free(firstkey); free(hids);
    }
    uint8_t hd[8] = {0xB5, (uint8_t)cbkey, (uint8_t)cbent, (uint8_t)levels, 0, 0, 0, 0};
    wr32(hd + 4, root);
    rc = hbuild_set(b, hh, hd, 8);
    if (rc) return rc;
    if (out_hdr) *out_hdr = hh;
    return 0;
}

/* rewrites the single BTH leaf `hid` (same size) after the value bytes of its records changed */
int hbuild_patch_bth_leaf(hbuild *b, uint32_t hid, const kv *recs, size_t n, unsigned cbkey, unsigned cbent) {
    uint8_t *buf = (uint8_t *)malloc(n * (cbkey + cbent) + 1);
    if (!buf) return NOMEM;
    for (size_t i = 0; i < n; i++) {
        memcpy(buf + i * (cbkey + cbent), recs[i].k, cbkey);
        memcpy(buf + i * (cbkey + cbent) + cbkey, recs[i].v, cbent);
    }
    int rc = hbuild_set(b, hid, buf, n * (cbkey + cbent));
    free(buf);
    return rc;
}

/* ---- decoded node data --------------------------------------------------------------------------------------------------------- */
static int leaf_rec(opw *w, uint64_t bid, hblocks *out, int depth) {
    opbuf raw;
    if (depth > 3) return BAD("data tree too deep");
    int rc = opw_read_block_raw(w, bid, &raw);
    if (rc) return rc;
    if (!(bid & 2)) {
        op_crypt_block(w->crypt, 0, raw.p, raw.n, (uint32_t)bid);
        return hb_push(out, raw.p, raw.n);
    }
    if (raw.n < 8 || raw.p[0] != 1) { free(raw.p); return BAD("expected an XBLOCK"); }
    unsigned cent = op_u16(raw.p + 2);
    if (8 + (size_t)cent * 8 > raw.n) { free(raw.p); return BAD("XBLOCK is truncated"); }
    for (unsigned i = 0; i < cent && !rc; i++) rc = leaf_rec(w, op_u64(raw.p + 8 + 8 * i), out, depth + 1);
    free(raw.p);
    return rc;
}
int opw_leaf_blocks(opw *w, uint64_t bid, hblocks *out) {
    out->b = NULL; out->n = 0;
    int rc = leaf_rec(w, bid, out, 0);
    if (rc) hb_free(out);
    return rc;
}

int opw_put_blocks(opw *w, const hblocks *blocks, uint64_t *top) {
    if (blocks->n == 0) return op_err(OPST_E_ARG, "no blocks to store");
    if (blocks->n == 1) return opw_add_block(w, blocks->b[0].p, blocks->b[0].n, 0, 2, top);
    uint64_t *ids = (uint64_t *)malloc(blocks->n * sizeof *ids);
    if (!ids) return NOMEM;
    uint64_t total = 0;
    int rc = 0;
    for (size_t i = 0; i < blocks->n && !rc; i++) {
        rc = opw_add_block(w, blocks->b[i].p, blocks->b[i].n, 0, 2, &ids[i]);
        total += blocks->b[i].n;
    }
    if (rc) { free(ids); return rc; }
    if (blocks->n <= 1021) {
        rc = opw_xblock(w, 1, ids, blocks->n, (uint32_t)total, top);
        free(ids);
        return rc;
    }
    size_t nsub = (blocks->n + 1020) / 1021;
    uint64_t *subs = (uint64_t *)malloc(nsub * sizeof *subs);
    if (!subs) { free(ids); return NOMEM; }
    for (size_t i = 0, s = 0; i < blocks->n && !rc; i += 1021, s++) {
        size_t m = blocks->n - i < 1021 ? blocks->n - i : 1021;
        uint32_t part = 0;
        for (size_t k = 0; k < m; k++) part += (uint32_t)blocks->b[i + k].n;
        rc = opw_xblock(w, 1, ids + i, m, part, &subs[s]);
    }
    if (!rc) rc = opw_xblock(w, 2, subs, nsub, (uint32_t)total, top);
    free(ids); free(subs);
    return rc;
}

int opw_put_bytes(opw *w, const uint8_t *d, size_t n, uint64_t *top) {
    opbuf b = {(uint8_t *)d, n};
    hblocks h = {&b, 1};
    return opw_put_blocks(w, &h, top);
}

void wsubs_free(wsubs *s) { free(s->e); s->e = NULL; s->n = 0; }

int opw_subnodes(opw *w, uint64_t bs, wsubs *out) {
    out->e = NULL; out->n = 0;
    if (!bs) return 0;
    opbuf raw;
    int rc = opw_read_block_raw(w, bs, &raw);
    if (rc) return rc;
    if (raw.n < 8 || raw.p[0] != 2 || raw.p[1] != 0) { free(raw.p); return op_err(OPST_E_UNSUPPORTED, "the subnode tree of this node is not a single SLBLOCK (not supported yet)"); }
    unsigned cent = op_u16(raw.p + 2);
    if (8 + (size_t)cent * 24 > raw.n) { free(raw.p); return BAD("subnode block is truncated"); }
    out->e = (wsub *)malloc((cent ? cent : 1) * sizeof *out->e);
    if (!out->e) { free(raw.p); return NOMEM; }
    for (unsigned i = 0; i < cent; i++) {
        out->e[i].nid = (uint32_t)(op_u64(raw.p + 8 + 24 * i) & 0xFFFFFFFFu);
        out->e[i].bd = op_u64(raw.p + 16 + 24 * i);
        out->e[i].bs = op_u64(raw.p + 24 + 24 * i);
    }
    out->n = cent;
    free(raw.p);
    return 0;
}

static int cmp_wsub(const void *a, const void *b) {
    uint32_t x = ((const wsub *)a)->nid, y = ((const wsub *)b)->nid;
    return x < y ? -1 : x > y;
}

int opw_put_subnodes(opw *w, const wsub *e, size_t n, uint64_t *bs) {
    *bs = 0;
    if (!n) return 0;
    wsub *s = (wsub *)malloc(n * sizeof *s);
    if (!s) return NOMEM;
    memcpy(s, e, n * sizeof *s);
    op_qsort(s, n, sizeof *s, cmp_wsub);
    bbuf b = {0};
    bb_u8(&b, 2); bb_u8(&b, 0); bb_u16(&b, (unsigned)n); bb_u32(&b, 0);
    for (size_t i = 0; i < n; i++) { bb_u64(&b, s[i].nid); bb_u64(&b, s[i].bd); bb_u64(&b, s[i].bs); }
    free(s);
    if (b.bad) { bb_free(&b); return NOMEM; }
    int rc = opw_add_block(w, b.p, b.n, 1, 2, bs);
    bb_free(&b);
    return rc;
}

/* independent copy of a node's blocks (data tree and subnode tree, recursively) */
int opw_clone_node(opw *w, uint64_t bd, uint64_t bs, patch_fn patch, void *ctx, uint64_t *nbd, uint64_t *nbs) {
    hblocks blocks;
    int rc = opw_leaf_blocks(w, bd, &blocks);
    if (rc) return rc;
    if (patch) patch(&blocks, ctx);
    rc = opw_put_blocks(w, &blocks, nbd);
    hb_free(&blocks);
    if (rc) return rc;
    *nbs = 0;
    if (bs) {
        wsubs subs;
        rc = opw_subnodes(w, bs, &subs);
        if (rc) return rc;
        wsub *ns = (wsub *)calloc(subs.n ? subs.n : 1, sizeof *ns);
        if (!ns) { wsubs_free(&subs); return NOMEM; }
        for (size_t i = 0; i < subs.n && !rc; i++) {
            ns[i].nid = subs.e[i].nid;
            rc = opw_clone_node(w, subs.e[i].bd, subs.e[i].bs, NULL, NULL, &ns[i].bd, &ns[i].bs);
        }
        if (!rc) rc = opw_put_subnodes(w, ns, subs.n, nbs);
        free(ns);
        wsubs_free(&subs);
    }
    return rc;
}

/* ---- table contexts -------------------------------------------------------------------------------------------------------------- */
void tc_free(tctx *t) {
    for (size_t r = 0; r < t->nrows; r++) {
        for (size_t c = 0; c < t->ncols; c++) bb_free(&t->rows[r].cell[c]);
        free(t->rows[r].cell); free(t->rows[r].present);
    }
    free(t->rows); free(t->cols);
    memset(t, 0, sizeof *t);
}

int tc_col(const tctx *t, unsigned pid) {
    for (size_t i = 0; i < t->ncols; i++) if (t->cols[i].pid == pid) return (int)i;
    return -1;
}
int tc_find(const tctx *t, uint32_t rowid) {
    for (size_t i = 0; i < t->nrows; i++) if (t->rows[i].rowid == rowid) return (int)i;
    return -1;
}
int tc_remove(tctx *t, uint32_t rowid) {
    int i = tc_find(t, rowid);
    if (i < 0) return op_err(OPST_E_NOTFOUND, "row 0x%x not in table", rowid);
    for (size_t c = 0; c < t->ncols; c++) bb_free(&t->rows[i].cell[c]);
    free(t->rows[i].cell); free(t->rows[i].present);
    memmove(t->rows + i, t->rows + i + 1, (t->nrows - (size_t)i - 1) * sizeof *t->rows);
    t->nrows--;
    return 0;
}
int tc_add_row(tctx *t, uint32_t rowid) {
    if (t->nrows == t->caprows) {
        size_t nc = t->caprows ? t->caprows * 2 : 16;
        tcrow *r = (tcrow *)realloc(t->rows, nc * sizeof *r);
        if (!r) return NOMEM;
        t->rows = r; t->caprows = nc;
    }
    tcrow *r = &t->rows[t->nrows];
    memset(r, 0, sizeof *r);
    r->rowid = rowid;
    r->present = (uint8_t *)calloc(t->ncols ? t->ncols : 1, 1);
    r->cell = (bbuf *)calloc(t->ncols ? t->ncols : 1, sizeof(bbuf));
    if (!r->present || !r->cell) { free(r->present); free(r->cell); return NOMEM; }
    t->nrows++;
    return 0;
}
int tc_set_cell(tctx *t, size_t row, int col, const uint8_t *d, size_t n) {
    tcrow *r = &t->rows[row];
    r->cell[col].n = 0;
    if (bb_put(&r->cell[col], d, n)) return NOMEM;
    r->present[col] = 1;
    return 0;
}
int tc_clear_rows(tctx *t) {
    for (size_t r = 0; r < t->nrows; r++) {
        for (size_t c = 0; c < t->ncols; c++) bb_free(&t->rows[r].cell[c]);
        free(t->rows[r].cell); free(t->rows[r].present);
    }
    t->nrows = 0;
    t->rows_nid = 0;
    return 0;
}

/* row values by property id; the pcprops entries are copies */
void pcprops_free(pcprops *p) {
    for (size_t i = 0; i < p->n; i++) bb_free(&p->p[i].v);
    free(p->p);
    memset(p, 0, sizeof *p);
}
pcprop *pcprops_find(pcprops *p, unsigned pid) {
    for (size_t i = 0; i < p->n; i++) if (p->p[i].pid == pid) return &p->p[i];
    return NULL;
}
int pcprops_set(pcprops *p, unsigned pid, unsigned ptype, const uint8_t *v, size_t n) {
    pcprop *e = pcprops_find(p, pid);
    if (!e) {
        if (p->n == p->cap) {
            size_t nc = p->cap ? p->cap * 2 : 16;
            pcprop *t = (pcprop *)realloc(p->p, nc * sizeof *t);
            if (!t) return NOMEM;
            p->p = t; p->cap = nc;
        }
        e = &p->p[p->n++];
        memset(e, 0, sizeof *e);
        e->pid = (uint16_t)pid;
    }
    e->ptype = (uint16_t)ptype;
    e->v.n = 0;
    return bb_put(&e->v, v, n) ? OPST_E_NOMEM : 0;
}
int pcprops_del(pcprops *p, unsigned pid) {
    for (size_t i = 0; i < p->n; i++) {
        if (p->p[i].pid == pid) {
            bb_free(&p->p[i].v);
            memmove(p->p + i, p->p + i + 1, (p->n - i - 1) * sizeof *p->p);
            p->n--;
            return 1;
        }
    }
    return 0;
}

int tc_row_pidvals(const tctx *t, size_t row, pcprops *out) {
    memset(out, 0, sizeof *out);
    for (size_t c = 0; c < t->ncols; c++) {
        if (!t->rows[row].present[c]) continue;
        int rc = pcprops_set(out, t->cols[c].pid, t->cols[c].ptype, t->rows[row].cell[c].p, t->rows[row].cell[c].n);
        if (rc) { pcprops_free(out); return rc; }
    }
    return 0;
}

int tc_add_by_pid(tctx *t, uint32_t rowid, const pcprops *vals) {
    if (tc_find(t, rowid) >= 0) return op_err(OPST_E_ARG, "row 0x%x already exists", rowid);
    int rc = tc_add_row(t, rowid);
    if (rc) return rc;
    size_t r = t->nrows - 1;
    for (size_t i = 0; i < vals->n; i++) {
        int c = tc_col(t, vals->p[i].pid);
        if (c < 0) continue;
        rc = tc_set_cell(t, r, c, vals->p[i].v.p, vals->p[i].v.n);
        if (rc) return rc;
    }
    return 0;
}

int tc_copy_row(tctx *dst, const tctx *src, size_t srow, uint32_t newid) {
    pcprops v;
    int rc = tc_row_pidvals(src, srow, &v);
    if (rc) return rc;
    rc = tc_add_by_pid(dst, newid, &v);
    pcprops_free(&v);
    return rc;
}

