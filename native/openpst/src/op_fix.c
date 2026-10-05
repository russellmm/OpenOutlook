/* op_fix.c - fixes, in place and without rebuilding the file, the "minor inconsistencies" SCANPST reports in PST files that Outlook has edited
 * (port of pstfix.py; every rule was found by repairing a COPY with SCANPST and diffing it against the original).
 *   R1 ids     message rows (contents tables) and folder rows (hierarchy tables, except the root table 0x12D) without the ID cells
 *              0x0E30/0x0E33/0x0E34 get them, plus an ID-map record (node 0xC01)
 *   R2 idmap   ID-map records whose node no longer exists get NID 0 (SCANPST's convention for a deleted object)
 *   R3 index   messages missing from Outlook's message index (node 0xE01) are appended to the bucket of the message they were copied from
 *              (found by comparing the properties of messages with the same conversation topic); the highest-NID field is raised
 *   R4 rowver  duplicate PidTagLtpRowVer values (0x67F3) get new store-wide unique values; dwUnique is kept above all of them
 *   R5 hwm     header NID high-water marks of types 5 and 31 must cover every node and sub-node
 * "no known issues" is not "SCANPST will find nothing". */
#include "op_wr.h"

#define NOMEM op_err(OPST_E_NOMEM, "out of memory")
#define ROOT_HIER 0x12Du

typedef struct { uint32_t *v; size_t n, cap; } u32vec;
static int vec_push(u32vec *s, uint32_t x) {
    if (s->n == s->cap) {
        size_t nc = s->cap ? s->cap * 2 : 256;
        uint32_t *t = (uint32_t *)realloc(s->v, nc * sizeof *t);
        if (!t) return OPST_E_NOMEM;
        s->v = t; s->cap = nc;
    }
    s->v[s->n++] = x;
    return 0;
}

static int tables_cb(void *ctx, const uint8_t *e) {
    uint32_t nid = (uint32_t)(op_u64(e) & 0xFFFFFFFFu);
    unsigned t = nid & 0x1F;
    if (t == 0xD || t == 0xE) return vec_push((u32vec *)ctx, nid);
    return 0;
}
static int msgs_cb(void *ctx, const uint8_t *e) {
    uint32_t nid = (uint32_t)(op_u64(e) & 0xFFFFFFFFu);
    if ((nid & 0x1F) == 4) return vec_push((u32vec *)ctx, nid);
    return 0;
}

int fix_replica_blob(ops *o, bbuf *out, int *found) {
    *found = 0;
    u32vec tabs = {0, 0, 0};
    int rc = bt_items(o->w, 1, tables_cb, &tabs);
    for (size_t i = 0; i < tabs.n && !rc && !*found; i++) {
        tctx tc;
        if (ed_load_tc(o->w, tabs.v[i], &tc) != 0) continue;          /* unreadable tables are skipped */
        int ci = tc_col(&tc, 0x0E34);
        for (size_t r = 0; r < tc.nrows && ci >= 0; r++) {
            if (tc.rows[r].present[ci] && tc.rows[r].cell[ci].n) {
                rc = bb_put(out, tc.rows[r].cell[ci].p, tc.rows[r].cell[ci].n) ? OPST_E_NOMEM : 0;
                *found = 1;
                break;
            }
        }
        tc_free(&tc);
    }
    free(tabs.v);
    return rc;
}

/* ---- message properties (read through the reader's caches: the committed state) ------------------------------------------------------ */
typedef struct { unsigned pid; bbuf raw; } rprop;
typedef struct { uint32_t nid; rprop *p; size_t n; int ok; } mprops;

static void mprops_free(mprops *m) { for (size_t i = 0; i < m->n; i++) bb_free(&m->p[i].raw); free(m->p); memset(m, 0, sizeof *m); }
static int cmp_rprop(const void *a, const void *b) { unsigned x = ((const rprop *)a)->pid, y = ((const rprop *)b)->pid; return x < y ? -1 : x > y; }

static int load_mprops(opst *p, uint32_t nid, mprops *m) {
    memset(m, 0, sizeof *m);
    m->nid = nid;
    opnode node; oppc pc;
    if (op_node_get(p, nid, &node) != 0 || op_pc_open(p, &node, &pc) != 0) return 0;      /* unreadable: no properties (as in Python) */
    m->p = (rprop *)calloc(pc.n ? pc.n : 1, sizeof *m->p);
    if (!m->p) { op_pc_close(&pc); return NOMEM; }
    for (size_t i = 0; i < pc.n; i++) {
        oppc_prop *pr = &pc.props[i];
        rprop *d = &m->p[m->n++];
        d->pid = pr->pid;
        if (pr->ptype == 0x0002 || pr->ptype == 0x0003 || pr->ptype == 0x0004 || pr->ptype == 0x000A || pr->ptype == 0x000B) {
            bb_put(&d->raw, pr->inl, 4);
        } else if (op_pc_value(&pc, pr) == 0) {
            if (pr->n) bb_put(&d->raw, pr->d, pr->n);
        }
    }
    op_pc_close(&pc);
    op_qsort(m->p, m->n, sizeof *m->p, cmp_rprop);
    m->ok = 1;
    return 0;
}
static const rprop *mfind(const mprops *m, unsigned pid) {
    size_t lo = 0, hi = m->n;
    while (lo < hi) { size_t mid = (lo + hi) / 2; if (m->p[mid].pid < pid) lo = mid + 1; else hi = mid; }
    return (lo < m->n && m->p[lo].pid == pid) ? &m->p[lo] : NULL;
}

/* ---- R3 helpers ------------------------------------------------------------------------------------------------------------------------ */
typedef struct { uint32_t nid; uint32_t bucket_key; } member;
typedef struct { member *m; size_t n, cap; const hblocks *hp; } members;

typedef struct { members *ms; uint32_t key; int err; } mctx;
static int bucket_cb2(void *ctx, const uint8_t *k, const uint8_t *v) {
    mctx *c = (mctx *)ctx;
    const uint8_t *lst; size_t ln;
    int rc = heap_get(c->ms->hp, op_u32(v), &lst, &ln);
    if (rc) return rc;
    for (size_t i = 0; i + 4 <= ln; i += 4) {
        if (c->ms->n == c->ms->cap) {
            size_t nc = c->ms->cap ? c->ms->cap * 2 : 1024;
            member *t = (member *)realloc(c->ms->m, nc * sizeof *t);
            if (!t) return OPST_E_NOMEM;
            c->ms->m = t; c->ms->cap = nc;
        }
        c->ms->m[c->ms->n].nid = op_u32(lst + i); c->ms->m[c->ms->n].bucket_key = op_u32(k); c->ms->n++;
    }
    return 0;
}
static int cmp_member(const void *a, const void *b) { uint32_t x = ((const member *)a)->nid, y = ((const member *)b)->nid; return x < y ? -1 : x > y; }
static int is_member(const members *ms, uint32_t nid) {
    size_t lo = 0, hi = ms->n;
    while (lo < hi) { size_t mid = (lo + hi) / 2; if (ms->m[mid].nid < nid) lo = mid + 1; else hi = mid; }
    return lo < ms->n && ms->m[lo].nid == nid;
}

typedef struct { bbuf topic; u32vec nids; } tgroup;

static int r3_index(ops *o, int apply, int32_t *count) {
    opw *w = o->w;
    *count = 0;
    nbt_e e;
    int rc = opw_node(w, 0xE01, &e);
    if (rc == OPST_E_NOTFOUND) return 0;
    if (rc) return rc;
    hblocks hp;
    rc = opw_leaf_blocks(w, e.bd, &hp);
    if (rc) return rc;
    if (!heap_is_heap(&hp) || heap_client(&hp) != 0xCC) { hb_free(&hp); return 0; }
    members ms = {0, 0, 0, &hp};
    mctx mc = {&ms, 0, 0};
    rc = heap_bth_walk(&hp, 0x40, NULL, NULL, bucket_cb2, &mc);
    if (rc) { free(ms.m); hb_free(&hp); return rc; }
    op_qsort(ms.m, ms.n, sizeof *ms.m, cmp_member);
    u32vec all = {0, 0, 0}, missing = {0, 0, 0};
    rc = bt_items(w, 1, msgs_cb, &all);
    for (size_t i = 0; i < all.n && !rc; i++) if (!is_member(&ms, all.v[i])) rc = vec_push(&missing, all.v[i]);
    free(all.v);
    if (rc || !missing.n) { free(missing.v); free(ms.m); hb_free(&hp); return rc; }
    *count = (int32_t)missing.n;
    if (!apply) { free(missing.v); free(ms.m); hb_free(&hp); return 0; }

    opst *p = o->p;
    /* the bucket key is a function of the conversation GUID (see msg_conv_key); only messages without a conversation index fall back to
       "next to the best matching message with the same topic" */
    keyednid *keyed = (keyednid *)malloc(missing.n * sizeof *keyed);
    u32vec nokey = {0, 0, 0};
    size_t nk = 0;
    if (!keyed) rc = OPST_E_NOMEM;
    for (size_t i = 0; i < missing.n && !rc; i++) {
        uint32_t key;
        if (msg_conv_key(w, missing.v[i], &key)) { keyed[nk].key = key; keyed[nk].nid = missing.v[i]; nk++; }
        else rc = vec_push(&nokey, missing.v[i]);
    }
    /* topics of all indexed messages, grouped (only needed for the fallback) */
    size_t nm = nokey.n ? ms.n : 0;
    bbuf *topics = (bbuf *)calloc(nm ? nm : 1, sizeof *topics);
    if (!topics) rc = OPST_E_NOMEM;
    for (size_t i = 0; i < nm && !rc; i++) {
        mprops mp;
        rc = load_mprops(p, ms.m[i].nid, &mp);
        if (rc) break;
        const rprop *t = mfind(&mp, 0x70);
        if (t) rc = bb_put(&topics[i], t->raw.p, t->raw.n) ? OPST_E_NOMEM : 0;
        mprops_free(&mp);
    }
    nidpair *pairs = (nidpair *)malloc((nokey.n ? nokey.n : 1) * sizeof *pairs);
    size_t np = 0;
    tgroup *groups = (tgroup *)calloc(nokey.n ? nokey.n : 1, sizeof *groups);
    size_t ng = 0;
    if (!pairs || !groups) rc = OPST_E_NOMEM;
    for (size_t mi = 0; mi < nokey.n && !rc; mi++) {
        mprops pn;
        rc = load_mprops(p, nokey.v[mi], &pn);
        if (rc) break;
        const rprop *tp = mfind(&pn, 0x70);
        const uint8_t *tbytes = tp ? tp->raw.p : NULL;
        size_t tlen = tp ? tp->raw.n : 0;
        int64_t best = -1;
        long score = -1;
        /* candidates: indexed messages with the same topic, ascending NID (ms.m is sorted by NID) */
        for (size_t ci = 0; ci < nm && !rc; ci++) {
            if (topics[ci].n != tlen || (tlen && memcmp(topics[ci].p, tbytes, tlen) != 0)) continue;
            mprops pc;
            rc = load_mprops(p, ms.m[ci].nid, &pc);
            if (rc) break;
            long sc = 0;
            for (size_t k = 0; k < pn.n; k++) {
                const rprop *q = mfind(&pc, pn.p[k].pid);
                if (q && q->raw.n == pn.p[k].raw.n && (q->raw.n == 0 || memcmp(q->raw.p, pn.p[k].raw.p, q->raw.n) == 0)) sc++;
            }
            if (sc > score) { best = ms.m[ci].nid; score = sc; }
            mprops_free(&pc);
        }
        if (!rc && best >= 0) { pairs[np].nid = (uint32_t)best; pairs[np].parent = nokey.v[mi]; np++; }
        else if (!rc) {
            size_t g;
            for (g = 0; g < ng; g++) if (groups[g].topic.n == tlen && (!tlen || memcmp(groups[g].topic.p, tbytes, tlen) == 0)) break;
            if (g == ng) { if (tlen) rc = bb_put(&groups[g].topic, tbytes, tlen) ? OPST_E_NOMEM : 0; ng++; }
            if (!rc) rc = vec_push(&groups[g].nids, nokey.v[mi]);
        }
        mprops_free(&pn);
    }
    for (size_t i = 0; i < nm; i++) bb_free(&topics[i]);
    free(topics);
    free(ms.m);
    hb_free(&hp);
    if (!rc && (np || ng || nk)) {
        idxgroup *ig = (idxgroup *)calloc(ng ? ng : 1, sizeof *ig);
        if (!ig) rc = OPST_E_NOMEM;
        for (size_t g = 0; g < ng && !rc; g++) { ig[g].topic = groups[g].topic.p; ig[g].topic_len = groups[g].topic.n; ig[g].nids = groups[g].nids.v; ig[g].n = groups[g].nids.n; }
        if (!rc) rc = ops_note_max_message_nid(o, pairs, np, ig, ng, keyed, nk);
        free(ig);
    }
    for (size_t g = 0; g < ng; g++) { bb_free(&groups[g].topic); free(groups[g].nids.v); }
    free(groups); free(pairs); free(missing.v); free(keyed); free(nokey.v);
    return rc;
}

/* ---- R5 -------------------------------------------------------------------------------------------------------------------------------- */
typedef struct { uint32_t mx[32]; opw *w; } hwmctx;
static void hwm_note(hwmctx *c, uint32_t n) {
    unsigned t = n & 0x1F;
    uint32_t i = n >> 5;
    if ((t == 5 || t == 0x1F) && i > c->mx[t]) c->mx[t] = i;
}
static void hwm_walk(hwmctx *c, uint64_t bs, int depth) {
    if (depth > 4) return;
    wsubs s;
    if (opw_subnodes(c->w, bs, &s) != 0) return;
    for (size_t i = 0; i < s.n; i++) {
        hwm_note(c, s.e[i].nid);
        if (s.e[i].bs) hwm_walk(c, s.e[i].bs, depth + 1);
    }
    wsubs_free(&s);
}
static int hwm_cb(void *ctx, const uint8_t *e) {
    hwmctx *c = (hwmctx *)ctx;
    hwm_note(c, (uint32_t)(op_u64(e) & 0xFFFFFFFFu));
    uint64_t bs = op_u64(e + 16);
    if (bs) hwm_walk(c, bs, 0);
    return 0;
}

/* ---- R4 -------------------------------------------------------------------------------------------------------------------------------- */
typedef struct { uint32_t *k; size_t cap, n; } u32hash;
static int uset_add(u32hash *s, uint32_t v, int *was_there);
static int uset_grow(u32hash *s) {
    size_t nc = s->cap ? s->cap * 2 : 1024;
    uint32_t *t = (uint32_t *)calloc(nc, sizeof *t * 2);          /* key + used flag */
    if (!t) return OPST_E_NOMEM;
    uint32_t *old = s->k;
    size_t oc = s->cap;
    s->k = t; s->cap = nc; s->n = 0;
    for (size_t i = 0; i < oc; i++) if (old[2 * i + 1]) { int w; uset_add(s, old[2 * i], &w); }
    free(old);
    return 0;
}
static int uset_add(u32hash *s, uint32_t v, int *was_there) {
    if ((s->n + 1) * 2 > s->cap) { int rc = uset_grow(s); if (rc) return rc; }
    size_t mask = s->cap - 1, h = (size_t)((v * 2654435761u) >> 4) & mask;
    while (s->k[2 * h + 1]) { if (s->k[2 * h] == v) { *was_there = 1; return 0; } h = (h + 1) & mask; }
    s->k[2 * h] = v; s->k[2 * h + 1] = 1; s->n++;
    *was_there = 0;
    return 0;
}

typedef struct { uint32_t nid; tctx tc; int ci; } rvtab;

int fix_run(ops *o, int apply, opst_fix_report *rep) {
    opw *w = o->w;
    memset(rep, 0, sizeof *rep);
    int rc = 0;
    u32vec tabs = {0, 0, 0};
    rc = bt_items(w, 1, tables_cb, &tabs);
    if (rc) return rc;
    bbuf blob = {0};
    int have_blob = 0;
    rc = fix_replica_blob(o, &blob, &have_blob);
    uint64_t change = w->bid_next_b;
    int rcim = 0;
    idmap *im = rc ? NULL : ops_idmap(o, &rcim);
    if (!rc) rc = rcim;

    /* ---- R1 ---- */
    for (size_t ti = 0; ti < tabs.n && !rc; ti++) {
        uint32_t tn = tabs.v[ti];
        if (tn == ROOT_HIER) continue;
        tctx tc;
        if (ed_load_tc(w, tn, &tc) != 0) continue;
        int c30 = tc_col(&tc, 0x0E30), c33 = tc_col(&tc, 0x0E33), c34 = tc_col(&tc, 0x0E34);
        if (c30 < 0 || !idmap_present(im) || !have_blob) { tc_free(&tc); continue; }
        int changed = 0;
        for (size_t r = 0; r < tc.nrows && !rc; r++) {
            if (tc.rows[r].present[c30] && tc.rows[r].cell[c30].n) continue;
            rep->rows_without_ids++;
            if (apply) {
                uint8_t g[16];
                rc = op_random(g, 16);
                if (!rc) rc = tc_set_cell(&tc, r, c30, g, 16);
                if (!rc && c33 >= 0) { uint8_t b8[8]; change += 4; wr64(b8, change); rc = tc_set_cell(&tc, r, c33, b8, 8); }
                if (!rc && c34 >= 0) rc = tc_set_cell(&tc, r, c34, blob.p, blob.n);
                if (!rc) rc = idmap_add(im, g, tc.rows[r].rowid);
                changed = 1;
            }
        }
        if (!rc && changed) rc = ed_store_tc(w, tn, &tc);
        tc_free(&tc);
    }

    /* ---- R2 ---- */
    if (!rc && im) {
        uint8_t g[16];
        uint32_t nid;
        for (size_t i = 0; i < idmap_count(im); i++) {
            if (!idmap_get(im, i, g, &nid) || !nid) continue;
            nbt_e e;
            if (opw_node(w, nid, &e) == OPST_E_NOTFOUND) {
                rep->dangling_idmap++;
                if (apply) idmap_zero_nid(im, nid);
            }
        }
    }

    /* ---- R3 ---- */
    if (!rc) rc = r3_index(o, apply, &rep->messages_not_indexed);

    /* ---- R4 ---- */
    if (!rc) {
        rvtab *rt = (rvtab *)calloc(tabs.n ? tabs.n : 1, sizeof *rt);
        size_t nrt = 0;
        if (!rt) rc = OPST_E_NOMEM;
        for (size_t ti = 0; ti < tabs.n && !rc; ti++) {
            tctx tc;
            if (ed_load_tc(w, tabs.v[ti], &tc) != 0) continue;
            int ci = tc_col(&tc, 0x67F3);
            if (ci < 0) { tc_free(&tc); continue; }
            rt[nrt].nid = tabs.v[ti]; rt[nrt].tc = tc; rt[nrt].ci = ci; nrt++;
        }
        u32hash seen = {0, 0, 0};
        uint32_t top = 0;
        int have_top = 0;
        int issues = 0;
        for (size_t ti = 0; ti < nrt && !rc; ti++) {
            const tctx *tc = &rt[ti].tc;
            for (size_t r = 0; r < tc->nrows && !rc; r++) {
                if (!tc->rows[r].present[rt[ti].ci] || tc->rows[r].cell[rt[ti].ci].n < 4) continue;
                uint32_t v = op_u32(tc->rows[r].cell[rt[ti].ci].p);
                int dup;
                rc = uset_add(&seen, v, &dup);
                if (!rc && dup) issues++;
                if (!have_top || v > top) { top = v; have_top = 1; }
            }
        }
        int dwu = have_top && top >= w->unique;
        if (dwu) issues++;
        rep->row_version_issues = issues;
        if (!rc && apply && issues) {
            if (top > w->unique) w->unique = top;                 /* max(w.unique, top) */
            free(seen.k); memset(&seen, 0, sizeof seen);
            for (size_t ti = 0; ti < nrt && !rc; ti++) {
                tctx *tc = &rt[ti].tc;
                int dirty = 0;
                for (size_t r = 0; r < tc->nrows && !rc; r++) {
                    if (!tc->rows[r].present[rt[ti].ci] || tc->rows[r].cell[rt[ti].ci].n < 4) continue;
                    uint32_t v = op_u32(tc->rows[r].cell[rt[ti].ci].p);
                    int dup;
                    rc = uset_add(&seen, v, &dup);
                    if (!rc && dup) {
                        uint8_t b4[4];
                        wr32(b4, ops_next_row_ver(o));
                        rc = tc_set_cell(tc, r, rt[ti].ci, b4, 4);
                        dirty = 1;
                    }
                }
                if (!rc && dirty) rc = ed_store_tc(w, rt[ti].nid, tc);
            }
        }
        free(seen.k);
        for (size_t ti = 0; ti < nrt; ti++) tc_free(&rt[ti].tc);
        free(rt);
    }

    /* ---- R5 ---- */
    if (!rc) {
        hwmctx hc;
        memset(&hc, 0, sizeof hc);
        hc.w = w;
        rc = bt_items(w, 1, hwm_cb, &hc);
        for (unsigned t = 0; t < 32 && !rc; t++) {
            if (!hc.mx[t]) continue;
            uint32_t cur = op_u32(w->hdr + 44 + 4 * t);
            if (hc.mx[t] > cur) {
                rep->nid_mark_issues++;
                if (apply) wr32(w->hdr + 44 + 4 * t, hc.mx[t]);
            }
        }
    }
    free(tabs.v);
    bb_free(&blob);
    if (!rc && apply && (rep->rows_without_ids || rep->dangling_idmap || rep->messages_not_indexed || rep->row_version_issues || rep->nid_mark_issues)) {
        rc = ops_note_max_message_nid(o, NULL, 0, NULL, 0, NULL, 0);
    }
    return rc;
}

int opst_fix(opst *p, int apply, opst_fix_report *rep) {
    opst_fix_report local;
    if (!p) return op_err(OPST_E_ARG, "null argument");
    if (!rep) rep = &local;
    if (p->fmt != OP_FMT_UNI512) return op_err(OPST_E_UNSUPPORTED, "the fixer supports Unicode files with 512-byte pages only");
    ops o;
    int rc;
    if (apply) {
        if (!p->writable) return op_err(OPST_E_STATE, "the file was opened read-only (use OPST_OPEN_WRITE)");
        rc = ops_begin(p, &o);
    } else rc = ops_begin_ro(p, &o);
    if (rc) return rc;
    rc = fix_run(&o, apply, rep);
    if (!apply) { ops_end_ro(&o); return rc; }
    if (rc) { ops_abort(&o); return rc; }
    return ops_commit(&o);
}
