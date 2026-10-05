/* op_folders.c - folder operations: create / rename / move / delete / purge, protection of special folders.
 * Port of pstfolders.py (and the protection rules of pstactions.py). */
#include "op_wr.h"

#define NOMEM op_err(OPST_E_NOMEM, "out of memory")
#define ROOT 0x122u
#define T_HIER 0x0Du
#define T_CONT 0x0Eu
#define T_FAI  0x0Fu

/* ---- text helpers ------------------------------------------------------------------------------------------------------------- */
/* UTF-8 -> UTF-16LE; returns the number of code points or -1 when the input is not valid UTF-8 */
static long utf8_to_utf16(const char *s, bbuf *out) {
    const unsigned char *p = (const unsigned char *)s;
    long cps = 0;
    while (*p) {
        uint32_t c = *p;
        int len = c < 0x80 ? 1 : (c >= 0xC2 && c <= 0xDF) ? 2 : (c >= 0xE0 && c <= 0xEF) ? 3 : (c >= 0xF0 && c <= 0xF4) ? 4 : 0;
        if (!len) return -1;
        if (len > 1) {
            c &= (len == 2 ? 0x1Fu : len == 3 ? 0x0Fu : 0x07u);
            for (int k = 1; k < len; k++) {
                if ((p[k] & 0xC0) != 0x80) return -1;
                c = (c << 6) | (p[k] & 0x3Fu);
            }
            if ((len == 3 && c < 0x800) || (len == 4 && (c < 0x10000 || c > 0x10FFFF)) || (c >= 0xD800 && c < 0xE000)) return -1;
        }
        p += len;
        cps++;
        if (c >= 0x10000) { c -= 0x10000; bb_u16(out, 0xD800 + (c >> 10)); bb_u16(out, 0xDC00 + (c & 0x3FF)); }
        else bb_u16(out, c);
    }
    return out->bad ? -1 : cps;
}

static int is_space_cp(uint32_t c) {
    return c == ' ' || (c >= 9 && c <= 13) || (c >= 0x1C && c <= 0x1F) || c == 0x85 || c == 0xA0 || c == 0x1680 || (c >= 0x2000 && c <= 0x200A) ||
           c == 0x2028 || c == 0x2029 || c == 0x202F || c == 0x205F || c == 0x3000;
}

static int check_name(const char *name) {
    if (!name || !*name) return op_err(OPST_E_ARG, "invalid folder name");
    const unsigned char *p = (const unsigned char *)name;
    /* leading / trailing white space, forbidden characters, length in characters */
    uint32_t first = 0, last = 0;
    long cps = 0;
    while (*p) {
        uint32_t c = *p;
        int len = c < 0x80 ? 1 : (c & 0xE0) == 0xC0 ? 2 : (c & 0xF0) == 0xE0 ? 3 : 4;
        if (len > 1) { c &= (len == 2 ? 0x1Fu : len == 3 ? 0x0Fu : 0x07u); for (int k = 1; k < len && p[k]; k++) c = (c << 6) | (p[k] & 0x3Fu); }
        if (!cps) first = c;
        last = c;
        if (c == '/' || c == '\\') return op_err(OPST_E_ARG, "invalid folder name '%s'", name);
        p += len;
        cps++;
    }
    if (is_space_cp(first) || is_space_cp(last) || cps > 255) return op_err(OPST_E_ARG, "invalid folder name '%s'", name);
    return 0;
}

/* ---- children / protection ------------------------------------------------------------------------------------------------------ */
typedef struct { uint32_t nid; char *name; } fchild;
typedef struct { fchild *c; size_t n; } fchildren;
static void fchildren_free(fchildren *f) { for (size_t i = 0; i < f->n; i++) free(f->c[i].name); free(f->c); memset(f, 0, sizeof *f); }

static int fo_children(ops *o, uint32_t nid, fchildren *out) {
    memset(out, 0, sizeof *out);
    uint32_t hn = (nid & ~0x1Fu) | T_HIER;
    nbt_e e;
    int rc = opw_node(o->w, hn, &e);
    if (rc == OPST_E_NOTFOUND) return 0;
    if (rc) return rc;
    tctx tc;
    rc = ed_load_tc(o->w, hn, &tc);
    if (rc) return rc;
    int ci = tc_col(&tc, 0x3001);
    out->c = (fchild *)calloc(tc.nrows ? tc.nrows : 1, sizeof *out->c);
    if (!out->c) { tc_free(&tc); return NOMEM; }
    for (size_t i = 0; i < tc.nrows; i++) {
        out->c[i].nid = tc.rows[i].rowid;
        if (ci >= 0 && tc.rows[i].present[ci]) out->c[i].name = op_utf16_to_utf8(tc.rows[i].cell[ci].p, tc.rows[i].cell[ci].n, NULL);
        else out->c[i].name = (char *)calloc(1, 1);
        if (!out->c[i].name) { out->n = i; fchildren_free(out); tc_free(&tc); return NOMEM; }
        out->n = i + 1;
    }
    tc_free(&tc);
    return 0;
}

static int parent_of(ops *o, uint32_t nid, uint32_t *par) {
    nbt_e e;
    int rc = opw_node(o->w, nid, &e);
    if (rc == OPST_E_NOTFOUND) return op_err(OPST_E_NOTFOUND, "folder 0x%x not found", nid);
    if (rc) return rc;
    *par = e.parent;
    return 0;
}

/* nid, parent, ..., root */
static int ancestors(ops *o, uint32_t nid, uint32_t *out, size_t *n, size_t cap) {
    size_t k = 0;
    while (nid != ROOT) {
        if (k >= cap || k > 256) return op_err(OPST_E_FORMAT, "folder hierarchy loop");
        out[k++] = nid;
        int rc = parent_of(o, nid, &nid);
        if (rc) return rc;
    }
    out[k++] = ROOT;
    *n = k;
    return 0;
}

typedef struct { uint32_t *v; size_t n, cap; } u32set;
static int set_add(u32set *s, uint32_t v) {
    for (size_t i = 0; i < s->n; i++) if (s->v[i] == v) return 0;
    if (s->n == s->cap) {
        size_t nc = s->cap ? s->cap * 2 : 32;
        uint32_t *t = (uint32_t *)realloc(s->v, nc * sizeof *t);
        if (!t) return OPST_E_NOMEM;
        s->v = t; s->cap = nc;
    }
    s->v[s->n++] = v;
    return 0;
}
static int set_has(const u32set *s, uint32_t v) { for (size_t i = 0; i < s->n; i++) if (s->v[i] == v) return 1; return 0; }

/* NIDs that may not be moved / deleted / renamed */
static int fo_protected(ops *o, u32set *prot) {
    memset(prot, 0, sizeof *prot);
    int rc = set_add(prot, ROOT);
    pcprops sp;
    memset(&sp, 0, sizeof sp);
    if (!rc) {
        int r2 = pcprops_get_ex(o->w, 0x21, &sp, 1);
        if (r2 && r2 != OPST_E_NOTFOUND && r2 != OPST_E_FORMAT) rc = r2;
    }
    for (unsigned pid = 0x35E0; pid < 0x35E8 && !rc; pid++) {
        pcprop *p = pcprops_find(&sp, pid);
        if (p && p->v.n >= 4) rc = set_add(prot, op_u32(p->v.p + p->v.n - 4));
    }
    pcprops_free(&sp);
    if (rc) return rc;
    size_t nbase = prot->n;
    /* default-folder entry ids on the root and on every protected folder that exists */
    u32set scan = {0, 0, 0};
    rc = set_add(&scan, ROOT);
    for (size_t i = 0; i < nbase && !rc; i++) {
        nbt_e e;
        if (prot->v[i] != ROOT && opw_node(o->w, prot->v[i], &e) == 0) rc = set_add(&scan, prot->v[i]);
    }
    for (size_t i = 0; i < scan.n && !rc; i++) {
        pcprops props;
        int r2 = pcprops_get(o->w, scan.v[i], &props);
        if (r2) continue;                                              /* not a property context / unreadable: ignored, as in Python */
        for (unsigned pid = 0x36D0; pid < 0x36D8 && !rc; pid++) {
            pcprop *p = pcprops_find(&props, pid);
            if (p && p->v.n >= 4) rc = set_add(prot, op_u32(p->v.p + p->v.n - 4));
        }
        pcprops_free(&props);
    }
    free(scan.v);
    if (rc) return rc;
    fchildren ch;
    rc = fo_children(o, ROOT, &ch);              /* everything directly under the root is structural */
    for (size_t i = 0; i < ch.n && !rc; i++) rc = set_add(prot, ch.c[i].nid);
    fchildren_free(&ch);
    /* discard(0) */
    for (size_t i = 0; i < prot->n; i++) if (prot->v[i] == 0) { prot->v[i] = prot->v[--prot->n]; break; }
    return rc;
}

static int check_user_folder(ops *o, uint32_t nid) {
    if ((nid & 0x1F) != 2) return op_err(OPST_E_ARG, "0x%x is not a normal folder", nid);
    u32set prot;
    int rc = fo_protected(o, &prot);
    if (rc) return rc;
    int is = set_has(&prot, nid);
    free(prot.v);
    if (is) return op_err(OPST_E_REFUSED, "folder 0x%x is a special folder and cannot be changed", nid);
    return 0;
}

static int sibling_clash(ops *o, uint32_t parent, const char *name, uint32_t ignore, int *clash) {
    fchildren ch;
    *clash = 0;
    int rc = fo_children(o, parent, &ch);
    if (rc) return rc;
    for (size_t i = 0; i < ch.n; i++) if (ch.c[i].nid != ignore && op_stricmp_utf8(ch.c[i].name, name) == 0) { *clash = 1; break; }
    fchildren_free(&ch);
    return 0;
}

static int name_of(ops *o, uint32_t nid, char **name) {
    uint32_t par = 0;
    int rc = parent_of(o, nid, &par);
    if (rc) return rc;
    fchildren ch;
    rc = fo_children(o, par, &ch);
    if (rc) return rc;
    *name = NULL;
    for (size_t i = 0; i < ch.n; i++) if (ch.c[i].nid == nid) { *name = ch.c[i].name; ch.c[i].name = NULL; break; }
    fchildren_free(&ch);
    if (!*name) return op_err(OPST_E_NOTFOUND, "folder 0x%x is not listed in its parent", nid);
    return 0;
}

/* ---- property context helpers -------------------------------------------------------------------------------------------------- */
static int pc_set(ops *o, uint32_t nid, unsigned pid, unsigned ptype, const uint8_t *raw, size_t n) {
    pcprops props;
    int rc = pcprops_get(o->w, nid, &props);
    if (rc) return rc;
    rc = pcprops_set(&props, pid, ptype, raw, n);
    if (!rc) rc = pc_store(o->w, nid, &props);
    pcprops_free(&props);
    return rc;
}

/* update a cell of folder_nid's row in its parent's hierarchy table */
static int hier_set_cell(ops *o, uint32_t folder, unsigned pid, const uint8_t *raw, size_t n) {
    uint32_t par = 0;
    int rc = parent_of(o, folder, &par);
    if (rc) return rc;
    uint32_t hn = (par & ~0x1Fu) | T_HIER;
    tctx tc;
    rc = ed_load_tc(o->w, hn, &tc);
    if (rc) return rc;
    int i = tc_find(&tc, folder), ci = tc_col(&tc, pid);
    if (i >= 0 && ci >= 0) {
        rc = tc_set_cell(&tc, (size_t)i, ci, raw, n);
        if (!rc) rc = ed_store_tc(o->w, hn, &tc);
    }
    tc_free(&tc);
    return rc;
}

static int set_has_sub(ops *o, uint32_t nid) {
    fchildren ch;
    int rc = fo_children(o, nid, &ch);
    if (rc) return rc;
    uint8_t has = ch.n ? 1 : 0;
    fchildren_free(&ch);
    pcprops props;
    rc = pcprops_get(o->w, nid, &props);
    if (rc) return rc;
    pcprop *p = pcprops_find(&props, 0x360A);
    int same = p && p->v.n >= 1 && p->v.p[0] == has;
    pcprops_free(&props);
    if (same) return 0;
    rc = pc_set(o, nid, 0x360A, 0x000B, &has, 1);
    nbt_e e;
    if (!rc && nid != ROOT && opw_node(o->w, nid, &e) == 0) rc = hier_set_cell(o, nid, 0x360A, &has, 1);
    return rc;
}

/* 0x0E34 cell of any existing folder row, searching the folder's own table, then its ancestors' */
static int replica_blob(ops *o, uint32_t folder, bbuf *out, int *found) {
    uint32_t anc[300];
    size_t na;
    *found = 0;
    int rc = ancestors(o, folder, anc, &na, 300);
    if (rc) return rc;
    for (size_t k = 0; k < na; k++) {
        uint32_t hn = (anc[k] & ~0x1Fu) | T_HIER;
        nbt_e e;
        if (opw_node(o->w, hn, &e) != 0) continue;
        tctx tc;
        rc = ed_load_tc(o->w, hn, &tc);
        if (rc) return rc;
        int ci = tc_col(&tc, 0x0E34);
        for (size_t r = 0; r < tc.nrows && ci >= 0; r++) {
            if (tc.rows[r].present[ci] && tc.rows[r].cell[ci].n) {
                rc = bb_put(out, tc.rows[r].cell[ci].p, tc.rows[r].cell[ci].n) ? OPST_E_NOMEM : 0;
                *found = 1;
                break;
            }
        }
        tc_free(&tc);
        if (*found || rc) return rc;
    }
    /* no row has one yet (a store whose ID map is still empty): the message store's own 0x0E34 */
    pcprops sp;
    if (pcprops_get_ex(o->w, 0x21, &sp, 1) == 0) {
        pcprop *p = pcprops_find(&sp, 0x0E34);
        if (p && p->v.n) { rc = bb_put(out, p->v.p, p->v.n) ? OPST_E_NOMEM : 0; *found = rc == 0; }
        pcprops_free(&sp);
    }
    return rc;
}

/* empty hierarchy / contents / FAI tables for folder nid, layouts copied from a sibling / the parent / Deleted Items; with only_missing the
   tables the folder already has are left alone */
static int fo_make_tables(ops *o, uint32_t nid, uint32_t parent, int only_missing) {
    opw *w = o->w;
    static const unsigned derived[3] = {T_HIER, T_CONT, T_FAI};
    fchildren sib;
    int rc = fo_children(o, parent, &sib);
    if (rc) return rc;
    uint32_t order[1024];
    size_t norder = 0;
    for (size_t i = 0; i < sib.n && norder < 1020; i++) if ((sib.c[i].nid & 0x1F) == 2 && sib.c[i].nid != nid) order[norder++] = sib.c[i].nid;
    fchildren_free(&sib);
    order[norder++] = parent;
    order[norder++] = o->deleted;
    for (int k = 0; k < 3 && !rc; k++) {
        unsigned t = derived[k];
        nbt_e have;
        if (only_missing && opw_node(w, (nid & ~0x1Fu) | t, &have) == 0) continue;
        uint32_t tmpl = 0;
        for (size_t i = 0; i < norder; i++) {
            nbt_e e;
            if (order[i] && opw_node(w, (order[i] & ~0x1Fu) | t, &e) == 0) { tmpl = (order[i] & ~0x1Fu) | t; break; }
        }
        if (!tmpl) { rc = op_err(OPST_E_FORMAT, "no template table of type 0x%x found", t); break; }
        nbt_e te = {0};
        rc = opw_node(w, tmpl, &te);
        tctx tc = {0};
        if (!rc) rc = ed_load_tc(w, tmpl, &tc);
        uint64_t tbd = 0;
        if (!rc) {
            if (tc.nrows == 0 && !te.bs) { rc = opw_add_ref(w, te.bd); tbd = te.bd; }
            else {
                tc_clear_rows(&tc);
                hblocks heap, rows;
                uint32_t rn;
                rc = tc_build(&tc, &heap, &rows, &rn);
                if (!rc) { rc = opw_put_blocks(w, &heap, &tbd); hb_free(&heap); hb_free(&rows); }
            }
            tc_free(&tc);
        }
        if (!rc) rc = opw_node_put(w, (nid & ~0x1Fu) | t, tbd, 0, 0);
        if (!rc) {                                                    /* the header's counter of this type must cover the folder's index */
            uint8_t *h = w->hdr + 44 + 4 * t;
            if (op_u32(h) < (nid >> 5)) wr32(h, nid >> 5);
        }
    }
    return rc;
}

/* a row cell that mirrors a folder property: a new folder gets the default, a repaired one exactly what the folder has (nothing if it has no such property) */
static int row_cell(pcprops *cells, const pcprops *mirror, unsigned pid, unsigned ptype, const uint8_t *dflt, size_t n) {
    if (!mirror) return pcprops_set(cells, pid, ptype, dflt, n);
    pcprop *p = pcprops_find((pcprops *)mirror, pid);
    return p ? pcprops_set(cells, pid, ptype, p->v.p, p->v.n) : 0;
}

/* row for folder nid in its parent's hierarchy table; nm / cl are UTF-16 name and container class; mirror = the folder's properties when
   repairing an existing folder (NULL when the folder is new) */
static int fo_add_row(ops *o, uint32_t nid, uint32_t parent, const bbuf *nm, const bbuf *cl, const pcprops *mirror) {
    opw *w = o->w;
    uint32_t hn = (parent & ~0x1Fu) | T_HIER;
    static const uint8_t z4[4] = {0, 0, 0, 0};
    tctx tc = {0};
    int rc = ed_load_tc(w, hn, &tc);
    if (rc) return rc;
    pcprops cells;
    memset(&cells, 0, sizeof cells);
    uint8_t b4[4], b0 = 0;
    rc = pcprops_set(&cells, 0x3001, 0x1F, nm->p, nm->n);
    if (!rc) rc = row_cell(&cells, mirror, 0x3602, 3, z4, 4);
    if (!rc) rc = row_cell(&cells, mirror, 0x3603, 3, z4, 4);
    if (!rc) rc = row_cell(&cells, mirror, 0x360A, 0x0B, &b0, 1);
    if (!rc) rc = row_cell(&cells, mirror, 0x6635, 3, z4, 4);
    if (!rc) rc = row_cell(&cells, mirror, 0x6636, 3, z4, 4);
    wr32(b4, nid);
    if (!rc) rc = pcprops_set(&cells, 0x67F2, 3, b4, 4);
    wr32(b4, ops_next_row_ver(o));
    if (!rc) rc = pcprops_set(&cells, 0x67F3, 3, b4, 4);
    if (!rc && cl && cl->n) rc = pcprops_set(&cells, 0x3613, 0x1F, cl->p, cl->n);
    idmap *im = NULL;
    if (!rc) im = ops_idmap(o, &rc);
    if (!rc && im && idmap_present(im) && tc_col(&tc, 0x0E30) >= 0) {
        uint8_t g[16], b8[8];
        rc = op_random(g, 16);
        if (!rc) rc = pcprops_set(&cells, 0x0E30, 0x0102, g, 16);
        wr64(b8, w->bid_next_b);
        if (!rc) rc = pcprops_set(&cells, 0x0E33, 0x0014, b8, 8);
        bbuf blob = {0};
        int have = 0;
        if (!rc) rc = replica_blob(o, parent, &blob, &have);
        if (!rc && have) rc = pcprops_set(&cells, 0x0E34, 0x0102, blob.p, blob.n);
        bb_free(&blob);
        if (!rc) rc = idmap_add(im, g, nid);
    }
    if (!rc) rc = tc_add_by_pid(&tc, nid, &cells);
    pcprops_free(&cells);
    if (!rc) rc = ed_store_tc(w, hn, &tc);
    tc_free(&tc);
    return rc;
}

/* ---- create ----------------------------------------------------------------------------------------------------------------------- */
int fo_create(ops *o, uint32_t parent, const char *name, const char *cls, uint32_t *out_nid) {
    opw *w = o->w;
    int rc = check_name(name);
    if (rc) return rc;
    nbt_e pe;
    if ((parent & 0x1F) != 2 || opw_node(w, parent, &pe) != 0) return op_err(OPST_E_ARG, "parent 0x%x is not a folder", parent);
    if (parent == ROOT) return op_err(OPST_E_REFUSED, "create folders below \"Top of ...\" rather than at the store root");
    int clash;
    rc = sibling_clash(o, parent, name, 0, &clash);
    if (rc) return rc;
    if (clash) return op_err(OPST_E_REFUSED, "a folder named '%s' already exists there", name);
    uint32_t idx = op_u32(w->hdr + 44 + 4 * 2) + 1;
    uint32_t nid = (idx << 5) | 2;
    static const unsigned used_types[7] = {2, T_HIER, T_CONT, T_FAI, 6, 7, 0x10};
    for (int k = 0; k < 7; k++) {
        nbt_e e;
        if (opw_node(w, (idx << 5) | used_types[k], &e) == 0) return op_err(OPST_E_FORMAT, "folder index 0x%x already in use", idx);
    }
    wr32(w->hdr + 44 + 4 * 2, idx);
    static const unsigned derived[3] = {T_HIER, T_CONT, T_FAI};
    for (int k = 0; k < 3; k++) {
        uint8_t *h = w->hdr + 44 + 4 * derived[k];
        if (op_u32(h) < idx) wr32(h, idx);
    }

    /* property context */
    pcprops props;
    memset(&props, 0, sizeof props);
    static const uint8_t z4[4] = {0, 0, 0, 0};
    bbuf nm = {0}, cl = {0};
    if (utf8_to_utf16(name, &nm) < 0) { bb_free(&nm); return op_err(OPST_E_ARG, "folder name is not valid UTF-8"); }
    rc = pcprops_set(&props, 0x3001, 0x001F, nm.p, nm.n);
    if (!rc) rc = pcprops_set(&props, 0x3602, 3, z4, 4);
    if (!rc) rc = pcprops_set(&props, 0x3603, 3, z4, 4);
    if (!rc) { uint8_t b0 = 0; rc = pcprops_set(&props, 0x360A, 0x000B, &b0, 1); }
    if (!rc) rc = pcprops_set(&props, 0x6635, 3, z4, 4);
    if (!rc) rc = pcprops_set(&props, 0x6636, 3, z4, 4);
    if (!rc && cls && *cls) {
        if (utf8_to_utf16(cls, &cl) < 0) rc = op_err(OPST_E_ARG, "container class is not valid UTF-8");
        else rc = pcprops_set(&props, 0x3613, 0x001F, cl.p, cl.n);
    }
    hblocks hb = {0, 0};
    uint64_t bd = 0;
    if (!rc) rc = pc_build(&props, &hb);
    if (!rc) { rc = opw_put_blocks(w, &hb, &bd); hb_free(&hb); }
    pcprops_free(&props);
    if (!rc) rc = opw_node_put(w, nid, bd, 0, parent);
    if (rc) { bb_free(&nm); bb_free(&cl); return rc; }

    const char *hook = getenv("OPST_TEST_INCOMPLETE_FOLDER");          /* test hook: leave the folder without tables and parent row, as early versions did */
    if (!(hook && *hook)) {
        rc = fo_make_tables(o, nid, parent, 0);
        if (!rc) rc = fo_add_row(o, nid, parent, &nm, &cl, NULL);
    }
    bb_free(&nm); bb_free(&cl);
    if (!rc) rc = set_has_sub(o, parent);
    if (!rc && out_nid) *out_nid = nid;
    return rc;
}

/* ---- repair of incomplete folders (R8) ------------------------------------------------------------------------------------------------- */
typedef struct { uint32_t nid, parent; } fnode;
typedef struct { fnode *v; size_t n, cap; } fnodes;
static int fnodes_cb(void *ctx, const uint8_t *e) {
    fnodes *f = (fnodes *)ctx;
    uint32_t nid = (uint32_t)(op_u64(e) & 0xFFFFFFFFu), par = op_u32(e + 24);
    if ((nid & 0x1F) != 2 || par == nid || (par & 0x1F) != 2) return 0;
    if (f->n == f->cap) {
        size_t nc = f->cap ? f->cap * 2 : 64;
        fnode *t = (fnode *)realloc(f->v, nc * sizeof *t);
        if (!t) return OPST_E_NOMEM;
        f->v = t; f->cap = nc;
    }
    f->v[f->n].nid = nid; f->v[f->n].parent = par; f->n++;
    return 0;
}

static const unsigned row_pids[7] = {0x3001, 0x3602, 0x3603, 0x360A, 0x3613, 0x6635, 0x6636};

/* Finds (apply = 0) or repairs (apply = 1):
   - folders without their hierarchy / contents / FAI tables or without a row in the parent's hierarchy table (SCANPST: "Adding folder back to the
     database"): tables are copied from a sibling, the row is built from the folder's own properties;
   - hierarchy rows that do not mirror their folder's properties (name, counts, class, has-subfolders = the truth about the children;
     SCANPST: "Hierarchy Table for X, row doesn't match sub-object").
   `say` (may be NULL) receives one line per finding. Returns the number of findings through *found. */
int fo_repair_all(ops *o, int apply, int *found, void (*say)(void *, const char *), void *ctx) {
    opw *w = o->w;
    char line[300];
    *found = 0;
    fnodes fn = {0, 0, 0};
    int rc = bt_items(w, 1, fnodes_cb, &fn);
    for (size_t i = 0; i < fn.n && !rc; i++) {
        uint32_t nid = fn.v[i].nid, par = fn.v[i].parent;
        nbt_e pe;
        if (opw_node(w, par, &pe) != 0) continue;
        unsigned missing = 0;
        for (unsigned t = 0xD; t <= 0xF; t++) { nbt_e e; if (opw_node(w, (nid & ~0x1Fu) | t, &e) != 0) missing |= 1u << (t - 0xD); }
        uint32_t hn = (par & ~0x1Fu) | T_HIER;
        tctx tc;
        int no_row = 0;
        if (ed_load_tc(w, hn, &tc) == 0) { no_row = tc_find(&tc, nid) < 0; tc_free(&tc); } else continue;
        if (!missing && !no_row) continue;
        (*found)++;
        if (say) {
            snprintf(line, sizeof line, "  folder 0x%x: %s%s%s", nid, missing ? "missing table nodes" : "", missing && no_row ? ", " : "", no_row ? "not listed in the hierarchy table of its parent" : "");
            say(ctx, line);
        }
        if (!apply) continue;
        pcprops props;
        rc = pcprops_get_ex(w, nid, &props, 1);
        if (rc) { rc = 0; continue; }                                   /* no readable property context: nothing to rebuild from */
        if (missing) rc = fo_make_tables(o, nid, par, 1);
        if (!rc && no_row) {
            pcprop *pn = pcprops_find(&props, 0x3001), *pc = pcprops_find(&props, 0x3613);
            bbuf nm = {0}, cl = {0};
            if (pn) bb_put(&nm, pn->v.p, pn->v.n);
            if (pc) bb_put(&cl, pc->v.p, pc->v.n);
            rc = fo_add_row(o, nid, par, &nm, &cl, &props);
            bb_free(&nm); bb_free(&cl);
        }
        pcprops_free(&props);
    }
    /* rows that do not mirror their folder (also after the repair above) */
    for (size_t i = 0; i < fn.n && !rc; i++) {
        uint32_t nid = fn.v[i].nid, par = fn.v[i].parent;
        nbt_e e;
        if (opw_node(w, par, &e) != 0 || opw_node(w, (par & ~0x1Fu) | T_HIER, &e) != 0) continue;
        pcprops props;
        if (pcprops_get_ex(w, nid, &props, 1) != 0) continue;
        uint32_t hn = (par & ~0x1Fu) | T_HIER;
        tctx tc;
        if (ed_load_tc(w, hn, &tc) != 0) { pcprops_free(&props); continue; }
        int ri = tc_find(&tc, nid);
        if (ri < 0) { tc_free(&tc); pcprops_free(&props); continue; }
        fchildren ch;
        int has = 0;
        if (opw_node(w, (nid & ~0x1Fu) | T_HIER, &e) == 0 && fo_children(o, nid, &ch) == 0) { has = ch.n ? 1 : 0; fchildren_free(&ch); }
        int dirty = 0, bad = 0;
        for (int k = 0; k < 7 && !rc; k++) {
            unsigned pid = row_pids[k];
            int ci = tc_col(&tc, pid);
            if (ci < 0) continue;
            pcprop *pp = pcprops_find(&props, pid);
            uint8_t one = (uint8_t)has;
            const uint8_t *want = pp ? (pid == 0x360A ? &one : pp->v.p) : NULL;
            size_t wn = pp ? (pid == 0x360A ? 1 : pp->v.n) : 0;
            int have = tc.rows[ri].present[ci] && tc.rows[ri].cell[ci].n;
            int same = want ? (have && tc.rows[ri].cell[ci].n == wn && memcmp(tc.rows[ri].cell[ci].p, want, wn) == 0) : !have;
            if (same) continue;
            bad = 1;
            if (!apply) break;
            if (want) rc = tc_set_cell(&tc, (size_t)ri, ci, want, wn);
            else { tc.rows[ri].cell[ci].n = 0; tc.rows[ri].present[ci] = 0; }
            dirty = 1;
        }
        if (bad) {
            (*found)++;
            if (say) { snprintf(line, sizeof line, "  folder 0x%x: its row in the hierarchy table of 0x%x does not match the folder's properties", nid, par); say(ctx, line); }
        }
        if (!rc && dirty) rc = ed_store_tc(w, hn, &tc);
        tc_free(&tc);
        /* the folder's own has-subfolders property follows the children too */
        {
            pcprop *hp = pcprops_find(&props, 0x360A);
            if (hp && hp->v.n >= 1 && hp->v.p[0] != (uint8_t)has) {
                (*found)++;
                if (say) { snprintf(line, sizeof line, "  folder 0x%x: has-subfolders property %d but %s", nid, hp->v.p[0], has ? "it has subfolders" : "no subfolders"); say(ctx, line); }
                if (apply) { uint8_t one = (uint8_t)has; rc = pc_set(o, nid, 0x360A, 0x000B, &one, 1); }
            }
        }
        pcprops_free(&props);
    }
    free(fn.v);
    return rc;
}

/* ---- rename / move ------------------------------------------------------------------------------------------------------------------ */
int fo_rename(ops *o, uint32_t nid, const char *name) {
    int rc = check_user_folder(o, nid);
    if (!rc) rc = check_name(name);
    if (rc) return rc;
    uint32_t par = 0;
    rc = parent_of(o, nid, &par);
    int clash = 0;
    if (!rc) rc = sibling_clash(o, par, name, nid, &clash);
    if (rc) return rc;
    if (clash) return op_err(OPST_E_REFUSED, "a folder named '%s' already exists there", name);
    bbuf nm = {0};
    if (utf8_to_utf16(name, &nm) < 0) { bb_free(&nm); return op_err(OPST_E_ARG, "folder name is not valid UTF-8"); }
    rc = pc_set(o, nid, 0x3001, 0x001F, nm.p, nm.n);
    if (!rc) rc = hier_set_cell(o, nid, 0x3001, nm.p, nm.n);
    bb_free(&nm);
    return rc;
}

int fo_move(ops *o, uint32_t nid, uint32_t dest, int *moved) {
    opw *w = o->w;
    if (moved) *moved = 0;
    int rc = check_user_folder(o, nid);
    if (rc) return rc;
    nbt_e de;
    if ((dest & 0x1F) != 2 || opw_node(w, dest, &de) != 0) return op_err(OPST_E_ARG, "destination 0x%x is not a folder", dest);
    if (dest == ROOT) return op_err(OPST_E_REFUSED, "cannot move a folder to the store root");
    uint32_t anc[300];
    size_t na;
    rc = ancestors(o, dest, anc, &na, 300);
    if (rc) return rc;
    for (size_t i = 0; i < na; i++) if (anc[i] == nid) return op_err(OPST_E_REFUSED, "cannot move a folder into itself or one of its subfolders");
    uint32_t old = 0;
    rc = parent_of(o, nid, &old);
    if (rc) return rc;
    if (old == dest) return 0;
    char *name = NULL;
    rc = name_of(o, nid, &name);
    int clash = 0;
    if (!rc) rc = sibling_clash(o, dest, name, 0, &clash);
    if (rc) { free(name); return rc; }
    if (clash) { op_err(OPST_E_REFUSED, "'%s' already exists in the destination folder", name); free(name); return OPST_E_REFUSED; }
    free(name);
    uint32_t hn_old = (old & ~0x1Fu) | T_HIER, hn_new = (dest & ~0x1Fu) | T_HIER;
    tctx otc, ntc;
    rc = ed_load_tc(w, hn_old, &otc);
    if (rc) return rc;
    int row = tc_find(&otc, nid);
    pcprops vals;
    memset(&vals, 0, sizeof vals);
    if (row < 0) rc = op_err(OPST_E_NOTFOUND, "row 0x%x not in table", nid);
    if (!rc) rc = tc_row_pidvals(&otc, (size_t)row, &vals);
    if (!rc) rc = tc_remove(&otc, nid);
    if (!rc) rc = ed_store_tc(w, hn_old, &otc);
    tc_free(&otc);
    if (rc) { pcprops_free(&vals); return rc; }
    rc = ed_load_tc(w, hn_new, &ntc);
    if (!rc) {
        uint8_t b4[4];
        wr32(b4, ops_next_row_ver(o));
        rc = pcprops_set(&vals, 0x67F3, 3, b4, 4);
        if (!rc) rc = tc_add_by_pid(&ntc, nid, &vals);
        if (!rc) rc = ed_store_tc(w, hn_new, &ntc);
        tc_free(&ntc);
    }
    pcprops_free(&vals);
    nbt_e e = {0};
    if (!rc) rc = opw_node(w, nid, &e);
    if (!rc) rc = opw_node_put(w, nid, e.bd, e.bs, dest);
    if (!rc) rc = set_has_sub(o, old);
    if (!rc) rc = set_has_sub(o, dest);
    if (!rc && moved) *moved = 1;
    return rc;
}

/* ---- purge / delete --------------------------------------------------------------------------------------------------------------- */
typedef struct { uint32_t nid, parent; } kidrec;
typedef struct { kidrec *k; size_t n, cap; } kidlist;

static int kids_cb(void *ctx, const uint8_t *e) {
    kidlist *l = (kidlist *)ctx;
    uint32_t n = (uint32_t)(op_u64(e) & 0xFFFFFFFFu), par = op_u32(e + 24);
    unsigned t = n & 0x1F;
    if ((t == 2 || t == 4 || t == 8) && par) {
        if (l->n == l->cap) {
            size_t nc = l->cap ? l->cap * 2 : 1024;
            kidrec *x = (kidrec *)realloc(l->k, nc * sizeof *x);
            if (!x) return OPST_E_NOMEM;
            l->k = x; l->cap = nc;
        }
        l->k[l->n].nid = n; l->k[l->n].parent = par; l->n++;
    }
    return 0;
}

static int purge_node(ops *o, uint32_t n) {
    nbt_e e;
    int rc = opw_node(o->w, n, &e);
    if (rc == OPST_E_NOTFOUND) return 0;
    if (rc) return rc;
    int rcim = 0;
    idmap *im = ops_idmap(o, &rcim);
    if (rcim) return rcim;
    idmap_zero_nid(im, n);
    rc = opw_node_del(o->w, n);
    if (!rc) rc = opw_release(o->w, e.bd);
    if (!rc && e.bs) rc = opw_release(o->w, e.bs);
    return rc;
}

static int cmp_u32(const void *a, const void *b) { uint32_t x = *(const uint32_t *)a, y = *(const uint32_t *)b; return x < y ? -1 : x > y; }

static int purge_tree(ops *o, const kidlist *kids, uint32_t nid, opst_purge_stats *st, int depth) {
    if (depth > 256) return op_err(OPST_E_FORMAT, "folder hierarchy loop");
    opw *w = o->w;
    fchildren ch;
    int rc = fo_children(o, nid, &ch);
    if (rc) return rc;
    u32set subs = {0, 0, 0}, msgs = {0, 0, 0};
    for (size_t i = 0; i < ch.n && !rc; i++) rc = set_add(&subs, ch.c[i].nid);
    fchildren_free(&ch);
    for (size_t i = 0; i < kids->n && !rc; i++) {
        if (kids->k[i].parent != nid) continue;
        unsigned t = kids->k[i].nid & 0x1F;
        if (t == 2) rc = set_add(&subs, kids->k[i].nid);
        else if (t == 4 || t == 8) rc = set_add(&msgs, kids->k[i].nid);
    }
    if (!rc && subs.n > 1) op_qsort(subs.v, subs.n, sizeof(uint32_t), cmp_u32);
    for (size_t i = 0; i < subs.n && !rc; i++) {
        nbt_e e;
        if (subs.v[i] != nid && opw_node(w, subs.v[i], &e) == 0) rc = purge_tree(o, kids, subs.v[i], st, depth + 1);
    }
    uint32_t base = nid & ~0x1Fu;
    static const unsigned ct[2] = {T_CONT, T_FAI};
    for (int k = 0; k < 2 && !rc; k++) {
        nbt_e e = {0};
        if (opw_node(w, base | ct[k], &e) != 0) continue;
        tctx tc = {0};
        rc = ed_load_tc(w, base | ct[k], &tc);
        for (size_t r = 0; r < tc.nrows && !rc; r++) if (opw_node(w, tc.rows[r].rowid, &e) == 0) rc = set_add(&msgs, tc.rows[r].rowid);
        tc_free(&tc);
    }
    if (!rc && msgs.n > 1) op_qsort(msgs.v, msgs.n, sizeof(uint32_t), cmp_u32);
    for (size_t i = 0; i < msgs.n && !rc; i++) { rc = purge_node(o, msgs.v[i]); st->messages++; }
    static const unsigned tt[3] = {T_HIER, T_CONT, T_FAI};
    for (int k = 0; k < 3 && !rc; k++) rc = purge_node(o, base | tt[k]);
    if (!rc) rc = purge_node(o, nid);
    if (!rc) st->folders++;
    free(subs.v); free(msgs.v);
    return rc;
}

int fo_purge(ops *o, uint32_t nid, opst_purge_stats *st) {
    opw *w = o->w;
    opst_purge_stats local = {0, 0};
    if (!st) st = &local;
    st->folders = st->messages = 0;
    int rc = check_user_folder(o, nid);
    if (rc) return rc;
    uint32_t parent = 0;
    rc = parent_of(o, nid, &parent);
    if (rc) return rc;
    kidlist kids = {0, 0, 0};
    rc = bt_items(w, 1, kids_cb, &kids);
    if (!rc) rc = purge_tree(o, &kids, nid, st, 0);
    free(kids.k);
    if (rc) return rc;
    uint32_t hn = (parent & ~0x1Fu) | T_HIER;
    tctx tc;
    rc = ed_load_tc(w, hn, &tc);
    if (rc) return rc;
    if (tc_find(&tc, nid) >= 0) {
        rc = tc_remove(&tc, nid);
        if (!rc) rc = ed_store_tc(w, hn, &tc);
    }
    tc_free(&tc);
    if (!rc) rc = set_has_sub(o, parent);
    return rc;
}

int fo_delete(ops *o, uint32_t nid, int *permanent, opst_purge_stats *st) {
    int rc = check_user_folder(o, nid);
    if (rc) return rc;
    if (!o->deleted) return op_err(OPST_E_FORMAT, "Deleted Items folder not found in the store");
    uint32_t anc[300];
    size_t na;
    rc = ancestors(o, nid, anc, &na, 300);
    if (rc) return rc;
    int in_del = 0;
    for (size_t i = 1; i < na; i++) if (anc[i] == o->deleted) in_del = 1;
    if (in_del) {
        if (permanent) *permanent = 1;
        return fo_purge(o, nid, st);
    }
    if (permanent) *permanent = 0;
    uint32_t par = 0;
    rc = parent_of(o, nid, &par);
    if (rc) return rc;
    if (par != o->deleted) {
        char *name = NULL;
        rc = name_of(o, nid, &name);
        int clash = 0;
        if (!rc) rc = sibling_clash(o, o->deleted, name, 0, &clash);
        if (!rc && clash) {                                    /* Outlook-style disambiguation */
            size_t nl = strlen(name) + 24;
            char *alt = (char *)malloc(nl);
            if (!alt) rc = OPST_E_NOMEM;
            for (int k = 2; !rc; k++) {
                snprintf(alt, nl, "%s (%d)", name, k);
                rc = sibling_clash(o, o->deleted, alt, 0, &clash);
                if (!rc && !clash) break;
            }
            if (!rc) rc = fo_rename(o, nid, alt);
            free(alt);
        }
        free(name);
        if (rc) return rc;
        rc = fo_move(o, nid, o->deleted, NULL);
    }
    return rc;
}

/* "protected" as the GUI sees it: ids recorded in the store, plus the English names of the standard folders directly below the
   top of the store */
int fo_is_protected(ops *o, uint32_t nid, int *prot_out) {
    u32set prot;
    int rc = fo_protected(o, &prot);
    if (rc) return rc;
    int is = set_has(&prot, nid);
    free(prot.v);
    if (!is && o->deleted) {
        uint32_t top = 0;
        if (parent_of(o, o->deleted, &top) == 0) {
            uint32_t par = 0;
            if (parent_of(o, nid, &par) == 0 && par == top) {
                char *name = NULL;
                if (name_of(o, nid, &name) == 0) {
                    static const char *const well_known[] = {"Inbox", "Outbox", "Sent Items", "Deleted Items", "Drafts", "Junk Email", "Junk E-mail", "Calendar",
                        "Contacts", "Tasks", "Notes", "Journal", "RSS Feeds", "Conversation History", "Sync Issues", "Search Root", "Quick Step Settings",
                        "Suggested Contacts", "Conversation Action Settings", NULL};
                    for (int i = 0; well_known[i]; i++) if (op_stricmp_utf8(name, well_known[i]) == 0) { is = 1; break; }
                    free(name);
                }
            }
        }
    }
    *prot_out = is;
    return 0;
}
