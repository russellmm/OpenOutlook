/* op_rtf.c - Rich Text (RTF) to HTML / plain text, HTML to plain text.
 *
 * Port of pstrtf.py (RTF -> HTML keeping the formatting), pstcore.rtf_to_html_or_text (RTF that encapsulates HTML, and plain
 * text extraction) and pstsearch._strip_html.  Input bytes are treated as in the Python code: bytes >= 0x80 outside \'hh
 * escapes are Latin-1 characters, \'hh escapes use the document / font code page, \uN is Unicode. */
#include "op_internal.h"
#include "op_htmlent.inc"

/* ---- string builder (UTF-8) --------------------------------------------------------------------------------------- */
typedef struct { char *p; size_t n, cap; int bad; uint32_t hi; } sb;

static int sb_reserve(sb *b, size_t extra) {
    if (b->bad) return 0;
    if (b->n + extra + 1 > b->cap) {
        size_t nc = b->cap ? b->cap : 256;
        while (nc < b->n + extra + 1) nc *= 2;
        char *t = (char *)realloc(b->p, nc);
        if (!t) { b->bad = 1; return 0; }
        b->p = t; b->cap = nc;
    }
    return 1;
}
static void sb_raw_cp(sb *b, uint32_t cp) {
    if (!sb_reserve(b, 4)) return;
    char *o = b->p + b->n;
    if (cp < 0x80) *o++ = (char)cp;
    else if (cp < 0x800) { *o++ = (char)(0xC0 | (cp >> 6)); *o++ = (char)(0x80 | (cp & 0x3F)); }
    else if (cp < 0x10000) { *o++ = (char)(0xE0 | (cp >> 12)); *o++ = (char)(0x80 | ((cp >> 6) & 0x3F)); *o++ = (char)(0x80 | (cp & 0x3F)); }
    else { *o++ = (char)(0xF0 | (cp >> 18)); *o++ = (char)(0x80 | ((cp >> 12) & 0x3F)); *o++ = (char)(0x80 | ((cp >> 6) & 0x3F)); *o++ = (char)(0x80 | (cp & 0x3F)); }
    b->n = (size_t)(o - b->p);
    b->p[b->n] = 0;
}
static void sb_flush_hi(sb *b) { if (b->hi) { b->hi = 0; sb_raw_cp(b, 0xFFFD); } }
/* one code point; UTF-16 surrogates given as two calls are joined, lone ones become U+FFFD */
static void sb_cp(sb *b, uint32_t cp) {
    if (cp >= 0xD800 && cp < 0xDC00) { sb_flush_hi(b); b->hi = cp; return; }
    if (cp >= 0xDC00 && cp < 0xE000) {
        if (b->hi) { cp = 0x10000 + ((b->hi - 0xD800) << 10) + (cp - 0xDC00); b->hi = 0; }
        else cp = 0xFFFD;
    } else sb_flush_hi(b);
    if (cp > 0x10FFFF) cp = 0xFFFD;
    sb_raw_cp(b, cp);
}
static void sb_cat(sb *b, const char *s, size_t n) {
    sb_flush_hi(b);
    if (!n || !sb_reserve(b, n)) return;
    memcpy(b->p + b->n, s, n);
    b->n += n;
    b->p[b->n] = 0;
}
static void sb_puts(sb *b, const char *s) { sb_cat(b, s, strlen(s)); }
static void sb_putc(sb *b, char c) { sb_cat(b, &c, 1); }
static void sb_free(sb *b) { free(b->p); memset(b, 0, sizeof *b); }
/* hands the buffer over (always NUL terminated, never NULL unless out of memory) */
static char *sb_take(sb *b, size_t *outlen) {
    sb_flush_hi(b);
    if (b->bad) { sb_free(b); return NULL; }
    if (!b->p && !sb_reserve(b, 0)) { sb_free(b); return NULL; }
    if (b->bad) { sb_free(b); return NULL; }
    b->p[b->n] = 0;
    char *r = b->p;
    if (outlen) *outlen = b->n;
    memset(b, 0, sizeof *b);
    return r;
}
/* HTML escape like Python's html.escape(s, quote=True) */
static void sb_escape(sb *b, const char *s, size_t n) {
    size_t start = 0;
    for (size_t i = 0; i <= n; i++) {
        const char *rep = NULL;
        if (i < n) {
            switch (s[i]) {
            case '&': rep = "&amp;"; break;
            case '<': rep = "&lt;"; break;
            case '>': rep = "&gt;"; break;
            case '"': rep = "&quot;"; break;
            case '\'': rep = "&#x27;"; break;
            case '\t': rep = "&emsp;"; break;      /* the RTF renderer shows tabs as an em space */
            default: break;
            }
        }
        if (rep || i == n) {
            sb_cat(b, s + start, i - start);
            if (rep) sb_puts(b, rep);
            start = i + 1;
        }
    }
}

static const uint16_t rtf_cp1252[32] = {
    0x20AC, 0, 0x201A, 0x0192, 0x201E, 0x2026, 0x2020, 0x2021, 0x02C6, 0x2030, 0x0160, 0x2039, 0x0152, 0, 0x017D, 0,
    0, 0x2018, 0x2019, 0x201C, 0x201D, 0x2022, 0x2013, 0x2014, 0x02DC, 0x2122, 0x0161, 0x203A, 0x0153, 0, 0x017E, 0x0178};

static int is_alpha(int c) { return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'); }
static int is_digit(int c) { return c >= '0' && c <= '9'; }
static int hexval(int c) { return is_digit(c) ? c - '0' : (c >= 'a' && c <= 'f') ? c - 'a' + 10 : (c >= 'A' && c <= 'F') ? c - 'A' + 10 : -1; }

/* ======================================================================================================================
 * RTF with encapsulated HTML (\fromhtml1) and plain text extraction  (pstcore.rtf_to_html_or_text)
 * ==================================================================================================================== */
typedef struct { int skip, htmlrtf, in_tag, uc; } plain_state;

/* returns malloc'd UTF-8; *fromhtml tells whether the result is HTML (de-encapsulated) or plain text */
static char *rtf_plain(const uint8_t *s, size_t n, int *fromhtml, size_t *outlen) {
    sb out = {0};
    plain_state cur = {0, 0, 0, 1};
    plain_state *stack = NULL;
    size_t nstack = 0, capstack = 0;
    size_t lim = n < 2000 ? n : 2000;
    int fh = 0;
    for (size_t k = 0; k + 9 <= lim; k++) if (memcmp(s + k, "\\fromhtml", 9) == 0) { fh = 1; break; }
    int pending = 0;                       /* characters still to skip after \uN */
    size_t i = 0;
    while (i < n) {
        uint8_t c = s[i];
        int emit_ok = !cur.skip && !(fh && cur.htmlrtf);
        if (c == '{') {
            if (nstack == capstack) {
                size_t nc = capstack ? capstack * 2 : 32;
                plain_state *t = (plain_state *)realloc(stack, nc * sizeof *t);
                if (!t) { free(stack); sb_free(&out); return NULL; }
                stack = t; capstack = nc;
            }
            stack[nstack++] = cur;
            i++;
            pending = 0;
            if (i + 1 < n && s[i] == '\\' && s[i + 1] == '*') {
                size_t m = i + 2;
                while (m < n && s[m] == ' ') m++;
                if (fh && m + 8 <= n && memcmp(s + m, "\\htmltag", 8) == 0) cur.in_tag = 1;
                else if (fh && m + 9 <= n && memcmp(s + m, "\\mhtmltag", 9) == 0) cur.skip = 1;
                else cur.skip = !(fh && cur.in_tag) ? 1 : cur.skip;
            }
        } else if (c == '}') {
            if (nstack) cur = stack[--nstack];
            pending = 0;
            i++;
        } else if (c == '\\') {
            i++;
            if (i >= n) break;
            uint8_t ch = s[i];
            if (ch == '\\' || ch == '{' || ch == '}') {
                if (emit_ok) sb_putc(&out, (char)ch);
                i++;
            } else if (ch == '\'') {
                int h1 = i + 1 < n ? hexval(s[i + 1]) : -1, h2 = i + 2 < n ? hexval(s[i + 2]) : -1;
                i += 3;
                if (i > n) i = n;
                if (pending) pending--;
                else if (emit_ok && h1 >= 0 && h2 >= 0) {
                    unsigned v = (unsigned)(h1 * 16 + h2);
                    if (v < 0x80 || v >= 0xA0) sb_cp(&out, v);
                    else if (rtf_cp1252[v - 0x80]) sb_cp(&out, rtf_cp1252[v - 0x80]);
                }
            } else if (ch == '*') {
                i++;
            } else if (ch == '\r' || ch == '\n') {
                i++;
            } else {
                size_t j = i;
                while (j < n && is_alpha(s[j])) j++;
                size_t wl = j - i;
                const char *word = (const char *)s + i;
                size_t k = j;
                int has_p = 0;
                long p = 0;
                int neg = 0;
                if (k < n && (s[k] == '-' || is_digit(s[k]))) {
                    if (s[k] == '-') { neg = 1; k++; }
                    else { has_p = 1; }
                    while (k < n && is_digit(s[k])) { has_p = 1; if (p < 100000000) p = p * 10 + (s[k] - '0'); k++; }
                    if (neg) p = -p;
                }
                if (k < n && s[k] == ' ') k++;
                i = k;
#define W(x) (wl == sizeof(x) - 1 && memcmp(word, x, wl) == 0)
                if (wl == 0) {
                    /* control symbol such as \~ : nothing consumed, the next loop turn treats the character as text (as Python) */
                } else if (W("htmlrtf")) cur.htmlrtf = !(has_p && p == 0);
                else if (W("fonttbl") || W("colortbl") || W("stylesheet") || W("info") || W("pict") || W("header") || W("footer")) cur.skip = 1;
                else if (W("uc") && has_p) cur.uc = (int)p;
                else if (W("u") && has_p && !cur.skip) {
                    long v = p < 0 ? p + 65536 : p;
                    if (v > 0) sb_cp(&out, (uint32_t)v);
                    pending = cur.uc;
                } else if (!cur.skip && !fh) {
                    if (W("par") || W("line")) sb_putc(&out, '\n');
                    else if (W("tab")) sb_putc(&out, '\t');
                    else if (W("emdash")) sb_cp(&out, 0x2014);
                    else if (W("endash")) sb_cp(&out, 0x2013);
                }
#undef W
            }
        } else {
            if (c != '\r' && c != '\n') {
                if (pending) pending--;
                else if (!cur.skip && !(fh && cur.htmlrtf && !cur.in_tag)) sb_cp(&out, c);
            }
            i++;
        }
    }
    free(stack);
    if (fromhtml) *fromhtml = fh;
    return sb_take(&out, outlen);
}

/* ======================================================================================================================
 * HTML -> plain text   (pstsearch._strip_html: drop script/style, tags become a space, entities are decoded)
 * ==================================================================================================================== */
int op_ci_prefix(const char *s, size_t n, const char *lit) {
    size_t l = strlen(lit);
    if (n < l) return 0;
    for (size_t i = 0; i < l; i++) {
        char a = s[i], b = lit[i];
        if (a >= 'A' && a <= 'Z') a = (char)(a + 32);
        if (a != b) return 0;
    }
    return 1;
}

static const op_htmlent *ent_find(const char *name, size_t n) {
    size_t lo = 0, hi = sizeof op_htmlents / sizeof op_htmlents[0];
    while (lo < hi) {
        size_t mid = (lo + hi) / 2;
        const char *e = op_htmlents[mid].name;
        int c = strncmp(name, e, n);
        if (c == 0 && e[n]) c = -1;
        if (c == 0) return &op_htmlents[mid];
        if (c < 0) hi = mid; else lo = mid + 1;
    }
    return NULL;
}

/* decodes one reference starting at s[0] == '&'; returns the number of input bytes consumed (0 = not a reference) */
static size_t decode_entity(const char *s, size_t n, sb *out) {
    if (n < 3) return 0;
    if (s[1] == '#') {
        size_t i = 2;
        int hex = 0;
        if (i < n && (s[i] == 'x' || s[i] == 'X')) { hex = 1; i++; }
        size_t st = i;
        uint64_t v = 0;
        while (i < n && (hex ? hexval(s[i]) >= 0 : is_digit(s[i]))) { if (v < 0x1000000) v = v * (hex ? 16 : 10) + (uint64_t)(hex ? hexval(s[i]) : s[i] - '0'); i++; }
        if (i == st) return 0;
        if (i < n && s[i] == ';') i++;
        uint32_t cp = (uint32_t)v;
        if (v == 0 || v > 0x10FFFF || (v >= 0xD800 && v < 0xE000)) cp = 0xFFFD;
        else if (v == 0x0D) cp = '\r';
        else if (v >= 0x80 && v <= 0x9F) cp = rtf_cp1252[v - 0x80] ? rtf_cp1252[v - 0x80] : (uint32_t)v;
        if ((cp >= 1 && cp <= 8) || cp == 0xB || (cp >= 0xE && cp <= 0x1F) || (cp >= 0x7F && cp <= 0x9F) ||
            (cp >= 0xFDD0 && cp <= 0xFDEF) || (cp & 0xFFFE) == 0xFFFE) return i;     /* Python drops these */
        sb_cp(out, cp);
        return i;
    }
    size_t i = 1;
    while (i < n && i < 33 && !strchr("\t\n\f <&#;", s[i])) i++;
    size_t nl = i - 1;
    if (!nl) return 0;
    if (i < n && s[i] == ';') {
        const op_htmlent *e = ent_find(s + 1, nl);
        if (e) { sb_puts(out, e->val); return i + 1; }
    }
    for (size_t l = nl; l >= 2; l--) {          /* longest legacy name that is a prefix (valid without ';') */
        const op_htmlent *e = ent_find(s + 1, l);
        if (e && e->legacy) {
            sb_puts(out, e->val);
            return 1 + l;
        }
    }
    return 0;
}

/* pass 1: <script ...>...</script> and <style ...>...</style> blocks become one space (also inside comments, like the regex) */
static char *drop_blocks(const char *h, size_t n, size_t *outlen) {
    sb out = {0};
    size_t i = 0;
    while (i < n) {
        const char *q = (const char *)memchr(h + i, '<', n - i);
        if (!q) { sb_cat(&out, h + i, n - i); break; }
        size_t lt = (size_t)(q - h);
        sb_cat(&out, h + i, lt - i);
        const char *nm = op_ci_prefix(h + lt + 1, n - lt - 1, "script") ? "script" : op_ci_prefix(h + lt + 1, n - lt - 1, "style") ? "style" : NULL;
        size_t skip_to = 0;
        if (nm) {
            size_t nl = strlen(nm);
            for (size_t k = lt + 1 + nl; k + 3 + nl <= n; k++) {
                if (h[k] == '<' && h[k + 1] == '/' && op_ci_prefix(h + k + 2, n - k - 2, nm) && h[k + 2 + nl] == '>') { skip_to = k + 3 + nl; break; }
            }
        }
        if (skip_to) { sb_putc(&out, ' '); i = skip_to; }
        else { sb_putc(&out, '<'); i = lt + 1; }
    }
    return sb_take(&out, outlen);
}

static char *html_to_text_impl(const char *h0, size_t n0, size_t *outlen) {
    size_t n = 0;
    char *hbuf = drop_blocks(h0, n0, &n);
    if (!hbuf) return NULL;
    const char *h = hbuf;
    sb out = {0};
    size_t i = 0;
    while (i < n) {
        char c = h[i];
        if (c == '<') {
            size_t skip_to = 0;
            size_t k = i + 1;
            while (k < n && h[k] != '>') k++;
            if (k < n && k > i + 1) skip_to = k + 1;
            if (skip_to) { sb_putc(&out, ' '); i = skip_to; continue; }
            sb_putc(&out, c); i++;
        } else if (c == '&') {
            size_t used = decode_entity(h + i, n - i, &out);
            if (used) i += used; else { sb_putc(&out, c); i++; }
        } else {
            size_t j = i;
            while (j < n && h[j] != '<' && h[j] != '&') j++;
            sb_cat(&out, h + i, j - i);
            i = j;
        }
    }
    free(hbuf);
    return sb_take(&out, outlen);
}

/* ======================================================================================================================
 * RTF -> HTML with formatting (pstrtf.py)
 * ==================================================================================================================== */
typedef struct grp grp;
typedef struct tok {
    uint8_t kind;                 /* 0 text, 1 hex byte, 2 control word, 3 group */
    uint8_t has_p;
    int     p;
    char    w[33];                /* control word */
    char   *s;  size_t sl;        /* text (UTF-8) */
    grp    *g;
} tok;
struct grp { tok *t; size_t n, cap; };
enum { T_TEXT, T_HEX, T_CTRL, T_GROUP };
#define MAX_DEPTH 256

static tok *grp_add(grp *g) {
    if (g->n == g->cap) {
        size_t nc = g->cap ? g->cap * 2 : 16;
        tok *t = (tok *)realloc(g->t, nc * sizeof *t);
        if (!t) return NULL;
        g->t = t; g->cap = nc;
    }
    tok *r = &g->t[g->n++];
    memset(r, 0, sizeof *r);
    return r;
}
static void grp_free(grp *g) {
    if (!g) return;
    for (size_t i = 0; i < g->n; i++) { free(g->t[i].s); grp_free(g->t[i].g); }
    free(g->t);
    free(g);
}

typedef struct { grp *root; int oom; } tokctx;

static void tk_flush(tokctx *tc, grp *g, sb *buf) {
    if (buf->n) {
        tok *t = grp_add(g);
        if (!t) tc->oom = 1;
        else { t->kind = T_TEXT; t->s = sb_take(buf, &t->sl); if (!t->s) tc->oom = 1; }
    }
    sb_free(buf);
}

static grp *tokenize(const uint8_t *s, size_t n, int *oom) {
    tokctx tc = {NULL, 0};
    grp *stack[MAX_DEPTH + 1];
    size_t sp = 0;
    tc.root = (grp *)calloc(1, sizeof(grp));
    if (!tc.root) { *oom = 1; return NULL; }
    stack[0] = tc.root;
    sb buf = {0};
    size_t i = 0;
    while (i < n && !tc.oom) {
        uint8_t ch = s[i];
        grp *cur = stack[sp];
        if (ch == '{') {
            tk_flush(&tc, cur, &buf);
            if (sp >= MAX_DEPTH) break;               /* absurdly deep: stop here, keep what we have */
            tok *t = grp_add(cur);
            grp *g = (grp *)calloc(1, sizeof(grp));
            if (!t || !g) { free(g); tc.oom = 1; break; }
            t->kind = T_GROUP; t->g = g;
            stack[++sp] = g;
            i++;
        } else if (ch == '}') {
            tk_flush(&tc, cur, &buf);
            if (sp > 0) sp--;
            i++;
        } else if (ch == '\\') {
            i++;
            if (i >= n) break;
            uint8_t c = s[i];
            if (is_alpha(c)) {
                size_t j = i;
                while (j < n && is_alpha(s[j]) && j - i < 32) j++;
                size_t k = j;
                if (k < n && (s[k] == '-' || is_digit(s[k]))) {
                    k++;
                    while (k < n && is_digit(s[k])) k++;
                }
                int has_p = 0, p = 0;
                if (k > j && !(k == j + 1 && s[j] == '-')) {
                    has_p = 1;
                    size_t q = j;
                    int neg = 0;
                    if (s[q] == '-') { neg = 1; q++; }
                    long v = 0;
                    for (; q < k; q++) if (v < 1000000000L) v = v * 10 + (s[q] - '0');
                    p = (int)(neg ? -v : v);
                }
                if (k < n && s[k] == ' ') k++;
                tk_flush(&tc, cur, &buf);
                tok *t = grp_add(cur);
                if (!t) { tc.oom = 1; break; }
                t->kind = T_CTRL; t->has_p = (uint8_t)has_p; t->p = p;
                memcpy(t->w, s + i, j - i);
                i = k;
            } else if (c == '\'') {
                tk_flush(&tc, cur, &buf);
                int h1 = i + 1 < n ? hexval(s[i + 1]) : -1, h2 = i + 2 < n ? hexval(s[i + 2]) : -1;
                if (h1 >= 0 && h2 >= 0) {
                    tok *t = grp_add(cur);
                    if (!t) { tc.oom = 1; break; }
                    t->kind = T_HEX; t->p = h1 * 16 + h2;
                }
                i += 3;
            } else if (c == '\\' || c == '{' || c == '}') {
                sb_putc(&buf, (char)c);
                i++;
            } else if (c == '*') {
                tk_flush(&tc, cur, &buf);
                tok *t = grp_add(cur);
                if (!t) { tc.oom = 1; break; }
                t->kind = T_CTRL; t->w[0] = '*';
                i++;
            } else if (c == '~') { sb_cp(&buf, 0xA0); i++; }
            else if (c == '-') { i++; }
            else if (c == '_') { sb_cp(&buf, 0x2011); i++; }
            else if (c == '\r' || c == '\n') {
                tk_flush(&tc, cur, &buf);
                tok *t = grp_add(cur);
                if (!t) { tc.oom = 1; break; }
                t->kind = T_CTRL; memcpy(t->w, "par", 4);
                i++;
            } else i++;
        } else if (ch == '\r' || ch == '\n') {
            i++;
        } else {
            while (i < n && s[i] != '{' && s[i] != '}' && s[i] != '\\' && s[i] != '\r' && s[i] != '\n') sb_cp(&buf, s[i++]);   /* Latin-1 */
        }
    }
    if (!tc.oom) tk_flush(&tc, stack[sp], &buf);
    sb_free(&buf);
    if (tc.oom) { *oom = 1; grp_free(tc.root); return NULL; }
    return tc.root;
}

static int is_w(const tok *t, const char *w) { return t->kind == T_CTRL && strcmp(t->w, w) == 0; }

typedef struct {
    unsigned char b, i, u, s, sup, sub;
    int fs, cf, hl, f;           /* 0 = unset (f: -1 = unset) */
    int qa;                      /* 0 none, 1 center, 2 right, 3 justify */
} style;
static void style_reset(style *s) { memset(s, 0, sizeof *s); s->f = -1; }

typedef struct { int id; char name[96]; int charset; } fontent;
typedef struct { sb *c; size_t n, cap; } cellvec;

typedef struct {
    fontent *fonts; size_t nfonts, capfonts;
    uint8_t  colors[1024][3]; uint8_t color_set[1024];
    int      cp;
    sb       out; size_t out_n;
    sb       cur;
    sb       trows;
    int      row_active; cellvec cells;
    char    *href;
    uint32_t hi;                 /* pending UTF-16 high surrogate (first half of a pair written as two uN words) */
    int      oom;
} R;

static void out_begin(R *r) { if (r->out_n++) sb_putc(&r->out, '\n'); }

static void flush_table(R *r) {
    if (!r->trows.n) return;
    out_begin(r);
    sb_puts(&r->out, "<table style=\"border-collapse:collapse\">");
    sb_cat(&r->out, r->trows.p, r->trows.n);
    sb_puts(&r->out, "</table>");
    r->trows.n = 0;
    if (r->trows.p) r->trows.p[0] = 0;
}

static void cells_add(R *r) {
    if (r->cells.n == r->cells.cap) {
        size_t nc = r->cells.cap ? r->cells.cap * 2 : 8;
        sb *t = (sb *)realloc(r->cells.c, nc * sizeof(sb));
        if (!t) { r->oom = 1; return; }
        r->cells.c = t; r->cells.cap = nc;
    }
    memset(&r->cells.c[r->cells.n++], 0, sizeof(sb));
}
static void cells_clear(R *r) {
    for (size_t i = 0; i < r->cells.n; i++) sb_free(&r->cells.c[i]);
    r->cells.n = 0;
}

static void write_block(sb *dst, sb *body, int qa) {
    static const char *const al[] = {"", "center", "right", "justify"};
    if (qa) { sb_puts(dst, "<p style=\"margin:0;text-align:"); sb_puts(dst, al[qa]); sb_puts(dst, "\">"); }
    else sb_puts(dst, "<p style=\"margin:0\">");
    sb_cat(dst, body->p, body->n);
    sb_puts(dst, "</p>");
}

static void end_par(R *r, const style *st) {
    if (r->row_active) {
        if (!r->cells.n) cells_add(r);
        if (r->oom) return;
        write_block(&r->cells.c[r->cells.n - 1], &r->cur, st->qa);
    } else {
        flush_table(r);
        out_begin(r);
        write_block(&r->out, &r->cur, st->qa);
    }
    r->cur.n = 0;
    if (r->cur.p) r->cur.p[0] = 0;
}

static const fontent *find_font(const R *r, int id) {
    for (size_t i = 0; i < r->nfonts; i++) if (r->fonts[i].id == id) return &r->fonts[i];
    return NULL;
}
static void set_font(R *r, int id, const char *name, int charset, int overwrite) {
    fontent *f = (fontent *)find_font(r, id);
    if (f && !overwrite) return;
    if (!f) {
        if (r->nfonts == r->capfonts) {
            size_t nc = r->capfonts ? r->capfonts * 2 : 16;
            fontent *t = (fontent *)realloc(r->fonts, nc * sizeof *t);
            if (!t) { r->oom = 1; return; }
            r->fonts = t; r->capfonts = nc;
        }
        f = &r->fonts[r->nfonts++];
    }
    f->id = id; f->charset = charset;
    snprintf(f->name, sizeof f->name, "%s", name);
}

/* name text without ';' and surrounding whitespace */
static void clean_name(char *dst, size_t cap, const char *src) {
    size_t n = 0;
    for (const char *p = src; *p && n + 1 < cap; p++) if (*p != ';') dst[n++] = *p;
    dst[n] = 0;
    while (n && (unsigned char)dst[n - 1] <= ' ') dst[--n] = 0;
    size_t lead = 0;
    while (dst[lead] && (unsigned char)dst[lead] <= ' ') lead++;
    if (lead) memmove(dst, dst + lead, strlen(dst + lead) + 1);
}

static void parse_fonts(R *r, const grp *g) {
    for (size_t k = 1; k < g->n; k++) {
        const tok *it = &g->t[k];
        if (it->kind != T_GROUP) continue;
        int fid = -1, charset = -1;
        char raw[256]; raw[0] = 0;
        for (size_t q = 0; q < it->g->n; q++) {
            const tok *x = &it->g->t[q];
            if (is_w(x, "f") && x->has_p) fid = x->p;
            else if (is_w(x, "fcharset")) charset = x->has_p ? x->p : -1;
            else if (x->kind == T_TEXT && strlen(raw) + x->sl < sizeof raw) strcat(raw, x->s);
        }
        if (fid >= 0) { char nm[96]; clean_name(nm, sizeof nm, raw); set_font(r, fid, nm, charset, 1); }
    }
    /* fonts written without braces: \f0\fnil Arial; */
    int fid = -1, charset = -1;
    char raw[256]; raw[0] = 0;
    for (size_t k = 1; k < g->n; k++) {
        const tok *it = &g->t[k];
        if (is_w(it, "f") && it->has_p) { fid = it->p; charset = -1; raw[0] = 0; }
        else if (is_w(it, "fcharset") && fid >= 0) charset = it->has_p ? it->p : -1;
        else if (it->kind == T_TEXT && fid >= 0) {
            if (strlen(raw) + it->sl < sizeof raw) strcat(raw, it->s);
            if (strchr(raw, ';')) {
                char nm[96]; clean_name(nm, sizeof nm, raw);
                set_font(r, fid, nm, charset, 0);
                fid = -1;
            }
        }
    }
}

static void parse_colors(R *r, const grp *g) {
    int idx = 0, have = 0, rgb[3] = {0, 0, 0};
    for (size_t k = 0; k < g->n; k++) {
        const tok *it = &g->t[k];
        int c = is_w(it, "red") ? 0 : is_w(it, "green") ? 1 : is_w(it, "blue") ? 2 : -1;
        if (c >= 0) { rgb[c] = it->has_p ? it->p : 0; have = 1; }
        else if (it->kind == T_TEXT) {
            for (size_t q = 0; q < it->sl; q++) {
                if (it->s[q] != ';') continue;
                if (have && idx < 1024) {
                    for (int z = 0; z < 3; z++) r->colors[idx][z] = (uint8_t)(rgb[z] < 0 ? 0 : rgb[z] > 255 ? 255 : rgb[z]);
                    r->color_set[idx] = 1;
                }
                have = 0; rgb[0] = rgb[1] = rgb[2] = 0;
                idx++;
            }
        }
    }
}

static void prescan(R *r, const grp *items) {
    for (size_t k = 0; k < items->n; k++) {
        const tok *it = &items->t[k];
        if (is_w(it, "ansicpg") && it->has_p && it->p) r->cp = it->p;
        else if (it->kind == T_GROUP && it->g->n) {
            const grp *g = it->g;
            if (is_w(&g->t[0], "fonttbl")) parse_fonts(r, g);
            else if (is_w(&g->t[0], "colortbl")) parse_colors(r, g);
            else if (is_w(&g->t[0], "*") && g->n > 1 && is_w(&g->t[1], "colortbl")) parse_colors(r, g);
        }
    }
}

static const struct { int cs, cp; } charset_cp[] = {
    {0, 1252}, {1, 1252}, {2, 42}, {77, 10000}, {128, 932}, {129, 949}, {130, 1361}, {134, 936}, {136, 950}, {161, 1253},
    {162, 1254}, {163, 1258}, {177, 1255}, {178, 1256}, {186, 1257}, {204, 1251}, {222, 874}, {238, 1250}, {254, 437}, {255, 850}};

static const char *const skip_words[] = {
    "fonttbl", "colortbl", "stylesheet", "info", "header", "headerl", "headerr", "headerf", "footer", "footerl", "footerr",
    "footerf", "footnote", "listtable", "listoverridetable", "rsidtbl", "generator", "private", "xmlnstbl", "themedata",
    "colorschememapping", "datastore", "latentstyles", "pnseclvl", "pgdsctbl", "revtbl", "protusertbl", "userprops",
    "mmathPr", "fldinst", "bkmkstart", "bkmkend", "object", "shpinst", "shptxt", "nonshppict", "falt", NULL};

static int special_cp(const char *w) {
    static const struct { const char *w; int cp; } sp[] = {
        {"emdash", 0x2014}, {"endash", 0x2013}, {"emspace", 0x2003}, {"enspace", 0x2002}, {"bullet", 0x2022}, {"lquote", 0x2018},
        {"rquote", 0x2019}, {"ldblquote", 0x201C}, {"rdblquote", 0x201D}, {"tab", '\t'}, {"zwj", 0x200D}, {"zwnj", 0x200C},
        {"ltrmark", 0x200E}, {"rtlmark", 0x200F}, {NULL, 0}};
    for (int i = 0; sp[i].w; i++) if (!strcmp(sp[i].w, w)) return sp[i].cp;
    return 0;
}

static void span(R *r, const style *st, const char *text, size_t n) {
    sb css = {0};
    if (st->fs > 0) {
        char t[48];
        snprintf(t, sizeof t, "font-size:%d.%dpt", st->fs / 2, (st->fs & 1) ? 5 : 0);
        sb_puts(&css, t);
    }
    if (st->cf > 0 && st->cf < 1024 && r->color_set[st->cf]) {
        char t[32];
        snprintf(t, sizeof t, "%scolor:#%02x%02x%02x", css.n ? ";" : "", r->colors[st->cf][0], r->colors[st->cf][1], r->colors[st->cf][2]);
        sb_puts(&css, t);
    }
    if (st->hl > 0 && st->hl < 1024 && r->color_set[st->hl]) {
        char t[40];
        snprintf(t, sizeof t, "%sbackground:#%02x%02x%02x", css.n ? ";" : "", r->colors[st->hl][0], r->colors[st->hl][1], r->colors[st->hl][2]);
        sb_puts(&css, t);
    }
    if (st->f >= 0) {
        const fontent *f = find_font(r, st->f);
        if (f && f->name[0]) {
            if (css.n) sb_putc(&css, ';');
            sb_puts(&css, "font-family:'");
            for (const char *p = f->name; *p; p++) if (*p != '"' && *p != '\'') sb_putc(&css, *p);     /* quote-safe inside style="" */
            sb_puts(&css, "'");
        }
    }
    if (st->u || st->s) {
        if (css.n) sb_putc(&css, ';');
        sb_puts(&css, "text-decoration:");
        if (st->u) sb_puts(&css, "underline");
        if (st->u && st->s) sb_putc(&css, ' ');
        if (st->s) sb_puts(&css, "line-through");
    }
    if (css.n) {
        sb_puts(&r->cur, "<span style=\"");
        for (size_t i = 0; i < css.n; i++) {
            char c = css.p[i];
            if (c == '&') sb_puts(&r->cur, "&amp;"); else if (c == '<') sb_puts(&r->cur, "&lt;"); else if (c == '>') sb_puts(&r->cur, "&gt;");
            else sb_putc(&r->cur, c);
        }
        sb_puts(&r->cur, "\">");
    }
    if (st->sub) sb_puts(&r->cur, "<sub>");
    if (st->sup) sb_puts(&r->cur, "<sup>");
    if (st->i) sb_puts(&r->cur, "<i>");
    if (st->b) sb_puts(&r->cur, "<b>");
    sb_escape(&r->cur, text, n);
    if (st->b) sb_puts(&r->cur, "</b>");
    if (st->i) sb_puts(&r->cur, "</i>");
    if (st->sup) sb_puts(&r->cur, "</sup>");
    if (st->sub) sb_puts(&r->cur, "</sub>");
    if (css.n) sb_puts(&r->cur, "</span>");
    if (css.bad) r->oom = 1;
    sb_free(&css);
}

static void emit_cp(R *r, const style *st, uint32_t cp);
static void emit(R *r, const style *st, const char *text, size_t n) {
    if (!n) return;
    if (r->hi) { r->hi = 0; emit_cp(r, st, 0xFFFD); }       /* a lone high surrogate */
    if (r->href) {
        sb_puts(&r->cur, "<a href=\"");
        sb_escape(&r->cur, r->href, strlen(r->href));
        sb_puts(&r->cur, "\">");
        span(r, st, text, n);
        sb_puts(&r->cur, "</a>");
    } else span(r, st, text, n);
}
static void emit_cp(R *r, const style *st, uint32_t cp) {
    sb t = {0};
    sb_cp(&t, cp);
    if (t.p) emit(r, st, t.p, t.n);
    sb_free(&t);
}

/* value of a Unicode escape (0..65535): UTF-16 surrogate pairs written as two such escapes are joined */
static void emit_u(R *r, const style *st, uint32_t v) {
    if (v >= 0xD800 && v < 0xDC00) {
        if (r->hi) { r->hi = 0; emit_cp(r, st, 0xFFFD); }
        r->hi = v;
    } else if (v >= 0xDC00 && v < 0xE000) {
        if (r->hi) { uint32_t cp = 0x10000 + ((r->hi - 0xD800) << 10) + (v - 0xDC00); r->hi = 0; emit_cp(r, st, cp); }
        else emit_cp(r, st, 0xFFFD);
    } else emit_cp(r, st, v);
}

/* concatenated text of all text tokens (nested groups included) */
static void text_of(const grp *g, sb *out) {
    for (size_t i = 0; i < g->n; i++) {
        if (g->t[i].kind == T_TEXT) sb_cat(out, g->t[i].s, g->t[i].sl);
        else if (g->t[i].kind == T_GROUP) text_of(g->t[i].g, out);
    }
}

/* HYPERLINK "url" in a field instruction (case-insensitive); malloc'd or NULL */
static char *find_hyperlink(const char *s, size_t n) {
    for (size_t i = 0; i + 9 <= n; i++) {
        if (!op_ci_prefix(s + i, n - i, "hyperlink")) continue;
        size_t k = i + 9;
        size_t ws = k;
        while (k < n && (s[k] == ' ' || s[k] == '\t' || s[k] == '\r' || s[k] == '\n' || s[k] == '\f' || s[k] == '\v')) k++;
        if (k == ws) continue;
        if (k < n && s[k] == '"') k++;
        size_t st = k;
        while (k < n && s[k] != '"' && !(s[k] == ' ' || s[k] == '\t' || s[k] == '\r' || s[k] == '\n' || s[k] == '\f' || s[k] == '\v')) k++;
        if (k == st) continue;
        char *u = (char *)malloc(k - st + 1);
        if (!u) return NULL;
        memcpy(u, s + st, k - st);
        u[k - st] = 0;
        return u;
    }
    return NULL;
}

static const tok *first_ctrl(const grp *g, size_t from) {
    for (size_t i = from; i < g->n; i++) if (g->t[i].kind == T_CTRL) return &g->t[i];
    return NULL;
}

static void flush_hex(R *r, const style *st, uint8_t *hex, size_t *nhex, int skip, unsigned cp) {
    if (*nhex && !skip) {
        size_t ol = 0;
        char *t = op_ansi_to_utf8(hex, *nhex, cp == 42 ? 1252u : cp, &ol);      /* Symbol charset: read as 1252, like the Python code */
        if (t) { emit(r, st, t, ol); free(t); } else r->oom = 1;
    }
    *nhex = 0;
}

static void walk(R *r, const grp *g, style st, int depth, int skip, int uc, unsigned cur_cp) {
    int uc_skip = 0;
    uint8_t hexbuf[64]; size_t nhex = 0;
    int is_pict = 0, pict_kind = 0, pict_w = 0;
    sb pict_hex = {0};
    if (r->oom) return;
    const tok *first = g->n ? &g->t[0] : NULL;
    const tok *head = first_ctrl(g, 0);
    if (first && is_w(first, "*")) {
        const tok *nx = first_ctrl(g, 1);
        if (!(nx && !strcmp(nx->w, "shppict"))) skip = 1;          /* \*\shppict holds the real PNG/JPEG: keep it */
    }
    if (head) for (int k = 0; skip_words[k]; k++) if (!strcmp(head->w, skip_words[k])) { skip = 1; break; }
    if (head && !strcmp(head->w, "pict")) is_pict = 1;
    int is_fld = head && !strcmp(head->w, "field");

    for (size_t idx = 0; idx < g->n && !r->oom; idx++) {
        const tok *it = &g->t[idx];
        if (it->kind == T_HEX) {
            if (uc_skip) { uc_skip--; continue; }
            if (nhex == sizeof hexbuf) flush_hex(r, &st, hexbuf, &nhex, skip, cur_cp);
            hexbuf[nhex++] = (uint8_t)it->p;
            continue;
        }
        flush_hex(r, &st, hexbuf, &nhex, skip, cur_cp);
        if (it->kind == T_TEXT) {
            if (is_pict) {
                for (size_t q = 0; q < it->sl; q++) if ((unsigned char)it->s[q] > ' ') sb_putc(&pict_hex, it->s[q]);
                continue;
            }
            const char *t = it->s;
            size_t tl = it->sl;
            if (uc_skip) {                                   /* drop characters (not bytes) that stand in for the \u character */
                while (uc_skip && tl) {
                    size_t l = 1;
                    unsigned char c0 = (unsigned char)*t;
                    if (c0 >= 0xF0) l = 4; else if (c0 >= 0xE0) l = 3; else if (c0 >= 0xC0) l = 2;
                    if (l > tl) l = tl;
                    t += l; tl -= l; uc_skip--;
                }
            }
            if (!skip) emit(r, &st, t, tl);
        } else if (it->kind == T_GROUP) {
            const grp *sub = it->g;
            if (is_fld) {
                const tok *gh = first_ctrl(sub, 0);
                if (gh && !strcmp(gh->w, "*")) gh = first_ctrl(sub, 1);
                if (gh && !strcmp(gh->w, "fldinst")) {
                    sb ft = {0};
                    text_of(sub, &ft);
                    free(r->href);
                    r->href = ft.p ? find_hyperlink(ft.p, ft.n) : NULL;
                    sb_free(&ft);
                    continue;
                }
                if (gh && !strcmp(gh->w, "fldrslt")) {
                    walk(r, sub, st, depth + 1, skip, uc, cur_cp);
                    free(r->href); r->href = NULL;
                    continue;
                }
            }
            walk(r, sub, st, depth + 1, skip, uc, cur_cp);
        } else {
            const char *w = it->w;
            int p = it->p, has_p = it->has_p;
            if (!strcmp(w, "uc")) uc = has_p ? p : 1;
            else if (!strcmp(w, "u") && has_p && !skip) {
                long v = p < 0 ? (long)p + 65536 : p;
                if (v > 0) emit_u(r, &st, (uint32_t)v);
                uc_skip = uc;
            } else if (is_pict) {
                if (!strcmp(w, "pngblip")) pict_kind = 1;
                else if (!strcmp(w, "jpegblip")) pict_kind = 2;
                else if (!strcmp(w, "picwgoal") && has_p && p) pict_w = p;
            } else if (skip) continue;
            else if (!strcmp(w, "b")) st.b = !(has_p && p == 0);
            else if (!strcmp(w, "i")) st.i = !(has_p && p == 0);
            else if (!strcmp(w, "ul") || !strcmp(w, "uld") || !strcmp(w, "uldb") || !strcmp(w, "ulw")) st.u = !(has_p && p == 0);
            else if (!strcmp(w, "ulnone")) st.u = 0;
            else if (!strcmp(w, "strike") || !strcmp(w, "striked")) st.s = !(has_p && p == 0);
            else if (!strcmp(w, "super")) { st.sup = 1; st.sub = 0; }
            else if (!strcmp(w, "sub")) { st.sub = 1; st.sup = 0; }
            else if (!strcmp(w, "nosupersub")) st.sup = st.sub = 0;
            else if (!strcmp(w, "fs") && has_p && p) st.fs = p;
            else if (!strcmp(w, "cf")) st.cf = has_p ? p : 0;
            else if ((!strcmp(w, "highlight") || !strcmp(w, "chcbpat") || !strcmp(w, "cb")) && has_p && p) st.hl = p;
            else if (!strcmp(w, "f") && has_p) {
                st.f = p;
                const fontent *f = find_font(r, p);
                if (f) for (size_t k = 0; k < sizeof charset_cp / sizeof charset_cp[0]; k++) if (charset_cp[k].cs == f->charset) cur_cp = (unsigned)charset_cp[k].cp;
            } else if (!strcmp(w, "plain")) { int a = st.qa; style_reset(&st); st.qa = a; }
            else if (!strcmp(w, "pard")) st.qa = 0;
            else if (!strcmp(w, "qc")) st.qa = 1;
            else if (!strcmp(w, "qr")) st.qa = 2;
            else if (!strcmp(w, "qj")) st.qa = 3;
            else if (!strcmp(w, "ql")) st.qa = 0;
            else if (!strcmp(w, "par") || !strcmp(w, "sect")) end_par(r, &st);
            else if (!strcmp(w, "line")) sb_puts(&r->cur, "<br>");
            else if (special_cp(w)) emit_cp(r, &st, (uint32_t)special_cp(w));
            else if (!strcmp(w, "trowd")) {
                if (!r->row_active) { r->row_active = 1; cells_clear(r); cells_add(r); }
            } else if (!strcmp(w, "cell")) {
                if (r->row_active) { end_par(r, &st); cells_add(r); }
            } else if (!strcmp(w, "row")) {
                if (r->row_active) {
                    sb_puts(&r->trows, "<tr>");
                    for (size_t k = 0; k < r->cells.n; k++) {
                        if (!r->cells.c[k].n) continue;
                        sb_puts(&r->trows, "<td style=\"padding:2px 6px;vertical-align:top\">");
                        sb_cat(&r->trows, r->cells.c[k].p, r->cells.c[k].n);
                        sb_puts(&r->trows, "</td>");
                    }
                    sb_puts(&r->trows, "</tr>");
                    r->row_active = 0;
                    cells_clear(r);
                }
            }
        }
    }
    flush_hex(r, &st, hexbuf, &nhex, skip, cur_cp);
    if (is_pict && pict_kind && pict_hex.n && !skip && !(pict_hex.n & 1)) {
        size_t nb = pict_hex.n / 2;
        uint8_t *bin = (uint8_t *)malloc(nb ? nb : 1);
        int ok = bin != NULL;
        for (size_t k = 0; ok && k < nb; k++) {
            int a = hexval(pict_hex.p[2 * k]), b = hexval(pict_hex.p[2 * k + 1]);
            if (a < 0 || b < 0) ok = 0; else bin[k] = (uint8_t)(a * 16 + b);
        }
        if (ok) {
            static const char b64[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
            sb_puts(&r->cur, pict_kind == 1 ? "<img src=\"data:image/png;base64," : "<img src=\"data:image/jpeg;base64,");
            for (size_t k = 0; k < nb; k += 3) {
                uint32_t v = (uint32_t)bin[k] << 16 | (k + 1 < nb ? (uint32_t)bin[k + 1] << 8 : 0) | (k + 2 < nb ? bin[k + 2] : 0u);
                char q[4] = {b64[v >> 18 & 63], b64[v >> 12 & 63], k + 1 < nb ? b64[v >> 6 & 63] : '=', k + 2 < nb ? b64[v & 63] : '='};
                sb_cat(&r->cur, q, 4);
            }
            sb_putc(&r->cur, '"');
            if (pict_w) { char t[32]; snprintf(t, sizeof t, " width=\"%d\"", pict_w / 15 > 1 ? pict_w / 15 : 1); sb_puts(&r->cur, t); }
            sb_putc(&r->cur, '>');
        }
        free(bin);
    }
    sb_free(&pict_hex);
}

static char *rtf_render_html(const uint8_t *s, size_t n, size_t *outlen) {
    int oom = 0;
    grp *root = tokenize(s, n, &oom);
    if (!root) return NULL;
    R *r = (R *)calloc(1, sizeof *r);
    if (!r) { grp_free(root); return NULL; }
    r->cp = 1252;
    const grp *top = (root->n && root->t[0].kind == T_GROUP) ? root->t[0].g : root;
    prescan(r, top);
    style st;
    style_reset(&st);
    walk(r, top, st, 0, 0, 1, (unsigned)r->cp);
    if (r->cur.n) end_par(r, &st);
    flush_table(r);
    sb res = {0};
    sb_puts(&res, "<html><head><meta charset=\"utf-8\"></head><body style=\"font-family:sans-serif;font-size:10pt\">");
    sb_cat(&res, r->out.p, r->out.n);
    sb_puts(&res, "</body></html>");
    int bad = r->oom || r->out.bad || r->cur.bad || r->trows.bad;
    sb_free(&r->out); sb_free(&r->cur); sb_free(&r->trows);
    cells_clear(r); free(r->cells.c); free(r->fonts); free(r->href);
    free(r);
    grp_free(root);
    if (bad) { sb_free(&res); return NULL; }
    return sb_take(&res, outlen);
}

/* ======================================================================================================================
 * public API
 * ==================================================================================================================== */
static int check_args(const char *in, char **out) {
    if (!out) return op_err(OPST_E_ARG, "null argument");
    *out = NULL;
    if (!in) return op_err(OPST_E_ARG, "null argument");
    return 0;
}

int opst_rtf_to_html(const char *rtf, size_t len, char **out, size_t *outlen) {
    int rc = check_args(rtf, out);
    if (rc) return rc;
    size_t lim = len < 2000 ? len : 2000, ol = 0;
    int fh = 0;
    for (size_t k = 0; k + 9 <= lim; k++) if (memcmp(rtf + k, "\\fromhtml", 9) == 0) { fh = 1; break; }
    char *res = fh ? rtf_plain((const uint8_t *)rtf, len, NULL, &ol) : rtf_render_html((const uint8_t *)rtf, len, &ol);
    if (!res) return op_err(OPST_E_NOMEM, "out of memory");
    *out = res;
    if (outlen) *outlen = ol;
    return 0;
}

int opst_rtf_to_text(const char *rtf, size_t len, char **out, size_t *outlen) {
    int rc = check_args(rtf, out);
    if (rc) return rc;
    int fh = 0;
    size_t ol = 0;
    char *res = rtf_plain((const uint8_t *)rtf, len, &fh, &ol);
    if (!res) return op_err(OPST_E_NOMEM, "out of memory");
    if (fh) {                                      /* encapsulated HTML: strip it like an ordinary HTML body */
        size_t tl = 0;
        char *t = html_to_text_impl(res, ol, &tl);
        free(res);
        if (!t) return op_err(OPST_E_NOMEM, "out of memory");
        res = t; ol = tl;
    }
    *out = res;
    if (outlen) *outlen = ol;
    return 0;
}

int opst_html_to_text(const char *html, size_t len, char **out, size_t *outlen) {
    int rc = check_args(html, out);
    if (rc) return rc;
    size_t ol = 0;
    char *res = html_to_text_impl(html, len, &ol);
    if (!res) return op_err(OPST_E_NOMEM, "out of memory");
    *out = res;
    if (outlen) *outlen = ol;
    return 0;
}
