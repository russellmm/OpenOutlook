/* test_basic.c - self-checks for the OpenPST library. Set OPST_TEST_PST=file.pst to also run the file based checks. */
#include "../src/op_internal.h"
#include <assert.h>

static int fails, checks;
#define CHECK(c) do { checks++; if (!(c)) { fails++; printf("FAIL line %d: %s\n", __LINE__, #c); } } while (0)

int main(void) {
    char iso[32];
    /* time conversion */
    CHECK(opst_filetime_to_unix(132550353630000000LL) == 1610561763LL);
    opst_filetime_to_iso(132550353630000000LL, iso, sizeof iso);
    CHECK(strcmp(iso, "2021-01-13T18:16:03Z") == 0);
    opst_filetime_to_iso(116444735990000000LL - 10000000LL, iso, sizeof iso);
    CHECK(strcmp(iso, "1969-12-31T23:59:58Z") == 0);
    opst_filetime_to_iso(0, iso, sizeof iso);
    CHECK(iso[0] == 0);

    /* text conversion */
    {
        const uint8_t u16[] = {'A', 0, 0xE9, 0, 0x3D, 0xD8, 0x00, 0xDE, 0x00, 0xD8, 'z', 0};   /* A é U+1F600 lone-high z */
        size_t n;
        char *s = op_utf16_to_utf8(u16, sizeof u16, &n);
        CHECK(s && strcmp(s, "A\xC3\xA9\xF0\x9F\x98\x80\xEF\xBF\xBDz") == 0);
        free(s);
        const uint8_t a[] = {'o', 'k', 0x80, 0xE9, 0x81};                       /* cp1252: euro, e-acute, undefined */
        s = op_ansi_to_utf8(a, sizeof a, 1252, &n);
        CHECK(s && strcmp(s, "ok\xE2\x82\xAC\xC3\xA9\xEF\xBF\xBD") == 0);
        free(s);
        const uint8_t bad[] = {'a', 0xC3, 'b', 0xF0, 0x9F};
        s = op_ansi_to_utf8(bad, sizeof bad, 65001, &n);
        CHECK(s && strcmp(s, "a\xEF\xBF\xBD" "b\xEF\xBF\xBD\xEF\xBF\xBD") == 0);
        free(s);
    }
    CHECK(op_stricmp_utf8("Inbox", "INBOX") == 0);
    CHECK(op_stricmp_utf8("\xC3\x89t\xC3\xA9", "\xC3\xA9T\xC3\x89") == 0);
    CHECK(op_stricmp_utf8("a", "b") < 0);
    CHECK(op_stricmp_utf8("ab", "a") > 0);

    /* compressed RTF: stored (MELA) form and a hand made LZFu stream */
    {
        uint8_t mela[16 + 5] = {0};
        mela[0] = 21; mela[4] = 5; mela[8] = 'M'; mela[9] = 'E'; mela[10] = 'L'; mela[11] = 'A';
        memcpy(mela + 16, "hello", 5);
        uint8_t *o; size_t n;
        CHECK(op_lzfu(mela, sizeof mela, &o, &n) == 0 && n == 5 && memcmp(o, "hello", 5) == 0);
        free(o);
        /* literal 'a', then a back reference into the initial dictionary: "{\rtf1" (offset 0, length 6) */
        uint8_t lz[] = {0,0,0,0, 0,0,0,0, 'L','Z','F','u', 0,0,0,0, 0x02, 'a', 0x00, 0x04};
        lz[0] = (uint8_t)(sizeof lz - 4); lz[4] = 7;
        CHECK(op_lzfu(lz, sizeof lz, &o, &n) == 0 && n == 7 && memcmp(o, "a{\\rtf1", 7) == 0);
        free(o);
        CHECK(op_lzfu((const uint8_t *)"0123456789abcdefXXXXXXXX", 24, &o, &n) == OPST_E_FORMAT);
    }

    /* search folding: case, accents, compatibility forms */
    {
        size_t n;
        const char *t1 = "Caf\xC3\xA9 \xC3\x9F\xC3\x89" "COLE \xEF\xBC\xA1\xEF\xBC\xA2 e\xCC\x81";   /* Cafe ss ECOLE (fullwidth AB) e + combining acute */
        char *a = op_fold_text(t1, strlen(t1), &n);
        CHECK(a && strcmp(a, "cafe ssecole ab e") == 0);
        free(a);
        const char *t2 = "\xCE\x86\xCE\xBB\xCF\x86\xCE\xB1 \xD0\x99\xD0\x9E\xD0\x95\xD0\x9B\xD0\x9A\xD0\x90 \xD1\x91";   /* Greek with tonos, Cyrillic */
        a = op_fold_text(t2, strlen(t2), &n);
        CHECK(a && strcmp(a, "\xCE\xB1\xCE\xBB\xCF\x86\xCE\xB1 \xD0\xB8\xD0\xBE\xD0\xB5\xD0\xBB\xD0\xBA\xD0\xB0 \xD0\xB5") == 0);
        free(a);
    }

    /* RTF -> HTML / text, HTML -> text */
    {
        char *o; size_t n;
        const char *rtf = "{\\rtf1\\ansi\\ansicpg1252{\\fonttbl{\\f0\\fswiss\\fcharset0 Arial;}}{\\colortbl ;\\red255\\green0\\blue0;}"
                          "\\pard\\qc\\f0\\fs24 Hello \\b bold\\b0  \\cf1 red\\cf0  caf\\'e9 \\u8364? \\u-10179?\\u-8694? <&>\\par second line\\line next\\par}";
        CHECK(opst_rtf_to_html(rtf, strlen(rtf), &o, &n) == 0);
        CHECK(strstr(o, "<p style=\"margin:0;text-align:center\">") != NULL);
        CHECK(strstr(o, "<b>bold</b>") != NULL);
        CHECK(strstr(o, "color:#ff0000") != NULL && strstr(o, "font-family:'Arial'") != NULL && strstr(o, "font-size:12.0pt") != NULL);
        /* e-acute, euro, U+1F60A (a surrogate pair written as two escapes); each run is its own span */
        CHECK(strstr(o, ">\xC3\xA9</span>") != NULL && strstr(o, ">\xE2\x82\xAC</span>") != NULL && strstr(o, ">\xF0\x9F\x98\x8A</span>") != NULL);
        CHECK(strstr(o, "&lt;&amp;&gt;") != NULL && strstr(o, "second line</span><br><span") != NULL);
        opst_free(o);
        CHECK(opst_rtf_to_text(rtf, strlen(rtf), &o, &n) == 0);
        CHECK(strstr(o, "Hello bold red caf\xC3\xA9 \xE2\x82\xAC ") != NULL && strstr(o, "<&>\nsecond line\nnext\n") != NULL);
        opst_free(o);
        /* RTF that encapsulates HTML */
        const char *enc = "{\\rtf1\\ansi\\fromhtml1 \\deff0{\\*\\htmltag64}{\\*\\htmltag0 <p>}\\htmlrtf Hello \\htmlrtf0 world &amp; co{\\*\\htmltag0 </p>}}";
        CHECK(opst_rtf_to_html(enc, strlen(enc), &o, &n) == 0 && strstr(o, "<p>world &amp; co</p>") != NULL);
        opst_free(o);
        CHECK(opst_rtf_to_text(enc, strlen(enc), &o, &n) == 0 && strstr(o, "world & co") != NULL && !strchr(o, '<'));
        opst_free(o);
        const char *hs = "<style>x{}</style>a<b>b</b>&eacute;&#233;&#x41;&nbsp;&bogus; <script>1</script>c";
        CHECK(opst_html_to_text(hs, strlen(hs), &o, &n) == 0);
        CHECK(strcmp(o, " a b \xC3\xA9\xC3\xA9" "A\xC2\xA0&bogus;  c") == 0);
        opst_free(o);
        CHECK(opst_rtf_to_html("", 0, &o, &n) == 0 && strstr(o, "<body") != NULL);     /* never crashes on junk */
        opst_free(o);
        const char *junk = "{{{\\b \\u \\' \\*\\x {\\pict\\pngblip zz} \\field{\\*\\fldinst HYPERLINK \"http://x\"}{\\fldrslt link}}";
        CHECK(opst_rtf_to_html(junk, strlen(junk), &o, &n) == 0);
        opst_free(o);
        CHECK(opst_rtf_to_html(NULL, 0, &o, &n) == OPST_E_ARG);
    }

    /* result lists */
    {
        oplist l;
        CHECK(op_list_init(&l, sizeof(opst_recipient)) == 0);
        for (int i = 0; i < 100; i++) {
            char t[16];
            snprintf(t, sizeof t, "name%d", i);
            size_t off = op_list_str(&l, t, strlen(t));
            opst_recipient *r = (opst_recipient *)op_list_push(&l);
            r->name = (const char *)(uintptr_t)off; r->email = (const char *)(uintptr_t)op_list_str(&l, "", 0); r->type = i;
        }
        for (size_t i = 0; i < l.n; i++) { opst_recipient *r = OP_LIST_AT(&l, opst_recipient, i); OP_FIX(&l, r->name); OP_FIX(&l, r->email); }
        size_t n = l.n;
        opst_recipient *arr = (opst_recipient *)op_list_finish(&l);
        CHECK(n == 100 && strcmp(arr[99].name, "name99") == 0 && arr[7].email[0] == 0 && arr[50].type == 50);
        opst_free_recipients(arr);
    }

    /* errors */
    {
        opst *p = (opst *)1;
        CHECK(opst_open("/nonexistent/none.pst", OPST_OPEN_READONLY, &p) == OPST_E_IO && p == NULL);
        CHECK(opst_open(NULL, 0, &p) == OPST_E_ARG);
        CHECK(opst_last_error()[0] != 0);
    }

    /* file based checks */
    const char *path = getenv("OPST_TEST_PST");
    if (path && *path) {
        opst *p;
        CHECK(opst_open(path, OPST_OPEN_READONLY, &p) == 0);
        if (p) {
            CHECK(opst_display_name(p)[0] != 0);
            CHECK(opst_ipm_root(p) != 0);
            opst_folder_info *kids; size_t nk;
            CHECK(opst_folder_children(p, opst_ipm_root(p), &kids, &nk) == 0 && nk > 0);
            size_t total = 0;
            for (size_t i = 0; i < nk; i++) {
                opst_msg_row *rows; size_t nr;
                CHECK(opst_messages(p, kids[i].nid, &rows, &nr) == 0);
                total += nr;
                for (size_t k = 0; k < nr && k < 3; k++) {
                    opst_msg *m;
                    CHECK(opst_msg_open(p, rows[k].nid, &m) == 0);
                    if (m) {
                        const char *subj = opst_msg_str(m, OPST_PID_SUBJECT);
                        CHECK(strcmp(subj ? subj : "", rows[k].subject) == 0);
                        opst_msg_close(m);
                    }
                }
                opst_free_messages(rows);
            }
            CHECK(total > 0);
            uint32_t nid;
            CHECK(opst_folder_find(p, 0, "no/such/folder", &nid) == OPST_E_NOTFOUND);
            CHECK(opst_folder_find(p, 0, "", &nid) == 0 && nid == opst_root_folder(p));
            CHECK(opst_folder_find(p, opst_ipm_root(p), kids[0].name, &nid) == 0 && nid == kids[0].nid);
            opst_free_folders(kids);
            /* search: everything / nothing / limit / bad date / cancel */
            {
                opst_hit *h; size_t nh, nh2;
                CHECK(opst_search(p, "zzzNoSuchWord123", NULL, &h, &nh) == 0 && nh == 0);
                opst_free_hits(h);
                CHECK(opst_search(p, "", NULL, &h, &nh) == 0 && nh == 0);
                CHECK(opst_search(p, "after:2020-13-45", NULL, &h, &nh) == OPST_E_ARG && strstr(opst_last_error(), "bad date") != NULL);
                CHECK(opst_search(p, "is:read", NULL, &h, &nh) == 0);
                size_t reads = nh;
                opst_free_hits(h);
                CHECK(opst_search(p, "is:unread", NULL, &h, &nh2) == 0);
                opst_free_hits(h);
                CHECK(reads + nh2 >= total);                      /* every message of the top level folders is read or unread */
                opst_search_opts o;
                memset(&o, 0, sizeof o);
                o.size = sizeof o; o.limit = 2;
                CHECK(opst_search(p, "is:read", &o, &h, &nh) == 0 && nh == (reads < 2 ? reads : 2));
                if (nh) {
                    CHECK(h[0].folder_path[0] != 0 && h[0].subject != NULL && h[0].sender != NULL);
                    opst_msg *m;
                    CHECK(opst_msg_open(p, h[0].nid, &m) == 0);
                    const char *subj = opst_msg_str(m, OPST_PID_SUBJECT);
                    /* the subject of the first hit finds that message again (as a phrase) */
                    if (subj && *subj && !strchr(subj, '"')) {
                        char q[600];
                        snprintf(q, sizeof q, "subject:\"%.500s\"", subj);
                        opst_hit *h2; size_t n2;
                        CHECK(opst_search(p, q, NULL, &h2, &n2) == 0 && n2 >= 1);
                        int found = 0;
                        for (size_t i = 0; i < n2; i++) if (h2[i].nid == h[0].nid) found = 1;
                        CHECK(found);
                        opst_free_hits(h2);
                    }
                    size_t tl; const char *txt = opst_msg_text(m, &tl);
                    CHECK(txt == NULL || strlen(txt) == tl);
                    opst_msg_close(m);
                }
                opst_free_hits(h);
                volatile int32_t stop = 1;
                memset(&o, 0, sizeof o);
                o.size = sizeof o; o.cancel = &stop;
                CHECK(opst_search(p, "is:read", &o, &h, &nh) == 0 && nh == 0);
                opst_free_hits(h);
                memset(&o, 0, sizeof o);
                o.size = 4;                                       /* too small to be valid */
                CHECK(opst_search(p, "x", &o, &h, &nh) == OPST_E_ARG);
            }
            opst_verify_report rep;
            CHECK(opst_verify(p, &rep, NULL, 0) == 0 && rep.problems == 0 && rep.blocks > 0);
            opst_close(p);
        }
    }
    printf("%d checks, %d failed\n", checks, fails);
    return fails ? 1 : 0;
}
