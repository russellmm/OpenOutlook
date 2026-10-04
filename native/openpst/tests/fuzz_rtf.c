/* fuzz_rtf.c - robustness test for the RTF / HTML converters: takes the RTF (and HTML) bodies of a real PST file, damages them
 * in random ways (bit flips, truncation, inserted braces / backslashes / control words, duplicated stretches) and feeds the
 * result to the converters. Build with -DOPST_SANITIZE=ON; any memory error or hang is a failure.
 * usage: fuzz_rtf FILE.pst [mutations_per_body (default 20)] [max_bodies (default 300)] */
#include "openpst.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static uint64_t rng = 0x9E3779B97F4A7C15ull;
static uint32_t rnd(void) { rng ^= rng << 13; rng ^= rng >> 7; rng ^= rng << 17; return (uint32_t)(rng >> 16); }

static const char *const frag[] = {"{", "}", "\\", "\\*", "\\'", "\\'zz", "\\u", "\\u-1?", "\\u55357?\\u56842?", "\\pict\\pngblip ", "\\field", "\\fldinst HYPERLINK \"",
                                   "\\trowd", "\\cell", "\\row", "\\fonttbl", "\\colortbl;\\red1\\green2\\blue", "\\f9999999999", "\\fs-5", "\\uc99", "\\htmlrtf", "\\*\\htmltag ",
                                   "\\fromhtml1", "<", ">", "&#", "&#x110000;", "&", "<script>", "</style>", "\r\n", "\xFF\xFE"};

static size_t mutate(char *buf, size_t n, size_t cap) {
    int k = 1 + (int)(rnd() % 4);
    while (k--) {
        if (n == 0) return 0;
        switch (rnd() % 5) {
        case 0: buf[rnd() % n] ^= (char)(1u << (rnd() % 8)); break;
        case 1: n = rnd() % (n + 1); break;                                     /* truncate */
        case 2: {                                                              /* insert a fragment */
            const char *f = frag[rnd() % (sizeof frag / sizeof frag[0])];
            size_t fl = strlen(f), at = rnd() % (n + 1);
            if (n + fl < cap) { memmove(buf + at + fl, buf + at, n - at); memcpy(buf + at, f, fl); n += fl; }
            break;
        }
        case 3: {                                                              /* delete a stretch */
            size_t at = rnd() % n, l = 1 + rnd() % 40;
            if (at + l > n) l = n - at;
            memmove(buf + at, buf + at + l, n - at - l); n -= l;
            break;
        }
        default: {                                                             /* duplicate a stretch */
            size_t at = rnd() % n, l = 1 + rnd() % 200;
            if (at + l > n) l = n - at;
            if (n + l < cap) { memmove(buf + at + l, buf + at, n - at); n += l; }
            break;
        }
        }
    }
    return n;
}

static void feed(const char *s, size_t n) {
    char *o; size_t ol;
    if (opst_rtf_to_html(s, n, &o, &ol) == 0) opst_free(o);
    if (opst_rtf_to_text(s, n, &o, &ol) == 0) opst_free(o);
    if (opst_html_to_text(s, n, &o, &ol) == 0) opst_free(o);
}

static long bodies_done, runs;
static long max_bodies;
static int per_body;

static void one(const char *b, size_t n) {
    size_t cap = n * 2 + 1024;
    char *w = (char *)malloc(cap);
    if (!w) return;
    for (int i = 0; i < per_body; i++) {
        memcpy(w, b, n);
        size_t m = mutate(w, n, cap);
        feed(w, m);
        runs++;
    }
    feed(b, n);
    free(w);
    bodies_done++;
}

static void walk(opst *p, uint32_t folder) {
    opst_msg_row *rows; size_t n;
    if (opst_messages(p, folder, &rows, &n) == 0) {
        for (size_t i = 0; i < n && bodies_done < max_bodies; i++) {
            opst_msg *m;
            if (opst_msg_open(p, rows[i].nid, &m) != 0) continue;
            size_t len;
            const char *b = opst_msg_body(m, OPST_BODY_RTF, &len);
            if (b && len) one(b, len);
            else if ((b = opst_msg_body(m, OPST_BODY_HTML, &len)) != NULL && len && (rnd() % 4) == 0) one(b, len);
            opst_msg_close(m);
        }
        opst_free_messages(rows);
    }
    opst_folder_info *kids; size_t nk;
    if (opst_folder_children(p, folder, &kids, &nk) == 0) {
        for (size_t i = 0; i < nk && bodies_done < max_bodies; i++) walk(p, kids[i].nid);
        opst_free_folders(kids);
    }
}

int main(int argc, char **argv) {
    if (argc < 2) { fputs("usage: fuzz_rtf FILE.pst [mutations] [bodies]\n", stderr); return 2; }
    per_body = argc > 2 ? atoi(argv[2]) : 20;
    max_bodies = argc > 3 ? atol(argv[3]) : 300;
    opst *p;
    if (opst_open(argv[1], OPST_OPEN_READONLY, &p)) { fprintf(stderr, "%s\n", opst_last_error()); return 1; }
    walk(p, opst_root_folder(p));
    opst_close(p);
    printf("fuzz_rtf: %ld bodies, %ld mutated runs, no crash\n", bodies_done, runs);
    return 0;
}
