/* op_ndb.c - NDB layer: file header, node/block B-trees, data blocks, subnodes, open/close, verification. */
#include "op_internal.h"
#include "op_mpbb.inc"
void op_txn_abort(opst *p);                                  /* op_wr.c */

/* ---- B-tree loading --------------------------------------------------------------------------------------------- */
static int nbt_push(opst *p, const op_nbt_ent *e) {
    if (p->nnbt == p->capnbt) {
        size_t nc = p->capnbt ? p->capnbt * 2 : 1024;
        op_nbt_ent *t = (op_nbt_ent *)realloc(p->nbt, nc * sizeof *t);
        if (!t) return OPST_E_NOMEM;
        p->nbt = t; p->capnbt = nc;
    }
    p->nbt[p->nnbt++] = *e;
    return 0;
}
static int bbt_push(opst *p, const op_bbt_ent *e) {
    if (p->nbbt == p->capbbt) {
        size_t nc = p->capbbt ? p->capbbt * 2 : 4096;
        op_bbt_ent *t = (op_bbt_ent *)realloc(p->bbt, nc * sizeof *t);
        if (!t) return OPST_E_NOMEM;
        p->bbt = t; p->capbbt = nc;
    }
    p->bbt[p->nbbt++] = *e;
    return 0;
}

/* one B-tree page. Layouts: Unicode 512 - 488 bytes of entries, cEnt/cEntMax/cbEnt/cLevel at 488, trailer at 496 (CRC over 496);
   ANSI - 496 bytes of entries, counts at 496, trailer at 500 (CRC at 508 over the first 500 bytes), 32-bit fields;
   Unicode 4K - 4056 bytes of entries, 16-bit cEnt/cEntMax at 4056, cbEnt/cLevel at 4060, trailer at 4072 (CRC over 4072). */
static int load_tree(opst *p, uint64_t ib, uint8_t ptype, int nbt, int depth) {
    uint8_t page[4096];
    size_t psz = p->fmt == OP_FMT_UNI4K ? 4096 : 512;
    if (depth > 8) return op_err(OPST_E_FORMAT, "B-tree too deep");
    if (ib + psz > p->fsize) return op_err(OPST_E_FORMAT, "B-tree page at 0x%llx is outside the file", (unsigned long long)ib);
    int rc = op_file_pread(p->f, page, psz, ib);
    if (rc) return rc;
    unsigned cent, cbent, clevel, esz_area, toff, crc_off, crc_len;
    if (p->fmt == OP_FMT_ANSI)        { cent = page[496]; cbent = page[498]; clevel = page[499]; esz_area = 496; toff = 500; crc_off = 508; crc_len = 500; }
    else if (p->fmt == OP_FMT_UNI4K)  { cent = op_u16(page + 4056); cbent = page[4060]; clevel = page[4061]; esz_area = 4056; toff = 4072; crc_off = 4076; crc_len = 4072; }
    else                              { cent = page[488]; cbent = page[490]; clevel = page[491]; esz_area = 488; toff = 496; crc_off = 500; crc_len = 496; }
    if (page[toff] != ptype || page[toff + 1] != ptype) return op_err(OPST_E_FORMAT, "B-tree page type mismatch at 0x%llx", (unsigned long long)ib);
    if (op_u32(page + crc_off) != op_crc(page, crc_len)) p->page_crc_errors++;
    unsigned minent = p->fmt == OP_FMT_ANSI ? 12 : 24;
    if (cbent < minent || cent * cbent > esz_area) return op_err(OPST_E_FORMAT, "bad B-tree page at 0x%llx", (unsigned long long)ib);
    for (unsigned i = 0; i < cent; i++) {
        const uint8_t *e = page + i * cbent;
        if (p->fmt == OP_FMT_ANSI) {
            if (clevel > 0) {                                    /* BTENTRY: key(4) bid(4) ib(4) */
                rc = load_tree(p, op_u32(e + 8), ptype, nbt, depth + 1);
                if (rc) return rc;
            } else if (nbt) {                                    /* NBTENTRY: nid(4) bidData(4) bidSub(4) nidParent(4) */
                op_nbt_ent n;
                n.nid = op_u32(e); n.bd = op_u32(e + 4); n.bs = op_u32(e + 8); n.parent = op_u32(e + 12);
                rc = nbt_push(p, &n);
                if (rc) return op_err(rc, "out of memory");
            } else {                                             /* BBTENTRY: bid(4) ib(4) cb(2) cRef(2) */
                op_bbt_ent b;
                b.bid = op_u32(e) & ~(uint64_t)1; b.ib = op_u32(e + 4); b.cb = op_u16(e + 8);
                rc = bbt_push(p, &b);
                if (rc) return op_err(rc, "out of memory");
            }
        } else if (clevel > 0) {
            rc = load_tree(p, op_u64(e + 16), ptype, nbt, depth + 1);
            if (rc) return rc;
        } else if (nbt) {
            op_nbt_ent n;
            n.nid = (uint32_t)(op_u64(e) & 0xFFFFFFFFu);
            n.bd = op_u64(e + 8); n.bs = op_u64(e + 16); n.parent = op_u32(e + 24);
            rc = nbt_push(p, &n);
            if (rc) return op_err(rc, "out of memory");
        } else {
            op_bbt_ent b;
            b.bid = op_u64(e) & ~(uint64_t)1; b.ib = op_u64(e + 8); b.cb = op_u16(e + 16);
            rc = bbt_push(p, &b);
            if (rc) return op_err(rc, "out of memory");
        }
    }
    return 0;
}

static int cmp_nbt(const void *a, const void *b) {
    uint32_t x = ((const op_nbt_ent *)a)->nid, y = ((const op_nbt_ent *)b)->nid;
    return x < y ? -1 : x > y;
}
static int cmp_bbt(const void *a, const void *b) {
    uint64_t x = ((const op_bbt_ent *)a)->bid, y = ((const op_bbt_ent *)b)->bid;
    return x < y ? -1 : x > y;
}

const op_nbt_ent *op_nbt_find(opst *p, uint32_t nid) {
    size_t lo = 0, hi = p->nnbt;
    while (lo < hi) {
        size_t mid = (lo + hi) / 2;
        if (p->nbt[mid].nid < nid) lo = mid + 1; else hi = mid;
    }
    return (lo < p->nnbt && p->nbt[lo].nid == nid) ? &p->nbt[lo] : NULL;
}
const op_bbt_ent *op_bbt_find(opst *p, uint64_t bid) {
    bid &= ~(uint64_t)1;
    size_t lo = 0, hi = p->nbbt;
    while (lo < hi) {
        size_t mid = (lo + hi) / 2;
        if (p->bbt[mid].bid < bid) lo = mid + 1; else hi = mid;
    }
    return (lo < p->nbbt && p->bbt[lo].bid == bid) ? &p->bbt[lo] : NULL;
}
int op_node_get(opst *p, uint32_t nid, opnode *out) {
    const op_nbt_ent *e = op_nbt_find(p, nid);
    if (!e) return op_err(OPST_E_NOTFOUND, "node 0x%x not found", nid);
    out->nid = nid; out->bd = e->bd; out->bs = e->bs;
    return 0;
}

/* ---- blocks ------------------------------------------------------------------------------------------------------ */
int op_read_raw(opst *p, uint64_t bid, opbuf *out) {
    const op_bbt_ent *e = op_bbt_find(p, bid);
    if (!e) return op_err(OPST_E_FORMAT, "block 0x%llx is not in the block B-tree", (unsigned long long)bid);
    if (e->ib + e->cb > p->fsize) return op_err(OPST_E_FORMAT, "block 0x%llx lies outside the file", (unsigned long long)bid);
    out->p = (uint8_t *)malloc(e->cb ? e->cb : 1);
    if (!out->p) return op_err(OPST_E_NOMEM, "out of memory");
    out->n = e->cb;
    int rc = op_file_pread(p->f, out->p, e->cb, e->ib);
    if (rc) { free(out->p); out->p = NULL; out->n = 0; return rc; }
    if (p->fmt == OP_FMT_UNI4K && e->cb >= 2) {
        /* 4K files keep a 24-byte trailer at the end of the 512-aligned block: cb(2) sig(2) crc(4) bid(8) flags(2) cbUncompressed(2) pad(4).
           A block whose cbUncompressed differs from cb is a zlib stream (starts 0x78) that inflates to cbUncompressed bytes. */
        uint8_t t[24];
        uint64_t tot = ((uint64_t)e->cb + 24 + 511) / 512 * 512;
        if (e->ib + tot <= p->fsize && op_file_pread(p->f, t, 24, e->ib + tot - 24) == 0) {
            unsigned unc = op_u16(t + 18);
            if (unc && unc != e->cb && out->p[0] == 0x78) {
                uint8_t *u = (uint8_t *)malloc(unc);
                if (!u) { free(out->p); out->p = NULL; out->n = 0; return op_err(OPST_E_NOMEM, "out of memory"); }
                if (op_zinflate(out->p, e->cb, u, unc) != 0) {
                    free(u); free(out->p); out->p = NULL; out->n = 0;
                    return op_err(OPST_E_FORMAT, "block 0x%llx is compressed and cannot be decompressed", (unsigned long long)bid);
                }
                free(out->p); out->p = u; out->n = unc;
            }
        }
    }
    return 0;
}

static int blocks_push(opblocks *bl, opbuf b) {
    opbuf *t = (opbuf *)realloc(bl->b, (bl->n + 1) * sizeof *t);
    if (!t) return OPST_E_NOMEM;
    bl->b = t;
    bl->b[bl->n++] = b;
    return 0;
}

static int blocks_rec(opst *p, uint64_t bid, opblocks *bl, int depth) {
    opbuf b;
    int rc = op_read_raw(p, bid, &b);
    if (rc) return rc;
    if (!(bid & 2)) {
        op_crypt_block(p->crypt, 0, b.p, b.n, (uint32_t)bid);
        rc = blocks_push(bl, b);
        if (rc) { free(b.p); return op_err(rc, "out of memory"); }
        return 0;
    }
    if (depth > 3 || b.n < 8 || b.p[0] != 1) { free(b.p); return op_err(OPST_E_FORMAT, "expected an XBLOCK for block 0x%llx", (unsigned long long)bid); }
    unsigned cent = op_u16(b.p + 2), bw = p->fmt == OP_FMT_ANSI ? 4 : 8;      /* width of a BID */
    if (8 + (size_t)cent * bw > b.n) { free(b.p); return op_err(OPST_E_FORMAT, "XBLOCK 0x%llx is truncated", (unsigned long long)bid); }
    for (unsigned i = 0; i < cent; i++) {
        rc = blocks_rec(p, bw == 4 ? op_u32(b.p + 8 + 4 * i) : op_u64(b.p + 8 + 8 * i), bl, depth + 1);
        if (rc) { free(b.p); return rc; }
    }
    free(b.p);
    return 0;
}

void op_blocks_free(opblocks *bl) {
    for (size_t i = 0; i < bl->n; i++) free(bl->b[i].p);
    free(bl->b);
    bl->b = NULL; bl->n = 0;
}
int op_blocks_load(opst *p, uint64_t bid, opblocks *out) {
    out->b = NULL; out->n = 0;
    if (!bid) return 0;
    int rc = blocks_rec(p, bid, out, 0);
    if (rc) op_blocks_free(out);
    return rc;
}
int op_data_concat(const opblocks *bl, uint8_t **out, size_t *n) {
    size_t tot = 0;
    for (size_t i = 0; i < bl->n; i++) tot += bl->b[i].n;
    uint8_t *r = (uint8_t *)malloc(tot ? tot : 1);
    if (!r) return op_err(OPST_E_NOMEM, "out of memory");
    size_t o = 0;
    for (size_t i = 0; i < bl->n; i++) { memcpy(r + o, bl->b[i].p, bl->b[i].n); o += bl->b[i].n; }
    *out = r; *n = tot;
    return 0;
}

/* ---- subnodes ---------------------------------------------------------------------------------------------------- */
static int subs_rec(opst *p, uint64_t bid, opsubs *s, size_t *cap, int depth) {
    opbuf b;
    int rc = op_read_raw(p, bid, &b);
    if (rc) return rc;
    if (depth > 3 || b.n < 8 || b.p[0] != 2) { free(b.p); return op_err(OPST_E_FORMAT, "expected an SLBLOCK/SIBLOCK for block 0x%llx", (unsigned long long)bid); }
    unsigned clevel = b.p[1], cent = op_u16(b.p + 2);
    int ansi = p->fmt == OP_FMT_ANSI;                      /* 4-byte fields, no padding after the 4-byte header */
    size_t esz = ansi ? (clevel == 0 ? 12 : 8) : (clevel == 0 ? 24 : 16), hdr = ansi ? 4 : 8;
    if (hdr + (size_t)cent * esz > b.n) { free(b.p); return op_err(OPST_E_FORMAT, "subnode block 0x%llx is truncated", (unsigned long long)bid); }
    for (unsigned i = 0; i < cent; i++) {
        const uint8_t *e = b.p + hdr + esz * i;
        if (clevel == 0) {
            if (s->n == *cap) {
                size_t nc = *cap ? *cap * 2 : 16;
                opsub *t = (opsub *)realloc(s->e, nc * sizeof *t);
                if (!t) { free(b.p); return op_err(OPST_E_NOMEM, "out of memory"); }
                s->e = t; *cap = nc;
            }
            opsub *d = &s->e[s->n++];
            if (ansi) { d->nid = op_u32(e); d->bd = op_u32(e + 4); d->bs = op_u32(e + 8); }
            else { d->nid = (uint32_t)(op_u64(e) & 0xFFFFFFFFu); d->bd = op_u64(e + 8); d->bs = op_u64(e + 16); }
        } else {
            rc = subs_rec(p, ansi ? op_u32(e + 4) : op_u64(e + 8), s, cap, depth + 1);
            if (rc) { free(b.p); return rc; }
        }
    }
    free(b.p);
    return 0;
}
static int cmp_sub(const void *a, const void *b) {
    uint32_t x = ((const opsub *)a)->nid, y = ((const opsub *)b)->nid;
    return x < y ? -1 : x > y;
}
int op_subs_load(opst *p, uint64_t bsub, opsubs *out) {
    size_t cap = 0;
    out->e = NULL; out->n = 0;
    if (!bsub) return 0;
    int rc = subs_rec(p, bsub, out, &cap, 0);
    if (rc) { op_subs_free(out); return rc; }
    op_qsort(out->e, out->n, sizeof *out->e, cmp_sub);
    return 0;
}
void op_subs_free(opsubs *s) { free(s->e); s->e = NULL; s->n = 0; }
const opsub *op_subs_find(const opsubs *s, uint32_t nid) {
    size_t lo = 0, hi = s->n;
    while (lo < hi) {
        size_t mid = (lo + hi) / 2;
        if (s->e[mid].nid < nid) lo = mid + 1; else hi = mid;
    }
    return (lo < s->n && s->e[lo].nid == nid) ? &s->e[lo] : NULL;
}

/* ---- open / close ----------------------------------------------------------------------------------------------- */
static void store_info(opst *p) {
    opnode n;
    oppc pc;
    if (op_node_get(p, OP_NID_STORE, &n) != 0) return;
    if (op_pc_open(p, &n, &pc) != 0) return;
    oppc_prop *pr = op_pc_find(&pc, 0x3001);
    if (pr && op_pc_value(&pc, pr) == 0) p->display_name = op_value_to_utf8(pr->ptype, pr->d, pr->n, 0, NULL);
    pr = op_pc_find(&pc, 0x35E3);                       /* wastebasket entry id: flags(4) + GUID(16) + NID(4) */
    if (pr && op_pc_value(&pc, pr) == 0 && pr->n >= 24) p->deleted = op_u32(pr->d + pr->n - 4);
    pr = op_pc_find(&pc, 0x35E0);                       /* IPM subtree entry id */
    if (pr && op_pc_value(&pc, pr) == 0 && pr->n >= 24) p->ipm_root = op_u32(pr->d + pr->n - 4);
    op_pc_close(&pc);
    if (!p->ipm_root && p->deleted) {
        const op_nbt_ent *e = op_nbt_find(p, p->deleted);
        if (e) p->ipm_root = e->parent;
    }
}

/* (re)reads the header and both B-trees into the in-memory caches */
static int load_all(opst *p) {
    free(p->nbt); free(p->bbt); free(p->display_name);
    p->nbt = NULL; p->bbt = NULL; p->display_name = NULL;
    p->nnbt = p->capnbt = p->nbbt = p->capbbt = 0;
    p->page_crc_errors = 0;
    p->ipm_root = p->deleted = 0;
    p->fsize = op_file_size(p->f);
    if (p->fsize < sizeof p->header) return op_err(OPST_E_FORMAT, "file is too small to be a PST");
    int rc = op_file_pread(p->f, p->header, sizeof p->header, 0);
    if (rc) return rc;
    if (memcmp(p->header, "!BDN", 4) != 0) return op_err(OPST_E_FORMAT, "not a PST file (bad signature)");
    p->ver = op_u16(p->header + 10);
    uint64_t nbt_ib, bbt_ib;
    if (p->ver == 14 || p->ver == 15) {                  /* ANSI: ROOT at 0xA4, 32-bit fields, bCryptMethod at 0x1CD */
        p->fmt = OP_FMT_ANSI; p->blk_data_max = 8180;
        nbt_ib = op_u32(p->header + 0xA4 + 24); bbt_ib = op_u32(p->header + 0xA4 + 32);
        p->crypt = p->header[0x1CD];
    } else if (p->ver >= 23) {                           /* Unicode: ROOT at 0xB4; 4K pages from wVer 36 */
        p->fmt = p->ver >= 36 ? OP_FMT_UNI4K : OP_FMT_UNI512;
        p->blk_data_max = p->fmt == OP_FMT_UNI4K ? 65512 : 8176;
        nbt_ib = op_u64(p->header + 224); bbt_ib = op_u64(p->header + 240);
        p->crypt = p->header[0x201];
    } else return op_err(OPST_E_UNSUPPORTED, "unsupported PST version (wVer=%u)", p->ver);
    if (p->crypt != 0 && p->crypt != 1 && p->crypt != 2) return op_err(OPST_E_UNSUPPORTED, "encryption method 0x%02x is not supported (Windows Information Protection?)", p->crypt);
    rc = load_tree(p, nbt_ib, 0x81, 1, 0);
    if (rc) return rc;
    rc = load_tree(p, bbt_ib, 0x80, 0, 0);
    if (rc) return rc;
    op_qsort(p->nbt, p->nnbt, sizeof *p->nbt, cmp_nbt);
    op_qsort(p->bbt, p->nbbt, sizeof *p->bbt, cmp_bbt);
    store_info(p);
    return 0;
}

int op_reload(opst *p) { return load_all(p); }
const uint8_t *op_mpbb_table(void) { return op_mpbb_i; }

/* MS-PST 5.1 (permutation: decode with mpbbI, encode with mpbbR) and 5.2 (cyclic, a symmetric cipher keyed by the low DWORD of the block's BID) */
void op_crypt_block(int method, int encode, uint8_t *p, size_t n, uint32_t key) {
    if (method == 1) {
        const uint8_t *t = encode ? op_mpbb_R : op_mpbb_i;
        for (size_t i = 0; i < n; i++) p[i] = t[p[i]];
    } else if (method == 2) {
        uint16_t w = (uint16_t)(key ^ (key >> 16));
        for (size_t i = 0; i < n; i++, w++) {
            uint8_t b = p[i];
            b = (uint8_t)(b + (uint8_t)w);
            b = op_mpbb_R[b];
            b = (uint8_t)(b + (uint8_t)(w >> 8));
            b = op_mpbb_S[b];
            b = (uint8_t)(b - (uint8_t)(w >> 8));
            b = op_mpbb_i[b];
            b = (uint8_t)(b - (uint8_t)w);
            p[i] = b;
        }
    }
}
int op_journal_recover(const char *path, int *recovered);      /* op_wr.c */

int opst_open(const char *path, unsigned flags, opst **out) {
    if (!path || !out) return op_err(OPST_E_ARG, "null argument");
    *out = NULL;
    opst *p = (opst *)calloc(1, sizeof *p);
    if (!p) return op_err(OPST_E_NOMEM, "out of memory");
    int writable = (flags & OPST_OPEN_WRITE) != 0;
    int rc = 0;
    if (writable) {                                  /* a leftover journal means an interrupted write: roll it back first */
        int recovered;
        rc = op_journal_recover(path, &recovered);
        if (rc) { free(p); return rc; }
        p->recovered = recovered;
    }
    rc = op_file_open(path, writable, &p->f);
    if (rc) { free(p); return rc; }
    p->writable = writable;
    size_t plen = strlen(path);
    p->path = (char *)malloc(plen + 1);
    if (p->path) memcpy(p->path, path, plen + 1);
    if (!writable) {
        size_t jl = strlen(path) + 9;
        char *jp = (char *)malloc(jl);
        if (jp) { snprintf(jp, jl, "%s.journal", path); p->journal_pending = op_file_exists(jp); free(jp); }
    }
    rc = load_all(p);
    if (rc) { opst_close(p); return rc; }
    if (writable && p->fmt != OP_FMT_UNI512) {
        int ver = (int)p->ver;
        opst_close(p);
        return op_err(OPST_E_UNSUPPORTED, "%s PST files (wVer=%d) can be read but not edited; editing supports Unicode files with 512-byte pages (wVer 23)",
                      ver >= 36 ? "4K-page" : "ANSI", ver);
    }
    *out = p;
    return 0;
}

void opst_close(opst *p) {
    if (!p) return;
    op_txn_abort(p);
    op_file_close(p->f);
    free(p->path); free(p->nbt); free(p->bbt); free(p->display_name);
    free(p);
}

const char *opst_display_name(opst *p) { return p && p->display_name ? p->display_name : ""; }
uint32_t opst_root_folder(opst *p) { (void)p; return OP_NID_ROOT; }
uint32_t opst_ipm_root(opst *p) { return p ? p->ipm_root : 0; }
uint32_t opst_deleted_items(opst *p) { return p ? p->deleted : 0; }

/* ---- verification ------------------------------------------------------------------------------------------------ */
int opst_verify(opst *p, opst_verify_report *rep, char *text, size_t cap) {
    if (!p || !rep) return op_err(OPST_E_ARG, "null argument");
    memset(rep, 0, sizeof *rep);
    size_t tl = 0;
    if (text && cap) text[0] = 0;
#define NOTE(...) do { rep->problems++; if (text && tl + 1 < cap) { int w_ = snprintf(text + tl, cap - tl, __VA_ARGS__); if (w_ > 0) tl += (size_t)w_ < cap - tl ? (size_t)w_ : cap - tl - 1; } } while (0)
    if (p->fmt != OP_FMT_UNI512) {            /* ANSI / 4K: only the B-tree page CRCs, which the loader already counted */
        rep->blocks = p->nbbt;
        if (p->page_crc_errors) { rep->crc_errors += p->page_crc_errors; NOTE("%llu B-tree page CRC mismatches\n", (unsigned long long)p->page_crc_errors); }
        return 0;
    }
    const uint8_t *h = p->header;
    if (op_u32(h + 4) != op_crc(h + 8, 471)) NOTE("header partial CRC mismatch\n");
    if (op_u32(h + 0x20C) != op_crc(h + 8, 516)) NOTE("header full CRC mismatch\n");
    if (p->page_crc_errors) { rep->crc_errors += p->page_crc_errors; NOTE("%llu B-tree page CRC mismatches\n", (unsigned long long)p->page_crc_errors); }
    for (size_t i = 0; i < p->nbbt; i++) {
        const op_bbt_ent *e = &p->bbt[i];
        rep->blocks++;
        size_t tot = ((size_t)e->cb + 16 + 63) / 64 * 64;
        if (e->ib + tot > p->fsize) { NOTE("block 0x%llx extends past the end of the file\n", (unsigned long long)e->bid); continue; }
        uint8_t *buf = (uint8_t *)malloc(tot);
        if (!buf) return op_err(OPST_E_NOMEM, "out of memory");
        if (op_file_pread(p->f, buf, tot, e->ib) != 0) { free(buf); NOTE("block 0x%llx cannot be read\n", (unsigned long long)e->bid); continue; }
        unsigned tcb = op_u16(buf + tot - 16);
        uint32_t crc = op_u32(buf + tot - 12);
        if (tcb != e->cb) NOTE("block 0x%llx trailer size %u != %u\n", (unsigned long long)e->bid, tcb, (unsigned)e->cb);
        if (crc != op_crc(buf, e->cb)) { rep->crc_errors++; NOTE("block 0x%llx CRC mismatch\n", (unsigned long long)e->bid); }
        free(buf);
    }
#undef NOTE
    return 0;
}
