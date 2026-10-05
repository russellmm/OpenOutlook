/* op_check.c - read-only scan-style checks (a stand-in for much of SCANPST); port of pstcheck.py.
 *   refs     block reference counts / orphan blocks            nids     header NID high-water marks
 *   tables   folder counts, parent links, contents rows vs messages
 *   idmap    ID-map records (node 0xC01) vs the ids in table rows
 *   xblocks  data-tree blocks: non-final blocks full, cbTotal consistent
 *   rowvers  PidTagLtpRowVer values unique store-wide and below dwUnique
 *   subnodes attachment sizes (PR_ATTACH_SIZE = sum of the sizes of the attachment's property values) and the header counters of the
 *            local NIDs used inside messages (attachment / LTP subnodes)            [found by repairing imported files with SCANPST]
 *   rowcells contents-table rows carry the row-only cells Outlook writes (0x0E17 message status, 0x3013 per-row key)
 *   amap     every block and tree page is marked allocated in the allocation maps, and the header's cbAMapFree equals the maps' free space
 *   folders  every folder has its hierarchy / contents / FAI tables and a row in its parent's hierarchy table that mirrors its properties
 *            [amap and folders were found by repairing a damaged archive with SCANPST] */
#include "op_wr.h"

#define NOMEM op_err(OPST_E_NOMEM, "out of memory")

typedef struct { bbuf *out; opst_check_report *rep; } chk;

static void note(chk *c, int *counter, const char *fmt, ...) OP_PRINTF(3, 4);
static void note(chk *c, int *counter, const char *fmt, ...) {
    char buf[400];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    if (counter) (*counter)++;
    c->rep->problems++;
    if (c->out && c->out->n < 60000) { bb_put(c->out, buf, strlen(buf)); bb_u8(c->out, '\n'); }
}
static void info(chk *c, const char *fmt, ...) OP_PRINTF(2, 3);
static void info(chk *c, const char *fmt, ...) {
    char buf[400];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    if (c->out) { bb_put(c->out, buf, strlen(buf)); bb_u8(c->out, '\n'); }
}

/* ---- open hash map bid -> counters --------------------------------------------------------------------------------------------------- */
typedef struct { uint64_t key; uint32_t refs, cref; uint8_t used, expanded; uint64_t ib; uint32_t cb; } hent;
typedef struct { hent *e; size_t cap, n; } hmap;
static hent *hm_get(hmap *m, uint64_t key, int create);
static int hm_grow(hmap *m) {
    size_t nc = m->cap ? m->cap * 2 : 4096;
    hent *t = (hent *)calloc(nc, sizeof *t);
    if (!t) return OPST_E_NOMEM;
    hent *old = m->e; size_t oc = m->cap;
    m->e = t; m->cap = nc; m->n = 0;
    for (size_t i = 0; i < oc; i++) if (old[i].used) { hent *d = hm_get(m, old[i].key, 1); *d = old[i]; }
    free(old);
    return 0;
}
static hent *hm_get(hmap *m, uint64_t key, int create) {
    if (create && (m->n + 1) * 2 > m->cap && hm_grow(m)) return NULL;
    if (!m->cap) return NULL;
    size_t mask = m->cap - 1, h = (size_t)((key * 0x9E3779B97F4A7C15ull) >> 24) & mask;
    for (;;) {
        hent *e = &m->e[h];
        if (!e->used) { if (!create) return NULL; e->used = 1; e->key = key; m->n++; return e; }
        if (e->key == key) return e;
        h = (h + 1) & mask;
    }
}

typedef struct { hmap *m; } bbtctx;
static int bbt_load_cb(void *ctx, const uint8_t *e) {
    hmap *m = ((bbtctx *)ctx)->m;
    hent *h = hm_get(m, op_u64(e) & ~(uint64_t)1, 1);
    if (!h) return OPST_E_NOMEM;
    h->ib = op_u64(e + 8); h->cb = op_u16(e + 16); h->cref = op_u16(e + 18);
    return 0;
}

typedef struct { uint64_t *v; size_t n, cap; } q64;
static int q_push(q64 *q, uint64_t x) {
    if (q->n == q->cap) {
        size_t nc = q->cap ? q->cap * 2 : 1024;
        uint64_t *t = (uint64_t *)realloc(q->v, nc * sizeof *t);
        if (!t) return OPST_E_NOMEM;
        q->v = t; q->cap = nc;
    }
    q->v[q->n++] = x;
    return 0;
}

typedef struct { q64 *q; hmap *m; } nbtctx;
static int nbt_ref_cb(void *ctx, const uint8_t *e) {
    nbtctx *c = (nbtctx *)ctx;
    for (int k = 0; k < 2; k++) {
        uint64_t b = op_u64(e + 8 + 8 * k);
        if (!b) continue;
        hent *h = hm_get(c->m, b & ~(uint64_t)1, 1);
        if (!h) return OPST_E_NOMEM;
        h->refs++;
        int rc = q_push(c->q, b);
        if (rc) return rc;
    }
    return 0;
}

static int check_refs(chk *c, opw *w, refsfix *fix) {
    hmap m = {0, 0, 0};
    bbtctx bc = {&m};
    int rc = bt_items(w, 0, bbt_load_cb, &bc);
    q64 q = {0, 0, 0};
    nbtctx nc = {&q, &m};
    if (!rc) rc = bt_items(w, 1, nbt_ref_cb, &nc);
    int probs = 0, orphans = 0, bad = 0, missing = 0;
    while (!rc && q.n) {
        uint64_t b = q.v[--q.n];
        hent *h = hm_get(&m, b & ~(uint64_t)1, 0);
        if (!h) { missing++; note(c, &probs, "  referenced block 0x%llx is missing from the BBT", (unsigned long long)b); continue; }
        if (h->expanded) continue;
        h->expanded = 1;
        if (!(b & 2)) continue;
        uint8_t *d = (uint8_t *)malloc(h->cb ? h->cb : 1);
        if (!d) { rc = OPST_E_NOMEM; break; }
        rc = opw_read(w, h->ib, h->cb, d);
        if (rc) { free(d); break; }
        unsigned bt = d[0], lv = d[1], cent = op_u16(d + 2);
        if (h->cb >= 8) {
            if (bt == 1 && 8 + (size_t)cent * 8 <= h->cb) {
                for (unsigned i = 0; i < cent && !rc; i++) { uint64_t k = op_u64(d + 8 + 8 * i); hent *ch = hm_get(&m, k & ~(uint64_t)1, 1); if (ch) ch->refs++; rc = q_push(&q, k); }
            } else if (bt == 2 && lv == 0 && 8 + (size_t)cent * 24 <= h->cb) {
                for (unsigned i = 0; i < cent && !rc; i++)
                    for (int k = 0; k < 2 && !rc; k++) { uint64_t x = op_u64(d + 8 + 24 * i + 8 + 8 * k); if (!x) continue; hent *ch = hm_get(&m, x & ~(uint64_t)1, 1); if (ch) ch->refs++; rc = q_push(&q, x); }
            } else if (bt == 2 && lv != 0 && 8 + (size_t)cent * 16 <= h->cb) {
                for (unsigned i = 0; i < cent && !rc; i++) { uint64_t x = op_u64(d + 8 + 16 * i + 8); hent *ch = hm_get(&m, x & ~(uint64_t)1, 1); if (ch) ch->refs++; rc = q_push(&q, x); }
            }
        }
        free(d);
    }
    size_t nb = 0;
    for (size_t i = 0; i < m.cap && !rc; i++) {
        hent *h = &m.e[i];
        if (!h->used || !h->ib) continue;                           /* entries created only by reference counting have no location */
        nb++;
        if (h->refs == 0) {
            orphans++;
            if (fix) {
                if (fix->norphan == fix->caporphan) {
                    size_t ncap = fix->caporphan ? fix->caporphan * 2 : 256;
                    uint64_t *t = (uint64_t *)realloc(fix->orphan, ncap * sizeof *t);
                    if (!t) { rc = OPST_E_NOMEM; break; }
                    fix->orphan = t; fix->caporphan = ncap;
                }
                fix->orphan[fix->norphan++] = h->key;
            }
            if (c->out || !fix) note(c, &probs, "  ??Couldn't find BBT entry in the RBT (%llX)", (unsigned long long)h->key); else probs++;
        } else if (h->cref != h->refs + 1) {
            bad++;
            if (fix) {
                if (fix->nfix == fix->capfix) {
                    size_t ncap = fix->capfix ? fix->capfix * 2 : 64;
                    uint64_t *tb = (uint64_t *)realloc(fix->fix_bid, ncap * sizeof *tb);
                    if (!tb) { rc = OPST_E_NOMEM; break; }
                    fix->fix_bid = tb;
                    unsigned *tcr = (unsigned *)realloc(fix->fix_cref, ncap * sizeof *tcr);
                    if (!tcr) { rc = OPST_E_NOMEM; break; }
                    fix->fix_cref = tcr; fix->capfix = ncap;
                }
                fix->fix_bid[fix->nfix] = h->key; fix->fix_cref[fix->nfix++] = h->refs + 1;
            }
            if (bad <= 50) note(c, &probs, "  refcount mismatch for block 0x%llx: BBT has %u, references imply %u", (unsigned long long)h->key, h->cref, h->refs + 1); else probs++;
        }
    }
    c->rep->refs_problems += probs;
    if (!rc) info(c, "refs: %zu blocks, %d orphans, %d refcount mismatches, %d missing blocks", nb, orphans, bad, missing);
    free(q.v); free(m.e);
    return rc;
}

/* ---- header NID high-water marks ------------------------------------------------------------------------------------------------------- */
typedef struct { uint32_t mx[32]; } nidmax;
static int nidmax_cb(void *ctx, const uint8_t *e) {
    nidmax *n = (nidmax *)ctx;
    uint32_t nid = (uint32_t)(op_u64(e) & 0xFFFFFFFFu);
    unsigned t = nid & 0x1F;
    uint32_t i = nid >> 5;
    if (i > n->mx[t]) n->mx[t] = i;
    return 0;
}
static int check_nids(chk *c, opw *w) {
    nidmax n;
    memset(&n, 0, sizeof n);
    int rc = bt_items(w, 1, nidmax_cb, &n);
    if (rc) return rc;
    int probs = 0;
    for (unsigned t = 0; t < 32; t++) {
        if (!n.mx[t]) continue;
        uint32_t v = op_u32(w->hdr + 44 + 4 * t);
        uint32_t hv = v;                                              /* raw index (types 6, 7, 0x10 hold NID-shaped values and are skipped) */
        if (t == 0x06 || t == 0x07 || t == 0x10) continue;          /* index derived from its folder; not counted */
        if (n.mx[t] > hv) note(c, &probs, "  type 0x%02x: highest index in NBT %u (0x%x), header says %u   <-- NBT has a higher index than the header allows", t, n.mx[t], n.mx[t], hv);
    }
    c->rep->nids_problems += probs;
    info(c, "nids: %d problems", probs);
    return 0;
}

/* ---- data trees ------------------------------------------------------------------------------------------------------------------------ */
typedef struct { opw *w; hmap *m; chk *c; int probs, n; int rc; } xbctx;
static int xb_cb(void *ctx, const uint8_t *e) {
    xbctx *x = (xbctx *)ctx;
    uint64_t bid = op_u64(e);
    if (!(bid & 2)) return 0;
    unsigned cb = op_u16(e + 16);
    uint64_t ib = op_u64(e + 8);
    if (cb < 8) return 0;
    uint8_t *r = (uint8_t *)malloc(cb);
    if (!r) return OPST_E_NOMEM;
    int rc = opw_read(x->w, ib, cb, r);
    if (rc) { free(r); return rc; }
    if (r[0] != 1) { free(r); return 0; }
    unsigned lvl = r[1], cent = op_u16(r + 2);
    uint32_t tot = op_u32(r + 4);
    if ((lvl != 1 && lvl != 2) || 8 + (size_t)cent * 8 > cb) { free(r); return 0; }
    x->n++;
    uint64_t sum = 0;
    int bad = 0, notfull = 0, absent = 0;
    for (unsigned i = 0; i < cent; i++) {
        hent *h = hm_get(x->m, op_u64(r + 8 + 8 * i) & ~(uint64_t)1, 0);
        if (!h || !h->ib) { absent = 1; continue; }
        if (lvl == 1) {
            sum += h->cb;
            if (i + 1 < cent && h->cb != 8176) notfull = 1;
        } else {
            uint8_t sub[8];
            if (opw_read(x->w, h->ib, 8, sub) == 0) sum += op_u32(sub + 4);
            (void)bad;
        }
    }
    char buf[200];
    if (absent) { snprintf(buf, sizeof buf, "  XBLOCK 0x%llx lists a block that is not in the BBT", (unsigned long long)bid); note(x->c, &x->probs, "%s", buf); }
    else {
        if (tot != sum) note(x->c, &x->probs, "  %sBLOCK 0x%llx: cbTotal %u != sum of children %llu", lvl == 1 ? "X" : "XX", (unsigned long long)bid, tot, (unsigned long long)sum);
        if (lvl == 1 && notfull) note(x->c, &x->probs, "  XBLOCK 0x%llx: a non-final block is not full", (unsigned long long)bid);
    }
    free(r);
    return 0;
}
static int check_xblocks(chk *c, opw *w) {
    hmap m = {0, 0, 0};
    bbtctx bc = {&m};
    int rc = bt_items(w, 0, bbt_load_cb, &bc);
    xbctx x = {w, &m, c, 0, 0, 0};
    if (!rc) rc = bt_items(w, 0, xb_cb, &x);
    c->rep->xblocks_problems += x.probs;
    if (!rc) info(c, "xblocks: %d data-tree blocks checked, %d problems", x.n, x.probs);
    free(m.e);
    return rc;
}

/* ---- folder tables vs messages (uses the reader's view of the committed file) -------------------------------------------------------- */
static int pc_has(opst *p, uint32_t nid, unsigned pid, int64_t *val) {
    opnode n; oppc pc;
    if (op_node_get(p, nid, &n) != 0 || op_pc_open(p, &n, &pc) != 0) return 0;
    oppc_prop *pr = op_pc_find(&pc, (uint16_t)pid);
    int has = 0;
    if (pr && op_pc_value(&pc, pr) == 0) { *val = op_value_i64(pr->ptype, pr->d, pr->n, 0); has = 1; }
    op_pc_close(&pc);
    return has;
}

typedef struct { opst *p; chk *c; int probs; size_t listed_n, listed_cap; uint32_t *listed; int rc; } tblctx;

static int cmp_u32c(const void *a, const void *b) { uint32_t x = *(const uint32_t *)a, y = *(const uint32_t *)b; return x < y ? -1 : x > y; }

static int walk_check_folder(tblctx *t, uint32_t nid, const char *name, int depth) {
    opst *p = t->p;
    chk *c = t->c;
    if (depth > 64) return 0;
    opst_msg_row *rows = NULL; size_t nr = 0;
    if (opst_messages(p, nid, &rows, &nr) != 0) { note(c, &t->probs, "  folder %s: cannot read contents table", name); rows = NULL; nr = 0; }
    int64_t v;
    size_t real_unread = 0;
    for (size_t i = 0; i < nr; i++) if (!(rows[i].flags & OPST_MSGFLAG_READ)) real_unread++;
    if (pc_has(p, nid, 0x3602, &v) && v != (int64_t)nr) note(c, &t->probs, "  folder %s: content count property %lld != %zu table rows", name, (long long)v, nr);
    if (pc_has(p, nid, 0x3603, &v) && v != (int64_t)real_unread) note(c, &t->probs, "  folder %s: unread property %lld != %zu unread rows", name, (long long)v, real_unread);
    opst_folder_info *kids = NULL; size_t nk = 0;
    if (opst_folder_children(p, nid, &kids, &nk) != 0) { kids = NULL; nk = 0; }
    int64_t flag;
    if (pc_has(p, nid, 0x360A, &flag) && (flag != 0) != (nk != 0)) note(c, &t->probs, "  folder %s: has-subfolders property %lld but %zu subfolders", name, (long long)flag, nk);
    for (size_t i = 0; i < nk; i++) {
        const op_nbt_ent *e = op_nbt_find(p, kids[i].nid);
        if (e && e->parent != nid) note(c, &t->probs, "  folder %s: nidParent of subfolder %s is 0x%x, expected 0x%x", name, kids[i].name, e->parent, nid);
    }
    for (size_t i = 0; i < nr; i++) {
        uint32_t mn = rows[i].nid;
        if (t->listed_n == t->listed_cap) {
            size_t nc2 = t->listed_cap ? t->listed_cap * 2 : 4096;
            uint32_t *tmp = (uint32_t *)realloc(t->listed, nc2 * sizeof *tmp);
            if (!tmp) { t->rc = OPST_E_NOMEM; break; }
            t->listed = tmp; t->listed_cap = nc2;
        }
        t->listed[t->listed_n++] = mn;
        const op_nbt_ent *e = op_nbt_find(p, mn);
        if (!e) { note(c, &t->probs, "  folder %s: row 0x%x has no message node", name, mn); continue; }
        if (e->parent != nid) note(c, &t->probs, "  folder %s: message 0x%x has nidParent 0x%x", name, mn, e->parent);
        opst_msg *m;
        if (opst_msg_open(p, mn, &m) != 0) { note(c, &t->probs, "  folder %s: message 0x%x unreadable", name, mn); continue; }
        const char *subj = opst_msg_str(m, OPST_PID_SUBJECT);
        int diffs = 0;
        if (strcmp(subj ? subj : "", rows[i].subject ? rows[i].subject : "") != 0) diffs++;
        int64_t mf = opst_msg_i64(m, OPST_PID_FLAGS, -1);
        if (mf >= 0 && rows[i].flags && (uint32_t)mf != rows[i].flags) diffs++;
        int64_t ms = opst_msg_i64(m, OPST_PID_SIZE, -1);
        if (ms >= 0 && rows[i].size && ms != rows[i].size) diffs++;
        int64_t mr = opst_msg_i64(m, OPST_PID_RECEIVED, 0);
        if (mr && rows[i].received && mr != rows[i].received) diffs++;
        if (diffs) note(c, &t->probs, "  folder %s: row 0x%x differs from its message (%d field(s): subject / flags / size / delivery time)", name, mn, diffs);
        opst_msg_close(m);
    }
    int rc = 0;
    for (size_t i = 0; i < nk && !rc && !t->rc; i++) rc = walk_check_folder(t, kids[i].nid, kids[i].name, depth + 1);
    opst_free_folders(kids);
    opst_free_messages(rows);
    return rc ? rc : t->rc;
}

static int check_tables(chk *c, opst *p, opw *w) {
    tblctx t;
    memset(&t, 0, sizeof t);
    t.p = p; t.c = c;
    int rc = walk_check_folder(&t, OP_NID_ROOT, "(root)", 0);
    size_t lone = 0, nmsg = 0;
    if (!rc) {
        op_qsort(t.listed, t.listed_n, sizeof *t.listed, cmp_u32c);
        for (size_t i = 0; i < p->nnbt; i++) {
            if ((p->nbt[i].nid & 0x1F) != 4) continue;
            nmsg++;
            if (!t.listed_n || !bsearch(&p->nbt[i].nid, t.listed, t.listed_n, sizeof *t.listed, cmp_u32c)) lone++;
        }
    }
    (void)w;
    c->rep->tables_problems += t.probs;
    if (!rc) info(c, "tables: %zu messages listed in contents tables, %zu message nodes not listed, %d problems", t.listed_n, lone, t.probs);
    free(t.listed);
    (void)nmsg;
    return rc;
}

/* ---- ID map and row versions (tables of every folder) ---------------------------------------------------------------------------------- */
typedef struct { uint32_t *v; size_t n, cap; } tabvec;
static int tab_cb(void *ctx, const uint8_t *e) {
    tabvec *t = (tabvec *)ctx;
    uint32_t nid = (uint32_t)(op_u64(e) & 0xFFFFFFFFu);
    unsigned ty = nid & 0x1F;
    if (ty != 0xD && ty != 0xE && ty != 0xF) return 0;
    if (t->n == t->cap) {
        size_t nc = t->cap ? t->cap * 2 : 64;
        uint32_t *x = (uint32_t *)realloc(t->v, nc * sizeof *x);
        if (!x) return OPST_E_NOMEM;
        t->v = x; t->cap = nc;
    }
    t->v[t->n++] = nid;
    return 0;
}

static int check_idmap(chk *c, ops *o, const tabvec *tabs) {
    int rcim = 0;
    idmap *im = ops_idmap(o, &rcim);
    if (rcim) return rcim;
    if (!idmap_present(im)) { info(c, "idmap: no node 0xC01 in this file"); return 0; }
    int probs = 0;
    size_t rows = 0, noid = 0;
    for (size_t ti = 0; ti < tabs->n; ti++) {
        tctx tc;
        if (ed_load_tc(o->w, tabs->v[ti], &tc) != 0) continue;
        int ci = tc_col(&tc, 0x0E30);
        for (size_t r = 0; r < tc.nrows && ci >= 0; r++) {
            rows++;
            if (!tc.rows[r].present[ci] || tc.rows[r].cell[ci].n != 16) { noid++; continue; }       /* original rows of some files never had IDs */
            uint32_t got = 0;
            int found = idmap_lookup(im, tc.rows[r].cell[ci].p, &got);
            if (!found || got != tc.rows[r].rowid) {
                char hex[40];
                for (int k = 0; k < 16; k++) snprintf(hex + 2 * k, 3, "%02x", tc.rows[r].cell[ci].p[k]);
                note(c, &probs, "  table 0x%x row 0x%x: ID %s maps to %u", tabs->v[ti], tc.rows[r].rowid, hex, found ? got : 0);
            }
        }
        tc_free(&tc);
    }
    size_t live = 0, stale = 0;
    uint8_t g[16];
    uint32_t nid;
    for (size_t i = 0; i < idmap_count(im); i++) {
        if (!idmap_get(im, i, g, &nid) || !nid) continue;
        live++;
        nbt_e e;
        if (opw_node(o->w, nid, &e) == OPST_E_NOTFOUND) { stale++; if (stale <= 10) note(c, &probs, "  record points to missing node 0x%x (should be 0)", nid); else probs++; }
    }
    c->rep->idmap_problems += probs;
    info(c, "idmap: %zu records (%zu live), %zu rows checked (%zu without an ID, informational), %d problems", idmap_count(im), live, rows, noid, probs);
    return 0;
}

static int check_rowvers(chk *c, ops *o, const tabvec *tabs) {
    int probs = 0;
    size_t tot = 0;
    uint32_t top = 0;
    int have = 0;
    typedef struct { uint32_t v; uint32_t tn, rid; } rv;
    rv *all = NULL;
    size_t nall = 0, capall = 0;
    for (size_t ti = 0; ti < tabs->n; ti++) {
        unsigned ty = tabs->v[ti] & 0x1F;
        if (ty != 0xD && ty != 0xE) continue;
        tctx tc;
        if (ed_load_tc(o->w, tabs->v[ti], &tc) != 0) continue;
        int ci = tc_col(&tc, 0x67F3);
        for (size_t r = 0; r < tc.nrows && ci >= 0; r++) {
            if (!tc.rows[r].present[ci] || tc.rows[r].cell[ci].n < 4) continue;
            if (nall == capall) {
                capall = capall ? capall * 2 : 1024;
                rv *t = (rv *)realloc(all, capall * sizeof *t);
                if (!t) { free(all); tc_free(&tc); return NOMEM; }
                all = t;
            }
            all[nall].v = op_u32(tc.rows[r].cell[ci].p); all[nall].tn = tabs->v[ti]; all[nall].rid = tc.rows[r].rowid;
            if (!have || all[nall].v > top) { top = all[nall].v; have = 1; }
            nall++; tot++;
        }
        tc_free(&tc);
    }
    /* duplicates: sort by value */
    uint32_t *vals = (uint32_t *)malloc((nall ? nall : 1) * sizeof *vals);
    if (!vals) { free(all); return NOMEM; }
    for (size_t i = 0; i < nall; i++) vals[i] = all[i].v;
    op_qsort(vals, nall, sizeof *vals, cmp_u32c);
    for (size_t i = 1; i < nall; i++) {
        if (vals[i] == vals[i - 1] && (i < 2 || vals[i - 2] != vals[i])) note(c, &probs, "  row version 0x%x is used by more than one row", vals[i]);
    }
    if (have && top >= o->w->unique) note(c, &probs, "  highest row version 0x%x >= dwUnique 0x%x", top, o->w->unique);
    c->rep->rowver_problems += probs;
    info(c, "rowvers: %zu rows with a version, %d problems", tot, probs);
    free(vals); free(all);
    return 0;
}

/* ---- messages: attachment sizes and local nid counters -------------------------------------------------------------------------------- */
static unsigned fixed_pt_size(unsigned t) {
    switch (t) {
    case 2: return 2;
    case 3: case 4: case 0xA: return 4;
    case 0xB: return 1;
    case 5: case 6: case 7: case 0x14: case 0x40: return 8;
    default: return 0;
    }
}

typedef struct { const hblocks *hp; const wsubs *subs; opw *w; uint64_t sum; int64_t stored; int64_t method; } pcsz;
static int pcsz_cb(void *ctx, const uint8_t *k, const uint8_t *v) {
    pcsz *c = (pcsz *)ctx;
    unsigned pid = op_u16(k), pt = op_u16(v);
    uint32_t hn = op_u32(v + 2);
    unsigned fs = fixed_pt_size(pt);
    if (fs && fs <= 4) {                                   /* value stored inline in the record: its own size counts */
        c->sum += fs;
        if (pid == 0x0E20) c->stored = (int32_t)hn;
        if (pid == 0x3705) c->method = (int32_t)hn;
        return 0;
    }
    if (!hn) return 0;
    if (hn & 0x1F) {                                       /* value in a subnode of the attachment */
        for (size_t i = 0; i < c->subs->n; i++) {
            if (c->subs->e[i].nid != hn) continue;
            hblocks hb;
            if (opw_leaf_blocks(c->w, c->subs->e[i].bd, &hb) == 0) { for (size_t b = 0; b < hb.n; b++) c->sum += hb.b[b].n; hb_free(&hb); }
            break;
        }
        return 0;
    }
    const uint8_t *d; size_t n;
    if (heap_get(c->hp, hn, &d, &n) == 0) c->sum += n;
    return 0;
}

typedef struct { opw *w; chk *c; uint32_t mx[32]; int probs; size_t atts, msgs, skipped; } subctx;
static int sub_msg_cb(void *ctx, const uint8_t *e) {
    subctx *x = (subctx *)ctx;
    uint32_t nid = (uint32_t)(op_u64(e) & 0xFFFFFFFFu);
    uint64_t bs = op_u64(e + 16);
    if ((nid & 0x1F) != 4 || !bs) return 0;
    wsubs subs;
    if (opw_subnodes(x->w, bs, &subs) != 0) { x->skipped++; return 0; }          /* multi-level subnode trees are not examined */
    x->msgs++;
    for (size_t i = 0; i < subs.n; i++) {
        unsigned t = subs.e[i].nid & 0x1F;
        uint32_t idx = subs.e[i].nid >> 5;
        if (idx > x->mx[t]) x->mx[t] = idx;
        if (t != 5) continue;
        hblocks hp;
        wsubs own = {0, 0};
        if (opw_leaf_blocks(x->w, subs.e[i].bd, &hp) != 0) continue;
        if (heap_is_heap(&hp) && heap_client(&hp) == 0xBC) {
            if (subs.e[i].bs && opw_subnodes(x->w, subs.e[i].bs, &own) != 0) {      /* a multi-level subnode tree: its sizes are not summed here */
                own.e = NULL; own.n = 0; x->skipped++; wsubs_free(&own); hb_free(&hp);
                continue;
            }
            pcsz z = {&hp, &own, x->w, 0, -1, -1};
            /* only by-value attachments (method 1): the size of an embedded message or OLE object includes storage that is not a property value */
            if (heap_bth_walk(&hp, heap_root(&hp), NULL, NULL, pcsz_cb, &z) == 0 && z.method == 1) {
                x->atts++;
                if (z.stored >= 0 && (uint64_t)z.stored != z.sum)
                    note(x->c, &x->probs, "  attachment 0x%x of message 0x%x: PR_ATTACH_SIZE %lld != %llu (the sum of its property value sizes)",
                         subs.e[i].nid, nid, (long long)z.stored, (unsigned long long)z.sum);
                else if (z.stored < 0)
                    note(x->c, &x->probs, "  attachment 0x%x of message 0x%x: PR_ATTACH_SIZE is missing", subs.e[i].nid, nid);
            }
            wsubs_free(&own);
        }
        hb_free(&hp);
    }
    wsubs_free(&subs);
    return 0;
}
static int check_subnodes(chk *c, opw *w) {
    subctx x;
    memset(&x, 0, sizeof x);
    x.w = w; x.c = c;
    int rc = bt_items(w, 1, sub_msg_cb, &x);
    if (rc) return rc;
    static const unsigned counted[2] = {5, 0x1F};          /* local attachment and LTP nids come from the header counters; the table nids (0x671, 0x692) are fixed */
    for (int k = 0; k < 2; k++) {
        unsigned t = counted[k];
        uint32_t hv = op_u32(w->hdr + 44 + 4 * t);
        if (x.mx[t] > hv) note(c, &x.probs, "  local nids of type 0x%02x: highest index in message subnodes %u (0x%x), header says %u   <-- below the highest NID in use", t, x.mx[t], x.mx[t], hv);
    }
    c->rep->subnode_problems += x.probs;
    info(c, "subnodes: %zu messages with subnodes, %zu attachments, %d problems", x.msgs, x.atts, x.probs);
    return 0;
}

/* ---- contents-table rows: the row-only cells ------------------------------------------------------------------------------------------- */
static int check_rowcells(chk *c, ops *o, const tabvec *tabs) {
    int probs = 0;
    size_t rows = 0;
    for (size_t ti = 0; ti < tabs->n; ti++) {
        if ((tabs->v[ti] & 0x1F) != 0xE) continue;
        tctx tc;
        if (ed_load_tc(o->w, tabs->v[ti], &tc) != 0) continue;
        int c17 = tc_col(&tc, 0x0E17), c30 = tc_col(&tc, 0x3013);
        size_t miss = 0, short_rows = 0;
        for (size_t r = 0; r < tc.nrows; r++) {
            rows++;
            if ((c17 >= 0 && !tc.rows[r].present[c17]) || (c30 >= 0 && !tc.rows[r].present[c30])) miss++;
            pcprops mp;
            if (pcprops_get_ex(o->w, tc.rows[r].rowid, &mp, 1) == 0) {
                if (row_fill_missing(&tc, r, &mp, 0) > 0) short_rows++;
                pcprops_free(&mp);
            }
        }
        if (short_rows) note(c, &probs, "  contents table 0x%x: %zu of %zu rows lack cells for columns of the table that the message has a value for (SCANPST: Contents Table row doesn't match sub-object)", tabs->v[ti], short_rows, tc.nrows);
        if (miss) note(c, &probs, "  contents table 0x%x: %zu of %zu rows lack the row cells 0x0E17 / 0x3013 that Outlook writes (SCANPST adds them)", tabs->v[ti], miss, tc.nrows);
        tc_free(&tc);
    }
    c->rep->rowcell_problems += probs;
    info(c, "rowcells: %zu message rows, %d problems", rows, probs);
    return 0;
}

static int check_amap(chk *c, opw *w) {
    uint64_t un, comp, hdr;
    int rc = opw_amap_state(w, &un, &comp, &hdr);
    if (rc) return rc;
    int probs = 0;
    if (un) note(c, &probs, "  %llu blocks / tree pages are referenced by the B-trees but not marked allocated in the AMaps   <-- SCANPST: Block is not allocated in AMAP", (unsigned long long)un);
    if (comp != hdr) note(c, &probs, "  Computed cbAMapFree of %llu, but header has %llu", (unsigned long long)comp, (unsigned long long)hdr);
    c->rep->amap_problems += probs;
    info(c, "amap: free %llu (header %llu), %llu blocks not allocated, %d problems", (unsigned long long)comp, (unsigned long long)hdr, (unsigned long long)un, probs);
    return 0;
}

static void folder_say(void *ctx, const char *line) {
    void **a = (void **)ctx;
    note((chk *)a[0], (int *)a[1], "%s", line);
}

static int check_folders(chk *c, ops *o) {
    int probs = 0, found = 0;
    void *a[2] = {c, &probs};
    int rc = fo_repair_all(o, 0, &found, folder_say, a);
    if (rc) return rc;
    c->rep->folder_problems += probs;
    info(c, "folders: %d problems", probs);
    return 0;
}

/* orphan blocks (nothing references them) and blocks whose BBT reference count differs from what the references imply (the fixer's R9) */
int refs_collect(opw *w, refsfix *out) {
    opst_check_report dummy;
    memset(&dummy, 0, sizeof dummy);
    chk c = {NULL, &dummy};
    memset(out, 0, sizeof *out);
    return check_refs(&c, w, out);
}
void refsfix_free(refsfix *r) { free(r->orphan); free(r->fix_bid); free(r->fix_cref); memset(r, 0, sizeof *r); }

int opst_check(opst *p, opst_check_report *rep, char *text, size_t text_cap) {
    if (!p || !rep) return op_err(OPST_E_ARG, "null argument");
    memset(rep, 0, sizeof *rep);
    if (p->fmt != OP_FMT_UNI512) {            /* the checks walk the Unicode 512-byte-page structures */
        if (text && text_cap) snprintf(text, text_cap, "the integrity check is not available for ANSI and 4K-page files\n");
        return 0;
    }
    bbuf out = {0};
    chk c = {&out, rep};
    ops o;
    int rc = ops_begin_ro(p, &o);
    if (rc) return rc;
    tabvec tabs = {0, 0, 0};
    rc = bt_items(o.w, 1, tab_cb, &tabs);
    if (!rc) rc = check_refs(&c, o.w, NULL);
    if (!rc) rc = check_nids(&c, o.w);
    if (!rc) rc = check_tables(&c, p, o.w);
    if (!rc) rc = check_idmap(&c, &o, &tabs);
    if (!rc) rc = check_xblocks(&c, o.w);
    if (!rc) rc = check_rowvers(&c, &o, &tabs);
    if (!rc) rc = check_subnodes(&c, o.w);
    if (!rc) rc = check_rowcells(&c, &o, &tabs);
    if (!rc) rc = check_amap(&c, o.w);
    if (!rc) rc = check_folders(&c, &o);
    free(tabs.v);
    ops_end_ro(&o);
    if (text && text_cap) {
        size_t n = out.n < text_cap - 1 ? out.n : text_cap - 1;
        if (n) memcpy(text, out.p, n);
        text[n] = 0;
    }
    bb_free(&out);
    return rc;
}
