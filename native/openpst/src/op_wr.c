/* op_wr.c - writer core: transactions with journal, allocation maps, blocks, B-trees (port of pstwrite.py).
 *
 * Design (same as the Python writer):
 *   - every public write operation is one transaction: all changes are buffered in memory (an overlay of 64-byte slots);
 *   - before the first byte of the file changes, the ORIGINAL bytes of every region about to be overwritten go to
 *     <file>.journal (flushed); a leftover journal is rolled back the next time the file is opened for writing;
 *   - commit order: journal -> header with fAMapValid=0 -> data / pages / AMaps -> final header -> flush -> drop journal. */
#include "op_wr.h"

const uint8_t *op_mpbb_table(void);       /* op_ndb.c: the decoding permutation */


/* ---- byte buffer -------------------------------------------------------------------------------------------------------- */
void bb_free(bbuf *b) { free(b->p); memset(b, 0, sizeof *b); }
static int bb_reserve(bbuf *b, size_t extra) {
    if (b->bad) return OPST_E_NOMEM;
    if (b->n + extra + 1 > b->cap) {
        size_t nc = b->cap ? b->cap : 64;
        while (nc < b->n + extra + 1) nc *= 2;
        uint8_t *t = (uint8_t *)realloc(b->p, nc);
        if (!t) { b->bad = 1; return OPST_E_NOMEM; }
        b->p = t; b->cap = nc;
    }
    return 0;
}
int bb_put(bbuf *b, const void *d, size_t n) {
    if (bb_reserve(b, n)) return OPST_E_NOMEM;
    if (n) memcpy(b->p + b->n, d, n);
    b->n += n;
    return 0;
}
int bb_zero(bbuf *b, size_t n) {
    if (bb_reserve(b, n)) return OPST_E_NOMEM;
    memset(b->p + b->n, 0, n);
    b->n += n;
    return 0;
}
int bb_u8(bbuf *b, unsigned v) { uint8_t x = (uint8_t)v; return bb_put(b, &x, 1); }
int bb_u16(bbuf *b, unsigned v) { uint8_t x[2]; wr16(x, v); return bb_put(b, x, 2); }
int bb_u32(bbuf *b, uint32_t v) { uint8_t x[4]; wr32(x, v); return bb_put(b, x, 4); }
int bb_u64(bbuf *b, uint64_t v) { uint8_t x[8]; wr64(x, v); return bb_put(b, x, 8); }

#define NOMEM op_err(OPST_E_NOMEM, "out of memory")
#define CORRUPT(...) op_err(OPST_E_FORMAT, __VA_ARGS__)

static unsigned sig_of(uint64_t ib, uint64_t bid) {
    uint32_t x = (uint32_t)((ib ^ bid) & 0xFFFFFFFFu);
    return ((x >> 16) ^ x) & 0xFFFFu;
}

/* body (496 bytes) + 16-byte page trailer -> 512 */
static void page_bytes(uint8_t *out512, const uint8_t *body496, unsigned ptype, uint64_t ib, uint64_t bid, int signed_) {
    memcpy(out512, body496, 496);
    out512[496] = (uint8_t)ptype; out512[497] = (uint8_t)ptype;
    wr16(out512 + 498, signed_ ? sig_of(ib, bid) : 0);
    wr32(out512 + 500, op_crc(body496, 496));
    wr64(out512 + 504, bid);
}

/* ---- journal ------------------------------------------------------------------------------------------------------------- */
static const char JMAGIC[8] = {'P', 'S', 'T', 'J', 'R', 'N', 'L', '1'};

static char *journal_path(const char *path) {
    size_t n = strlen(path);
    char *j = (char *)malloc(n + 9);
    if (j) { memcpy(j, path, n); memcpy(j + n, ".journal", 9); }
    return j;
}

/* rolls back a leftover journal; *recovered = 1 when something was undone */
int op_journal_recover(const char *path, int *recovered) {
    *recovered = 0;
    char *jp = journal_path(path);
    if (!jp) return NOMEM;
    if (!op_file_exists(jp)) { free(jp); return 0; }
    uint8_t *raw; size_t rn;
    int rc = op_file_read_all(jp, &raw, &rn);
    if (rc) { free(jp); return rc; }
    if (rn < 16 || memcmp(raw, JMAGIC, 8) != 0) { free(raw); rc = op_file_remove(jp); free(jp); return rc; }
    uint64_t orig_eof = op_u64(raw + 8);
    opfile *f;
    rc = op_file_open(path, 1, &f);
    if (rc) { free(raw); free(jp); return rc; }
    /* records are applied in reverse order */
    size_t nrec = 0, cap = 0, p = 16;
    struct rec { uint64_t off; uint32_t len; size_t at; } *recs = NULL;
    while (p + 12 <= rn) {
        uint64_t off = op_u64(raw + p);
        uint32_t len = op_u32(raw + p + 8);
        if (p + 12 + len > rn) break;
        if (nrec == cap) {
            cap = cap ? cap * 2 : 64;
            struct rec *t = (struct rec *)realloc(recs, cap * sizeof *t);
            if (!t) { free(recs); free(raw); op_file_close(f); free(jp); return NOMEM; }
            recs = t;
        }
        recs[nrec].off = off; recs[nrec].len = len; recs[nrec].at = p + 12;
        nrec++;
        p += 12 + len;
    }
    for (size_t i = nrec; i > 0 && !rc; i--) rc = op_file_pwrite(f, raw + recs[i - 1].at, recs[i - 1].len, recs[i - 1].off);
    if (!rc) rc = op_file_truncate(f, orig_eof);
    if (!rc) rc = op_file_sync(f);
    op_file_close(f);
    free(recs); free(raw);
    if (!rc) { rc = op_file_remove(jp); *recovered = 1; }
    free(jp);
    return rc;
}

/* ---- overlay ------------------------------------------------------------------------------------------------------------- */
static ovl_ent *ov_find(opw *w, uint64_t slot) {
    if (!w->ovidxcap) return NULL;
    size_t mask = w->ovidxcap - 1, h = (size_t)((slot * 0x9E3779B97F4A7C15ull) >> 20) & mask;
    for (;;) {
        uint32_t v = w->ovidx[h];
        if (!v) return NULL;
        if (w->ov[v - 1].slot == slot) return &w->ov[v - 1];
        h = (h + 1) & mask;
    }
}
static int ov_rehash(opw *w, size_t ncap) {
    uint32_t *t = (uint32_t *)calloc(ncap, sizeof *t);
    if (!t) return NOMEM;
    size_t mask = ncap - 1;
    for (size_t i = 0; i < w->nov; i++) {
        size_t h = (size_t)((w->ov[i].slot * 0x9E3779B97F4A7C15ull) >> 20) & mask;
        while (t[h]) h = (h + 1) & mask;
        t[h] = (uint32_t)(i + 1);
    }
    free(w->ovidx);
    w->ovidx = t; w->ovidxcap = ncap;
    return 0;
}
static ovl_ent *ov_get(opw *w, uint64_t slot) {
    ovl_ent *e = ov_find(w, slot);
    if (e) return e;
    if ((w->nov + 1) * 2 > w->ovidxcap) {
        if (ov_rehash(w, w->ovidxcap ? w->ovidxcap * 2 : 4096)) return NULL;
    }
    if (w->nov == w->capov) {
        size_t nc = w->capov ? w->capov * 2 : 1024;
        ovl_ent *t = (ovl_ent *)realloc(w->ov, nc * sizeof *t);
        if (!t) return NULL;
        w->ov = t; w->capov = nc;
    }
    e = &w->ov[w->nov++];
    e->slot = slot;
    size_t mask = w->ovidxcap - 1, h = (size_t)((slot * 0x9E3779B97F4A7C15ull) >> 20) & mask;
    while (w->ovidx[h]) h = (h + 1) & mask;
    w->ovidx[h] = (uint32_t)w->nov;
    return e;
}

int opw_write(opw *w, uint64_t off, const void *data, size_t n) {
    if ((off & 63) || (n & 63)) return op_err(OPST_E_ARG, "internal: unaligned write at 0x%llx", (unsigned long long)off);
    const uint8_t *d = (const uint8_t *)data;
    for (size_t i = 0; i < n; i += 64) {
        ovl_ent *e = ov_get(w, (off + i) >> 6);
        if (!e) return NOMEM;
        memcpy(e->d, d + i, 64);
    }
    return 0;
}

int opw_read(opw *w, uint64_t off, size_t n, uint8_t *buf) {
    uint64_t fsz = op_file_size(w->p->f);
    memset(buf, 0, n);
    if (off < fsz) {
        size_t m = (size_t)(fsz - off < n ? fsz - off : n);
        int rc = op_file_pread(w->p->f, buf, m, off);
        if (rc) return rc;
    }
    if (w->nov && n) {
        for (uint64_t slot = off >> 6; slot <= (off + n - 1) >> 6; slot++) {
            ovl_ent *e = ov_find(w, slot);
            if (!e) continue;
            uint64_t s0 = slot << 6, lo = s0 > off ? s0 : off, hi = s0 + 64 < off + n ? s0 + 64 : off + n;
            memcpy(buf + (lo - off), e->d + (lo - s0), (size_t)(hi - lo));
        }
    }
    return 0;
}

/* ---- allocation maps ------------------------------------------------------------------------------------------------------ */
static int w_cap_secs(opw *w, size_t need) {
    if (need <= w->capamaps) return 0;
    size_t nc = w->capamaps ? w->capamaps : 256;
    while (nc < need) nc *= 2;
    uint8_t **a = (uint8_t **)realloc(w->amaps, nc * sizeof *a);
    if (!a) return NOMEM;
    w->amaps = a;
    uint8_t *d = (uint8_t *)realloc(w->dirty, nc);
    if (!d) return NOMEM;
    w->dirty = d;
    int32_t *f = (int32_t *)realloc(w->freecnt, nc * sizeof *f);
    if (!f) return NOMEM;
    w->freecnt = f;
    for (size_t i = w->capamaps; i < nc; i++) { w->amaps[i] = NULL; w->dirty[i] = 0; w->freecnt[i] = -1; }
    w->capamaps = nc;
    return 0;
}

static uint8_t *amap(opw *w, uint32_t sec, int *err) {
    *err = 0;
    if (w_cap_secs(w, (size_t)sec + 1)) { *err = OPST_E_NOMEM; return NULL; }
    if (!w->amaps[sec]) {
        uint8_t pg[512];
        uint64_t ib = OPW_AMAP0 + (uint64_t)sec * OPW_SECT;
        int rc = opw_read(w, ib, 512, pg);
        if (rc) { *err = rc; return NULL; }
        if (pg[496] != 0x84) { *err = CORRUPT("AMap page expected at 0x%llx", (unsigned long long)ib); return NULL; }
        w->amaps[sec] = (uint8_t *)malloc(496);
        if (!w->amaps[sec]) { *err = OPST_E_NOMEM; return NULL; }
        memcpy(w->amaps[sec], pg, 496);
    }
    return w->amaps[sec];
}

static int setbits(opw *w, uint32_t sec, unsigned start, unsigned n, int val) {
    int err;
    uint8_t *a = amap(w, sec, &err);
    if (!a) return err;
    for (unsigned i = start; i < start + n; i++) {
        uint8_t m = (uint8_t)(0x80 >> (i & 7));
        if (val) a[i >> 3] |= m; else a[i >> 3] &= (uint8_t)~m;
    }
    w->dirty[sec] = 1;
    w->freecnt[sec] = -1;
    return 0;
}

static int bit(const uint8_t *a, unsigned i) { return (a[i >> 3] >> (7 - (i & 7))) & 1; }

typedef struct { uint64_t ib; size_t size; } aspan;
typedef struct { aspan *s; size_t n, cap; } aspans;
static int aspans_add(aspans *sp, uint64_t ib, size_t size) {
    if (sp->n == sp->cap) {
        size_t nc = sp->cap ? sp->cap * 2 : 1024;
        aspan *t = (aspan *)realloc(sp->s, nc * sizeof *t);
        if (!t) return NOMEM;
        sp->s = t; sp->cap = nc;
    }
    sp->s[sp->n].ib = ib; sp->s[sp->n].size = size; sp->n++;
    return 0;
}

static int walk_pages(opw *w, int nbt, uint64_t ib, uint64_t bid, aspans *sp, int depth) {
    if (depth > 8) return CORRUPT("B-tree too deep");
    int rc = aspans_add(sp, ib, 512);
    if (rc) return rc;
    uint8_t pg[512];
    rc = opw_read(w, ib, 512, pg);
    if (rc) return rc;
    if (pg[496] != (nbt ? 0x81 : 0x80)) return CORRUPT("B-tree page type mismatch at 0x%llx", (unsigned long long)ib);
    (void)bid;
    if (pg[491]) {
        unsigned cent = pg[488], cb = pg[490];
        if (cb < 24 || cent * cb > 488) return CORRUPT("bad B-tree page at 0x%llx", (unsigned long long)ib);
        for (unsigned i = 0; i < cent; i++) {
            const uint8_t *e = pg + i * cb;
            rc = walk_pages(w, nbt, op_u64(e + 16), op_u64(e + 8), sp, depth + 1);
            if (rc) return rc;
        }
    }
    return 0;
}

typedef struct { aspans *sp; } blkctx;
static int cb_bbt_span(void *ctx, const uint8_t *e) {
    blkctx *c = (blkctx *)ctx;
    return aspans_add(c->sp, op_u64(e + 8), ((size_t)op_u16(e + 16) + 16 + 63) / 64 * 64);
}

/* marks every block / tree page referenced by the B-trees as allocated (sets bits only) */
static int reconcile(opw *w) {
    if (w->reconciled) return 0;
    w->reconciled = 1;
    aspans sp = {0, 0, 0};
    int rc = walk_pages(w, 1, w->nbt_ib, w->nbt_bid, &sp, 0);
    if (!rc) rc = walk_pages(w, 0, w->bbt_ib, w->bbt_bid, &sp, 0);
    if (!rc) { blkctx c = {&sp}; rc = bt_items(w, 0, cb_bbt_span, &c); }
    uint64_t fixed = 0;
    for (size_t i = 0; i < sp.n && !rc; i++) {
        uint64_t k0 = (sp.s[i].ib - OPW_AMAP0) / 64;
        for (uint64_t k = k0; k < k0 + sp.s[i].size / 64 && !rc; k++) {
            uint32_t sec = (uint32_t)(k / OPW_SLOTS), idx = (uint32_t)(k % OPW_SLOTS);
            if (sec >= w->nsec) continue;                 /* outside the file's sections: the file is damaged; leave it */
            int err;
            uint8_t *a = amap(w, sec, &err);
            if (!a) { rc = err; break; }
            uint8_t m = (uint8_t)(0x80 >> (idx & 7));
            if (!(a[idx >> 3] & m)) {
                a[idx >> 3] |= m;
                w->dirty[sec] = 1;
                w->freecnt[sec] = -1;
                fixed++;
            }
        }
    }
    free(sp.s);
    if (rc) return rc;
    w->reconciled_fixes = fixed;
    w->free_delta -= (int64_t)fixed * 64;
    return 0;
}

int opw_grow(opw *w, unsigned add) {
    uint32_t first = w->nsec;
    if ((uint64_t)first + add > OPW_MAX_SECTIONS) return op_err(OPST_E_UNSUPPORTED, "file growth beyond %u sections (about 16 GB) is not supported", OPW_MAX_SECTIONS);
    int rc = w_cap_secs(w, (size_t)first + add);
    if (rc) return rc;
    for (uint32_t s = first; s < first + add; s++) {
        uint64_t base = OPW_AMAP0 + (uint64_t)s * OPW_SECT;
        unsigned used = 8;
        uint8_t body[496], pg[512];
        memset(body, 0xFF, sizeof body);
        if (s % 8 == 0) {
            page_bytes(pg, body, 0x83, base + 512, base + 512, 0);
            rc = opw_write(w, base + 512, pg, 512);
            if (rc) return rc;
            used += 8;
        }
        if (s >= 128 && (s - 128) % 496 == 0) {
            page_bytes(pg, body, 0x82, base + 1024, base + 1024, 0);
            rc = opw_write(w, base + 1024, pg, 512);
            if (rc) return rc;
            used += 8;
        }
        if (s >= OPW_FPMAP_FIRST && (s - OPW_FPMAP_FIRST) % OPW_FPMAP_EVERY == 0) {
            page_bytes(pg, body, 0x85, base + 1024, base + 1024, 0);
            rc = opw_write(w, base + 1024, pg, 512);
            if (rc) return rc;
            used += 8;
        }
        uint8_t *bits = (uint8_t *)calloc(1, 496);
        if (!bits) return NOMEM;
        for (unsigned i = 0; i < used; i++) bits[i >> 3] |= (uint8_t)(0x80 >> (i & 7));
        free(w->amaps[s]);
        w->amaps[s] = bits;
        w->freecnt[s] = -1;
        w->dirty[s] = 1;
        w->free_delta += (int64_t)(OPW_SLOTS - used) * 64;
    }
    w->nsec = first + add;
    w->eof = OPW_AMAP0 + (uint64_t)w->nsec * OPW_SECT;
    w->amap_last = OPW_AMAP0 + (uint64_t)(w->nsec - 1) * OPW_SECT;
    return 0;
}

int opw_alloc(opw *w, size_t size, size_t align, uint64_t *out) {
    int rc = reconcile(w);
    if (rc) return rc;
    unsigned n = (unsigned)((size + 63) / 64), ab = (unsigned)(align / 64);
    if (!ab) ab = 1;
    for (;;) {
        for (int64_t sec = (int64_t)w->nsec - 1; sec >= 0; sec--) {
            int err;
            uint8_t *a = amap(w, (uint32_t)sec, &err);
            if (!a) return err;
            int32_t fc = w->freecnt[sec];
            if (fc < 0) {
                int ones = 0;
                for (unsigned i = 0; i < 496; i++) { uint8_t v = a[i]; while (v) { ones += v & 1; v >>= 1; } }
                fc = w->freecnt[sec] = (int32_t)(OPW_SLOTS - (unsigned)ones);
            }
            if ((unsigned)fc < n) continue;
            /* first aligned start of a run of n free slots */
            unsigned i = 0, found = OPW_SLOTS + 1, longest = 0, runlen = 0;
            while (i + n <= OPW_SLOTS) {
                unsigned j = i;
                while (j < i + n && !bit(a, j)) j++;
                if (j == i + n) { found = i; break; }
                i = ((j / ab) + 1) * ab;                  /* next aligned position after the blocking slot */
            }
            if (found <= OPW_SLOTS) {
                rc = setbits(w, (uint32_t)sec, found, n, 1);
                if (rc) return rc;
                w->free_delta -= (int64_t)n * 64;
                *out = OPW_AMAP0 + (uint64_t)sec * OPW_SECT + (uint64_t)found * 64;
                return 0;
            }
            for (unsigned k = 0; k < OPW_SLOTS; k++) {
                if (!bit(a, k)) { runlen++; if (runlen > longest) longest = runlen; } else runlen = 0;
            }
            w->freecnt[sec] = (int32_t)longest;           /* exact longest free run: skip this section next time */
        }
        rc = opw_grow(w, OPW_GROW);
        if (rc) return rc;
    }
}

int opw_free(opw *w, uint64_t ib, size_t size) {
    unsigned n = (unsigned)((size + 63) / 64);
    uint64_t rel = ib - OPW_AMAP0;
    uint32_t sec = (uint32_t)(rel / OPW_SECT);
    unsigned off = (unsigned)(rel % OPW_SECT);
    int rc = setbits(w, sec, off / 64, n, 0);
    if (rc) return rc;
    w->free_delta += (int64_t)n * 64;
    return 0;
}

int opw_is_allocated(opw *w, uint64_t ib, size_t size) {
    unsigned n = (unsigned)((size + 63) / 64);
    uint64_t rel = ib - OPW_AMAP0;
    uint32_t sec = (uint32_t)(rel / OPW_SECT);
    unsigned off = (unsigned)(rel % OPW_SECT) / 64;
    int err;
    if (sec >= w->nsec) return 0;
    uint8_t *a = amap(w, sec, &err);
    if (!a) return 0;
    for (unsigned i = off; i < off + n && i < OPW_SLOTS; i++) if (!bit(a, i)) return 0;
    return 1;
}

uint64_t opw_alloc_page_bid(opw *w) { return w->bid_next_p++; }

/* ---- B-trees ---------------------------------------------------------------------------------------------------------------- */
typedef struct {
    uint64_t ib, bid;
    int      level, cent, idx;
    uint8_t  ents[736];                   /* up to 22 entries of 32 bytes: a full page plus one entry before a split */
} btn;

static unsigned bt_esz(int nbt, int level) { return level == 0 ? (nbt ? 32u : 24u) : 24u; }
static uint64_t ekey(const uint8_t *e) { return op_u64(e); }

static int bt_read(opw *w, int nbt, uint64_t ib, uint64_t bid, btn *n) {
    uint8_t pg[512];
    int rc = opw_read(w, ib, 512, pg);
    if (rc) return rc;
    if (pg[496] != (nbt ? 0x81 : 0x80)) return CORRUPT("B-tree page type mismatch at 0x%llx", (unsigned long long)ib);
    unsigned cent = pg[488], cb = pg[490], level = pg[491];
    if (level > 8 || cb != bt_esz(nbt, (int)level) || cent * cb > 488) return CORRUPT("bad B-tree page at 0x%llx", (unsigned long long)ib);
    n->ib = ib; n->bid = bid; n->level = (int)level; n->cent = (int)cent; n->idx = 0;
    memcpy(n->ents, pg, (size_t)cent * cb);
    return 0;
}

static int bt_page_write(opw *w, int nbt, const btn *node, const uint8_t *ents, int n) {
    unsigned esz = bt_esz(nbt, node->level);
    uint8_t body[496], pg[512];
    memset(body, 0, sizeof body);
    memcpy(body, ents, (size_t)n * esz);
    body[488] = (uint8_t)n; body[489] = (uint8_t)(488 / esz); body[490] = (uint8_t)esz; body[491] = (uint8_t)node->level;
    page_bytes(pg, body, nbt ? 0x81 : 0x80, node->ib, node->bid, 1);
    return opw_write(w, node->ib, pg, 512);
}

#define BT_MAXDEPTH 10
static int bt_descend(opw *w, int nbt, uint64_t key, btn *path, int *depth) {
    uint64_t bid = nbt ? w->nbt_bid : w->bbt_bid, ib = nbt ? w->nbt_ib : w->bbt_ib;
    int d = 0;
    for (;;) {
        if (d >= BT_MAXDEPTH) return CORRUPT("B-tree too deep");
        int rc = bt_read(w, nbt, ib, bid, &path[d]);
        if (rc) return rc;
        btn *nd = &path[d];
        if (nd->level == 0) { *depth = d + 1; return 0; }
        int idx = 0;
        for (int i = 0; i < nd->cent; i++) { if (ekey(nd->ents + i * 24) <= key) idx = i; else break; }
        if (nd->cent == 0) return CORRUPT("empty index page at 0x%llx", (unsigned long long)nd->ib);
        nd->idx = idx;
        bid = op_u64(nd->ents + idx * 24 + 8);
        ib = op_u64(nd->ents + idx * 24 + 16);
        d++;
    }
}

static void bt_set_root(opw *w, int nbt, uint64_t bid, uint64_t ib) {
    if (nbt) { w->nbt_bid = bid; w->nbt_ib = ib; } else { w->bbt_bid = bid; w->bbt_ib = ib; }
}

static int bt_store(opw *w, int nbt, btn *path, int i, const uint8_t *ents, int n) {
    btn *node = &path[i];
    unsigned esz = bt_esz(nbt, node->level);
    int cmax = (int)(488 / esz);
    int rc;
    if (n <= cmax) {
        rc = bt_page_write(w, nbt, node, ents, n);
        if (rc) return rc;
        node->cent = n;
        if (ents != node->ents) memmove(node->ents, ents, (size_t)n * esz);
        if (i > 0 && n) {
            btn *parent = &path[i - 1];
            const uint8_t *pe = parent->ents + parent->idx * 24;
            uint64_t k0 = ekey(node->ents);
            if (ekey(pe) != k0) {
                uint8_t pents[736];
                memcpy(pents, parent->ents, (size_t)parent->cent * 24);
                wr64(pents + parent->idx * 24, k0);
                return bt_store(w, nbt, path, i - 1, pents, parent->cent);
            }
        }
        return 0;
    }
    int mid = n / 2;
    uint8_t left[736], right[736];
    memcpy(left, ents, (size_t)mid * esz);
    memcpy(right, ents + (size_t)mid * esz, (size_t)(n - mid) * esz);
    rc = bt_page_write(w, nbt, node, left, mid);
    if (rc) return rc;
    uint64_t nbid = opw_alloc_page_bid(w), nib;
    rc = opw_alloc(w, 512, 512, &nib);
    if (rc) return rc;
    btn nnode;
    nnode.ib = nib; nnode.bid = nbid; nnode.level = node->level;
    rc = bt_page_write(w, nbt, &nnode, right, n - mid);
    if (rc) return rc;
    uint8_t new_e[24];
    wr64(new_e, ekey(right)); wr64(new_e + 8, nbid); wr64(new_e + 16, nib);
    if (i == 0) {
        uint64_t rbid = opw_alloc_page_bid(w), rib;
        rc = opw_alloc(w, 512, 512, &rib);
        if (rc) return rc;
        btn rnode;
        rnode.ib = rib; rnode.bid = rbid; rnode.level = node->level + 1;
        uint8_t re[48];
        wr64(re, ekey(left)); wr64(re + 8, node->bid); wr64(re + 16, node->ib);
        memcpy(re + 24, new_e, 24);
        rc = bt_page_write(w, nbt, &rnode, re, 2);
        if (rc) return rc;
        bt_set_root(w, nbt, rbid, rib);
        return 0;
    }
    btn *parent = &path[i - 1];
    uint8_t pents[736];
    int at = parent->idx + 1;
    memcpy(pents, parent->ents, (size_t)at * 24);
    memcpy(pents + (size_t)at * 24, new_e, 24);
    memcpy(pents + (size_t)(at + 1) * 24, parent->ents + (size_t)at * 24, (size_t)(parent->cent - at) * 24);
    return bt_store(w, nbt, path, i - 1, pents, parent->cent + 1);
}

int bt_get(opw *w, int nbt, uint64_t key, uint8_t *ent, int *found) {
    btn path[BT_MAXDEPTH];
    int depth;
    *found = 0;
    int rc = bt_descend(w, nbt, key, path, &depth);
    if (rc) return rc;
    btn *leaf = &path[depth - 1];
    unsigned esz = bt_esz(nbt, 0);
    for (int i = 0; i < leaf->cent; i++) {
        if (ekey(leaf->ents + (size_t)i * esz) == key) {
            if (ent) memcpy(ent, leaf->ents + (size_t)i * esz, esz);
            *found = 1;
            break;
        }
    }
    return 0;
}

int bt_put(opw *w, int nbt, uint64_t key, const uint8_t *entry) {
    btn path[BT_MAXDEPTH];
    int depth;
    int rc = bt_descend(w, nbt, key, path, &depth);
    if (rc) return rc;
    btn *leaf = &path[depth - 1];
    unsigned esz = bt_esz(nbt, 0);
    uint8_t ents[736];
    int n = leaf->cent, at = n, replace = 0;
    for (int i = 0; i < n; i++) {
        uint64_t k = ekey(leaf->ents + (size_t)i * esz);
        if (k == key) { at = i; replace = 1; break; }
        if (k > key) { at = i; break; }
    }
    memcpy(ents, leaf->ents, (size_t)at * esz);
    memcpy(ents + (size_t)at * esz, entry, esz);
    if (replace) memcpy(ents + (size_t)(at + 1) * esz, leaf->ents + (size_t)(at + 1) * esz, (size_t)(n - at - 1) * esz);
    else memcpy(ents + (size_t)(at + 1) * esz, leaf->ents + (size_t)at * esz, (size_t)(n - at) * esz);
    return bt_store(w, nbt, path, depth - 1, ents, replace ? n : n + 1);
}

static int bt_remove_node(opw *w, int nbt, btn *path, int i) {
    btn *node = &path[i];
    int rc = opw_free(w, node->ib, 512);
    if (rc) return rc;
    btn *parent = &path[i - 1];
    uint8_t pents[736];
    int pn = 0;
    for (int j = 0; j < parent->cent; j++) {
        if (j == parent->idx) continue;
        memcpy(pents + (size_t)pn * 24, parent->ents + (size_t)j * 24, 24);
        pn++;
    }
    if (pn == 0 && i - 1 > 0) return bt_remove_node(w, nbt, path, i - 1);
    if (pn == 0) {                         /* the root became empty: it turns into an empty leaf */
        parent->level = 0;
        parent->cent = 0;
        return bt_page_write(w, nbt, parent, pents, 0);
    }
    return bt_store(w, nbt, path, i - 1, pents, pn);
}

int bt_delete(opw *w, int nbt, uint64_t key, int *found) {
    btn path[BT_MAXDEPTH];
    int depth;
    *found = 0;
    int rc = bt_descend(w, nbt, key, path, &depth);
    if (rc) return rc;
    btn *leaf = &path[depth - 1];
    unsigned esz = bt_esz(nbt, 0);
    uint8_t ents[736];
    int n = 0;
    for (int i = 0; i < leaf->cent; i++) {
        if (ekey(leaf->ents + (size_t)i * esz) == key) continue;
        memcpy(ents + (size_t)n * esz, leaf->ents + (size_t)i * esz, esz);
        n++;
    }
    if (n == leaf->cent) return 0;
    *found = 1;
    if (n == 0 && depth > 1) return bt_remove_node(w, nbt, path, depth - 1);
    return bt_store(w, nbt, path, depth - 1, ents, n);
}

static int bt_walk(opw *w, int nbt, uint64_t ib, uint64_t bid, bt_cb cb, void *ctx, int depth) {
    if (depth > BT_MAXDEPTH) return CORRUPT("B-tree too deep");
    btn n;
    int rc = bt_read(w, nbt, ib, bid, &n);
    if (rc) return rc;
    for (int i = 0; i < n.cent; i++) {
        if (n.level) {
            const uint8_t *e = n.ents + (size_t)i * 24;
            rc = bt_walk(w, nbt, op_u64(e + 16), op_u64(e + 8), cb, ctx, depth + 1);
        } else rc = cb(ctx, n.ents + (size_t)i * bt_esz(nbt, 0));
        if (rc) return rc;
    }
    return 0;
}
int bt_items(opw *w, int nbt, bt_cb cb, void *ctx) {
    return bt_walk(w, nbt, nbt ? w->nbt_ib : w->bbt_ib, nbt ? w->nbt_bid : w->bbt_bid, cb, ctx, 0);
}

/* ---- blocks ------------------------------------------------------------------------------------------------------------------ */
int opw_bbt_entry(opw *w, uint64_t bid, bbt_e *out) {
    uint8_t e[24];
    int found;
    int rc = bt_get(w, 0, bid & ~(uint64_t)1, e, &found);
    if (rc) return rc;
    if (!found) return CORRUPT("BID 0x%llx not in the block B-tree", (unsigned long long)bid);
    out->bid = op_u64(e); out->ib = op_u64(e + 8); out->cb = op_u16(e + 16); out->cref = op_u16(e + 18);
    return 0;
}

int opw_read_block_raw(opw *w, uint64_t bid, opbuf *out) {
    bbt_e e;
    int rc = opw_bbt_entry(w, bid, &e);
    if (rc) return rc;
    out->p = (uint8_t *)malloc(e.cb ? e.cb : 1);
    if (!out->p) return NOMEM;
    out->n = e.cb;
    rc = opw_read(w, e.ib, e.cb, out->p);
    if (rc) { free(out->p); out->p = NULL; }
    return rc;
}

static void bbt_pack(uint8_t *e, uint64_t bid, uint64_t ib, unsigned cb, unsigned cref) {
    wr64(e, bid); wr64(e + 8, ib); wr16(e + 16, cb); wr16(e + 18, cref); wr32(e + 20, 0);
}

int opw_add_block(opw *w, const uint8_t *data, size_t cb, int internal, unsigned cref, uint64_t *out) {
    if (cb > OP_BLOCKMAX) return op_err(OPST_E_ARG, "block too large");
    uint64_t bid = w->bid_next_b | (internal ? 2u : 0u);
    w->bid_next_b += 4;
    size_t size = (cb + 16 + 63) / 64 * 64;
    uint64_t ib;
    int rc = opw_alloc(w, size, 64, &ib);
    if (rc) return rc;
    uint8_t *buf = (uint8_t *)calloc(1, size);
    if (!buf) return NOMEM;
    if (cb) memcpy(buf, data, cb);
    if (!internal) op_crypt_block(w->crypt, 1, buf, cb, (uint32_t)bid);
    uint8_t *t = buf + size - 16;
    wr16(t, (unsigned)cb); wr16(t + 2, sig_of(ib, bid)); wr32(t + 4, op_crc(buf, cb)); wr64(t + 8, bid);
    rc = opw_write(w, ib, buf, size);
    free(buf);
    if (rc) return rc;
    uint8_t e[24];
    bbt_pack(e, bid, ib, (unsigned)cb, cref);
    rc = bt_put(w, 0, bid, e);
    if (rc) return rc;
    *out = bid;
    return 0;
}

int opw_xblock(opw *w, int level, const uint64_t *ids, size_t n, uint32_t total, uint64_t *bid) {
    bbuf b = {0};
    int rc = bb_u8(&b, 1);
    if (!rc) rc = bb_u8(&b, (unsigned)level);
    if (!rc) rc = bb_u16(&b, (unsigned)n);
    if (!rc) rc = bb_u32(&b, total);
    for (size_t i = 0; i < n && !rc; i++) rc = bb_u64(&b, ids[i]);
    if (rc) { bb_free(&b); return NOMEM; }
    rc = opw_add_block(w, b.p, b.n, 1, 2, bid);
    bb_free(&b);
    return rc;
}

int opw_add_ref(opw *w, uint64_t bid) {
    bbt_e e;
    int rc = opw_bbt_entry(w, bid, &e);
    if (rc) return rc;
    uint8_t ent[24];
    bbt_pack(ent, e.bid, e.ib, e.cb, e.cref + 1);
    return bt_put(w, 0, e.bid, ent);
}

int opw_release(opw *w, uint64_t bid) {
    if (!bid) return 0;
    bbt_e e;
    int rc = opw_bbt_entry(w, bid, &e);
    if (rc) return rc;
    if (e.cref - 1 > 1) {
        uint8_t ent[24];
        bbt_pack(ent, e.bid, e.ib, e.cb, e.cref - 1);
        return bt_put(w, 0, e.bid, ent);
    }
    uint64_t *children = NULL;
    size_t nch = 0;
    if (bid & 2) {
        uint8_t *d = (uint8_t *)malloc(e.cb ? e.cb : 1);
        if (!d) return NOMEM;
        rc = opw_read(w, e.ib, e.cb, d);
        if (rc) { free(d); return rc; }
        unsigned bt = d[0], lvl = d[1], cent = op_u16(d + 2);
        children = (uint64_t *)malloc((size_t)(cent * 2 + 1) * sizeof *children);
        if (!children) { free(d); return NOMEM; }
        if (bt == 1 && 8 + (size_t)cent * 8 <= e.cb) {
            for (unsigned i = 0; i < cent; i++) children[nch++] = op_u64(d + 8 + 8 * i);
        } else if (bt == 2) {
            if (lvl == 0 && 8 + (size_t)cent * 24 <= e.cb) {
                for (unsigned i = 0; i < cent; i++) { children[nch++] = op_u64(d + 8 + 24 * i + 8); children[nch++] = op_u64(d + 8 + 24 * i + 16); }
            } else if (lvl != 0 && 8 + (size_t)cent * 16 <= e.cb) {
                for (unsigned i = 0; i < cent; i++) children[nch++] = op_u64(d + 8 + 16 * i + 8);
            }
        }
        free(d);
    }
    rc = opw_free(w, e.ib, ((size_t)e.cb + 16 + 63) / 64 * 64);
    int found;
    if (!rc) rc = bt_delete(w, 0, e.bid, &found);
    for (size_t i = 0; i < nch && !rc; i++) rc = opw_release(w, children[i]);
    free(children);
    return rc;
}

/* ---- nodes ------------------------------------------------------------------------------------------------------------------- */
int opw_node(opw *w, uint32_t nid, nbt_e *out) {
    uint8_t e[32];
    int found;
    int rc = bt_get(w, 1, nid, e, &found);
    if (rc) return rc;
    if (!found) return OPST_E_NOTFOUND;
    out->bd = op_u64(e + 8); out->bs = op_u64(e + 16); out->parent = op_u32(e + 24);
    return 0;
}
int opw_node_put(opw *w, uint32_t nid, uint64_t bd, uint64_t bs, uint32_t parent) {
    uint8_t e[32];
    wr64(e, nid); wr64(e + 8, bd); wr64(e + 16, bs); wr32(e + 24, parent); wr32(e + 28, 0);
    return bt_put(w, 1, nid, e);
}
int opw_node_del(opw *w, uint32_t nid) {
    int found;
    return bt_delete(w, 1, nid, &found);
}

/* ---- commit / rollback -------------------------------------------------------------------------------------------------------- */
static void header_bytes(opw *w, int valid, uint8_t *h) {
    memcpy(h, w->hdr, 564);
    wr64(h + 32, w->bid_next_p);
    wr32(h + 40, (uint32_t)(w->unique + 1));
    wr64(h + 184, w->eof); wr64(h + 192, w->amap_last); wr64(h + 200, w->cb_amap_free);
    wr64(h + 216, w->nbt_bid); wr64(h + 224, w->nbt_ib);
    wr64(h + 232, w->bbt_bid); wr64(h + 240, w->bbt_ib);
    h[248] = (uint8_t)valid;
    wr64(h + 516, w->bid_next_b);
    wr32(h + 4, op_crc(h + 8, 471));
    wr32(h + 524, op_crc(h + 8, 516));
}

static void load_header(opw *w) {
    const uint8_t *h = w->hdr;
    w->bid_next_p = op_u64(h + 32);
    w->unique = op_u32(h + 40);
    w->eof = op_u64(h + 184); w->amap_last = op_u64(h + 192); w->cb_amap_free = op_u64(h + 200);
    w->nbt_bid = op_u64(h + 216); w->nbt_ib = op_u64(h + 224);
    w->bbt_bid = op_u64(h + 232); w->bbt_ib = op_u64(h + 240);
    w->bid_next_b = op_u64(h + 516);
    w->eof_start = w->eof;
}

static unsigned maxrun(const uint8_t *a) {
    unsigned best = 0, run = 0;
    for (unsigned i = 0; i < OPW_SLOTS; i++) {
        if (!bit(a, i)) { run++; if (run > best) best = run; } else run = 0;
    }
    return best > 255 ? 255 : best;
}

static int fmap_update(opw *w) {
    int rc = 0;
    struct fp { uint64_t ib; uint8_t d[496]; int ok; } *pages = NULL;
    size_t np = 0, cap = 0;
    for (uint32_t sec = 0; sec < w->nsec && !rc; sec++) {
        if (!w->dirty[sec]) continue;
        int err;
        uint8_t *a = amap(w, sec, &err);
        if (!a) { rc = err; break; }
        unsigned val = maxrun(a);
        if (sec < 128) { w->hdr[256 + sec] = (uint8_t)val; continue; }
        uint64_t ib = OPW_AMAP0 + (uint64_t)(128 + 496 * ((sec - 128) / 496)) * OPW_SECT + 1024;
        size_t k;
        for (k = 0; k < np; k++) if (pages[k].ib == ib) break;
        if (k == np) {
            if (np == cap) {
                cap = cap ? cap * 2 : 8;
                struct fp *t = (struct fp *)realloc(pages, cap * sizeof *t);
                if (!t) { rc = OPST_E_NOMEM; break; }
                pages = t;
            }
            uint8_t raw[512];
            rc = opw_read(w, ib, 512, raw);
            if (rc) break;
            pages[np].ib = ib;
            pages[np].ok = raw[496] == 0x82;
            memcpy(pages[np].d, raw, 496);
            np++;
        }
        if (pages[k].ok) pages[k].d[(sec - 128) % 496] = (uint8_t)val;
    }
    for (size_t k = 0; k < np && !rc; k++) {
        if (!pages[k].ok) continue;
        uint8_t pg[512];
        page_bytes(pg, pages[k].d, 0x82, pages[k].ib, pages[k].ib, 0);
        rc = opw_write(w, pages[k].ib, pg, 512);
    }
    free(pages);
    return rc;
}

static int dlist_update(opw *w) {
    uint8_t d[512];
    int rc = opw_read(w, OPW_DLIST_IB, 512, d);
    if (rc) return rc;
    if (d[496] != 0x86) return 0;
    unsigned cent = d[1];
    for (unsigned i = 0; i < cent && 8 + 4 * i + 4 <= 496; i++) {
        uint32_t v = op_u32(d + 8 + 4 * i);
        uint32_t pn = v & 0xFFFFF;
        if (pn < w->nsec && w->dirty[pn]) {
            int err;
            uint8_t *a = amap(w, pn, &err);
            if (!a) return err;
            unsigned zeros = 0;
            for (unsigned k = 0; k < OPW_SLOTS; k++) zeros += !bit(a, k);
            wr32(d + 8 + 4 * i, pn | (zeros << 20));
        }
    }
    wr32(d + 500, op_crc(d, 496));
    return opw_write(w, OPW_DLIST_IB, d, 512);
}

static int cmp_slot(const void *a, const void *b) {
    uint64_t x = ((const ovl_ent *)a)->slot, y = ((const ovl_ent *)b)->slot;
    return x < y ? -1 : x > y;
}

/* test hook: OPST_TEST_CRASH=n stops the commit as if the machine died at point n (1 journal written, 2 header marked invalid,
   3 data written, 4 final header written); the journal is left behind and no rollback is attempted */
static int crash_point(void) {
    const char *e = getenv("OPST_TEST_CRASH");
    return e && *e ? atoi(e) : 0;
}
#define CRASH(n) do { if (crash_point() == (n)) { free(jp); return op_err(OPST_E_IO, "simulated crash at point %d", (n)); } } while (0)

static int commit_w(opw *w) {
    opst *p = w->p;
    int rc = reconcile(w);
    if (rc) return rc;
    if (!w->nov && w->eof == w->eof_start) {
        int any = 0;
        for (uint32_t s = 0; s < w->nsec; s++) if (w->dirty[s]) { any = 1; break; }
        if (!any) return 0;
    }
    for (uint32_t sec = 0; sec < w->nsec && !rc; sec++) {
        if (!w->dirty[sec]) continue;
        uint64_t ib = OPW_AMAP0 + (uint64_t)sec * OPW_SECT;
        uint8_t pg[512];
        page_bytes(pg, w->amaps[sec], 0x84, ib, ib, 0);
        rc = opw_write(w, ib, pg, 512);
    }
    if (!rc) rc = fmap_update(w);
    if (!rc) rc = dlist_update(w);
    if (rc) return rc;
    int64_t cb = (int64_t)w->cb_amap_free + w->free_delta;
    w->cb_amap_free = cb < 0 ? 0 : (uint64_t)cb;

    /* the pending slots in file order, coalesced into runs */
    op_qsort(w->ov, w->nov, sizeof *w->ov, cmp_slot);
    /* journal: original header + original bytes of every run that lies inside the old file */
    bbuf j = {0};
    bb_put(&j, JMAGIC, 8);
    bb_u64(&j, w->eof_start);
    uint8_t orig_hdr[564];
    rc = op_file_pread(p->f, orig_hdr, 564, 0);
    if (rc) { bb_free(&j); return rc; }
    bb_u64(&j, 0); bb_u32(&j, 564); bb_put(&j, orig_hdr, 564);
    for (size_t i = 0; i < w->nov && !rc;) {
        size_t k = i + 1;
        while (k < w->nov && w->ov[k].slot == w->ov[k - 1].slot + 1) k++;
        uint64_t off = w->ov[i].slot << 6, len = (uint64_t)(k - i) * 64;
        if (off < w->eof_start) {
            size_t m = (size_t)(len < w->eof_start - off ? len : w->eof_start - off);
            uint8_t *tmp = (uint8_t *)malloc(m);
            if (!tmp) { rc = OPST_E_NOMEM; break; }
            rc = op_file_pread(p->f, tmp, m, off);
            if (!rc) { bb_u64(&j, off); bb_u32(&j, (uint32_t)m); bb_put(&j, tmp, m); }
            free(tmp);
        }
        i = k;
    }
    if (!rc && j.bad) rc = OPST_E_NOMEM;
    char *jp = journal_path(p->path);
    if (!rc && !jp) rc = OPST_E_NOMEM;
    if (!rc) rc = op_file_write_all(jp, j.p, j.n);
    bb_free(&j);
    if (rc) { free(jp); return rc; }
    CRASH(1);

    uint8_t h[564];
    header_bytes(w, 0, h);
    rc = op_file_pwrite(p->f, h, 564, 0);
    if (!rc) rc = op_file_sync(p->f);
    if (!rc) CRASH(2);
    if (!rc && w->eof > w->eof_start) rc = op_file_truncate(p->f, w->eof);      /* grow to the exact section grid first */
    for (size_t i = 0; i < w->nov && !rc;) {
        size_t k = i + 1;
        while (k < w->nov && w->ov[k].slot == w->ov[k - 1].slot + 1) k++;
        uint8_t *tmp = (uint8_t *)malloc((k - i) * 64);
        if (!tmp) { rc = OPST_E_NOMEM; break; }
        for (size_t q = i; q < k; q++) memcpy(tmp + (q - i) * 64, w->ov[q].d, 64);
        rc = op_file_pwrite(p->f, tmp, (k - i) * 64, w->ov[i].slot << 6);
        free(tmp);
        i = k;
    }
    if (!rc) rc = op_file_sync(p->f);
    if (!rc) CRASH(3);
    if (!rc) {
        header_bytes(w, 2, h);
        rc = op_file_pwrite(p->f, h, 564, 0);
        if (!rc) rc = op_file_sync(p->f);
    }
    if (!rc) CRASH(4);
    if (!rc) { op_file_remove(jp); memcpy(p->header, h, sizeof p->header); }
    free(jp);
    return rc;
}

/* ---- transaction object ---------------------------------------------------------------------------------------------------- */
static void opw_free_all(opw *w) {
    if (!w) return;
    free(w->ov); free(w->ovidx);
    for (size_t i = 0; i < w->capamaps; i++) free(w->amaps[i]);
    free(w->amaps); free(w->dirty); free(w->freecnt);
    free(w);
}

static int opw_create(opst *p, opw **out) {
    opw *w = (opw *)calloc(1, sizeof *w);
    if (!w) return NOMEM;
    w->p = p;
    w->crypt = p->crypt;
    w->reconciled = p->reconciled;          /* reconciling walks both B-trees (hundreds of ms on a multi-GB file); once per handle is enough */
    int rc = op_file_pread(p->f, w->hdr, 564, 0);
    if (rc) { free(w); return rc; }
    load_header(w);
    w->nsec = (uint32_t)((w->eof - OPW_AMAP0) / OPW_SECT);
    rc = w_cap_secs(w, w->nsec ? w->nsec : 1);          /* the per-section arrays exist even when the transaction allocates nothing (commit walks them) */
    if (rc) { free(w); return rc; }
    const uint8_t *fwd = op_mpbb_table();
    for (int i = 0; i < 256; i++) w->mpbb_r[fwd[i]] = (uint8_t)i;       /* inverse permutation (encode) */
    *out = w;
    return 0;
}

int op_txn_begin(opst *p, opw **out) {
    if (!p->writable) return op_err(OPST_E_STATE, "the file was opened read-only");
    if (p->w) { *out = p->w; return 0; }
    int rc = opw_create(p, out);
    if (!rc) p->w = *out;
    return rc;
}

/* a transaction object that is never committed: for reading through the tree code (e.g. the source file of a copy) */
int op_ro_begin(opst *p, opw **out) { return opw_create(p, out); }
void op_ro_end(opw *w) { opw_free_all(w); }

int op_txn_commit(opst *p) {
    if (!p->w) return 0;
    int rc = commit_w(p->w);
    if (rc) {
        int simulated = crash_point() != 0;
        op_txn_abort(p);
        if (!simulated) {                       /* a real failure: undo what may have been written, using the journal */
            int recovered;
            char saved[320];
            snprintf(saved, sizeof saved, "%s", opst_last_error());
            if (op_journal_recover(p->path, &recovered) == 0) op_reload(p);
            op_err(rc, "%s", saved);
        }
        return rc;
    }
    if (p->w->reconciled) p->reconciled = 1;
    opw_free_all(p->w);
    p->w = NULL;
    return op_reload(p);
}

void op_txn_abort(opst *p) {
    if (!p || !p->w) return;
    opw_free_all(p->w);
    p->w = NULL;
}

/* ---- validation (structural check of the trees against the maps) -------------------------------------------------------------- */
typedef struct { opw *w; bbuf *rep; size_t *np; int nbt; uint64_t last; int have_last; struct { uint64_t bid, ib; unsigned cb; } *blocks; size_t nb, capb; } vctx;
#define VNOTE(...) do { char t_[240]; snprintf(t_, sizeof t_, __VA_ARGS__); bb_put(c->rep, t_, strlen(t_)); bb_u8(c->rep, '\n'); (*c->np)++; } while (0)

static int v_walk(vctx *c, uint64_t ib, uint64_t bid, int level, int have_pkey, uint64_t pkey, int depth) {
    opw *w = c->w;
    const char *nm = c->nbt ? "NBT" : "BBT";
    uint8_t pg[512];
    if (depth > 8) return 0;
    int rc = opw_read(w, ib, 512, pg);
    if (rc) return rc;
    if (have_pkey && pg[488] && op_u64(pg) != pkey) VNOTE("%s page 0x%llx btkeyMin mismatch (page %llx, parent %llx)", nm, (unsigned long long)ib, (unsigned long long)op_u64(pg), (unsigned long long)pkey);
    unsigned ptype = c->nbt ? 0x81 : 0x80;
    if (pg[496] != ptype || pg[497] != ptype) { VNOTE("%s page type bad at 0x%llx", nm, (unsigned long long)ib); return 0; }
    if (op_u32(pg + 500) != op_crc(pg, 496)) VNOTE("%s page CRC bad at 0x%llx", nm, (unsigned long long)ib);
    if (op_u16(pg + 498) != sig_of(ib, op_u64(pg + 504))) VNOTE("%s page sig bad at 0x%llx", nm, (unsigned long long)ib);
    if (op_u64(pg + 504) != bid) VNOTE("%s page bid mismatch at 0x%llx", nm, (unsigned long long)ib);
    if (!opw_is_allocated(w, ib, 512)) VNOTE("%s page 0x%llx not marked allocated in AMap", nm, (unsigned long long)ib);
    unsigned cent = pg[488], cbent = pg[490], lv = pg[491];
    if ((int)lv != level) VNOTE("%s level mismatch at 0x%llx", nm, (unsigned long long)ib);
    if (cbent < 24 || cent * cbent > 488) return 0;
    for (unsigned i = 0; i < cent; i++) {
        const uint8_t *e = pg + i * cbent;
        if (lv) {
            rc = v_walk(c, op_u64(e + 16), op_u64(e + 8), (int)lv - 1, 1, op_u64(e), depth + 1);
            if (rc) return rc;
        } else {
            uint64_t k = op_u64(e);
            if (c->have_last && k <= c->last) VNOTE("%s keys out of order at 0x%llx", nm, (unsigned long long)ib);
            c->last = k; c->have_last = 1;
            if (!c->nbt) {
                if (c->nb == c->capb) {
                    size_t nc = c->capb ? c->capb * 2 : 1024;
                    void *t = realloc(c->blocks, nc * sizeof *c->blocks);
                    if (!t) return OPST_E_NOMEM;
                    c->blocks = t; c->capb = nc;
                }
                c->blocks[c->nb].bid = k; c->blocks[c->nb].ib = op_u64(e + 8); c->blocks[c->nb].cb = op_u16(e + 16); c->nb++;
            }
        }
    }
    return 0;
}

int opw_validate(opw *w, bbuf *rep, size_t *nproblems) {
    size_t np = 0;
    vctx cs;
    vctx *c = &cs;
    memset(&cs, 0, sizeof cs);
    c->w = w; c->rep = rep; c->np = &np;
    int rc = 0;
    for (int pass = 0; pass < 2 && !rc; pass++) {
        c->nbt = pass == 0;
        c->have_last = 0;
        uint64_t rb = c->nbt ? w->nbt_bid : w->bbt_bid, ri = c->nbt ? w->nbt_ib : w->bbt_ib;
        uint8_t pg[512];
        rc = opw_read(w, ri, 512, pg);
        if (!rc) rc = v_walk(c, ri, rb, pg[491], 0, 0, 0);
    }
    for (size_t i = 0; i < c->nb && !rc; i++) {
        uint64_t bid = c->blocks[i].bid, ib = c->blocks[i].ib;
        unsigned cb = c->blocks[i].cb;
        size_t size = ((size_t)cb + 16 + 63) / 64 * 64;
        if (!opw_is_allocated(w, ib, size)) VNOTE("block 0x%llx not allocated in AMap", (unsigned long long)bid);
        uint8_t *buf = (uint8_t *)malloc(size);
        if (!buf) { rc = OPST_E_NOMEM; break; }
        rc = opw_read(w, ib, size, buf);
        if (rc) { free(buf); break; }
        const uint8_t *t = buf + size - 16;
        if (op_u64(t + 8) != bid || op_u16(t) != cb) VNOTE("block 0x%llx trailer mismatch", (unsigned long long)bid);
        if (op_u16(t + 2) != sig_of(ib, bid)) VNOTE("block 0x%llx sig mismatch", (unsigned long long)bid);
        if (op_u32(t + 4) != op_crc(buf, cb)) VNOTE("block 0x%llx CRC mismatch", (unsigned long long)bid);
        free(buf);
    }
    for (uint32_t sec = 0; sec < w->nsec && !rc; sec++) {
        int err;
        uint8_t *a = amap(w, sec, &err);
        if (!a) { rc = err; break; }
        unsigned val = maxrun(a), cur;
        if (sec < 128) cur = w->hdr[256 + sec];
        else {
            uint8_t pg[512];
            rc = opw_read(w, OPW_AMAP0 + (uint64_t)(128 + 496 * ((sec - 128) / 496)) * OPW_SECT + 1024, 512, pg);
            if (rc) break;
            if (pg[496] != 0x82) continue;
            cur = pg[(sec - 128) % 496];
        }
        if (cur != 0xFF && cur != val) VNOTE("FMap byte for AMap %u is %u, expected %u", sec, cur, val);
    }
    free(c->blocks);
    *nproblems = np;
    return rc;
}
