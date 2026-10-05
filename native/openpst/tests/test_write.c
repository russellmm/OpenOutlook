/* test_write.c - tests of the write API on a COPY of a real file. Set OPST_TEST_PST=small.pst (e.g. rmarrash_2.pst); the copy is made next
 * to the test executable's working directory as "opst_write_test.pst" and deleted at the end. Never point it at a file you care about:
 * only the copy is written, but the original is read. */
#if !defined(_WIN32) && !defined(_POSIX_C_SOURCE)
#  define _POSIX_C_SOURCE 200809L          /* setenv */
#endif
#include "openpst.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#ifdef _WIN32
#  include <windows.h>
#  define SETENV(k, v) _putenv_s((k), (v))
#else
#  define SETENV(k, v) setenv((k), (v), 1)
#endif

static int fails, checks;
#define CHECK(c) do { checks++; if (!(c)) { fails++; printf("FAIL line %d: %s   [%s]\n", __LINE__, #c, opst_last_error()); } } while (0)

static int copy_file(const char *from, const char *to) {
    FILE *a = fopen(from, "rb"), *b = a ? fopen(to, "wb") : NULL;
    if (!a || !b) { if (a) fclose(a); return 0; }
    char *buf = (char *)malloc(1 << 20);
    size_t n;
    int ok = buf != NULL;
    while (ok && (n = fread(buf, 1, 1 << 20, a)) > 0) ok = fwrite(buf, 1, n, b) == n;
    free(buf);
    fclose(a);
    return fclose(b) == 0 && ok;
}

static char *slurp(const char *path, size_t *n) {
    FILE *f = fopen(path, "rb");
    if (!f) return NULL;
    fseek(f, 0, SEEK_END);
    long sz = ftell(f);
    fseek(f, 0, SEEK_SET);
    char *b = (char *)malloc((size_t)sz + 1);
    if (b && fread(b, 1, (size_t)sz, f) != (size_t)sz) { free(b); b = NULL; }
    fclose(f);
    if (n) *n = (size_t)sz;
    return b;
}

/* MS-PST CRC (CRC-32 with a zero seed and no final inversion), used to re-sign a page edited by the test */
static uint32_t crc_ms(const uint8_t *p, size_t n) {
    uint32_t c = 0;
    for (size_t i = 0; i < n; i++) {
        c ^= p[i];
        for (int k = 0; k < 8; k++) c = (c >> 1) ^ (0xEDB88320u & (0u - (c & 1)));
    }
    return c;
}

static int open_w(const char *path, opst **p) { return opst_open(path, OPST_OPEN_WRITE, p); }

/* first folder below the IPM root that holds at least `want` messages (not Deleted Items); returns its NID or 0 */
static uint32_t find_source_folder(opst *p, size_t want) {
    opst_folder_info *kids; size_t nk;
    uint32_t found = 0;
    if (opst_folder_children(p, opst_ipm_root(p), &kids, &nk) != 0) return 0;
    for (size_t i = 0; i < nk && !found; i++) if (kids[i].nid != opst_deleted_items(p) && (size_t)kids[i].content_count >= want) found = kids[i].nid;
    opst_free_folders(kids);
    return found;
}

static int findings(opst *p) {
    opst_check_report r;
    memset(&r, 0, sizeof r);
    if (opst_check(p, &r, NULL, 0) != 0) return -1;
    return r.problems;
}

int main(void) {
    const char *src = getenv("OPST_TEST_PST");
    if (!src || !*src) { printf("test_write: OPST_TEST_PST not set, skipped\n"); return 0; }
    const char *tmp = "opst_write_test.pst";
    char jrn[256];
    snprintf(jrn, sizeof jrn, "%s.journal", tmp);
    if (!copy_file(src, tmp)) { printf("cannot copy %s\n", src); return 1; }

    /* read-only handles refuse every write */
    {
        opst *p;
        CHECK(opst_open(tmp, OPST_OPEN_READONLY, &p) == 0);
        uint32_t nid = 0x200024;
        CHECK(opst_msgs_purge(p, &nid, 1) == OPST_E_STATE);
        CHECK(opst_folder_create(p, opst_ipm_root(p), "x", NULL, NULL) == OPST_E_STATE);
        CHECK(opst_fix(p, 1, NULL) == OPST_E_STATE);
        opst_fix_report rep;
        CHECK(opst_fix(p, 0, &rep) == 0);                 /* report only works read-only */
        CHECK(opst_folder_is_protected(p, opst_ipm_root(p)) == 1 && opst_journal_pending(p) == 0);
        opst_close(p);
    }

    opst *p;
    CHECK(open_w(tmp, &p) == 0);
    if (!p) return 1;
    CHECK(opst_recovered(p) == 0);
    int base_findings = findings(p);
    CHECK(base_findings == 0);
    uint32_t top = opst_ipm_root(p), del = opst_deleted_items(p);
    CHECK(opst_folder_is_protected(p, top) == 1 && opst_folder_is_protected(p, del) == 1);
    CHECK(opst_folder_rename(p, del, "Gone") == OPST_E_REFUSED);

    /* folders: create / duplicate / invalid / rename / find */
    uint32_t f1 = 0, f2 = 0;
    CHECK(opst_folder_create(p, top, "UT1", NULL, &f1) == 0 && f1 != 0);
    CHECK(opst_folder_create(p, top, "ut1", NULL, NULL) == OPST_E_REFUSED);          /* case-insensitive clash */
    CHECK(opst_folder_create(p, top, "a/b", NULL, NULL) == OPST_E_ARG);
    CHECK(opst_folder_create(p, top, " lead", NULL, NULL) == OPST_E_ARG);
    CHECK(opst_folder_create(p, top, "", NULL, NULL) == OPST_E_ARG);
    CHECK(opst_folder_create(p, 0x122, "atroot", NULL, NULL) == OPST_E_REFUSED);     /* not at the store root */
    CHECK(opst_folder_create(p, f1, "Sub \xC3\xA9\xE2\x82\xAC", "IPF.Contact", &f2) == 0);   /* non-ASCII name */
    CHECK(opst_folder_rename(p, f1, "UT2") == 0);
    uint32_t found = 0;
    CHECK(opst_folder_find(p, top, "UT2", &found) == 0 && found == f1);
    CHECK(opst_folder_find(p, top, "UT1", &found) == OPST_E_NOTFOUND);
    CHECK(opst_folder_find(p, top, "UT2/Sub \xC3\xA9\xE2\x82\xAC", &found) == 0 && found == f2);
    CHECK(opst_folder_move(p, f1, f2) == OPST_E_REFUSED);                            /* into its own subfolder */
    CHECK(findings(p) == base_findings);

    /* messages: copy / read back / move / delete */
    uint32_t sf = find_source_folder(p, 3);
    CHECK(sf != 0);
    opst_msg_row *rows; size_t nr = 0;
    CHECK(opst_messages(p, sf, &rows, &nr) == 0 && nr >= 3);
    uint32_t ids[3] = {rows[0].nid, rows[1].nid, rows[2].nid}, copies[3] = {0, 0, 0};
    char subj[3][200];
    for (int i = 0; i < 3; i++) snprintf(subj[i], sizeof subj[i], "%s", rows[i].subject);
    opst_free_messages(rows);
    CHECK(opst_msgs_copy(p, ids, 3, f1, copies) == 0);
    CHECK(copies[0] && copies[1] && copies[2] && copies[0] != ids[0]);
    CHECK(opst_messages(p, f1, &rows, &nr) == 0 && nr == 3);
    for (int i = 0; i < 3; i++) {
        int hit = 0;
        for (size_t k = 0; k < nr; k++) if (rows[k].nid == copies[i] && strcmp(rows[k].subject, subj[i]) == 0) hit = 1;
        CHECK(hit);
        opst_msg *m;
        CHECK(opst_msg_open(p, copies[i], &m) == 0);
        if (m) { const char *s = opst_msg_str(m, OPST_PID_SUBJECT); CHECK(s && strcmp(s, subj[i]) == 0); opst_msg_close(m); }
    }
    opst_free_messages(rows);
    /* read / flag state: toggle the first two source messages, check message, row and folder unread count, undo, check again */
    {
        opst_folder_info sfi0, sfi1;
        char nb2[64];
        CHECK(opst_folder_info_get(p, sf, &sfi0, nb2, sizeof nb2) == 0);
        opst_msg_row *r0; size_t n0 = 0;
        CHECK(opst_messages(p, sf, &r0, &n0) == 0);
        int was_read[2] = {0, 0}, unread_before = 0, rd[2] = {0, 0};
        for (size_t k = 0; k < n0; k++) { if (!(r0[k].flags & 1)) unread_before++; }
        for (int i = 0; i < 2; i++) for (size_t k = 0; k < n0; k++) if (r0[k].nid == ids[i]) was_read[i] = (int)(r0[k].flags & 1);
        opst_free_messages(r0);
        for (int i = 0; i < 2; i++) rd[i] = !was_read[i];
        uint32_t one[1];
        int delta = 0;
        for (int i = 0; i < 2; i++) { one[0] = ids[i]; CHECK(opst_msgs_set_state(p, one, 1, rd[i], -1) == 0); delta += rd[i] ? -1 : 1; }
        CHECK(opst_msgs_set_state(p, ids, 1, -1, 2) == 0);                     /* flag the first */
        CHECK(opst_messages(p, sf, &r0, &n0) == 0);
        for (int i = 0; i < 2; i++) for (size_t k = 0; k < n0; k++) if (r0[k].nid == ids[i]) CHECK((int)(r0[k].flags & 1) == rd[i]);
        for (size_t k = 0; k < n0; k++) if (r0[k].nid == ids[0]) CHECK(r0[k].flag_status == 2);
        opst_free_messages(r0);
        for (int i = 0; i < 2; i++) {
            opst_msg *m;
            CHECK(opst_msg_open(p, ids[i], &m) == 0);
            if (m) { CHECK(((int)opst_msg_i64(m, 0x0E07, 0) & 1) == rd[i]); if (i == 0) CHECK(opst_msg_i64(m, 0x1090, 0) == 2); opst_msg_close(m); }
        }
        CHECK(opst_folder_info_get(p, sf, &sfi1, nb2, sizeof nb2) == 0 && sfi1.unread_count == sfi0.unread_count + delta);
        CHECK(findings(p) == base_findings);
        CHECK(opst_msgs_set_state(p, ids, 1, -1, 0) == 0);                     /* unflag */
        for (int i = 0; i < 2; i++) { one[0] = ids[i]; CHECK(opst_msgs_set_state(p, one, 1, was_read[i], -1) == 0); }
        CHECK(opst_folder_info_get(p, sf, &sfi1, nb2, sizeof nb2) == 0 && sfi1.unread_count == sfi0.unread_count);
        CHECK(opst_msgs_set_state(p, ids, 2, 7, -1) == OPST_E_ARG);
        (void)unread_before;
        CHECK(findings(p) == base_findings);
    }
    /* import: a message built from plain fields (long text body, HTML, 3 recipients, a 20 KB attachment and an inline picture) */
    {
        size_t blen = 20000, hlen = 9000;
        char *body = (char *)malloc(blen + 1), *html = (char *)malloc(hlen + 1);
        uint8_t *att = (uint8_t *)malloc(20000);
        CHECK(body && html && att);
        for (size_t i = 0; i < blen; i++) body[i] = (i % 80 == 79) ? '\n' : (char)('a' + (i % 26));
        body[blen] = 0;
        memcpy(body, "caf\xC3\xA9 \xE2\x82\xAC start ", 14);
        for (size_t i = 0; i < hlen; i++) html[i] = (char)('A' + (i % 26));
        memcpy(html, "<html><body><p>Hi <img src=\"cid:pic1@x\"></p>", 44);
        html[hlen] = 0;
        for (size_t i = 0; i < 20000; i++) att[i] = (uint8_t)(i * 7 + (i >> 8));
        static const uint8_t pic[] = {0x89, 'P', 'N', 'G', 13, 10, 26, 10, 1, 2, 3, 4, 5};
        opst_import_recipient rc3[3] = {{"Ann Example", "ann@example.test", 1}, {"", "bob@example.test", 2}, {"Cy", "cy@example.test", 3}};
        opst_import_attachment at[2];
        memset(at, 0, sizeof at);
        at[0].filename = "data.bin"; at[0].mime = "application/octet-stream"; at[0].data = att; at[0].len = 20000;
        at[1].filename = "pic.png"; at[1].mime = "image/png"; at[1].content_id = "pic1@x"; at[1].data = pic; at[1].len = sizeof pic;
        opst_import_msg im;
        memset(&im, 0, sizeof im);
        im.subject = "RE: Imported \xC3\xA9 test"; im.sender_name = "Dan Sender"; im.sender_email = "dan@example.test";
        im.body_text = body; im.body_html = html; im.transport_headers = "Received: from x\r\nSubject: imported\r\n";
        im.message_id = "<imp-1@example.test>"; im.sent = 132550353630000000LL; im.importance = 2;
        im.recipients = rc3; im.nrecipients = 3; im.attachments = at; im.nattachments = 2;
        opst_folder_info fi0, fi1;
        char nb3[64];
        CHECK(opst_folder_info_get(p, f1, &fi0, nb3, sizeof nb3) == 0);
        uint32_t inid = 0;
        CHECK(opst_msg_import(p, f1, &im, &inid) == 0 && inid != 0 && (inid & 0x1F) == 4);
        CHECK(opst_folder_info_get(p, f1, &fi1, nb3, sizeof nb3) == 0 && fi1.content_count == fi0.content_count + 1 && fi1.unread_count == fi0.unread_count + 1);
        CHECK(findings(p) == base_findings);
        opst_msg *im2;
        CHECK(opst_msg_open(p, inid, &im2) == 0);
        if (im2) {
            const char *sj = opst_msg_str(im2, OPST_PID_SUBJECT);
            CHECK(sj && strcmp(sj, "RE: Imported \xC3\xA9 test") == 0);
            const char *tp = opst_msg_str(im2, 0x0070);
            CHECK(tp && strcmp(tp, "Imported \xC3\xA9 test") == 0);
            const char *sn = opst_msg_str(im2, 0x0C1A);
            CHECK(sn && strcmp(sn, "Dan Sender") == 0);
            size_t bl = 0;
            const char *bt = opst_msg_body(im2, OPST_BODY_TEXT, &bl);
            CHECK(bt && bl == strlen(body) && memcmp(bt, body, bl) == 0);
            const char *bh = opst_msg_body(im2, OPST_BODY_HTML, &bl);
            CHECK(bh && bl == hlen && memcmp(bh, html, bl) == 0);
            const char *hd = opst_msg_str(im2, 0x007D);
            CHECK(hd && strncmp(hd, "Received: from x", 16) == 0);
            CHECK(opst_msg_i64(im2, 0x0E07, 0) == 0x10 && opst_msg_i64(im2, 0x0017, 9) == 2);
            opst_recipient *rl; size_t nrl;
            CHECK(opst_msg_recipients(im2, &rl, &nrl) == 0 && nrl == 3);
            if (nrl == 3) {
                CHECK(rl[0].type == 1 && strcmp(rl[0].name, "Ann Example") == 0 && strcmp(rl[0].email, "ann@example.test") == 0);
                CHECK(rl[1].type == 2 && strcmp(rl[1].email, "bob@example.test") == 0);
                CHECK(rl[2].type == 3 && strcmp(rl[2].name, "Cy") == 0);
                opst_free_recipients(rl);
            }
            opst_attachment *al; size_t nal;
            CHECK(opst_msg_attachments(im2, &al, &nal) == 0 && nal == 2);
            if (nal == 2) {
                CHECK(strcmp(al[0].filename, "data.bin") == 0 && al[1].cid && strcmp(al[1].cid, "pic1@x") == 0);
                const uint8_t *ad; size_t an;
                CHECK(opst_attachment_data(im2, al[0].index, &ad, &an) == 0 && an == 20000 && memcmp(ad, att, 20000) == 0);
                CHECK(opst_attachment_data(im2, al[1].index, &ad, &an) == 0 && an == sizeof pic && memcmp(ad, pic, an) == 0);
                opst_free_attachments(al);
            }
            opst_msg_close(im2);
        }
        opst_msg_row *ir; size_t nir = 0;
        CHECK(opst_messages(p, f1, &ir, &nir) == 0);
        int seen = 0;
        for (size_t k = 0; k < nir; k++) if (ir[k].nid == inid) { seen = 1; CHECK(strcmp(ir[k].sender, "Dan Sender") == 0 && ir[k].has_attachments && !(ir[k].flags & 1)); }
        CHECK(seen);
        opst_free_messages(ir);
        size_t one = 1;
        CHECK(opst_msgs_set_state(p, &inid, one, 1, -1) == 0);            /* it behaves like any other message afterwards */
        CHECK(opst_msgs_set_state(p, &inid, one, -1, 2) == 0);            /* flag: the imported message has no flag property yet */
        {
            opst_msg_row *fr; size_t nfr = 0;
            CHECK(opst_messages(p, f1, &fr, &nfr) == 0);
            /* the row only carries the flag when the folder's table has a flag-status column (this test folder is cloned from a table that may not) */
            for (size_t k = 0; k < nfr; k++) if (fr[k].nid == inid) CHECK(fr[k].flags & 1);
            opst_free_messages(fr);
            opst_msg *fm;
            CHECK(opst_msg_open(p, inid, &fm) == 0);
            if (fm) { CHECK(opst_msg_i64(fm, 0x1090, 0) == 2); opst_msg_close(fm); }
        }
        CHECK(opst_msgs_move(p, &inid, one, f2) == 0);
        CHECK(opst_msgs_purge(p, &inid, one) == 0);
        CHECK(findings(p) == base_findings);
        free(body); free(html); free(att);
    }
    opst_folder_info fi;
    char nb[64];
    CHECK(opst_folder_info_get(p, f1, &fi, nb, sizeof nb) == 0 && fi.content_count == 3);
    CHECK(opst_msgs_move(p, copies, 2, f2) == 0);
    CHECK(opst_folder_info_get(p, f2, &fi, nb, sizeof nb) == 0 && fi.content_count == 2);
    CHECK(opst_folder_info_get(p, f1, &fi, nb, sizeof nb) == 0 && fi.content_count == 1 && fi.has_subfolders == 1);
    size_t moved = 0, purged = 0;
    CHECK(opst_msgs_delete(p, copies, 2, &moved, &purged) == 0 && moved == 2 && purged == 0);      /* to Deleted Items */
    CHECK(opst_msgs_delete(p, copies, 2, &moved, &purged) == 0 && moved == 0 && purged == 2);      /* now for good */
    CHECK(opst_msgs_purge(p, copies, 1) == OPST_E_NOTFOUND);                                       /* already gone */
    CHECK(opst_msgs_move(p, ids, 1, 0x1234) == OPST_E_ARG);
    CHECK(findings(p) == base_findings);

    /* folder delete: first into Deleted Items, then for good */
    int permanent = -1;
    opst_purge_stats st = {0, 0};
    CHECK(opst_folder_delete(p, f1, &permanent, &st) == 0 && permanent == 0);
    CHECK(opst_folder_delete(p, f1, &permanent, &st) == 0 && permanent == 1 && st.folders == 2 && st.messages == 1);
    CHECK(opst_folder_find(p, top, "UT2", &found) == OPST_E_NOTFOUND);
    CHECK(findings(p) == base_findings);
    opst_fix_report rep;
    CHECK(opst_fix(p, 1, &rep) == 0 && rep.rows_without_ids == 0 && rep.messages_not_indexed == 0 && rep.row_version_issues == 0);
    CHECK(opst_verify(p, &(opst_verify_report){0}, NULL, 0) == 0);
    opst_close(p);

    /* crash recovery: the file after an interrupted write is rolled back to the state before it, byte for byte */
    for (int point = 1; point <= 4; point++) {
        size_t n0, n1;
        char *before = slurp(tmp, &n0);
        char num[8];
        snprintf(num, sizeof num, "%d", point);
        CHECK(open_w(tmp, &p) == 0);
        uint32_t nf = 0;
        SETENV("OPST_TEST_CRASH", num);
        int rc = opst_folder_create(p, opst_ipm_root(p), "Crash", NULL, &nf);
        SETENV("OPST_TEST_CRASH", "");
        CHECK(rc == OPST_E_IO);
        opst_close(p);
        FILE *j = fopen(jrn, "rb");
        CHECK(j != NULL);                                   /* the journal is left behind */
        if (j) fclose(j);
        CHECK(opst_open(tmp, OPST_OPEN_READONLY, &p) == 0 && opst_journal_pending(p) == 1);      /* a reader is warned */
        opst_close(p);
        CHECK(open_w(tmp, &p) == 0 && opst_recovered(p) == 1);
        opst_close(p);
        char *after = slurp(tmp, &n1);
        CHECK(before && after && n0 == n1 && memcmp(before, after, n0) == 0);
        j = fopen(jrn, "rb");
        CHECK(j == NULL);
        if (j) fclose(j);
        free(before); free(after);
    }

    /* the same write without a crash still works afterwards */
    CHECK(open_w(tmp, &p) == 0);
    uint32_t nf = 0;
    CHECK(opst_folder_create(p, opst_ipm_root(p), "AfterCrash", NULL, &nf) == 0 && nf != 0);
    CHECK(opst_folder_purge(p, nf, &st) == 0 && st.folders == 1);
    CHECK(findings(p) == base_findings);
    opst_close(p);

    /* the checker catches the two defects SCANPST found in the first version of the importer (a padded PR_ATTACH_SIZE and nid counters that
       were not advanced); the hooks only exist to reproduce them */
    {
        const char *tmp2 = "opst_write_test2.pst";
        if (copy_file(src, tmp2)) {
            opst *q;
            CHECK(open_w(tmp2, &q) == 0);
            if (q) {
                int base2 = findings(q);
                uint32_t dest = find_source_folder(q, 1);
                static const uint8_t data[300] = {1, 2, 3};
                opst_import_attachment at = {0};
                at.filename = "x.bin"; at.data = data; at.len = sizeof data;
                opst_import_msg m;
                memset(&m, 0, sizeof m);
                m.subject = "defect probe"; m.attachments = &at; m.nattachments = 1;
                SETENV("OPST_TEST_BAD_ATTSIZE", "1");
                CHECK(dest && opst_msg_import(q, dest, &m, NULL) == 0);
                CHECK(findings(q) > base2);
                SETENV("OPST_TEST_BAD_ATTSIZE", "");
                opst_close(q);
                remove(tmp2);
                char j2[256]; snprintf(j2, sizeof j2, "%s.journal", tmp2); remove(j2);
            }
        }
        if (copy_file(src, tmp2)) {
            opst *q;
            CHECK(open_w(tmp2, &q) == 0);
            if (q) {
                int base2 = findings(q);
                uint32_t dest = find_source_folder(q, 1);
                static const uint8_t data[300] = {1, 2, 3};
                opst_import_attachment at = {0};
                at.filename = "x.bin"; at.data = data; at.len = sizeof data;
                opst_import_msg m;
                memset(&m, 0, sizeof m);
                m.subject = "defect probe 2"; m.attachments = &at; m.nattachments = 1;
                SETENV("OPST_TEST_BAD_NIDCOUNTER", "1");
                CHECK(dest && opst_msg_import(q, dest, &m, NULL) == 0);
                CHECK(findings(q) > base2);
                SETENV("OPST_TEST_BAD_NIDCOUNTER", "");
                opst_close(q);
                remove(tmp2);
                char j2[256]; snprintf(j2, sizeof j2, "%s.journal", tmp2); remove(j2);
            }
        }
    }
    /* rows without the row-only cells: the checker finds them and the fixer (rule R6) adds them */
    {
        const char *tmp2 = "opst_write_test2.pst";
        if (copy_file(src, tmp2)) {
            opst *q;
            CHECK(open_w(tmp2, &q) == 0);
            if (q) {
                int base2 = findings(q);
                uint32_t dest = find_source_folder(q, 1);
                opst_import_msg m;
                memset(&m, 0, sizeof m);
                m.subject = "row cells probe";
                SETENV("OPST_TEST_NO_ROWCELLS", "1");
                CHECK(dest && opst_msg_import(q, dest, &m, NULL) == 0);
                SETENV("OPST_TEST_NO_ROWCELLS", "");
                int after = findings(q);
                opst_fix_report fr;
                memset(&fr, 0, sizeof fr);
                CHECK(opst_fix(q, 0, &fr) == 0);
                int nfound = fr.rowcell_issues;
                CHECK(after > base2 && nfound >= 1);                       /* both checker and fixer see it (a table may lack one of the two columns) */
                CHECK(opst_fix(q, 1, &fr) == 0);
                CHECK(findings(q) == base2);
                memset(&fr, 0, sizeof fr);
                CHECK(opst_fix(q, 0, &fr) == 0 && fr.rowcell_issues == 0);
                opst_close(q);
                remove(tmp2);
                char j2[256]; snprintf(j2, sizeof j2, "%s.journal", tmp2); remove(j2);
            }
        }
    }
    /* allocation maps: a bit cleared behind the library's back is found (and cbAMapFree no longer matches), and the fixer (R7) repairs it */
    {
        const char *tmp2 = "opst_write_test2.pst";
        if (copy_file(src, tmp2)) {
            FILE *f = fopen(tmp2, "r+b");
            uint8_t pg[512];
            int hit = 0;
            if (f && fseek(f, 0x4400, SEEK_SET) == 0 && fread(pg, 1, 512, f) == 512 && pg[496] == 0x84 && crc_ms(pg, 496) == (uint32_t)(pg[500] | pg[501] << 8 | pg[502] << 16 | (uint32_t)pg[503] << 24)) {
                for (int b = 495; b >= 0 && !hit; b--)
                    for (int k = 0; k < 8 && !hit; k++)
                        if ((pg[b] >> k) & 1 && b * 8 + (7 - k) >= 64) { pg[b] = (uint8_t)(pg[b] & ~(1 << k)); hit = 1; }
                uint32_t c = crc_ms(pg, 496);
                pg[500] = (uint8_t)c; pg[501] = (uint8_t)(c >> 8); pg[502] = (uint8_t)(c >> 16); pg[503] = (uint8_t)(c >> 24);
                fseek(f, 0x4400, SEEK_SET);
                fwrite(pg, 1, 512, f);
            }
            if (f) fclose(f);
            CHECK(hit);
            opst *q;
            CHECK(opst_open(tmp2, 0, &q) == 0);
            if (q) {
                opst_check_report cr;
                CHECK(opst_check(q, &cr, NULL, 0) == 0 && cr.amap_problems >= 1);
                opst_close(q);
            }
            CHECK(open_w(tmp2, &q) == 0);
            if (q) {
                opst_fix_report fr;
                memset(&fr, 0, sizeof fr);
                CHECK(opst_fix(q, 0, &fr) == 0 && fr.amap_issues >= 1);
                CHECK(opst_fix(q, 1, &fr) == 0);
                opst_check_report cr;
                CHECK(opst_check(q, &cr, NULL, 0) == 0 && cr.amap_problems == 0);
                memset(&fr, 0, sizeof fr);
                CHECK(opst_fix(q, 0, &fr) == 0 && fr.amap_issues == 0);
                opst_close(q);
            }
            remove(tmp2);
            char j2[256]; snprintf(j2, sizeof j2, "%s.journal", tmp2); remove(j2);
        }
    }
    /* a folder created without its tables and parent row (what early versions left behind): found, then completed by the fixer (R8) */
    {
        const char *tmp2 = "opst_write_test2.pst";
        if (copy_file(src, tmp2)) {
            opst *q;
            CHECK(open_w(tmp2, &q) == 0);
            if (q) {
                uint32_t parent = find_source_folder(q, 1), nid = 0;
                SETENV("OPST_TEST_INCOMPLETE_FOLDER", "1");
                CHECK(parent && opst_folder_create(q, parent, "half made", "IPF.Note", &nid) == 0);
                SETENV("OPST_TEST_INCOMPLETE_FOLDER", "");
                opst_check_report cr;
                CHECK(opst_check(q, &cr, NULL, 0) == 0 && cr.folder_problems >= 1);
                opst_fix_report fr;
                memset(&fr, 0, sizeof fr);
                CHECK(opst_fix(q, 0, &fr) == 0 && fr.folder_issues >= 1);
                CHECK(opst_fix(q, 1, &fr) == 0);
                CHECK(opst_check(q, &cr, NULL, 0) == 0 && cr.folder_problems == 0 && cr.amap_problems == 0);
                memset(&fr, 0, sizeof fr);
                CHECK(opst_fix(q, 0, &fr) == 0 && fr.folder_issues == 0);
                opst_close(q);
            }
            remove(tmp2);
            char j2[256]; snprintf(j2, sizeof j2, "%s.journal", tmp2); remove(j2);
        }
    }
    remove(tmp);
    printf("%d checks, %d failed\n", checks, fails);
    return fails ? 1 : 0;
}
