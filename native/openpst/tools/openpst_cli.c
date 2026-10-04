/* openpst_cli.c - small command line front end for the OpenPST library (also serves as a usage example). */
#include "openpst.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#ifdef _WIN32
#  define WIN32_LEAN_AND_MEAN
#  include <windows.h>
#  include <shellapi.h>
#  include <fcntl.h>
#  include <io.h>

/* Windows: take the command line as UTF-16 and convert it to UTF-8, and write binary (no CRLF translation). */
static char **win_utf8_args(int *argc) {
    int n;
    wchar_t **w = CommandLineToArgvW(GetCommandLineW(), &n);
    if (!w) return NULL;
    char **v = (char **)calloc((size_t)n + 1, sizeof *v);
    for (int i = 0; v && i < n; i++) {
        int len = WideCharToMultiByte(CP_UTF8, 0, w[i], -1, NULL, 0, NULL, NULL);
        v[i] = (char *)malloc((size_t)len);
        WideCharToMultiByte(CP_UTF8, 0, w[i], -1, v[i], len, NULL, NULL);
    }
    LocalFree(w);
    *argc = n;
    return v;
}
#endif

static void usage(void) {
    fputs("usage: openpst FILE.pst COMMAND [args]\n"
          "  info                         store name and basic facts\n"
          "  tree                         folder tree below the root\n"
          "  list FOLDER                  messages of a folder (path like Inbox/Sub, or a NID)\n"
          "  show NID [text|html|rtf]     headers and body of a message\n"
          "  body NID text|html           display text / HTML of a message (HTML or RTF converted when needed)\n"
          "  search QUERY [--body] [--limit N] [--no-deleted]   search all folders (from:x subject:x has:attachment ...)\n"
          "  searchdump QUERY [--body]    like search, as TSV (nid, folder, path, subject) for comparisons\n"
          "  rtf2html FILE|-              convert an RTF file to HTML on stdout\n"
          "  att NID INDEX OUTFILE        save one attachment\n"
          "  verify                       CRC / structure check\n"
          "  dump                         everything, as TSV (used to compare against the Python reader)\n", stderr);
}

static void tsv(const char *s) {
    if (!s) return;
    for (; *s; s++) putchar((*s == '\t' || *s == '\n' || *s == '\r') ? ' ' : *s);
}

static int resolve_folder(opst *p, const char *arg, uint32_t *nid) {
    char *end;
    unsigned long v = strtoul(arg, &end, 0);
    if (*arg && !*end && v) { *nid = (uint32_t)v; return 0; }
    return opst_folder_find(p, opst_ipm_root(p), arg, nid) == 0 ? 0 : opst_folder_find(p, 0, arg, nid);
}

static size_t parse_nids(const char *s, uint32_t *out, size_t cap) {
    size_t n = 0;
    while (*s && n < cap) {
        char *end;
        unsigned long v = strtoul(s, &end, 0);
        if (end == s) break;
        out[n++] = (uint32_t)v;
        s = *end == ',' ? end + 1 : end;
    }
    return n;
}

static void print_tree(opst *p, uint32_t nid, int depth) {
    opst_folder_info *kids; size_t n;
    if (opst_folder_children(p, nid, &kids, &n) != 0) return;
    for (size_t i = 0; i < n; i++) {
        printf("%*s%s  [nid 0x%x, %d messages, %d unread]\n", depth * 2, "", kids[i].name, kids[i].nid, kids[i].content_count, kids[i].unread_count);
        print_tree(p, kids[i].nid, depth + 1);
    }
    opst_free_folders(kids);
}

static void dump_message(opst *p, uint32_t nid) {
    opst_msg *m;
    if (opst_msg_open(p, nid, &m) != 0) { printf("E\t%u\t", nid); tsv(opst_last_error()); putchar('\n'); return; }
    size_t tl = 0, hl = 0, rl = 0;
    opst_msg_body(m, OPST_BODY_TEXT, &tl);
    opst_msg_body(m, OPST_BODY_HTML, &hl);
    opst_msg_body(m, OPST_BODY_RTF, &rl);
    opst_recipient *r; size_t nr = 0;
    opst_msg_recipients(m, &r, &nr);
    opst_attachment *a; size_t na = 0;
    opst_msg_attachments(m, &a, &na);
    printf("D\t%u\t%zu\t%zu\t%zu\t%zu\t%zu\t", nid, tl, hl, rl, nr, na);
    for (size_t i = 0; i < na; i++) { if (i) putchar(','); printf("%lld", (long long)a[i].size); }
    putchar('\t');
    for (size_t i = 0; i < na; i++) { if (i) putchar(','); tsv(a[i].filename); }
    putchar('\t');
    for (size_t i = 0; i < nr; i++) { if (i) putchar(','); printf("%d:", r[i].type); tsv(r[i].email); }
    putchar('\n');
    opst_free_recipients(r);
    opst_free_attachments(a);
    opst_msg_close(m);
}

static void dump_folder(opst *p, uint32_t nid, const char *name, int depth, int count) {
    printf("F\t%d\t%u\t", depth, nid);
    tsv(name);
    printf("\t%d\n", count);
    opst_msg_row *rows; size_t n;
    if (opst_messages(p, nid, &rows, &n) == 0) {
        for (size_t i = 0; i < n; i++) {
            char iso[32];
            opst_filetime_to_iso(rows[i].received, iso, sizeof iso);
            printf("M\t%u\t%u\t%s\t%lld\t", rows[i].nid, rows[i].flags, iso, (long long)rows[i].size);
            tsv(rows[i].subject); putchar('\t'); tsv(rows[i].sender); putchar('\n');
            dump_message(p, rows[i].nid);
        }
        opst_free_messages(rows);
    }
    opst_folder_info *kids; size_t nk;
    if (opst_folder_children(p, nid, &kids, &nk) == 0) {
        for (size_t i = 0; i < nk; i++) dump_folder(p, kids[i].nid, kids[i].name, depth + 1, kids[i].content_count);
        opst_free_folders(kids);
    }
}

/* writes DIR/<nid>.html (RTF converted) and DIR/<nid>.txt (plain text of the message) for every message that has an RTF body */
static void rtf_walk(opst *p, uint32_t folder, const char *dir) {
    opst_msg_row *rows; size_t n;
    if (opst_messages(p, folder, &rows, &n) == 0) {
        for (size_t i = 0; i < n; i++) {
            opst_msg *m;
            if (opst_msg_open(p, rows[i].nid, &m) != 0) continue;
            size_t rl = 0;
            const char *rtf = opst_msg_body(m, OPST_BODY_RTF, &rl);
            if (rtf && rl) {
                char *html; size_t hl; char *txt; size_t tl; char path[1024];
                if (opst_rtf_to_html(rtf, rl, &html, &hl) == 0) {
                    snprintf(path, sizeof path, "%s/%u.html", dir, rows[i].nid);
                    FILE *f = fopen(path, "wb");
                    if (f) { fwrite(html, 1, hl, f); fclose(f); }
                    opst_free(html);
                }
                if (opst_rtf_to_text(rtf, rl, &txt, &tl) == 0) {
                    snprintf(path, sizeof path, "%s/%u.txt", dir, rows[i].nid);
                    FILE *f = fopen(path, "wb");
                    if (f) { fwrite(txt, 1, tl, f); fclose(f); }
                    opst_free(txt);
                }
            }
            opst_msg_close(m);
        }
        opst_free_messages(rows);
    }
    opst_folder_info *kids; size_t nk;
    if (opst_folder_children(p, folder, &kids, &nk) == 0) {
        for (size_t i = 0; i < nk; i++) rtf_walk(p, kids[i].nid, dir);
        opst_free_folders(kids);
    }
}

/* internal test hooks (op_test.c; available because the CLI links the static library) */
extern int op_test_rebuild(opst *p, int *count);
extern int op_test_validate(opst *p, char **report, size_t *nproblems);

int main(int argc, char **argv) {
#ifdef _WIN32
    char **wargv = win_utf8_args(&argc);
    if (wargv) argv = wargv;
    _setmode(_fileno(stdout), _O_BINARY);
    SetConsoleOutputCP(CP_UTF8);
#endif
    if (argc < 3) { usage(); return 2; }
    opst *p;
    const char *cmd0 = argv[2];
    static const char *const wcmds[] = {"rebuild", "wvalidate", "wmove", "wcopy", "wdel", "wpurge", "fcreate", "frename", "fmove", "fdelete", "fpurge", "wxcopy", "wfix", NULL};
    int wcmd = 0;
    for (int i = 0; wcmds[i]; i++) if (!strcmp(cmd0, wcmds[i])) wcmd = 1;
    int rc = opst_open(argv[1], wcmd ? OPST_OPEN_WRITE : OPST_OPEN_READONLY, &p);
    if (rc) { fprintf(stderr, "openpst: %s\n", opst_last_error()); return 1; }
    const char *cmd = argv[2];
    int ret = 0;
    if (!strcmp(cmd, "info")) {
        printf("store name   : %s\nroot folder  : 0x%x\nipm root     : 0x%x\ndeleted items: 0x%x\n", opst_display_name(p), opst_root_folder(p), opst_ipm_root(p), opst_deleted_items(p));
    } else if (!strcmp(cmd, "tree")) {
        print_tree(p, opst_root_folder(p), 0);
    } else if (!strcmp(cmd, "list") && argc >= 4) {
        uint32_t f;
        if (resolve_folder(p, argv[3], &f)) { fprintf(stderr, "openpst: %s\n", opst_last_error()); ret = 1; }
        else {
            opst_msg_row *rows; size_t n;
            if (opst_messages(p, f, &rows, &n)) { fprintf(stderr, "openpst: %s\n", opst_last_error()); ret = 1; }
            else {
                for (size_t i = 0; i < n; i++) {
                    char iso[32];
                    opst_filetime_to_iso(rows[i].received, iso, sizeof iso);
                    printf("0x%x  %s  %c  %-24.24s  %s\n", rows[i].nid, iso, rows[i].has_attachments ? '@' : ' ', rows[i].sender, rows[i].subject);
                }
                opst_free_messages(rows);
            }
        }
    } else if (!strcmp(cmd, "show") && argc >= 4) {
        opst_msg *m;
        uint32_t nid = (uint32_t)strtoul(argv[3], NULL, 0);
        if (opst_msg_open(p, nid, &m)) { fprintf(stderr, "openpst: %s\n", opst_last_error()); ret = 1; }
        else {
            const char *s;
            printf("Subject : %s\n", (s = opst_msg_str(m, OPST_PID_SUBJECT)) ? s : "");
            printf("From    : %s\n", (s = opst_msg_str(m, OPST_PID_SENDER_NAME)) ? s : "");
            printf("To      : %s\n", (s = opst_msg_str(m, OPST_PID_TO)) ? s : "");
            printf("Cc      : %s\n", (s = opst_msg_str(m, OPST_PID_CC)) ? s : "");
            char iso[32];
            opst_filetime_to_iso(opst_msg_i64(m, OPST_PID_RECEIVED, 0), iso, sizeof iso);
            printf("Received: %s\n\n", iso);
            opst_body_kind k = OPST_BODY_TEXT;
            if (argc >= 5) k = !strcmp(argv[4], "html") ? OPST_BODY_HTML : !strcmp(argv[4], "rtf") ? OPST_BODY_RTF : OPST_BODY_TEXT;
            size_t len;
            const char *b = opst_msg_body(m, k, &len);
            if (b) fwrite(b, 1, len, stdout); else puts("(no such body)");
            putchar('\n');
            opst_attachment *a; size_t na;
            if (opst_msg_attachments(m, &a, &na) == 0) {
                for (size_t i = 0; i < na; i++) printf("attachment %u: %s (%lld bytes, %s)\n", a[i].index, a[i].filename, (long long)a[i].size, a[i].mime);
                opst_free_attachments(a);
            }
            opst_msg_close(m);
        }
    } else if (!strcmp(cmd, "body") && argc >= 4) {
        opst_msg *m;
        if (opst_msg_open(p, (uint32_t)strtoul(argv[3], NULL, 0), &m)) { fprintf(stderr, "openpst: %s\n", opst_last_error()); ret = 1; }
        else {
            size_t len;
            const char *b = (argc >= 5 && !strcmp(argv[4], "html")) ? opst_msg_html(m, &len) : opst_msg_text(m, &len);
            if (b) fwrite(b, 1, len, stdout); else { fputs("(no such body)\n", stderr); ret = 1; }
            opst_msg_close(m);
        }
    } else if ((!strcmp(cmd, "search") || !strcmp(cmd, "searchdump")) && argc >= 4) {
        int dump = !strcmp(cmd, "searchdump");
        opst_search_opts o;
        memset(&o, 0, sizeof o);
        o.size = sizeof o;
        for (int i = 4; i < argc; i++) {
            if (!strcmp(argv[i], "--body")) o.flags |= OPST_SEARCH_BODY;
            else if (!strcmp(argv[i], "--no-deleted")) o.flags |= OPST_SEARCH_SKIP_DELETED;
            else if (!strcmp(argv[i], "--limit") && i + 1 < argc) o.limit = (uint32_t)strtoul(argv[++i], NULL, 0);
        }
        opst_hit *h; size_t n;
        if (opst_search(p, argv[3], &o, &h, &n)) { fprintf(stderr, "openpst: %s\n", opst_last_error()); ret = 1; }
        else {
            for (size_t i = 0; i < n; i++) {
                if (dump) { printf("%u\t%u\t", h[i].nid, h[i].folder); tsv(h[i].folder_path); putchar('\t'); tsv(h[i].subject); putchar('\n'); continue; }
                char iso[32];
                opst_filetime_to_iso(h[i].received ? h[i].received : h[i].sent, iso, sizeof iso);
                printf("0x%-8x %-26.26s %-22.22s %.10s  %c  %s\n", h[i].nid, h[i].folder_path, h[i].sender, iso, h[i].has_attachments ? '@' : ' ', h[i].subject);
            }
            if (!dump) printf("%zu hit(s)\n", n);
            opst_free_hits(h);
        }
    } else if (!strcmp(cmd, "rebuild")) {
        int ntab = 0;
        rc = op_test_rebuild(p, &ntab);
        if (rc) { fprintf(stderr, "openpst: %s\n", opst_last_error()); ret = 1; }
        else printf("rebuilt %d tables\n", ntab);
    } else if ((!strcmp(cmd, "wmove") || !strcmp(cmd, "wcopy")) && argc >= 5) {
        uint32_t dest, nids[4096], newn[4096];
        size_t n = parse_nids(argv[3], nids, 4096);
        if (resolve_folder(p, argv[4], &dest)) { fprintf(stderr, "openpst: %s\n", opst_last_error()); ret = 1; }
        else {
            rc = !strcmp(cmd, "wmove") ? opst_msgs_move(p, nids, n, dest) : opst_msgs_copy(p, nids, n, dest, newn);
            if (rc) { fprintf(stderr, "openpst: %s\n", opst_last_error()); ret = 1; }
            else if (!strcmp(cmd, "wcopy")) { printf("copied; new NIDs:"); for (size_t i = 0; i < n; i++) printf(" 0x%x", newn[i]); printf("\n"); }
            else printf("moved %zu message(s)\n", n);
        }
    } else if ((!strcmp(cmd, "wdel") || !strcmp(cmd, "wpurge")) && argc >= 4) {
        uint32_t nids[4096];
        size_t n = parse_nids(argv[3], nids, 4096), moved = 0, purged = 0;
        rc = !strcmp(cmd, "wdel") ? opst_msgs_delete(p, nids, n, &moved, &purged) : opst_msgs_purge(p, nids, n);
        if (rc) { fprintf(stderr, "openpst: %s\n", opst_last_error()); ret = 1; }
        else if (!strcmp(cmd, "wdel")) printf("moved to Deleted Items: %zu, permanently deleted: %zu\n", moved, purged);
        else printf("permanently deleted %zu message(s)\n", n);
    } else if (!strcmp(cmd, "fcreate") && argc >= 5) {
        uint32_t parent, nid = 0;
        if (resolve_folder(p, argv[3], &parent)) { fprintf(stderr, "openpst: %s\n", opst_last_error()); ret = 1; }
        else {
            rc = opst_folder_create(p, parent, argv[4], argc > 5 ? argv[5] : NULL, &nid);
            if (rc) { fprintf(stderr, "openpst: %s\n", opst_last_error()); ret = 1; }
            else printf("created folder '%s', NID 0x%x\n", argv[4], nid);
        }
    } else if ((!strcmp(cmd, "frename") || !strcmp(cmd, "fmove") || !strcmp(cmd, "fdelete") || !strcmp(cmd, "fpurge")) && argc >= 4) {
        uint32_t f, dest = 0;
        opst_purge_stats st = {0, 0};
        int permanent = 0;
        if (resolve_folder(p, argv[3], &f)) { fprintf(stderr, "openpst: %s\n", opst_last_error()); ret = 1; }
        else if (!strcmp(cmd, "fmove") && (argc < 5 || resolve_folder(p, argv[4], &dest))) { fprintf(stderr, "openpst: %s\n", argc < 5 ? "destination missing" : opst_last_error()); ret = 1; }
        else if (!strcmp(cmd, "frename") && argc < 5) { fprintf(stderr, "openpst: new name missing\n"); ret = 1; }
        else {
            if (!strcmp(cmd, "frename")) rc = opst_folder_rename(p, f, argv[4]);
            else if (!strcmp(cmd, "fmove")) rc = opst_folder_move(p, f, dest);
            else if (!strcmp(cmd, "fdelete")) rc = opst_folder_delete(p, f, &permanent, &st);
            else rc = opst_folder_purge(p, f, &st);
            if (rc) { fprintf(stderr, "openpst: %s\n", opst_last_error()); ret = 1; }
            else if (!strcmp(cmd, "fdelete") && !permanent) printf("moved to Deleted Items\n");
            else if (!strcmp(cmd, "fdelete") || !strcmp(cmd, "fpurge")) printf("permanently deleted: %d folder(s), %d message(s)\n", st.folders, st.messages);
            else printf("done\n");
        }
    } else if (!strcmp(cmd, "wxcopy") && argc >= 6) {
        /* openpst DEST.pst wxcopy SRC.pst NIDS DEST_FOLDER : copy messages of SRC (read only) into DEST */
        opst *src;
        uint32_t nids[4096], newn[4096], dest;
        size_t n = parse_nids(argv[4], nids, 4096);
        rc = opst_open(argv[3], OPST_OPEN_READONLY, &src);
        if (rc) { fprintf(stderr, "openpst: %s\n", opst_last_error()); ret = 1; }
        else {
            if (resolve_folder(p, argv[5], &dest)) { fprintf(stderr, "openpst: %s\n", opst_last_error()); ret = 1; }
            else {
                rc = opst_msgs_copy_to(src, nids, n, p, dest, newn);
                if (rc) { fprintf(stderr, "openpst: %s\n", opst_last_error()); ret = 1; }
                else { printf("copied; new NIDs:"); for (size_t i = 0; i < n; i++) printf(" 0x%x", newn[i]); printf("\n"); }
            }
            opst_close(src);
        }
    } else if (!strcmp(cmd, "wfix")) {
        opst_fix_report fr;
        int apply = argc >= 4 && !strcmp(argv[3], "--apply");
        rc = opst_fix(p, apply, &fr);
        if (rc) { fprintf(stderr, "openpst: %s\n", opst_last_error()); ret = 1; }
        else {
            printf("R1 rows without ID cells          %d\nR2 ID-map records of deleted nodes %d\nR3 messages missing from the index %d\n"
                   "R4 duplicate / high row versions   %d\nR5 NID high-water marks too low    %d\n",
                   fr.rows_without_ids, fr.dangling_idmap, fr.messages_not_indexed, fr.row_version_issues, fr.nid_mark_issues);
            if (apply) printf("applied\n");
        }
    } else if (!strcmp(cmd, "check")) {
        opst_check_report cr = {0};
        char *txt = (char *)malloc(1 << 20);
        rc = txt ? opst_check(p, &cr, txt, 1 << 20) : OPST_E_NOMEM;
        if (rc) { fprintf(stderr, "openpst: %s\n", opst_last_error()); ret = 1; }
        else { fputs(txt, stdout); printf("%d finding(s)\n", cr.problems); ret = cr.problems ? 1 : 0; }
        free(txt);
    } else if (!strcmp(cmd, "wvalidate")) {
        char *rep; size_t np;
        rc = op_test_validate(p, &rep, &np);
        if (rc) { fprintf(stderr, "openpst: %s\n", opst_last_error()); ret = 1; }
        else { printf("%zu problem(s)\n%s", np, rep); free(rep); ret = np ? 1 : 0; }
    } else if (!strcmp(cmd, "rtfall") && argc >= 4) {
        rtf_walk(p, opst_root_folder(p), argv[3]);
    } else if (!strcmp(cmd, "rtf2html") && argc >= 4) {
        FILE *f = strcmp(argv[3], "-") ? fopen(argv[3], "rb") : stdin;
        if (!f) { fprintf(stderr, "openpst: cannot read %s\n", argv[3]); ret = 1; }
        else {
            size_t cap = 1 << 16, n = 0, r;
            char *buf = (char *)malloc(cap);
            while (buf && (r = fread(buf + n, 1, cap - n, f)) > 0) { n += r; if (n == cap) { cap *= 2; buf = (char *)realloc(buf, cap); } }
            char *out; size_t ol;
            if (!buf || opst_rtf_to_html(buf, n, &out, &ol)) { fprintf(stderr, "openpst: %s\n", opst_last_error()); ret = 1; }
            else { fwrite(out, 1, ol, stdout); opst_free(out); }
            free(buf);
        }
    } else if (!strcmp(cmd, "att") && argc >= 6) {
        opst_msg *m;
        if (opst_msg_open(p, (uint32_t)strtoul(argv[3], NULL, 0), &m)) { fprintf(stderr, "openpst: %s\n", opst_last_error()); ret = 1; }
        else {
            const uint8_t *d; size_t n;
            if (opst_attachment_data(m, (uint32_t)strtoul(argv[4], NULL, 0), &d, &n)) { fprintf(stderr, "openpst: %s\n", opst_last_error()); ret = 1; }
            else {
                FILE *f = fopen(argv[5], "wb");
                if (!f || fwrite(d, 1, n, f) != n) { fprintf(stderr, "openpst: cannot write %s\n", argv[5]); ret = 1; }
                if (f) fclose(f);
                else ret = 1;
                if (!ret) printf("wrote %zu bytes to %s\n", n, argv[5]);
            }
            opst_msg_close(m);
        }
    } else if (!strcmp(cmd, "verify")) {
        opst_verify_report rep;
        char text[8192];
        opst_verify(p, &rep, text, sizeof text);
        printf("%llu blocks checked, %llu CRC errors, %llu problems\n%s", (unsigned long long)rep.blocks, (unsigned long long)rep.crc_errors, (unsigned long long)rep.problems, text);
        ret = rep.problems ? 1 : 0;
    } else if (!strcmp(cmd, "dump")) {
        dump_folder(p, opst_root_folder(p), "", 0, 0);
    } else { usage(); ret = 2; }
    opst_close(p);
    return ret;
}
