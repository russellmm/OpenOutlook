/* op_npm.c - the Name-to-ID map (node 0x61): named property id <-> (GUID, name) lookup, adding names, saving (port of pstnpm.py).
 *
 * Format (MS-PST 2.4.7): a property context with   0x0001 bucket count (normally 251)   0x0002 GUID stream (16-byte GUIDs)
 *   0x0003 entry stream (8-byte NAMEID records)   0x0004 string stream (length-prefixed UTF-16 names, 4-byte aligned)
 *   0x1000 + n   hash bucket n: NAMEID records; for string names dwPropertyID in a bucket record is the PST CRC-32 of the name
 * NAMEID = dwPropertyID (numeric id, or offset into the string stream when N = 1), wGuid (bit 0 = N, bits 1..15 = guid index:
 * 1 = PS_MAPI, 2 = PS_PUBLIC_STRINGS, >= 3 = index + 3 into the GUID stream), wPropIdx (property id = 0x8000 + wPropIdx);
 * bucket = (dwPropertyID ^ wGuid) % bucket count. */
#include "op_wr.h"

#define NOMEM op_err(OPST_E_NOMEM, "out of memory")

static const uint8_t PS_MAPI[16] = {0x29, 0x03, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0xc0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46};
static const uint8_t PS_PUBLIC_STRINGS[16] = {0x08, 0x29, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0xc0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46};

typedef struct { uint32_t dw; uint16_t wg, idx; } nm_ent;
typedef struct { unsigned pid; uint32_t nid; } nm_sub;

struct namemap {
    int       present;
    uint8_t (*guids)[16];  size_t nguids, capguids;
    nm_ent   *ent;         size_t nent, capent;
    bbuf      strings;
    unsigned  buckets;
    int       dirty;
    nm_sub   *subs;        size_t nsubs;           /* streams that were stored in subnodes: pid -> nid */
};

void nm_free(namemap *m) {
    if (!m) return;
    free(m->guids); free(m->ent); free(m->subs);
    bb_free(&m->strings);
    free(m);
}
int nm_present(namemap *m) { return m && m->present; }

/* a stream of the name map as bytes (heap item or subnode data) */
static int nm_blob(opw *w, const hblocks *hp, const wsubs *subs, uint32_t hn, bbuf *out, uint32_t *sub_nid) {
    *sub_nid = 0;
    if (hn == 0) return 0;
    if (hn & 0x1F) {
        *sub_nid = hn;
        const wsub *s = NULL;
        for (size_t i = 0; i < subs->n; i++) if (subs->e[i].nid == hn) { s = &subs->e[i]; break; }
        if (!s) return op_err(OPST_E_FORMAT, "subnode 0x%x of the name map is missing", hn);
        hblocks hb;
        int rc = opw_leaf_blocks(w, s->bd, &hb);
        for (size_t i = 0; i < hb.n && !rc; i++) rc = bb_put(out, hb.b[i].p, hb.b[i].n) ? OPST_E_NOMEM : 0;
        if (!rc || hb.b) hb_free(&hb);
        return rc;
    }
    const uint8_t *d; size_t n;
    int rc = heap_get(hp, hn, &d, &n);
    if (rc) return rc;
    return bb_put(out, d, n) ? OPST_E_NOMEM : 0;
}

typedef struct { const hblocks *hp; struct { unsigned pid, pt; uint32_t hn; } *p; size_t n, cap; } npprops;
static int np_cb(void *ctx, const uint8_t *k, const uint8_t *v) {
    npprops *c = (npprops *)ctx;
    if (c->n == c->cap) {
        size_t nc = c->cap ? c->cap * 2 : 64;
        void *t = realloc(c->p, nc * sizeof *c->p);
        if (!t) return OPST_E_NOMEM;
        c->p = t; c->cap = nc;
    }
    c->p[c->n].pid = op_u16(k); c->p[c->n].pt = op_u16(v); c->p[c->n].hn = op_u32(v + 2);
    c->n++;
    return 0;
}

int nm_load(opw *w, namemap **out) {
    *out = NULL;
    namemap *m = (namemap *)calloc(1, sizeof *m);
    if (!m) return NOMEM;
    m->buckets = 251;
    nbt_e e;
    int rc = opw_node(w, 0x61, &e);
    if (rc == OPST_E_NOTFOUND) { *out = m; return 0; }
    if (rc) { free(m); return rc; }
    m->present = 1;
    hblocks hp;
    rc = opw_leaf_blocks(w, e.bd, &hp);
    if (rc) { nm_free(m); return rc; }
    wsubs subs;
    rc = heap_is_heap(&hp) && heap_client(&hp) == 0xBC ? 0 : op_err(OPST_E_FORMAT, "node 0x61 is not a property context");
    if (!rc) rc = opw_subnodes(w, e.bs, &subs);
    if (rc) { hb_free(&hp); nm_free(m); return rc; }
    npprops pr = {&hp, NULL, 0, 0};
    rc = heap_bth_walk(&hp, heap_root(&hp), NULL, NULL, np_cb, &pr);
    bbuf g = {0}, en = {0};
    for (size_t i = 0; i < pr.n && !rc; i++) {
        unsigned pid = pr.p[i].pid;
        if (pid == 1) { if (pr.p[i].hn) m->buckets = pr.p[i].hn; continue; }
        if (pid != 2 && pid != 3 && pid != 4) continue;
        bbuf *dst = pid == 2 ? &g : pid == 3 ? &en : &m->strings;
        uint32_t sub = 0;
        rc = nm_blob(w, &hp, &subs, pr.p[i].hn, dst, &sub);
        if (!rc && sub) {
            nm_sub *t = (nm_sub *)realloc(m->subs, (m->nsubs + 1) * sizeof *t);
            if (!t) rc = OPST_E_NOMEM;
            else { m->subs = t; m->subs[m->nsubs].pid = pid; m->subs[m->nsubs].nid = sub; m->nsubs++; }
        }
    }
    free(pr.p);
    if (!rc) {
        m->nguids = m->capguids = g.n / 16;
        m->guids = (uint8_t (*)[16])malloc((m->nguids ? m->nguids : 1) * 16);
        m->nent = m->capent = en.n / 8;
        m->ent = (nm_ent *)malloc((m->nent ? m->nent : 1) * sizeof *m->ent);
        if (!m->guids || !m->ent) rc = OPST_E_NOMEM;
    }
    if (!rc) {
        if (m->nguids) memcpy(m->guids, g.p, m->nguids * 16);
        for (size_t i = 0; i < m->nent; i++) { m->ent[i].dw = op_u32(en.p + 8 * i); m->ent[i].wg = (uint16_t)op_u16(en.p + 8 * i + 4); m->ent[i].idx = (uint16_t)op_u16(en.p + 8 * i + 6); }
    }
    bb_free(&g); bb_free(&en);
    wsubs_free(&subs);
    hb_free(&hp);
    if (rc) { nm_free(m); return rc; }
    *out = m;
    return 0;
}

static const uint8_t *guid_of(const namemap *m, unsigned wg) {
    unsigned gi = wg >> 1;
    if (gi == 1) return PS_MAPI;
    if (gi == 2) return PS_PUBLIC_STRINGS;
    if (gi >= 3 && gi - 3 < m->nguids) return m->guids[gi - 3];
    return NULL;
}

/* UTF-8 name stored at `off` of the string stream (malloc'd, "" when out of range) */
static char *string_at(const namemap *m, uint32_t off) {
    if ((size_t)off + 4 > m->strings.n) return (char *)calloc(1, 1);
    uint32_t n = op_u32(m->strings.p + off);
    if ((size_t)off + 4 + n > m->strings.n) n = (uint32_t)(m->strings.n - off - 4);
    return op_utf16_to_utf8(m->strings.p + off + 4, n, NULL);
}

size_t nm_count(const namemap *m) { return m ? m->nent : 0; }

int nm_name_of(const namemap *m, unsigned propid, nm_name *out) {
    for (size_t i = 0; i < m->nent; i++) {
        if (0x8000u + m->ent[i].idx != propid) continue;
        memset(out, 0, sizeof *out);
        const uint8_t *g = guid_of(m, m->ent[i].wg);
        if (g) { memcpy(out->guid, g, 16); out->has_guid = 1; }
        out->is_string = m->ent[i].wg & 1;
        if (out->is_string) { out->name = string_at(m, m->ent[i].dw); if (!out->name) return NOMEM; }
        else out->num = m->ent[i].dw;
        return 1;
    }
    return 0;
}

/* property id of a (GUID, name) pair; 0 when absent. Several entries with the same name: the last one wins (like the Python dict). */
static unsigned lookup(const namemap *m, const nm_name *n) {
    for (size_t i = m->nent; i > 0; i--) {
        const nm_ent *e = &m->ent[i - 1];
        int es = e->wg & 1;
        if (es != (n->is_string != 0)) continue;
        const uint8_t *g = guid_of(m, e->wg);
        if ((g != NULL) != (n->has_guid != 0) || (g && memcmp(g, n->guid, 16) != 0)) continue;
        if (es) {
            char *s = string_at(m, e->dw);
            int same = s && op_stricmp_utf8(s, n->name) == 0;
            free(s);
            if (!same) continue;
        } else if (e->dw != n->num) continue;
        return 0x8000u + e->idx;
    }
    return 0;
}

static unsigned guid_index(namemap *m, const uint8_t *g, int *rc) {
    *rc = 0;
    if (memcmp(g, PS_MAPI, 16) == 0) return 1;
    if (memcmp(g, PS_PUBLIC_STRINGS, 16) == 0) return 2;
    for (size_t i = 0; i < m->nguids; i++) if (memcmp(m->guids[i], g, 16) == 0) return (unsigned)i + 3;
    if (m->nguids == m->capguids) {
        size_t nc = m->capguids ? m->capguids * 2 : 16;
        uint8_t (*t)[16] = (uint8_t (*)[16])realloc(m->guids, nc * 16);
        if (!t) { *rc = OPST_E_NOMEM; return 0; }
        m->guids = t; m->capguids = nc;
    }
    memcpy(m->guids[m->nguids++], g, 16);
    return (unsigned)m->nguids - 1 + 3;
}

int nm_add(namemap *m, const nm_name *n, unsigned *propid) {
    unsigned p = lookup(m, n);
    if (p) { *propid = p; return 0; }
    if (m->nent >= 0x7FFF) return op_err(OPST_E_UNSUPPORTED, "the named property table is full");
    if (!n->has_guid) return op_err(OPST_E_ARG, "a named property needs a GUID");
    int rc;
    unsigned gi = guid_index(m, n->guid, &rc);
    if (rc) return rc;
    unsigned wg = (gi << 1) | (n->is_string ? 1u : 0u);
    uint32_t dw;
    if (n->is_string) {
        bbuf raw = {0};
        long cps = 0;
        const unsigned char *s = (const unsigned char *)n->name;
        (void)cps;
        /* UTF-8 -> UTF-16LE */
        while (*s) {
            uint32_t c = *s;
            int len = c < 0x80 ? 1 : (c & 0xE0) == 0xC0 ? 2 : (c & 0xF0) == 0xE0 ? 3 : 4;
            if (len > 1) { c &= (len == 2 ? 0x1Fu : len == 3 ? 0x0Fu : 0x07u); for (int k = 1; k < len && s[k]; k++) c = (c << 6) | (s[k] & 0x3Fu); }
            s += len;
            if (c >= 0x10000) { c -= 0x10000; bb_u16(&raw, 0xD800 + (c >> 10)); bb_u16(&raw, 0xDC00 + (c & 0x3FF)); }
            else bb_u16(&raw, c);
        }
        if (raw.bad) { bb_free(&raw); return NOMEM; }
        dw = (uint32_t)m->strings.n;
        rc = bb_u32(&m->strings, (uint32_t)raw.n);
        if (!rc) rc = bb_put(&m->strings, raw.p, raw.n);
        if (!rc) rc = bb_zero(&m->strings, (4 - (raw.n % 4)) % 4);
        bb_free(&raw);
        if (rc) return NOMEM;
    } else dw = n->num;
    if (m->nent == m->capent) {
        size_t nc = m->capent ? m->capent * 2 : 64;
        nm_ent *t = (nm_ent *)realloc(m->ent, nc * sizeof *t);
        if (!t) return NOMEM;
        m->ent = t; m->capent = nc;
    }
    m->ent[m->nent].dw = dw; m->ent[m->nent].wg = (uint16_t)wg; m->ent[m->nent].idx = (uint16_t)m->nent;
    *propid = 0x8000u + (unsigned)m->nent;
    m->nent++;
    m->dirty = 1;
    return 0;
}

/* ---- save --------------------------------------------------------------------------------------------------------------------------- */
typedef struct { unsigned pid; bbuf data; } stream;
static int cmp_stream(const void *a, const void *b) { unsigned x = ((const stream *)a)->pid, y = ((const stream *)b)->pid; return x < y ? -1 : x > y; }

int nm_save(ops *o, namemap *m) {
    if (!m->dirty) return 0;
    opw *w = o->w;
    nbt_e e;
    int rc = opw_node(w, 0x61, &e);
    if (rc) return rc;
    size_t nb = m->buckets;
    stream *st = (stream *)calloc(3 + nb, sizeof *st);
    if (!st) return NOMEM;
    size_t ns = 0;
    st[ns].pid = 2; bb_put(&st[ns].data, m->guids, m->nguids * 16); ns++;
    st[ns].pid = 3;
    for (size_t i = 0; i < m->nent; i++) { bb_u32(&st[ns].data, m->ent[i].dw); bb_u16(&st[ns].data, m->ent[i].wg); bb_u16(&st[ns].data, m->ent[i].idx); }
    ns++;
    st[ns].pid = 4; bb_put(&st[ns].data, m->strings.p, m->strings.n); ns++;
    /* buckets, records in entry order; only non-empty buckets get a stream */
    bbuf *bk = (bbuf *)calloc(nb, sizeof *bk);
    if (!bk) { free(st); return NOMEM; }
    for (size_t i = 0; i < m->nent && !rc; i++) {
        const nm_ent *en = &m->ent[i];
        uint32_t key;
        if (en->wg & 1) {
            uint32_t n = (size_t)en->dw + 4 <= m->strings.n ? op_u32(m->strings.p + en->dw) : 0;
            if ((size_t)en->dw + 4 + n > m->strings.n) n = 0;
            key = op_crc(m->strings.p + en->dw + 4, n);         /* the PST CRC (no inversion) of the UTF-16LE name */
        } else key = en->dw;
        size_t b = (size_t)((key ^ en->wg) % m->buckets);
        rc = bb_u32(&bk[b], key);
        if (!rc) rc = bb_u16(&bk[b], en->wg);
        if (!rc) rc = bb_u16(&bk[b], en->idx);
    }
    for (size_t b = 0; b < nb; b++) {
        if (bk[b].n) { st[ns].pid = 0x1000 + (unsigned)b; st[ns].data = bk[b]; ns++; }
        else bb_free(&bk[b]);
    }
    free(bk);
    if (rc) { for (size_t i = 0; i < ns; i++) bb_free(&st[i].data); free(st); return OPST_E_NOMEM; }
    op_qsort(st, ns, sizeof *st, cmp_stream);

    hbuild hb;
    rc = hbuild_init(&hb, 0xBC);
    uint32_t hh = 0;
    if (!rc) rc = hbuild_alloc(&hb, NULL, 8, &hh);
    size_t nrec = 1 + ns;
    uint8_t (*keys)[2] = (uint8_t (*)[2])malloc(nrec * 2);
    uint8_t (*vals)[6] = (uint8_t (*)[6])malloc(nrec * 6);
    kv *recs = (kv *)malloc(nrec * sizeof *recs);
    size_t *var = (size_t *)malloc(nrec * sizeof *var), *varrec = (size_t *)malloc(nrec * sizeof *varrec);
    wsub *subs = (wsub *)calloc(ns + 1, sizeof *subs);
    size_t *subidx = (size_t *)malloc((ns + 1) * sizeof *subidx);
    if (!keys || !vals || !recs || !var || !varrec || !subs || !subidx) rc = OPST_E_NOMEM;
    size_t nvar = 0, nsub = 0;
    uint32_t nxt = 3;
    uint32_t next_sub = 0x400u << 5;
    for (size_t i = 0; i < m->nsubs; i++) if ((m->subs[i].nid & ~0x1Fu) > next_sub) next_sub = m->subs[i].nid & ~0x1Fu;
    next_sub += 0x20;
    size_t r = 0;
    if (!rc) { wr16(keys[r], 1); wr16(vals[r], 3); wr32(vals[r] + 2, m->buckets); recs[r].k = keys[r]; recs[r].v = vals[r]; r++; }
    for (size_t i = 0; i < ns && !rc; i++) {
        wr16(keys[r], st[i].pid);
        wr16(vals[r], 0x0102);
        varrec[nvar] = r;
        if (st[i].data.n > OP_MAXALLOC) {
            uint32_t nid = 0;
            for (size_t q = 0; q < m->nsubs; q++) if (m->subs[q].pid == st[i].pid) nid = m->subs[q].nid;
            if (!nid) { nid = next_sub | 0x1F; next_sub += 0x20; }
            subs[nsub].nid = nid;
            subidx[nsub++] = i;
            wr32(vals[r] + 2, nid);
        } else {
            wr32(vals[r] + 2, nxt << 5);
            var[nvar++] = i;
            nxt++;
        }
        recs[r].k = keys[r]; recs[r].v = vals[r];
        r++;
    }
    if (!rc) rc = build_bth(&hb, 2, 6, recs, r, hh, NULL);
    int patched = 0;
    for (size_t j = 0; j < nvar && !rc; j++) {
        uint32_t got;
        rc = hbuild_alloc(&hb, st[var[j]].data.p, st[var[j]].data.n, &got);
        if (!rc && got != ((uint32_t)(3 + j) << 5)) { wr32(vals[varrec[j]] + 2, got); patched = 1; }       /* the heap spilled into another block */
    }
    if (!rc && patched) rc = hbuild_patch_bth_leaf(&hb, 0x40, recs, r, 2, 6);
    hblocks blocks = {0, 0};
    if (!rc) rc = hbuild_finalize(&hb, hh, &blocks);
    hbuild_free(&hb);
    uint64_t new_bd = 0, new_bs = 0;
    if (!rc) { rc = opw_put_blocks(w, &blocks, &new_bd); hb_free(&blocks); }
    for (size_t j = 0; j < nsub && !rc; j++) {
        const bbuf *d = &st[subidx[j]].data;
        size_t nch = (d->n + OP_BLOCKMAX - 1) / OP_BLOCKMAX;
        if (!nch) nch = 1;
        hblocks ch = {0, 0};
        ch.b = (opbuf *)calloc(nch, sizeof *ch.b);
        if (!ch.b) { rc = OPST_E_NOMEM; break; }
        ch.n = nch;
        for (size_t q = 0; q < nch; q++) {
            size_t from = q * OP_BLOCKMAX, len = d->n - from < OP_BLOCKMAX ? d->n - from : OP_BLOCKMAX;
            if (d->n == 0) len = 0;
            ch.b[q].p = (uint8_t *)malloc(len ? len : 1);
            ch.b[q].n = len;
            if (!ch.b[q].p) { rc = OPST_E_NOMEM; break; }
            if (len) memcpy(ch.b[q].p, d->p + from, len);
        }
        if (!rc) rc = opw_put_blocks(w, &ch, &subs[j].bd);
        hb_free(&ch);
        subs[j].bs = 0;
    }
    if (!rc && nsub) rc = opw_put_subnodes(w, subs, nsub, &new_bs);
    if (!rc) rc = opw_node_put(w, 0x61, new_bd, new_bs, e.parent);
    if (!rc) rc = opw_release(w, e.bd);
    if (!rc && e.bs) rc = opw_release(w, e.bs);
    if (!rc) m->dirty = 0;
    for (size_t i = 0; i < ns; i++) bb_free(&st[i].data);
    free(st); free(keys); free(vals); free(recs); free(var); free(varrec); free(subs); free(subidx);
    return rc;
}

/* subnode ids of the name map's streams (for the NID high-water marks) */
size_t nm_subnids(const namemap *m, uint32_t *out, size_t cap) {
    size_t n = 0;
    for (size_t i = 0; i < m->nsubs && n < cap; i++) out[n++] = m->subs[i].nid;
    return n;
}
