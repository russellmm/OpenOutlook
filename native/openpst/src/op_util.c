/* op_util.c - CRC, text conversion, case folding, compressed RTF, result lists, time helpers. */
#include "op_internal.h"

/* ---- CRC (MS-PST 5.3: reflected CRC-32, polynomial 0xEDB88320, no inversion) ----------------------------------- */
static uint32_t g_crc[256];
static int g_crc_ready;
uint32_t op_crc(const uint8_t *p, size_t n) {
    if (!g_crc_ready) {
        for (uint32_t i = 0; i < 256; i++) {
            uint32_t c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) ? (c >> 1) ^ 0xEDB88320u : c >> 1;
            g_crc[i] = c;
        }
        g_crc_ready = 1;
    }
    uint32_t crc = 0;
    for (size_t i = 0; i < n; i++) crc = g_crc[(crc ^ p[i]) & 0xFF] ^ (crc >> 8);
    return crc;
}

/* ---- UTF-8 output buffer --------------------------------------------------------------------------------------- */
typedef struct { char *p; size_t n, cap; int bad; } u8buf;
static void u8_put(u8buf *b, uint32_t cp) {
    if (b->bad) return;
    if (b->n + 5 > b->cap) {
        size_t nc = b->cap ? b->cap * 2 : 64;
        char *t = (char *)realloc(b->p, nc);
        if (!t) { b->bad = 1; return; }
        b->p = t; b->cap = nc;
    }
    char *o = b->p + b->n;
    if (cp < 0x80) { *o++ = (char)cp; }
    else if (cp < 0x800) { *o++ = (char)(0xC0 | (cp >> 6)); *o++ = (char)(0x80 | (cp & 0x3F)); }
    else if (cp < 0x10000) { *o++ = (char)(0xE0 | (cp >> 12)); *o++ = (char)(0x80 | ((cp >> 6) & 0x3F)); *o++ = (char)(0x80 | (cp & 0x3F)); }
    else { *o++ = (char)(0xF0 | (cp >> 18)); *o++ = (char)(0x80 | ((cp >> 12) & 0x3F)); *o++ = (char)(0x80 | ((cp >> 6) & 0x3F)); *o++ = (char)(0x80 | (cp & 0x3F)); }
    b->n = (size_t)(o - b->p);
}
static char *u8_done(u8buf *b, size_t *outlen) {
    if (b->bad) { free(b->p); return NULL; }
    if (!b->p) { b->p = (char *)malloc(1); if (!b->p) return NULL; }
    else if (b->n + 1 > b->cap) { char *t = (char *)realloc(b->p, b->n + 1); if (!t) { free(b->p); return NULL; } b->p = t; }
    b->p[b->n] = 0;
    if (outlen) *outlen = b->n;
    return b->p;
}

char *op_utf16_to_utf8(const uint8_t *s, size_t nbytes, size_t *outlen) {
    u8buf b = {0, 0, 0, 0};
    size_t n = nbytes / 2;
    for (size_t i = 0; i < n; i++) {
        uint32_t c = op_u16(s + 2 * i);
        if (c >= 0xD800 && c < 0xDC00) {
            if (i + 1 < n) {
                uint32_t d = op_u16(s + 2 * (i + 1));
                if (d >= 0xDC00 && d < 0xE000) { c = 0x10000 + ((c - 0xD800) << 10) + (d - 0xDC00); i++; }
                else c = 0xFFFD;
            } else c = 0xFFFD;
        } else if (c >= 0xDC00 && c < 0xE000) c = 0xFFFD;
        u8_put(&b, c);
    }
    return u8_done(&b, outlen);
}

static const uint16_t cp1252_hi[32] = {
    0x20AC, 0xFFFD, 0x201A, 0x0192, 0x201E, 0x2026, 0x2020, 0x2021, 0x02C6, 0x2030, 0x0160, 0x2039, 0x0152, 0xFFFD, 0x017D, 0xFFFD,
    0xFFFD, 0x2018, 0x2019, 0x201C, 0x201D, 0x2022, 0x2013, 0x2014, 0x02DC, 0x2122, 0x0161, 0x203A, 0x0153, 0xFFFD, 0x017E, 0x0178};

/* copy UTF-8, replacing invalid sequences by U+FFFD */
static char *utf8_sanitize(const uint8_t *s, size_t n, size_t *outlen) {
    u8buf b = {0, 0, 0, 0};
    size_t i = 0;
    while (i < n) {
        uint8_t c = s[i];
        if (c < 0x80) { u8_put(&b, c); i++; continue; }
        int len = (c >= 0xC2 && c <= 0xDF) ? 2 : (c >= 0xE0 && c <= 0xEF) ? 3 : (c >= 0xF0 && c <= 0xF4) ? 4 : 0;
        uint32_t cp = 0;
        int ok = len && i + (size_t)len <= n;
        if (ok) {
            cp = len == 2 ? (c & 0x1Fu) : len == 3 ? (c & 0x0Fu) : (c & 0x07u);
            for (int k = 1; k < len; k++) {
                if ((s[i + (size_t)k] & 0xC0) != 0x80) { ok = 0; break; }
                cp = (cp << 6) | (s[i + (size_t)k] & 0x3Fu);
            }
            if (ok && ((len == 3 && cp < 0x800) || (len == 4 && (cp < 0x10000 || cp > 0x10FFFF)) || (cp >= 0xD800 && cp < 0xE000))) ok = 0;
        }
        if (ok) { u8_put(&b, cp); i += (size_t)len; } else { u8_put(&b, 0xFFFD); i++; }
    }
    return u8_done(&b, outlen);
}

char *op_ansi_to_utf8(const uint8_t *s, size_t n, unsigned cp, size_t *outlen) {
    if (cp == 65001) return utf8_sanitize(s, n, outlen);
    if (cp == 0 || cp == 1252 || cp == 28591 || cp == 20105 || cp == 20127 || cp == 819) {
        u8buf b = {0, 0, 0, 0};
        for (size_t i = 0; i < n; i++) {
            uint32_t c = s[i];
            if (c >= 0x80) {
                if (cp == 20127) c = 0xFFFD;
                else if ((cp == 0 || cp == 1252) && c < 0xA0) c = cp1252_hi[c - 0x80];
            }
            u8_put(&b, c);
        }
        return u8_done(&b, outlen);
    }
    char *r = op_os_ansi_to_utf8(s, n, cp, outlen);
    if (r) return r;
    return op_ansi_to_utf8(s, n, 1252, outlen);
}

/* ---- case folding (Latin, Greek, Cyrillic) --------------------------------------------------------------------- */
uint32_t op_lower(uint32_t c) {
    if (c < 0x80) return (c >= 'A' && c <= 'Z') ? c + 32 : c;
    if (c >= 0xC0 && c <= 0xDE && c != 0xD7) return c + 32;
    if (c >= 0x100 && c <= 0x137) return (c & 1) ? c : c + 1;
    if (c >= 0x139 && c <= 0x148) return (c & 1) ? c + 1 : c;
    if (c >= 0x14A && c <= 0x177) return (c & 1) ? c : c + 1;
    if (c == 0x178) return 0xFF;
    if (c >= 0x179 && c <= 0x17E) return (c & 1) ? c + 1 : c;
    if (c >= 0x391 && c <= 0x3A9 && c != 0x3A2) return c + 32;
    if (c >= 0x410 && c <= 0x42F) return c + 32;
    if (c >= 0x400 && c <= 0x40F) return c + 80;
    return c;
}

static uint32_t next_cp(const unsigned char **ps) {
    const unsigned char *s = *ps;
    uint32_t c = *s;
    int len = c < 0x80 ? 1 : (c & 0xE0) == 0xC0 ? 2 : (c & 0xF0) == 0xE0 ? 3 : (c & 0xF8) == 0xF0 ? 4 : 1;
    if (len == 1) { *ps = s + 1; return c < 0x80 ? c : 0xFFFD; }
    c &= (len == 2 ? 0x1Fu : len == 3 ? 0x0Fu : 0x07u);
    int k = 1;
    for (; k < len && (s[k] & 0xC0) == 0x80; k++) c = (c << 6) | (s[k] & 0x3Fu);
    *ps = s + k;
    return k == len ? c : 0xFFFD;
}

int op_stricmp_utf8(const char *a, const char *b) {
    const unsigned char *x = (const unsigned char *)a, *y = (const unsigned char *)b;
    for (;;) {
        if (!*x || !*y) return (*x ? 1 : 0) - (*y ? 1 : 0);
        uint32_t c = op_lower(next_cp(&x)), d = op_lower(next_cp(&y));
        if (c != d) return c < d ? -1 : 1;
    }
}

/* ---- compressed RTF (MS-OXRTFCP, "LZFu") ---------------------------------------------------------------------- */
/* the specified initial dictionary is 207 bytes; the trailing three bytes are zero */
static const char lz_init[207] =
    "{\\rtf1\\ansi\\mac\\deff0\\deftab720{\\fonttbl;}{\\f0\\fnil \\froman \\fswiss \\fmodern "
    "\\fscript \\fdecor MS Sans SerifSymbolArialTimes New RomanCourier{\\colortbl\\red0\\green0"
    "\\blue0\r\n\\par \\pard\\plain\\f0\\fs20\\b\\i\\ul\\tx";

int op_lzfu(const uint8_t *in, size_t n, uint8_t **out, size_t *outlen) {
    uint8_t *o = NULL;
    size_t on = 0, ocap = 0;
    *out = NULL; *outlen = 0;
    if (n < 16) { o = (uint8_t *)malloc(1); if (!o) return OPST_E_NOMEM; *out = o; return 0; }
    uint32_t csize = op_u32(in), rsize = op_u32(in + 4), magic = op_u32(in + 8);
    if (magic == 0x414C454Du) {                                    /* "MELA": stored uncompressed */
        size_t len = rsize;
        if (len > n - 16) len = n - 16;
        o = (uint8_t *)malloc(len + 1);
        if (!o) return OPST_E_NOMEM;
        memcpy(o, in + 16, len);
        *out = o; *outlen = len;
        return 0;
    }
    if (magic != 0x75465A4Cu) return op_err(OPST_E_FORMAT, "bad compressed RTF signature");
    if (rsize > (512u << 20)) return op_err(OPST_E_FORMAT, "compressed RTF too large");
    ocap = (size_t)rsize + 64;
    o = (uint8_t *)malloc(ocap);
    uint8_t *d = (uint8_t *)calloc(1, 4096);
    if (!o || !d) { free(o); free(d); return op_err(OPST_E_NOMEM, "out of memory"); }
    memcpy(d, lz_init, 207);
    unsigned wp = 207;
    size_t p = 16;
    size_t end = (size_t)csize + 4 < n ? (size_t)csize + 4 : n;
    int done = 0;
#define EMIT(ch) do { if (on + 1 > ocap) { size_t nc = ocap * 2; uint8_t *t = (uint8_t *)realloc(o, nc); \
                      if (!t) { free(o); free(d); return op_err(OPST_E_NOMEM, "out of memory"); } o = t; ocap = nc; } \
                      o[on++] = (uint8_t)(ch); } while (0)
    while (p < end && !done) {
        unsigned ctl = in[p++];
        for (int bit = 0; bit < 8; bit++) {
            if (ctl & (1u << bit)) {
                if (p + 2 > n) { done = 1; break; }
                unsigned hi = in[p], lo = in[p + 1];
                p += 2;
                unsigned off = (hi << 4) | (lo >> 4), ln = (lo & 0xF) + 2;
                if (off == wp) { done = 1; break; }
                for (unsigned k = 0; k < ln; k++) {
                    uint8_t c = d[off];
                    EMIT(c);
                    d[wp] = c;
                    wp = (wp + 1) & 4095;
                    off = (off + 1) & 4095;
                }
            } else {
                if (p >= end) break;
                uint8_t c = in[p++];
                EMIT(c);
                d[wp] = c;
                wp = (wp + 1) & 4095;
            }
        }
    }
#undef EMIT
    free(d);
    *out = o; *outlen = on;
    return 0;
}

/* ---- result lists ----------------------------------------------------------------------------------------------- */
int op_list_init(oplist *l, size_t esz) {
    memset(l, 0, sizeof *l);
    l->esz = esz;
    l->cap = 16;
    l->base = (uint8_t *)calloc(1, 16 + l->cap * esz);
    l->pcap = 256;
    l->pool = (char *)malloc(l->pcap);
    if (!l->base || !l->pool) { free(l->base); free(l->pool); memset(l, 0, sizeof *l); return OPST_E_NOMEM; }
    l->pool[0] = 0;
    l->plen = 1;
    return 0;
}
void *op_list_push(oplist *l) {
    if (l->n == l->cap) {
        size_t nc = l->cap * 2;
        uint8_t *t = (uint8_t *)realloc(l->base, 16 + nc * l->esz);
        if (!t) return NULL;
        l->base = t;
        memset(l->base + 16 + l->cap * l->esz, 0, (nc - l->cap) * l->esz);
        l->cap = nc;
    }
    return l->base + 16 + (l->n++) * l->esz;
}
size_t op_list_str(oplist *l, const char *s, size_t n) {
    if (!s || n == 0) return 0;
    if (l->plen + n + 1 > l->pcap) {
        size_t nc = l->pcap * 2;
        while (nc < l->plen + n + 1) nc *= 2;
        char *t = (char *)realloc(l->pool, nc);
        if (!t) return 0;
        l->pool = t; l->pcap = nc;
    }
    size_t off = l->plen;
    memcpy(l->pool + off, s, n);
    l->pool[off + n] = 0;
    l->plen = off + n + 1;
    return off;
}
void *op_list_finish(oplist *l) {
    char *pool = l->pool;
    memcpy(l->base, &pool, sizeof pool);
    void *r = l->base + 16;
    memset(l, 0, sizeof *l);
    return r;
}
void op_list_abort(oplist *l) { free(l->base); free(l->pool); memset(l, 0, sizeof *l); }
void op_arr_free(void *arr) {
    if (!arr) return;
    uint8_t *base = (uint8_t *)arr - 16;
    char *pool;
    memcpy(&pool, base, sizeof pool);
    free(pool);
    free(base);
}

void opst_free(void *p) { free(p); }

/* ---- time ------------------------------------------------------------------------------------------------------- */
static int64_t floordiv(int64_t a, int64_t b) { int64_t q = a / b; if ((a % b != 0) && ((a < 0) != (b < 0))) q--; return q; }

int64_t opst_filetime_to_unix(int64_t ft) {
    if (ft == 0) return 0;
    return floordiv(ft, 10000000) - 11644473600LL;
}

int opst_filetime_to_iso(int64_t ft, char *buf, size_t cap) {
    if (!buf || cap < 21) return OPST_E_ARG;
    if (ft == 0) { buf[0] = 0; return 0; }
    int64_t t = opst_filetime_to_unix(ft);
    int64_t days = floordiv(t, 86400);
    int64_t sod = t - days * 86400;
    days += 719468;
    int64_t era = floordiv(days, 146097);
    int64_t doe = days - era * 146097;
    int64_t yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
    int64_t y = yoe + era * 400;
    int64_t doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    int64_t mp = (5 * doy + 2) / 153;
    int64_t d = doy - (153 * mp + 2) / 5 + 1;
    int64_t m = mp < 10 ? mp + 3 : mp - 9;
    if (m <= 2) y++;
    snprintf(buf, cap, "%04d-%02d-%02dT%02d:%02d:%02dZ", (int)y, (int)m, (int)d, (int)(sod / 3600), (int)(sod % 3600 / 60), (int)(sod % 60));
    return 0;
}
