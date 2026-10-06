/* op_import.c - builds a new message from plain fields (subject, addresses, bodies, recipients, attachments) and files it into a
 * folder: the message node (property context + recipient table + attachment table + attachment nodes + large values in subnodes), its row
 * in the folder's contents table, the folder counts and the message index. The structure follows what Outlook itself writes
 * (inspected with tools/inspect_msg on real archives). */
#include "op_wr.h"
#include <time.h>

#define NOMEM op_err(OPST_E_NOMEM, "out of memory")
#define BIG_VALUE 3000u                       /* values larger than this live in a subnode, not in the property heap (heap items are limited to 3580 bytes) */

static int test_hook(const char *name) { const char *e = getenv(name); return e && *e; }       /* defect-reproduction hooks for the tests */

/* ---- small helpers ------------------------------------------------------------------------------------------------------------- */
static int64_t now_filetime(void) { return (int64_t)time(NULL) * 10000000LL + 116444736000000000LL; }

/* UTF-8 -> UTF-16LE; invalid sequences become U+FFFD instead of failing (imported mail is often sloppy) */
static int u8_to_u16(const char *s, bbuf *out) {
    const unsigned char *p = (const unsigned char *)(s ? s : "");
    while (*p) {
        uint32_t c = *p;
        int len = c < 0x80 ? 1 : (c >= 0xC2 && c <= 0xDF) ? 2 : (c >= 0xE0 && c <= 0xEF) ? 3 : (c >= 0xF0 && c <= 0xF4) ? 4 : 0;
        int ok = len > 0;
        if (len > 1) {
            c &= (len == 2 ? 0x1Fu : len == 3 ? 0x0Fu : 0x07u);
            for (int k = 1; k < len && ok; k++) {
                if ((p[k] & 0xC0) != 0x80) ok = 0; else c = (c << 6) | (p[k] & 0x3Fu);
            }
            if (ok && ((len == 3 && c < 0x800) || (len == 4 && (c < 0x10000 || c > 0x10FFFF)) || (c >= 0xD800 && c < 0xE000))) ok = 0;
        }
        if (!ok) { c = 0xFFFD; len = 1; }
        p += len;
        if (c >= 0x10000) { c -= 0x10000; bb_u16(out, 0xD800 + (c >> 10)); bb_u16(out, 0xDC00 + (c & 0x3FF)); }
        else bb_u16(out, c);
    }
    return out->bad ? OPST_E_NOMEM : 0;
}

typedef struct { wsub *e; size_t n, cap; size_t ext_bytes; } sublist;

/* a new local NID of `type`, taken from the header's counter of that type like Outlook does (SCANPST compares every NID in the file with these counters) */
static uint32_t next_local(opw *w, unsigned type) {
    uint32_t idx = op_u32(w->hdr + 44 + 4 * type) + 1;
    if (!test_hook("OPST_TEST_BAD_NIDCOUNTER")) wr32(w->hdr + 44 + 4 * type, idx);      /* test hook: leave the counter behind, as the first version did */
    return (idx << 5) | type;
}
static void sublist_free(sublist *s) { free(s->e); memset(s, 0, sizeof *s); }
static int sublist_add(sublist *s, uint32_t nid, uint64_t bd, uint64_t bs) {
    if (s->n == s->cap) {
        size_t nc = s->cap ? s->cap * 2 : 16;
        wsub *t = (wsub *)realloc(s->e, nc * sizeof *t);
        if (!t) return OPST_E_NOMEM;
        s->e = t; s->cap = nc;
    }
    s->e[s->n].nid = nid; s->e[s->n].bd = bd; s->e[s->n].bs = bs;
    s->n++;
    return 0;
}

static int pset(pcprops *p, unsigned pid, unsigned ptype, const void *d, size_t n) { return pcprops_set(p, pid, ptype, (const uint8_t *)d, n); }
static int p_i32(pcprops *p, unsigned pid, int32_t v) { uint8_t b[4]; wr32(b, (uint32_t)v); return pset(p, pid, 3, b, 4); }
static int p_bool(pcprops *p, unsigned pid, int v) { uint8_t b = v ? 1 : 0; return pset(p, pid, 0xB, &b, 1); }
static int p_time(pcprops *p, unsigned pid, int64_t v) { uint8_t b[8]; wr64(b, (uint64_t)v); return pset(p, pid, 0x40, b, 8); }
static int p_bin(pcprops *p, unsigned pid, const uint8_t *d, size_t n) { return pset(p, pid, 0x102, d, n); }
static int p_str(pcprops *p, unsigned pid, const char *utf8) {
    bbuf b = {0};
    int rc = u8_to_u16(utf8, &b);
    if (!rc) rc = pset(p, pid, 0x1F, b.p, b.n);
    bb_free(&b);
    return rc;
}

/* stores `data` as a data tree and makes the property a reference to a new subnode of `subs` (type LTP, local nid unique in the list) */
static int p_ext(opw *w, sublist *subs, pcprops *p, unsigned pid, unsigned ptype, const uint8_t *data, size_t n) {
    uint32_t nid = next_local(w, 0x1F);
    subs->ext_bytes += n;
    size_t nb = (n + OP_BLOCKMAX - 1) / OP_BLOCKMAX;
    opbuf *bl = (opbuf *)calloc(nb ? nb : 1, sizeof *bl);
    if (!bl) return NOMEM;
    for (size_t i = 0; i < nb; i++) { bl[i].p = (uint8_t *)data + i * OP_BLOCKMAX; bl[i].n = n - i * OP_BLOCKMAX < OP_BLOCKMAX ? n - i * OP_BLOCKMAX : OP_BLOCKMAX; }
    hblocks hb = {bl, nb};
    uint64_t top;
    int rc = opw_put_blocks(w, &hb, &top);
    free(bl);
    if (rc) return rc;
    rc = sublist_add(subs, nid, top, 0);
    if (!rc) rc = pset(p, pid, ptype, "", 0);
    if (!rc) pcprops_find(p, pid)->ext_nid = nid;
    return rc;
}

/* string / binary value, in the heap when small and in a subnode when large */
static int p_str_big(opw *w, sublist *subs, pcprops *p, unsigned pid, const char *utf8) {
    bbuf b = {0};
    int rc = u8_to_u16(utf8, &b);
    if (!rc) rc = b.n > ((pid == 0x0E03 || pid == 0x0E04) ? OP_BIGDISPLAY : BIG_VALUE) ? p_ext(w, subs, p, pid, 0x1F, b.p, b.n) : pset(p, pid, 0x1F, b.p, b.n);
    bb_free(&b);
    return rc;
}

/* ---- identity values ------------------------------------------------------------------------------------------------------------ */
/* one-off entry id: flags, provider GUID, version/flags 0x8000 (Unicode), then display name, address type and address as UTF-16 strings */
static int one_off_entry_id(const char *name, const char *email, bbuf *out) {
    static const uint8_t guid[16] = {0x81, 0x2B, 0x1F, 0xA4, 0xBE, 0xA3, 0x10, 0x19, 0x9D, 0x6E, 0x00, 0xDD, 0x01, 0x0F, 0x54, 0x02};
    bb_u32(out, 0);
    bb_put(out, guid, 16);
    bb_u16(out, 0); bb_u16(out, 0x8000);
    int rc = u8_to_u16(name && *name ? name : email, out);
    bb_u16(out, 0);
    if (!rc) rc = u8_to_u16("SMTP", out);
    bb_u16(out, 0);
    if (!rc) rc = u8_to_u16(email, out);
    bb_u16(out, 0);
    return rc ? rc : (out->bad ? OPST_E_NOMEM : 0);
}

/* "SMTP:ADDRESS" in upper case plus a NUL: the search key of an SMTP address */
static int search_key(const char *email, bbuf *out) {
    bb_put(out, "SMTP:", 5);
    for (const char *c = email; *c; c++) bb_u8(out, (unsigned char)((*c >= 'a' && *c <= 'z') ? *c - 32 : *c));
    bb_u8(out, 0);
    return out->bad ? OPST_E_NOMEM : 0;
}

static int has_prefix_ci(const char *s, const char *pre) {
    size_t n = strlen(pre);
    for (size_t i = 0; i < n; i++) {
        char a = s[i], b = pre[i];
        if (!a) return 0;
        if (a >= 'A' && a <= 'Z') a = (char)(a + 32);
        if (a != b) return 0;
    }
    return 1;
}
/* ---- conversation id = MD5 of the upper-cased topic ---------------------------------------------------------------------------------- */
/* Found by probing SCANPST with imported messages that differ in one field each: the 0x3013 value it writes depends only on the conversation topic and
   equals MD5 over the UTF-16LE bytes of the topic in upper case. Outlook's conversation-index GUID of a new conversation is the same value, so the
   importer uses it for both. (Upper-casing covers ASCII, Latin-1, Latin Extended-A, Greek and Cyrillic; other scripts are left as they are.) */
static uint16_t upper16(uint16_t c) {
    if (c >= 'a' && c <= 'z') return (uint16_t)(c - 32);
    if (c == 0xB5) return 0x39C;
    if (c >= 0xE0 && c <= 0xFE && c != 0xF7) return (uint16_t)(c - 32);
    if (c == 0xFF) return 0x178;
    if ((c >= 0x100 && c <= 0x137) || (c >= 0x14A && c <= 0x177)) return (c & 1) ? (uint16_t)(c - 1) : c;
    if ((c >= 0x13A && c <= 0x148) || c == 0x17A || c == 0x17C || c == 0x17E) return (c & 1) ? c : (uint16_t)(c - 1);
    if (c == 0x3C2) return 0x3A3;
    if (c >= 0x3B1 && c <= 0x3C9) return (uint16_t)(c - 32);
    if (c >= 0x430 && c <= 0x44F) return (uint16_t)(c - 32);
    if (c >= 0x450 && c <= 0x45F) return (uint16_t)(c - 80);
    return c;
}

static void md5_block(uint32_t st[4], const uint8_t b[64]) {
    static const uint32_t K[64] = {
        0xd76aa478, 0xe8c7b756, 0x242070db, 0xc1bdceee, 0xf57c0faf, 0x4787c62a, 0xa8304613, 0xfd469501, 0x698098d8, 0x8b44f7af, 0xffff5bb1, 0x895cd7be,
        0x6b901122, 0xfd987193, 0xa679438e, 0x49b40821, 0xf61e2562, 0xc040b340, 0x265e5a51, 0xe9b6c7aa, 0xd62f105d, 0x02441453, 0xd8a1e681, 0xe7d3fbc8,
        0x21e1cde6, 0xc33707d6, 0xf4d50d87, 0x455a14ed, 0xa9e3e905, 0xfcefa3f8, 0x676f02d9, 0x8d2a4c8a, 0xfffa3942, 0x8771f681, 0x6d9d6122, 0xfde5380c,
        0xa4beea44, 0x4bdecfa9, 0xf6bb4b60, 0xbebfbc70, 0x289b7ec6, 0xeaa127fa, 0xd4ef3085, 0x04881d05, 0xd9d4d039, 0xe6db99e5, 0x1fa27cf8, 0xc4ac5665,
        0xf4292244, 0x432aff97, 0xab9423a7, 0xfc93a039, 0x655b59c3, 0x8f0ccc92, 0xffeff47d, 0x85845dd1, 0x6fa87e4f, 0xfe2ce6e0, 0xa3014314, 0x4e0811a1,
        0xf7537e82, 0xbd3af235, 0x2ad7d2bb, 0xeb86d391};
    static const uint8_t R[64] = {7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20,
                                  4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21};
    uint32_t m[16], a = st[0], bb = st[1], cc = st[2], d = st[3];
    for (int i = 0; i < 16; i++) m[i] = (uint32_t)b[4 * i] | (uint32_t)b[4 * i + 1] << 8 | (uint32_t)b[4 * i + 2] << 16 | (uint32_t)b[4 * i + 3] << 24;
    for (int i = 0; i < 64; i++) {
        uint32_t f; int g;
        if (i < 16) { f = (bb & cc) | (~bb & d); g = i; }
        else if (i < 32) { f = (d & bb) | (~d & cc); g = (5 * i + 1) & 15; }
        else if (i < 48) { f = bb ^ cc ^ d; g = (3 * i + 5) & 15; }
        else { f = cc ^ (bb | ~d); g = (7 * i) & 15; }
        uint32_t t = d; d = cc; cc = bb;
        uint32_t x = a + f + K[i] + m[g];
        bb = bb + ((x << R[i]) | (x >> (32 - R[i])));
        a = t;
    }
    st[0] += a; st[1] += bb; st[2] += cc; st[3] += d;
}
static void md5(const uint8_t *p, size_t n, uint8_t out[16]) {
    uint32_t st[4] = {0x67452301, 0xefcdab89, 0x98badcfe, 0x10325476};
    size_t i = 0;
    for (; i + 64 <= n; i += 64) md5_block(st, p + i);
    uint8_t tail[128];
    size_t r = n - i;
    memset(tail, 0, sizeof tail);
    if (r) memcpy(tail, p + i, r);
    tail[r] = 0x80;
    size_t tl = r < 56 ? 64 : 128;
    uint64_t bits = (uint64_t)n * 8;
    for (int k = 0; k < 8; k++) tail[tl - 8 + k] = (uint8_t)(bits >> (8 * k));
    md5_block(st, tail);
    if (tl == 128) md5_block(st, tail + 64);
    for (int k = 0; k < 4; k++) { out[4 * k] = (uint8_t)st[k]; out[4 * k + 1] = (uint8_t)(st[k] >> 8); out[4 * k + 2] = (uint8_t)(st[k] >> 16); out[4 * k + 3] = (uint8_t)(st[k] >> 24); }
}
/* the conversation GUID of a topic */
static int topic_guid(const char *topic, uint8_t out[16]) {
    bbuf b = {0};
    int rc = u8_to_u16(topic, &b);
    if (rc) { bb_free(&b); return rc; }
    for (size_t i = 0; i + 1 < b.n; i += 2) { uint16_t u = upper16((uint16_t)(b.p[i] | b.p[i + 1] << 8)); b.p[i] = (uint8_t)u; b.p[i + 1] = (uint8_t)(u >> 8); }
    md5(b.p, b.n, out);
    bb_free(&b);
    return 0;
}

/* the conversation topic: the subject without any leading RE: / FW: / FWD: markers */
static const char *normalized_subject(const char *s) {
    for (;;) {
        while (*s == ' ') s++;
        if (has_prefix_ci(s, "re:")) s += 3;
        else if (has_prefix_ci(s, "fw:")) s += 3;
        else if (has_prefix_ci(s, "fwd:")) s += 4;
        else break;
    }
    while (*s == ' ') s++;
    return s;
}

/* ---- tables --------------------------------------------------------------------------------------------------------------------- */
typedef struct { uint16_t pid, ptype, ibd; uint8_t cbd, ibit; } colspec;

static int table_new(tctx *t, const colspec *cols, size_t n, unsigned r0, unsigned r1, unsigned r2, unsigned r3) {
    memset(t, 0, sizeof *t);
    t->cols = (tcol *)calloc(n, sizeof *t->cols);
    if (!t->cols) return NOMEM;
    t->ncols = n;
    for (size_t i = 0; i < n; i++) { t->cols[i].pid = cols[i].pid; t->cols[i].ptype = cols[i].ptype; t->cols[i].ibd = cols[i].ibd; t->cols[i].cbd = cols[i].cbd; t->cols[i].ibit = cols[i].ibit; }
    t->rgib[0] = (uint16_t)r0; t->rgib[1] = (uint16_t)r1; t->rgib[2] = (uint16_t)r2; t->rgib[3] = (uint16_t)r3;
    return 0;
}
static int cell(tctx *t, size_t row, unsigned pid, const void *d, size_t n) {
    int c = tc_col(t, pid);
    return c < 0 ? 0 : tc_set_cell(t, row, c, (const uint8_t *)d, n);
}
static int cell_i32(tctx *t, size_t row, unsigned pid, int32_t v) { uint8_t b[4]; wr32(b, (uint32_t)v); return cell(t, row, pid, b, 4); }
static int cell_str(tctx *t, size_t row, unsigned pid, const char *utf8) {
    bbuf b = {0};
    int rc = u8_to_u16(utf8, &b);
    if (!rc) rc = cell(t, row, pid, b.p, b.n);
    bb_free(&b);
    return rc;
}

/* stores a built table as the data of a subnode: heap blocks, plus the row matrix as a nested subnode when it does not fit the heap */
static int cmp_wsub_nid2(const void *a, const void *b) { uint32_t x = ((const wsub *)a)->nid, y = ((const wsub *)b)->nid; return x < y ? -1 : x > y; }

static int table_store(opw *w, tctx *t, uint64_t *bd, uint64_t *bs, size_t *bytes) {
    hblocks heap, rows;
    uint32_t rows_nid;
    tcbig *bigs = NULL;
    size_t nbig = 0;
    int rc = tc_build_ex(t, w, &heap, &rows, &rows_nid, &bigs, &nbig);
    if (rc) return rc;
    *bs = 0;
    *bytes = 0;
    for (size_t i = 0; i < heap.n; i++) *bytes += heap.b[i].n;
    for (size_t i = 0; i < rows.n; i++) *bytes += rows.b[i].n;
    for (size_t i = 0; i < nbig; i++) *bytes += bigs[i].n;
    wsub *ns = (wsub *)calloc(nbig + 2, sizeof *ns);
    size_t nn = 0;
    if (!ns) rc = OPST_E_NOMEM;
    if (!rc) rc = opw_put_blocks(w, &heap, bd);
    if (!rc && rows.n) {
        uint64_t top;
        rc = opw_put_blocks(w, &rows, &top);
        if (!rc) { ns[nn].nid = rows_nid; ns[nn].bd = top; ns[nn].bs = 0; nn++; }
    }
    if (!rc && nbig) { rc = tcbig_put(w, bigs, nbig, ns + nn); nn += nbig; }
    if (!rc && nn) { op_qsort(ns, nn, sizeof *ns, cmp_wsub_nid2); rc = opw_put_subnodes(w, ns, nn, bs); }
    free(ns);
    tcbig_free(bigs, nbig);
    hb_free(&heap); hb_free(&rows);
    return rc;
}

/* the recipient table as Outlook lays it out: 18 columns, 69-byte rows */
static const colspec RECIP_COLS[] = {
    {0x0C15, 3, 24, 4, 7}, {0x0E0F, 0xB, 64, 1, 2}, {0x0FF9, 0x102, 32, 4, 9}, {0x0FFE, 3, 36, 4, 10}, {0x0FFF, 0x102, 16, 4, 5},
    {0x3001, 0x1F, 20, 4, 6}, {0x3002, 0x1F, 8, 4, 3}, {0x3003, 0x1F, 12, 4, 4}, {0x300B, 0x102, 28, 4, 8}, {0x3900, 3, 40, 4, 11},
    {0x39FE, 0x1F, 48, 4, 14}, {0x39FF, 0x1F, 44, 4, 13}, {0x3A20, 0x1F, 52, 4, 15}, {0x3A40, 0xB, 65, 1, 12}, {0x5FF7, 0x102, 56, 4, 16},
    {0x5FFD, 3, 60, 4, 17}, {0x67F2, 3, 0, 4, 0}, {0x67F3, 3, 4, 4, 1}};
static const colspec ATTACH_COLS[] = {
    {0x0E20, 3, 12, 4, 3}, {0x3704, 0x1F, 20, 4, 5}, {0x3705, 3, 16, 4, 4}, {0x370B, 3, 8, 4, 2}, {0x67F2, 3, 0, 4, 0}, {0x67F3, 3, 4, 4, 1}};

static int build_recipient_table(ops *o, const opst_import_msg *m, uint64_t *bd, uint64_t *bs, size_t *bytes) {
    tctx t;
    int rc = table_new(&t, RECIP_COLS, sizeof RECIP_COLS / sizeof *RECIP_COLS, 64, 64, 66, 69);
    for (size_t i = 0; i < m->nrecipients && !rc; i++) {
        const opst_import_recipient *r = &m->recipients[i];
        const char *email = r->email ? r->email : "", *name = r->name && *r->name ? r->name : email;
        rc = tc_add_row(&t, 0x1000u + (uint32_t)i);
        if (rc) break;
        bbuf eid = {0}, key = {0}, rk = {0};
        rc = one_off_entry_id(name, email, &eid);
        if (!rc) rc = search_key(email, &key);
        uint8_t rec[16];
        if (!rc) rc = op_random(rec, 16);
        if (!rc) bb_put(&rk, rec, 16);
        if (!rc) rc = cell_i32(&t, i, 0x0C15, r->type >= 1 && r->type <= 3 ? r->type : 1);
        uint8_t one = 1, zero = 0;
        if (!rc) rc = cell(&t, i, 0x0E0F, &one, 1);
        if (!rc) rc = cell(&t, i, 0x0FF9, rk.p, rk.n);
        if (!rc) rc = cell_i32(&t, i, 0x0FFE, 6);
        if (!rc) rc = cell(&t, i, 0x0FFF, eid.p, eid.n);
        if (!rc) rc = cell_str(&t, i, 0x3001, name);
        if (!rc) rc = cell_str(&t, i, 0x3002, "SMTP");
        if (!rc) rc = cell_str(&t, i, 0x3003, email);
        if (!rc) rc = cell(&t, i, 0x300B, key.p, key.n);
        if (!rc) rc = cell_i32(&t, i, 0x3900, 0);
        if (!rc) rc = cell_str(&t, i, 0x39FE, email);
        if (!rc) rc = cell_str(&t, i, 0x39FF, name);
        if (!rc) rc = cell_str(&t, i, 0x3A20, name);
        if (!rc) rc = cell(&t, i, 0x3A40, &zero, 1);
        if (!rc) rc = cell(&t, i, 0x5FF7, eid.p, eid.n);
        if (!rc) rc = cell_i32(&t, i, 0x5FFD, 1);
        if (!rc) rc = cell_i32(&t, i, 0x67F2, (int32_t)(0x1000u + i));
        if (!rc) rc = cell_i32(&t, i, 0x67F3, (int32_t)ops_next_row_ver(o));
        bb_free(&eid); bb_free(&key); bb_free(&rk);
    }
    if (!rc) rc = table_store(o->w, &t, bd, bs, bytes);
    tc_free(&t);
    return rc;
}

static int build_attachment_table(ops *o, const opst_import_msg *m, const uint32_t *att_nids, const int32_t *att_sizes, uint64_t *bd, uint64_t *bs, size_t *bytes) {
    tctx t;
    int rc = table_new(&t, ATTACH_COLS, sizeof ATTACH_COLS / sizeof *ATTACH_COLS, 24, 24, 24, 25);
    for (size_t i = 0; i < m->nattachments && !rc; i++) {
        const opst_import_attachment *a = &m->attachments[i];
        rc = tc_add_row(&t, att_nids[i]);
        if (!rc) rc = cell_i32(&t, i, 0x0E20, att_sizes[i]);
        if (!rc) rc = cell_str(&t, i, 0x3704, a->filename && *a->filename ? a->filename : "attachment");
        if (!rc) rc = cell_i32(&t, i, 0x3705, 1);
        if (!rc) rc = cell_i32(&t, i, 0x370B, -1);
        if (!rc) rc = cell_i32(&t, i, 0x67F2, (int32_t)att_nids[i]);
        if (!rc) rc = cell_i32(&t, i, 0x67F3, (int32_t)ops_next_row_ver(o));
    }
    if (!rc) rc = table_store(o->w, &t, bd, bs, bytes);
    tc_free(&t);
    return rc;
}

/* ---- attachments ---------------------------------------------------------------------------------------------------------------- */
static const char *extension_of(const char *name) {
    const char *dot = NULL;
    for (const char *c = name; *c; c++) if (*c == '.') dot = c;
    return dot ? dot : "";
}

static int build_attachment(ops *o, const opst_import_attachment *a, int64_t now, uint64_t *bd, uint64_t *bs, int32_t *size_out) {
    opw *w = o->w;
    pcprops p = {0};
    sublist own = {0};
    const char *name = a->filename && *a->filename ? a->filename : "attachment";
    int rc = p_i32(&p, 0x0E20, 0);                                       /* the real size is set below */
    if (!rc) rc = p_str(&p, 0x3001, name);
    if (!rc) rc = p_time(&p, 0x3007, a->modified ? a->modified : now);
    if (!rc) rc = p_time(&p, 0x3008, a->modified ? a->modified : now);
    if (!rc) rc = a->len > BIG_VALUE ? p_ext(w, &own, &p, 0x3701, 0x102, a->data, a->len) : p_bin(&p, 0x3701, a->data ? a->data : (const uint8_t *)"", a->len);
    if (!rc) rc = p_str(&p, 0x3703, extension_of(name));
    if (!rc) rc = p_str(&p, 0x3704, name);
    if (!rc) rc = p_i32(&p, 0x3705, 1);                                  /* attach by value */
    if (!rc) rc = p_str(&p, 0x3707, name);
    if (!rc) rc = p_i32(&p, 0x370B, -1);
    if (!rc && a->mime && *a->mime) rc = p_str(&p, 0x370E, a->mime);
    if (!rc && a->content_id && *a->content_id) rc = p_str(&p, 0x3712, a->content_id);
    if (!rc) rc = p_i32(&p, 0x3714, a->content_id && *a->content_id ? 4 : 0);
    if (!rc) rc = p_bool(&p, 0x7FFE, a->hidden);
    if (!rc) {
        size_t total = a->len;                                     /* the data, wherever it is stored */
        for (size_t i = 0; i < p.n; i++) if (p.p[i].pid != 0x3701) total += p.p[i].v.n;
        if (test_hook("OPST_TEST_BAD_ATTSIZE")) total += 1024;          /* test hook: the first version padded the size */
        *size_out = (int32_t)total;
        rc = p_i32(&p, 0x0E20, *size_out);
    }
    hblocks hb = {0};
    if (!rc) rc = pc_build(&p, &hb);
    if (!rc) rc = opw_put_blocks(w, &hb, bd);
    hb_free(&hb);
    if (!rc) rc = opw_put_subnodes(w, own.e, own.n, bs);
    pcprops_free(&p);
    sublist_free(&own);
    return rc;
}

/* ---- the message ---------------------------------------------------------------------------------------------------------------- */
/* the first `maxchars` characters of a UTF-8 string, copied into out (cap bytes, always terminated); Outlook keeps subjects and display names to 255 characters */
static const char *cut_chars(const char *s, size_t maxchars, char *out, size_t cap) {
    size_t used = 0, chars = 0;
    while (s[used] && chars < maxchars && used + 5 < cap) {
        unsigned char c = (unsigned char)s[used];
        size_t len = c < 0x80 ? 1 : (c & 0xE0) == 0xC0 ? 2 : (c & 0xF0) == 0xE0 ? 3 : 4;
        for (size_t k = 0; k < len && s[used]; k++) { out[used] = s[used]; used++; }
        chars++;
    }
    out[used] = 0;
    return out;
}

/* builds one message and adds its row to `dtc` (the destination contents table, stored by the caller); *key tells how to file it in the message index */
static int import_one(ops *o, uint32_t folder, tctx *dtc, const opst_import_msg *m, uint32_t *nid_out, keyednid *key) {
    opw *w = o->w;
    int rc = 0;
    int64_t now = now_filetime();
    int64_t sent = m->sent ? m->sent : (m->received ? m->received : now);
    int64_t recv = m->received ? m->received : sent;
    char subject_cut[1100];
    const char *subject = cut_chars(m->subject ? m->subject : "", 255, subject_cut, sizeof subject_cut);
    const char *cls = m->message_class && *m->message_class ? m->message_class : "IPM.Note";
    const char *sname = m->sender_name && *m->sender_name ? m->sender_name : (m->sender_email ? m->sender_email : "");
    char sname_cut[1100];                                  /* also part of binary entry ids, which must stay small */
    sname = cut_chars(sname, 255, sname_cut, sizeof sname_cut);
    const char *semail = m->sender_email ? m->sender_email : "";
    int imp = m->importance >= 0 && m->importance <= 2 ? m->importance : 1;
    size_t natt = m->nattachments;
    if (natt > 250 || m->nrecipients > 2000) return op_err(OPST_E_ARG, "too many attachments or recipients");

    /* display strings: "To" and "Cc" lists */
    bbuf to = {0}, cc = {0};
    for (size_t i = 0; i < m->nrecipients; i++) {
        const opst_import_recipient *r = &m->recipients[i];
        bbuf *b = r->type == 2 ? &cc : r->type == 3 ? NULL : &to;
        if (!b) continue;
        const char *nm = r->name && *r->name ? r->name : (r->email ? r->email : "");
        if (b->n) bb_put(b, "; ", 2);
        bb_put(b, nm, strlen(nm));
    }
    bb_u8(&to, 0); bb_u8(&cc, 0);

    pcprops p = {0};
    sublist subs = {0};
    uint32_t *att_nids = (uint32_t *)calloc(natt ? natt : 1, sizeof *att_nids);
    int32_t *att_sizes = (int32_t *)calloc(natt ? natt : 1, sizeof *att_sizes);
    if (!att_nids || !att_sizes || to.bad || cc.bad) rc = OPST_E_NOMEM;
    uint8_t guid[16], rk[22], pred[23], conv[22], skey[16];
    uint32_t idx = op_u32(w->hdr + 44 + 4 * 4) + 1;
    uint32_t nn = (idx << 5) | 4;
    uint64_t bd = 0, bs = 0;
    int hasatt = natt > 0;

    if (!rc) rc = op_random(guid, 16);
    if (!rc) rc = op_random(skey, 16);
    if (!rc) {
        /* change key / source key: GUID + 6-byte counter; predecessor change list: length byte + change key */
        memcpy(rk, guid, 16);
        uint64_t ctr = (uint64_t)(now / 10000000LL) & 0xFFFFFFFFFFFFull;
        for (int i = 5; i >= 0; i--) { rk[16 + i] = (uint8_t)(ctr & 0xFF); ctr >>= 8; }
        pred[0] = 22; memcpy(pred + 1, rk, 22);
        /* conversation index: 0x01, 5 bytes of the time, a fresh GUID; the message index buckets messages by that GUID */
        uint8_t g2[16];
        rc = topic_guid(normalized_subject(subject), g2);
        conv[0] = 1;
        uint64_t t5 = (uint64_t)now >> 24;                 /* the top 40 bits of the FILETIME, as Outlook writes them */
        for (int i = 0; i < 5; i++) conv[1 + i] = (uint8_t)(t5 >> (8 * (4 - i)));
        memcpy(conv + 6, g2, 16);
    }
    if (!rc) rc = p_str(&p, 0x001A, cls);
    if (!rc) rc = p_i32(&p, 0x0017, imp);
    if (!rc) rc = p_i32(&p, 0x0026, imp - 1);
    if (!rc) rc = p_bool(&p, 0x0029, 0);
    if (!rc) rc = p_i32(&p, 0x0036, 0);
    if (!rc) rc = p_str_big(w, &subs, &p, 0x0037, subject);
    if (!rc) rc = p_time(&p, 0x0039, sent);
    if (!rc) rc = p_str_big(w, &subs, &p, 0x0042, sname);
    if (!rc) rc = p_str(&p, 0x0064, "SMTP");
    if (!rc) rc = p_str_big(w, &subs, &p, 0x0065, semail);
    if (!rc) rc = p_str_big(w, &subs, &p, 0x0070, normalized_subject(subject));
    if (!rc) rc = p_bin(&p, 0x0071, conv, sizeof conv);
    if (!rc) rc = p_bool(&p, 0x0C06, 0);
    if (!rc) rc = p_str_big(w, &subs, &p, 0x0C1A, sname);
    if (!rc) rc = p_str(&p, 0x0C1E, "SMTP");
    if (!rc) rc = p_str_big(w, &subs, &p, 0x0C1F, semail);
    if (!rc && *(const char *)cc.p) rc = p_str_big(w, &subs, &p, 0x0E03, (const char *)cc.p);       /* an empty display-cc is not written (SCANPST removes it) */
    if (!rc && *(const char *)to.p) rc = p_str_big(w, &subs, &p, 0x0E04, (const char *)to.p);       /* an empty display-to is not written either (found with SCANPST: it deletes the property) */
    if (!rc) rc = p_time(&p, 0x0E06, recv);
    if (!rc) rc = p_i32(&p, 0x0E07, (m->read ? 1 : 0) | (hasatt ? 0x10 : 0));
    if (!rc) rc = p_i32(&p, 0x1080, -1);
    if (!rc) rc = p_time(&p, 0x3007, now);
    if (!rc) rc = p_time(&p, 0x3008, now);
    if (!rc) rc = p_bin(&p, 0x300B, skey, sizeof skey);
    if (!rc) rc = p_i32(&p, 0x3FDE, 65001);
    if (!rc) rc = p_i32(&p, 0x3FF1, 1033);
    if (!rc) rc = p_i32(&p, 0x3FFD, 65001);
    if (!rc) rc = p_str_big(w, &subs, &p, 0x5D01, semail);
    if (!rc) rc = p_str_big(w, &subs, &p, 0x5D02, semail);
    if (!rc) rc = p_bin(&p, 0x65E0, rk, sizeof rk);
    if (!rc) rc = p_bin(&p, 0x65E2, rk, sizeof rk);
    if (!rc) rc = p_bin(&p, 0x65E3, pred, sizeof pred);
    if (!rc && m->message_id && *m->message_id) rc = p_str_big(w, &subs, &p, 0x1035, m->message_id);
    if (!rc && *semail) {
        bbuf e1 = {0}, k1 = {0};
        rc = one_off_entry_id(sname, semail, &e1);
        if (!rc) rc = search_key(semail, &k1);
        if (!rc) rc = p_bin(&p, 0x0041, e1.p, e1.n);
        if (!rc) rc = p_bin(&p, 0x0C19, e1.p, e1.n);
        if (!rc) rc = p_bin(&p, 0x003B, k1.p, k1.n);
        if (!rc) rc = p_bin(&p, 0x0C1D, k1.p, k1.n);
        bb_free(&e1); bb_free(&k1);
    }
    if (!rc && m->transport_headers && *m->transport_headers) rc = p_str_big(w, &subs, &p, 0x007D, m->transport_headers);
    if (!rc && m->body_text && *m->body_text) rc = p_str_big(w, &subs, &p, 0x1000, m->body_text);
    if (!rc && m->body_html && *m->body_html) {
        size_t hn = strlen(m->body_html);
        rc = hn > BIG_VALUE ? p_ext(w, &subs, &p, 0x1013, 0x102, (const uint8_t *)m->body_html, hn) : p_bin(&p, 0x1013, (const uint8_t *)m->body_html, hn);
    }

    /* subnodes: recipient table, attachment table, attachments */
    uint64_t rbd = 0, rbs = 0;
    size_t table_bytes = 0, tb = 0;
    if (!rc) rc = build_recipient_table(o, m, &rbd, &rbs, &tb);
    table_bytes += tb;
    if (!rc) rc = sublist_add(&subs, OP_NID_RECIP_TBL, rbd, rbs);
    for (size_t i = 0; i < natt && !rc; i++) {
        att_nids[i] = next_local(w, 5);
        uint64_t abd, abs;
        rc = build_attachment(o, &m->attachments[i], now, &abd, &abs, &att_sizes[i]);
        if (!rc) rc = sublist_add(&subs, att_nids[i], abd, abs);
    }
    if (!rc && natt) {
        uint64_t tbd, tbs;
        rc = build_attachment_table(o, m, att_nids, att_sizes, &tbd, &tbs, &tb);
        table_bytes += tb;
        if (!rc) rc = sublist_add(&subs, OP_NID_ATTACH_TBL, tbd, tbs);
    }
    if (!rc) {
        /* PidTagMessageSize: every property value of the message plus its attachments */
        size_t total = subs.ext_bytes + table_bytes;               /* Outlook counts the recipient and attachment tables too; SCANPST compares the stored size with the real one */
        for (size_t i = 0; i < p.n; i++) total += p.p[i].v.n;
        for (size_t i = 0; i < natt; i++) total += (size_t)att_sizes[i];
        rc = p_i32(&p, 0x0E08, (int32_t)((total + 4) > 0x7FFFFFFF ? 0x7FFFFFFF : total + 4));
    }
    hblocks hb = {0};
    if (!rc) rc = pc_build(&p, &hb);
    if (!rc) rc = opw_put_blocks(w, &hb, &bd);
    hb_free(&hb);
    if (!rc) rc = opw_put_subnodes(w, subs.e, subs.n, &bs);
    if (!rc) {
        wr32(w->hdr + 44 + 4 * 4, idx);
        rc = opw_node_put(w, nn, bd, bs, folder);
    }
    if (!rc) {
        /* PidTagMessageSize as SCANPST computes it: the stored size of the message's data and subnode trees minus 40 bytes (measured, not estimated;
           the estimate above is only the placeholder that makes the property exist with the right width) */
        uint64_t real = 0;
        rc = opw_tree_bytes(w, bd, &real, 0);
        if (!rc && bs) rc = opw_tree_bytes(w, bs, &real, 0);
        if (!rc) {
            if (real > 40) real -= 40;
            if (real > 0x7FFFFFFF) real = 0x7FFFFFFF;
            rc = ops_set_i32(o, nn, 0x0E08, (uint32_t)real);
            if (!rc) rc = p_i32(&p, 0x0E08, (int32_t)real);
        }
    }

    /* the row in the folder's contents table: every column the table has and the message has a value for */
    if (!rc) {
        pcprops row = {0};
        for (size_t i = 0; i < p.n && !rc; i++) {
            if (p.p[i].ext_nid) continue;                       /* large values are added below, from their source */
            if ((p.p[i].ptype == 0x1F || p.p[i].ptype == 0x1E) && p.p[i].v.n == 0 && p.p[i].pid != 0x0037 && p.p[i].pid != 0x0042 && p.p[i].pid != 0x0070) continue;
            /* an empty string is no row cell (SCANPST drops it), except for subject, sender name and conversation topic: a message that has them empty gets empty cells
               (found on 9 of 1,140 mirrored messages: SCANPST's repair added exactly these cells, "row doesn't match sub-object") */
            rc = pcprops_set(&row, p.p[i].pid, p.p[i].ptype, p.p[i].v.p, p.p[i].v.n);
        }
        static const struct { unsigned pid; int which; } bigcells[2] = {{0x0E03, 0}, {0x0E04, 1}};
        for (int k = 0; k < 2 && !rc; k++) {                    /* display-to / display-cc beyond the heap limit: in the property context they are subnodes, in the row they are cells with their own subnode */
            pcprop *pp = pcprops_find(&p, bigcells[k].pid);
            if (pp && pp->ext_nid) {
                bbuf u = {0};
                rc = u8_to_u16(bigcells[k].which ? (const char *)to.p : (const char *)cc.p, &u);
                if (!rc) rc = pcprops_set(&row, bigcells[k].pid, 0x1F, u.p, u.n);
                bb_free(&u);
            }
        }
        if (!rc) rc = p_i32(&row, 0x67F2, (int32_t)nn);
        if (!rc) rc = p_i32(&row, 0x67F3, (int32_t)ops_next_row_ver(o));
        if (!rc) rc = p_bool(&row, 0x0E1B, hasatt);
        /* row-only cells Outlook writes for every message (found by running SCANPST's repair over imported files and diffing): the message
           status and a 16-byte per-row key. */
        uint8_t rkey[16];
        if (!rc && !test_hook("OPST_TEST_NO_ROWCELLS")) rc = p_i32(&row, 0x0E17, 0);
        if (!rc) rc = op_random(rkey, 16);
        rkey[7] = (uint8_t)((rkey[7] & 0x0F) | 0x40);            /* a version-4 GUID, like Outlook's own */
        rkey[8] = (uint8_t)((rkey[8] & 0x3F) | 0x80);
        if (!rc && !test_hook("OPST_TEST_NO_ROWCELLS")) rc = p_bin(&row, 0x3013, rkey, sizeof rkey);
        /* files that keep an ID map (node 0xC01, replication ids) register each message there and carry the id, change number and version
           history in the row, exactly as the copy path does */
        if (!rc && tc_col(dtc, 0x0E30) >= 0) {
            idmap *im = ops_idmap(o, &rc);
            if (!rc && im && idmap_present(im)) {
                uint8_t g[16], vh[24], cn[8];
                rc = op_random(g, 16);
                if (!rc) rc = idmap_add(im, g, nn);
                if (!rc) rc = p_bin(&row, 0x0E30, g, sizeof g);
                wr64(cn, w->bid_next_b);
                if (!rc) rc = pset(&row, 0x0E33, 0x14, cn, 8);
                uint8_t g2[16];
                if (!rc) rc = op_random(g2, 16);
                wr32(vh, 1); memcpy(vh + 4, g2, 16); wr32(vh + 20, 1);
                if (!rc) rc = p_bin(&row, 0x0E34, vh, sizeof vh);
            }
        }
        if (!rc) rc = tc_add_by_pid(dtc, nn, &row);
        if (!rc) rc = row_sync_conv_id(w, dtc, nn);
        if (!rc) {                                                   /* every other column of this folder's table gets its cell too (Outlook's rows are complete) */
            int ri = tc_find(dtc, nn);
            if (ri >= 0) { int added = row_fill_missing(dtc, (size_t)ri, &p, 1); if (added < 0) rc = -added; }
        }
        pcprops_free(&row);
    }
    if (!rc) {
        uint32_t k;
        key->nid = nn;
        key->key = msg_conv_key(w, nn, &k) ? k : 0;
    }
    if (!rc && nid_out) *nid_out = nn;
    bb_free(&to); bb_free(&cc);
    pcprops_free(&p);
    sublist_free(&subs);
    free(att_nids);
    free(att_sizes);
    return rc;
}

/* files `n` messages into `folder` with one contents-table rewrite, one folder-count update and one message-index update */
int ops_import_msgs(ops *o, uint32_t folder, const opst_import_msg *msgs, size_t n, uint32_t *nids) {
    opw *w = o->w;
    nbt_e fe;
    if ((folder & 0x1F) != 2 || opw_node(w, folder, &fe) != 0) return op_err(OPST_E_ARG, "destination 0x%x is not a folder", folder);
    if (n == 0) return 0;
    uint32_t dtc_nid = (folder & ~0x1Fu) | 0x0E;
    tctx dtc;
    int rc = ed_load_tc(w, dtc_nid, &dtc);
    if (rc) return rc;
    keyednid *keys = (keyednid *)calloc(n, sizeof *keys), *all = (keyednid *)calloc(2 * n, sizeof *all);
    if (!keys || !all) { free(keys); free(all); tc_free(&dtc); return NOMEM; }
    int unread = 0;
    for (size_t i = 0; i < n && !rc; i++) {
        rc = import_one(o, folder, &dtc, &msgs[i], &nids[i], &keys[i]);
        if (!rc && !msgs[i].read) unread++;
    }
    if (!rc) rc = ed_store_tc(w, dtc_nid, &dtc);
    if (!rc) rc = ops_counts(o, folder, (int)n, unread);
    if (!rc) {
        size_t nk = 0;
        for (size_t i = 0; i < n; i++) {
            uint32_t k2;
            if (keys[i].key) all[nk++] = keys[i];                                  /* every imported message carries a conversation index */
            if (row_conv_key(&dtc, nids[i], &k2) && k2 != keys[i].key) { all[nk].key = k2; all[nk].nid = nids[i]; nk++; }
        }
        rc = ops_note_max_message_nid(o, NULL, 0, NULL, 0, all, nk);
    }
    free(keys); free(all);
    tc_free(&dtc);
    return rc;
}
