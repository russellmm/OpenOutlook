/* op_xcopy.c - copy messages from one PST file into a folder of another (port of pstxcopy.py).
 *
 * For every message (the source is only read): the message node with its recipient / attachment sub-nodes is cloned into the destination as
 * new, independent blocks; named property ids (0x8000+) are translated through both files' name-to-id maps (names missing in the
 * destination are added; properties whose name is unknown in the source are dropped); the contents-table row (named columns dropped)
 * gets ID cells, an ID-map record and a unique row version; folder counts, the highest message NID and the NID high-water marks are updated. */
#include "op_wr.h"

#define NOMEM op_err(OPST_E_NOMEM, "out of memory")

/* ---- property id translation ------------------------------------------------------------------------------------------------------ */
typedef struct { unsigned from, to; } pidmap_e;          /* to == 0: drop the property */
typedef struct { pidmap_e *e; size_t n, cap; } pidmap;

static int pidmap_find(const pidmap *m, unsigned from, unsigned *to) {
    for (size_t i = 0; i < m->n; i++) if (m->e[i].from == from) { *to = m->e[i].to; return 1; }
    return 0;
}
static int pidmap_put(pidmap *m, unsigned from, unsigned to) {
    if (m->n == m->cap) {
        size_t nc = m->cap ? m->cap * 2 : 64;
        pidmap_e *t = (pidmap_e *)realloc(m->e, nc * sizeof *t);
        if (!t) return OPST_E_NOMEM;
        m->e = t; m->cap = nc;
    }
    m->e[m->n].from = from; m->e[m->n].to = to; m->n++;
    return 0;
}

typedef struct { unsigned *p; size_t n, cap; } pidlist;
static int named_cb(void *ctx, const uint8_t *k, const uint8_t *v) {
    (void)v;
    pidlist *l = (pidlist *)ctx;
    unsigned pid = op_u16(k);
    if (pid < 0x8000) return 0;
    for (size_t i = 0; i < l->n; i++) if (l->p[i] == pid) return 0;
    if (l->n == l->cap) {
        size_t nc = l->cap ? l->cap * 2 : 32;
        unsigned *t = (unsigned *)realloc(l->p, nc * sizeof *t);
        if (!t) return OPST_E_NOMEM;
        l->p = t; l->cap = nc;
    }
    l->p[l->n++] = pid;
    return 0;
}
static int cmp_uns(const void *a, const void *b) { unsigned x = *(const unsigned *)a, y = *(const unsigned *)b; return x < y ? -1 : x > y; }

/* named property ids in a property context (ascending); empty for anything else */
static int named_pids(const hblocks *h, pidlist *out) {
    memset(out, 0, sizeof *out);
    if (!heap_is_heap(h) || heap_client(h) != 0xBC) return 0;
    int rc = heap_bth_walk(h, heap_root(h), NULL, NULL, named_cb, out);
    if (rc) { free(out->p); memset(out, 0, sizeof *out); return rc; }
    op_qsort(out->p, out->n, sizeof *out->p, cmp_uns);
    return 0;
}

typedef struct { unsigned pid; uint8_t rest[6]; size_t seq; } prec;
static int cmp_prec(const void *a, const void *b) {
    const prec *x = (const prec *)a, *y = (const prec *)b;
    if (x->pid != y->pid) return x->pid < y->pid ? -1 : 1;
    return x->seq < y->seq ? -1 : x->seq > y->seq;       /* stable, like Python's sort */
}

/* re-key the properties of a property context heap (single-level BTH) */
static int pc_remap(hblocks *h, const pidmap *tmap) {
    if (!heap_is_heap(h) || heap_client(h) != 0xBC) return 0;
    const uint8_t *hd; size_t hn;
    int rc = heap_get(h, heap_root(h), &hd, &hn);
    if (rc) return rc;
    if (hn < 8) return op_err(OPST_E_FORMAT, "bad BTH header");
    if (hd[3] != 0) return op_err(OPST_E_UNSUPPORTED, "a property context with an index level cannot be re-keyed (more than ~440 properties)");
    uint32_t leaf = op_u32(hd + 4);
    const uint8_t *d; size_t dn;
    rc = heap_get(h, leaf, &d, &dn);
    if (rc) return rc;
    size_t n = dn / 8;
    prec *recs = (prec *)malloc((n ? n : 1) * sizeof *recs);
    uint8_t *old = (uint8_t *)malloc(dn ? dn : 1);
    if (!recs || !old) { free(recs); free(old); return NOMEM; }
    memcpy(old, d, dn);
    size_t m = 0;
    for (size_t i = 0; i < n; i++) {
        unsigned pid = op_u16(old + i * 8);
        if (pid >= 0x8000) {
            unsigned to;
            if (!pidmap_find(tmap, pid, &to) || to == 0) continue;
            pid = to;
        }
        recs[m].pid = pid; memcpy(recs[m].rest, old + i * 8 + 2, 6); recs[m].seq = m; m++;
    }
    op_qsort(recs, m, sizeof *recs, cmp_prec);
    uint8_t *nw = (uint8_t *)malloc(m * 8 + 1);
    if (!nw) { free(recs); free(old); return NOMEM; }
    for (size_t i = 0; i < m; i++) { wr16(nw + i * 8, recs[i].pid); memcpy(nw + i * 8 + 2, recs[i].rest, 6); }
    free(recs);
    int same = m * 8 == dn && memcmp(nw, old, dn) == 0;
    free(old);
    if (!same) {
        int g = heap_grow_item(h, leaf, nw, m * 8);
        free(nw);
        if (g < 0) return g;
        if (g == 0) return op_err(OPST_E_UNSUPPORTED, "no room to re-key a property context");
        return 0;
    }
    free(nw);
    return 0;
}

/* ---- cloning from the source file --------------------------------------------------------------------------------------------------- */
typedef struct {
    ops    *dst;
    opw    *src;
    namemap *snpm, *dnpm;
    pidmap  tmap;
    uint32_t *hw; size_t nhw, caphw;
} xctx;

static int hw_add(xctx *x, uint32_t nid) {
    if (x->nhw == x->caphw) {
        size_t nc = x->caphw ? x->caphw * 2 : 32;
        uint32_t *t = (uint32_t *)realloc(x->hw, nc * sizeof *t);
        if (!t) return OPST_E_NOMEM;
        x->hw = t; x->caphw = nc;
    }
    x->hw[x->nhw++] = nid;
    return 0;
}

static int translate(xctx *x, const pidlist *pids) {
    for (size_t i = 0; i < pids->n; i++) {
        unsigned pid = pids->p[i], to;
        if (pidmap_find(&x->tmap, pid, &to)) continue;
        nm_name nm;
        int r = nm_name_of(x->snpm, pid, &nm);
        if (r < 0) return r;
        unsigned np = 0;
        if (r == 1) {
            int rc = nm_add(x->dnpm, &nm, &np);
            free(nm.name);
            if (rc) return rc;
        }
        int rc = pidmap_put(&x->tmap, pid, np);
        if (rc) return rc;
    }
    return 0;
}

/* a sub-node that is not a property context (recipient / attachment tables, ...) is copied as it is */
static int clone_plain(xctx *x, uint64_t bd, uint64_t bs, uint64_t *nbd, uint64_t *nbs) {
    hblocks blocks;
    int rc = opw_leaf_blocks(x->src, bd, &blocks);
    if (rc) return rc;
    rc = opw_put_blocks(x->dst->w, &blocks, nbd);
    hb_free(&blocks);
    if (rc) return rc;
    *nbs = 0;
    if (bs) {
        wsubs subs;
        rc = opw_subnodes(x->src, bs, &subs);
        if (rc) return rc;
        wsub *ns = (wsub *)calloc(subs.n ? subs.n : 1, sizeof *ns);
        if (!ns) { wsubs_free(&subs); return NOMEM; }
        for (size_t i = 0; i < subs.n && !rc; i++) {
            ns[i].nid = subs.e[i].nid;
            rc = clone_plain(x, subs.e[i].bd, subs.e[i].bs, &ns[i].bd, &ns[i].bs);
        }
        for (size_t i = 0; i < subs.n && !rc; i++) rc = hw_add(x, ns[i].nid);
        if (!rc) rc = opw_put_subnodes(x->dst->w, ns, subs.n, nbs);
        free(ns);
        wsubs_free(&subs);
    }
    return rc;
}

/* a property context node (message, attachment) with its sub-nodes */
static int clone_pc(xctx *x, uint64_t bd, uint64_t bs, uint64_t *nbd, uint64_t *nbs) {
    hblocks blocks;
    int rc = opw_leaf_blocks(x->src, bd, &blocks);
    if (rc) return rc;
    pidlist pids;
    rc = named_pids(&blocks, &pids);
    if (!rc) rc = translate(x, &pids);
    free(pids.p);
    if (!rc) rc = pc_remap(&blocks, &x->tmap);
    if (!rc) rc = opw_put_blocks(x->dst->w, &blocks, nbd);
    hb_free(&blocks);
    if (rc) return rc;
    *nbs = 0;
    if (bs) {
        wsubs subs;
        rc = opw_subnodes(x->src, bs, &subs);
        if (rc) return rc;
        wsub *ns = (wsub *)calloc(subs.n ? subs.n : 1, sizeof *ns);
        if (!ns) { wsubs_free(&subs); return NOMEM; }
        for (size_t i = 0; i < subs.n && !rc; i++) {
            ns[i].nid = subs.e[i].nid;
            if ((subs.e[i].nid & 0x1F) == 5) rc = clone_pc(x, subs.e[i].bd, subs.e[i].bs, &ns[i].bd, &ns[i].bs);       /* attachment */
            else rc = clone_plain(x, subs.e[i].bd, subs.e[i].bs, &ns[i].bd, &ns[i].bs);
        }
        for (size_t i = 0; i < subs.n && !rc; i++) rc = hw_add(x, ns[i].nid);
        if (!rc) rc = opw_put_subnodes(x->dst->w, ns, subs.n, nbs);
        free(ns);
        wsubs_free(&subs);
    }
    return rc;
}

int xc_copy(ops *dst, opst *srcp, const uint32_t *nids, size_t n, uint32_t dest, uint32_t *new_nids) {
    opw *w = dst->w;
    xctx x;
    memset(&x, 0, sizeof x);
    x.dst = dst;
    int rc = op_ro_begin(srcp, &x.src);
    if (rc) return rc;
    rc = nm_load(x.src, &x.snpm);
    if (!rc) rc = nm_load(w, &x.dnpm);
    if (!rc && !nm_present(x.dnpm)) rc = op_err(OPST_E_FORMAT, "the destination has no name-to-id map (node 0x61)");
    uint32_t dtc_nid = (dest & ~0x1Fu) | 0x0E;
    tctx dtc = {0};
    if (!rc) rc = ed_load_tc(w, dtc_nid, &dtc);
    bbuf blob = {0};
    int have_blob = 0;
    if (!rc) rc = fix_replica_blob(dst, &blob, &have_blob);
    idmap *im = NULL;
    if (!rc) im = ops_idmap(dst, &rc);
    uint64_t change = w->bid_next_b;
    int cnt = 0, unread = 0;
    /* source contents tables, loaded once each */
    struct { uint32_t nid; tctx tc; } *cache = NULL;
    size_t ncache = 0;
    for (size_t k = 0; k < n && !rc; k++) {
        uint32_t m = nids[k];
        nbt_e e;
        int r = opw_node(x.src, m, &e);
        if (r == OPST_E_NOTFOUND || (r == 0 && (m & 0x1F) != 4)) { rc = op_err(OPST_E_NOTFOUND, "message 0x%x not found in the source file", m); break; }
        if (r) { rc = r; break; }
        uint32_t tcn = (e.parent & ~0x1Fu) | 0x0E;
        size_t ci;
        for (ci = 0; ci < ncache; ci++) if (cache[ci].nid == tcn) break;
        if (ci == ncache) {
            void *t = realloc(cache, (ncache + 1) * sizeof *cache);
            if (!t) { rc = OPST_E_NOMEM; break; }
            cache = t;
            cache[ncache].nid = tcn;
            rc = ed_load_tc(x.src, tcn, &cache[ncache].tc);
            if (rc) break;
            ncache++;
        }
        tctx *stc = &cache[ci].tc;
        int row = tc_find(stc, m);
        if (row < 0) { rc = op_err(OPST_E_NOTFOUND, "row 0x%x not in table", m); break; }
        pcprops vals;
        rc = tc_row_pidvals(stc, (size_t)row, &vals);
        if (rc) break;
        pcprops named;                                    /* row cells of named properties: re-keyed after the clone */
        memset(&named, 0, sizeof named);
        for (size_t i = 0; i < vals.n;) {
            if (vals.p[i].pid >= 0x8000) {
                if (!rc) rc = pcprops_set(&named, vals.p[i].pid, vals.p[i].ptype, vals.p[i].v.p, vals.p[i].v.n);
                pcprops_del(&vals, vals.p[i].pid);
            } else i++;
        }
        uint32_t idx = op_u32(w->hdr + 44 + 4 * 4) + 1;
        wr32(w->hdr + 44 + 4 * 4, idx);
        uint32_t nn = (idx << 5) | 4;
        uint64_t nbd = 0, nbs = 0;
        rc = clone_pc(&x, e.bd, e.bs, &nbd, &nbs);
        if (!rc) rc = opw_node_put(w, nn, nbd, nbs, dest);
        for (size_t i = 0; i < named.n && !rc; i++) {      /* SCANPST compares these cells with the message: keep them under the destination's ids */
            unsigned to;
            if (pidmap_find(&x.tmap, named.p[i].pid, &to) && to) rc = pcprops_set(&vals, to, named.p[i].ptype, named.p[i].v.p, named.p[i].v.n);
        }
        pcprops_free(&named);
        uint8_t b4[4];
        wr32(b4, nn);
        if (!rc) rc = pcprops_set(&vals, 0x67F2, 3, b4, 4);
        wr32(b4, ops_next_row_ver(dst));
        if (!rc) rc = pcprops_set(&vals, 0x67F3, 3, b4, 4);
        pcprops_del(&vals, 0x0E30); pcprops_del(&vals, 0x0E33); pcprops_del(&vals, 0x0E34);
        if (!rc && idmap_present(im) && have_blob) {
            uint8_t g[16], b8[8];
            rc = op_random(g, 16);
            if (!rc) rc = pcprops_set(&vals, 0x0E30, 0x0102, g, 16);
            change += 4;
            wr64(b8, change);
            if (!rc) rc = pcprops_set(&vals, 0x0E33, 0x0014, b8, 8);
            if (!rc) rc = pcprops_set(&vals, 0x0E34, 0x0102, blob.p, blob.n);
            if (!rc) rc = idmap_add(im, g, nn);
        }
        if (!rc) rc = tc_add_by_pid(&dtc, nn, &vals);
        if (!rc) { cnt++; unread += ops_unread(&vals); if (new_nids) new_nids[cnt - 1] = nn; }
        pcprops_free(&vals);
    }
    for (size_t i = 0; i < ncache; i++) tc_free(&cache[i].tc);
    free(cache);
    bb_free(&blob);
    if (!rc) rc = ed_store_tc(w, dtc_nid, &dtc);
    tc_free(&dtc);
    if (!rc) rc = ops_counts(dst, dest, cnt, unread);
    if (!rc) rc = nm_save(dst, x.dnpm);
    if (!rc) {
        nbt_e ne;
        if (opw_node(w, 0x61, &ne) == 0 && ne.bs) {
            wsubs s;
            if (opw_subnodes(w, ne.bs, &s) == 0) {
                for (size_t i = 0; i < s.n && !rc; i++) rc = hw_add(&x, s.e[i].nid);
                wsubs_free(&s);
            }
        }
    }
    if (!rc) rc = ops_bump_hwm(dst, x.hw, x.nhw);
    if (!rc) rc = ops_note_max_message_nid(dst, NULL, 0, NULL, 0, NULL, 0);
    free(x.hw); free(x.tmap.e);
    nm_free(x.snpm); nm_free(x.dnpm);
    op_ro_end(x.src);
    return rc;
}

/* ---- public ------------------------------------------------------------------------------------------------------------------------- */
int opst_msgs_copy_to(opst *src, const uint32_t *nids, size_t n, opst *dst, uint32_t dest, uint32_t *new_nids) {
    if (!src || !dst || (!nids && n)) return op_err(OPST_E_ARG, "null argument");
    if (!dst->writable) return op_err(OPST_E_STATE, "the destination file was opened read-only (use OPST_OPEN_WRITE)");
    if (op_path_same(src->path, dst->path)) return op_err(OPST_E_ARG, "source and destination are the same file (use opst_msgs_copy)");
    ops o;
    int rc = ops_begin(dst, &o);
    if (rc) return rc;
    nbt_e e;
    if ((dest & 0x1F) != 2 || opw_node(o.w, dest, &e) != 0) { ops_abort(&o); return op_err(OPST_E_ARG, "destination 0x%x is not a folder", dest); }
    rc = xc_copy(&o, src, nids, n, dest, new_nids);
    if (rc) { ops_abort(&o); return rc; }
    rc = ops_commit(&o);
    if (rc) return rc;
    /* message index: put each new message next to its best match (fixer rule R3) */
    opst_fix_report rep;
    rc = opst_fix(dst, 1, &rep);
    return rc;
}
