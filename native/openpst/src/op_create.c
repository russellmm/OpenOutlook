/* op_create.c - creating a new, empty Unicode PST (wVer 23, 512-byte pages, permutation encryption) from nothing.
 *
 * Step 1 writes the skeleton of a file with one allocation section: the header, the DList page, the first AMap and PMap pages and an empty
 * NBT leaf and BBT leaf. Step 2 opens it for writing and builds everything else with the same writer the editing code uses (one transaction):
 *   0x21 message store, 0x61 name-to-ID map, 0xC01 ID map, 0xE01 message index, the six standard table templates (0x60D, 0x60E, 0x60F, 0x610, 0x671, 0x692),
 *   the root folder 0x122 and below it "Top of ..." 0x8022 (holding Deleted Items 0x8062), Search Root 0x8042 and IPM_COMMON_VIEWS 0x8082.
 * Everything is checked with SCANPST like the rest of the engine (see docs/native-engine-status.md). */
#include "op_wr.h"
#include "op_create_tmpl.h"

#define NOMEM op_err(OPST_E_NOMEM, "out of memory")
#define SEC0_END (OPW_AMAP0 + OPW_SECT)                     /* the file is exactly one section long */

static unsigned sig_of(uint64_t ib, uint64_t bid) {
    uint32_t x = (uint32_t)((ib ^ bid) & 0xFFFFFFFFu);
    return ((x >> 16) ^ x) & 0xFFFFu;
}

static void put_page(uint8_t *file, uint64_t ib, const uint8_t *body496, unsigned ptype, uint64_t bid, int signed_) {
    uint8_t *pg = file + ib;
    memcpy(pg, body496, 496);
    pg[496] = (uint8_t)ptype; pg[497] = (uint8_t)ptype;
    wr16(pg + 498, signed_ ? sig_of(ib, bid) : 0);
    wr32(pg + 500, op_crc(body496, 496));
    wr64(pg + 504, bid);
}

#define BID_DLIST 1u
#define BID_NBT   2u
#define BID_BBT   3u
#define IB_NBT    0x4800u
#define IB_BBT    0x4A00u
#define USED_SLOTS 32u                                     /* AMap (8) + PMap (8) + NBT root (8) + BBT root (8) */

/* the bytes of the empty file */
static uint8_t *skeleton(size_t *n) {
    size_t size = (size_t)SEC0_END;
    uint8_t *f = (uint8_t *)calloc(1, size);
    if (!f) return NULL;
    uint8_t *h = f;
    memcpy(h, "!BDN", 4);
    h[8] = 'S'; h[9] = 'M';
    wr16(h + 10, 23);                                      /* wVer: Unicode */
    wr16(h + 12, 19);                                      /* wVerClient */
    h[14] = 1; h[15] = 1;                                  /* bPlatformCreate / bPlatformAccess */
    wr64(h + 32, 4);                                       /* bidNextP: 1..3 are the pages written here */
    wr32(h + 40, 0x100);                                   /* dwUnique (row versions of the store come from here) */
    for (int i = 0; i < 32; i++) wr32(h + 44 + 4 * i, 0x400);   /* rgnid: every type starts at index 0x400 ... */
    wr32(h + 44 + 4 * 3, 0x4000);                          /* ... except search folders, normal messages (first message NID 0x200024) and associated messages, */
    wr32(h + 44 + 4 * 4, 0x10000);                         /* which is what Outlook writes into a blank file */
    wr32(h + 44 + 4 * 8, 0x8000);
    uint64_t free_bytes = (uint64_t)(OPW_SLOTS - USED_SLOTS) * 64;
    wr64(h + 184, SEC0_END);                               /* ibFileEof */
    wr64(h + 192, OPW_AMAP0);                              /* ibAMapLast */
    wr64(h + 200, free_bytes);                             /* cbAMapFree */
    wr64(h + 216, BID_NBT); wr64(h + 224, IB_NBT);
    wr64(h + 232, BID_BBT); wr64(h + 240, IB_BBT);
    h[248] = 2;                                            /* fAMapValid */
    memset(h + 256, 0xFF, 256);                            /* rgbFM / rgbFP: 255 = a long free run */
    h[512] = 0x80;                                         /* bSentinel */
    h[513] = 1;                                            /* bCryptMethod: permutation */
    wr64(h + 516, 0x10);                                   /* bidNextB */
    wr32(h + 4, op_crc(h + 8, 471));
    wr32(h + 524, op_crc(h + 8, 516));

    uint8_t body[496];
    /* DList: flags 1, no entries (the writer adds none for sections it grows either), current page 0 */
    memset(body, 0, sizeof body);
    body[0] = 1;
    put_page(f, OPW_DLIST_IB, body, 0x86, BID_DLIST, 1);
    /* AMap of section 0 */
    memset(body, 0, sizeof body);
    for (unsigned i = 0; i < USED_SLOTS; i++) body[i >> 3] |= (uint8_t)(0x80 >> (i & 7));
    put_page(f, OPW_AMAP0, body, 0x84, OPW_AMAP0, 0);
    /* PMap (section 0 is a multiple of 8) */
    memset(body, 0xFF, sizeof body);
    put_page(f, OPW_AMAP0 + 512, body, 0x83, OPW_AMAP0 + 512, 0);
    /* empty B-tree roots */
    memset(body, 0, sizeof body);
    body[488] = 0; body[489] = 15; body[490] = 32; body[491] = 0;
    put_page(f, IB_NBT, body, 0x81, BID_NBT, 1);
    memset(body, 0, sizeof body);
    body[488] = 0; body[489] = 20; body[490] = 24; body[491] = 0;
    put_page(f, IB_BBT, body, 0x80, BID_BBT, 1);
    *n = size;
    return f;
}

/* ---- nodes --------------------------------------------------------------------------------------------------------------------- */
static int put_pc(opw *w, uint32_t nid, uint32_t parent, const pcprops *p) {
    hblocks hb;
    int rc = pc_build(p, &hb);
    if (rc) return rc;
    uint64_t bd = 0;
    rc = opw_put_blocks(w, &hb, &bd);
    hb_free(&hb);
    return rc ? rc : opw_node_put(w, nid, bd, 0, parent);
}

static int str_prop(pcprops *p, unsigned pid, const char *utf8) {
    bbuf u = {0};
    if (utf8_to_utf16(utf8, &u) < 0) { bb_free(&u); return op_err(OPST_E_ARG, "text is not valid UTF-8"); }
    int rc = pcprops_set(p, pid, 0x1F, u.p, u.n);
    bb_free(&u);
    return rc;
}
static int i32_prop(pcprops *p, unsigned pid, uint32_t v) { uint8_t b[4]; wr32(b, v); return pcprops_set(p, pid, 3, b, 4); }
static int bool_prop(pcprops *p, unsigned pid, int v) { uint8_t b = v ? 1 : 0; return pcprops_set(p, pid, 0x0B, &b, 1); }

/* an empty table template node: only the column layout */
static int put_template(opw *w, uint32_t nid, const tcol *cols, size_t ncols, const uint16_t rgib[4]) {
    tctx t;
    memset(&t, 0, sizeof t);
    t.cols = (tcol *)malloc(ncols * sizeof *t.cols);
    if (!t.cols) return NOMEM;
    memcpy(t.cols, cols, ncols * sizeof *t.cols);
    t.ncols = ncols;
    memcpy(t.rgib, rgib, sizeof t.rgib);
    hblocks heap, rows;
    uint32_t rn;
    int rc = tc_build(&t, &heap, &rows, &rn);
    uint64_t bd = 0;
    if (!rc) { rc = opw_put_blocks(w, &heap, &bd); hb_free(&heap); hb_free(&rows); }
    free(t.cols);
    return rc ? rc : opw_node_put(w, nid, bd, 0, 0);
}

/* the receive folder table: one row, the default message class (empty) delivers to the root folder; row id 1 */
static int put_receive_table(ops *o) {
    opw *w = o->w;
    tctx t;
    memset(&t, 0, sizeof t);
    size_t ncols = sizeof TMPL_62B_COLS / sizeof *TMPL_62B_COLS;
    t.cols = (tcol *)malloc(ncols * sizeof *t.cols);
    if (!t.cols) return NOMEM;
    memcpy(t.cols, TMPL_62B_COLS, ncols * sizeof *t.cols);
    t.ncols = ncols;
    memcpy(t.rgib, TMPL_62B_RGIB, sizeof t.rgib);
    int rc = tc_add_row(&t, 1);
    uint8_t b[4];
    if (!rc) { static const uint8_t none = 0; rc = tc_set_cell(&t, 0, tc_col(&t, 0x001A), &none, 0); }
    if (!rc) { wr32(b, OP_NID_ROOT); rc = tc_set_cell(&t, 0, tc_col(&t, 0x6605), b, 4); }
    if (!rc) { wr32(b, 1); rc = tc_set_cell(&t, 0, tc_col(&t, 0x67F2), b, 4); }
    if (!rc) { wr32(b, ops_next_row_ver(o)); rc = tc_set_cell(&t, 0, tc_col(&t, 0x67F3), b, 4); }
    hblocks heap, rows;
    uint32_t rn = 0;
    if (!rc) rc = tc_build(&t, &heap, &rows, &rn);
    uint64_t bd = 0, bs = 0;
    if (!rc) {
        rc = opw_put_blocks(w, &heap, &bd);
        if (!rc && rows.n) {
            uint64_t top;
            rc = opw_put_blocks(w, &rows, &top);
            if (!rc) { wsub s = {rn, top, 0}; rc = opw_put_subnodes(w, &s, 1, &bs); }
        }
        hb_free(&heap); hb_free(&rows);
    }
    tc_free(&t);
    return rc ? rc : opw_node_put(w, 0x62B, bd, bs, 0);
}

/* heap-on-node with a BTH header item, used by the ID map and the message index */
static int put_empty_idmap(opw *w) {
    hbuild hb;
    int rc = hbuild_init(&hb, 0x9C);
    if (rc) return rc;
    uint32_t h0 = 0, hh = 0;
    rc = hbuild_alloc(&hb, NULL, 4, &h0);
    if (!rc) rc = build_bth(&hb, 16, 4, NULL, 0, 0, &hh);
    if (!rc) { uint8_t b[4]; wr32(b, hh); rc = hbuild_set(&hb, h0, b, 4); }
    hblocks blocks = {0, 0};
    if (!rc) rc = hbuild_finalize(&hb, h0, &blocks);
    hbuild_free(&hb);
    if (rc) return rc;
    uint64_t bd;
    rc = opw_put_blocks(w, &blocks, &bd);
    hb_free(&blocks);
    return rc ? rc : opw_node_put(w, 0xC01, bd, 0, 0);
}

/* the message index: heap client 0xCC, root item {HID of the BTH, 1, highest message NID}, BTH (key 4, value 4) with no buckets yet */
static int put_empty_msgindex(opw *w) {
    hbuild hb;
    int rc = hbuild_init(&hb, 0xCC);
    if (rc) return rc;
    uint32_t root = 0, bth = 0;
    rc = hbuild_alloc(&hb, NULL, 12, &root);
    if (!rc) rc = build_bth(&hb, 4, 4, NULL, 0, 0, &bth);
    if (!rc) { uint8_t b[12] = {0}; wr32(b, bth); wr32(b + 4, 1); wr32(b + 8, 0); rc = hbuild_set(&hb, root, b, 12); }
    hblocks blocks = {0, 0};
    if (!rc) rc = hbuild_finalize(&hb, root, &blocks);
    hbuild_free(&hb);
    if (rc) return rc;
    uint64_t bd;
    rc = opw_put_blocks(w, &blocks, &bd);
    hb_free(&blocks);
    return rc ? rc : opw_node_put(w, 0xE01, bd, 0, 0);
}

/* the name-to-ID map without any name: bucket count 251 and three empty streams */
static int put_empty_namemap(opw *w) {
    pcprops p;
    memset(&p, 0, sizeof p);
    int rc = i32_prop(&p, 0x0001, 251);
    for (unsigned pid = 2; pid <= 4 && !rc; pid++) rc = pcprops_set(&p, pid, 0x0102, NULL, 0);
    if (!rc) rc = put_pc(w, 0x61, 0, &p);
    pcprops_free(&p);
    return rc;
}

static void entry_id(uint8_t out[24], const uint8_t key[16], uint32_t nid) {
    memset(out, 0, 4);
    memcpy(out + 4, key, 16);
    wr32(out + 20, nid);
}

static int put_store(opw *w, const char *name, const uint8_t key[16], const uint8_t replica[16]) {
    pcprops p;
    memset(&p, 0, sizeof p);
    uint8_t blob[24] = {1, 0, 0, 0}, eid[24];
    memcpy(blob + 4, replica, 16);
    blob[20] = 1;
    int rc = pcprops_set(&p, 0x0E34, 0x0102, blob, 24);
    if (!rc) rc = i32_prop(&p, 0x0E38, 0);
    if (!rc) rc = pcprops_set(&p, 0x0FF9, 0x0102, key, 16);
    if (!rc) rc = str_prop(&p, 0x3001, name);
    if (!rc) rc = i32_prop(&p, 0x35DF, 0xC9);                           /* valid folder mask: IPM subtree, Deleted Items, Common Views, Finder */
    static const struct { unsigned pid; uint32_t nid; } ids[4] = {{0x35E0, 0x8022}, {0x35E3, 0x8062}, {0x35E6, 0x8082}, {0x35E7, 0x8042}};
    for (int i = 0; i < 4 && !rc; i++) { entry_id(eid, key, ids[i].nid); rc = pcprops_set(&p, ids[i].pid, 0x0102, eid, 24); }
    if (!rc) rc = bool_prop(&p, 0x6633, 1);
    if (!rc) rc = i32_prop(&p, 0x66FA, 0x000E0011u);
    if (!rc) { uint8_t r[4]; rc = op_random(r, 4); if (!rc) rc = i32_prop(&p, 0x66FC, op_u32(r) & 0x7FFFFFu); }
    if (!rc) rc = i32_prop(&p, 0x67FF, 0);                              /* no password */
    if (!rc) rc = put_pc(w, 0x21, 0, &p);
    pcprops_free(&p);
    return rc;
}

/* one of the folders every file starts with; the first rows of the hierarchy tables are written by fo_add_row like for any folder */
static int mk_folder(ops *o, uint32_t nid, uint32_t parent, const char *name, const char *comment) {
    opw *w = o->w;
    pcprops p;
    memset(&p, 0, sizeof p);
    int rc = str_prop(&p, 0x3001, name);
    if (!rc && comment) rc = str_prop(&p, 0x3004, comment);
    if (!rc) rc = i32_prop(&p, 0x3602, 0);
    if (!rc) rc = i32_prop(&p, 0x3603, 0);
    if (!rc) rc = bool_prop(&p, 0x360A, 0);
    if (!rc) rc = i32_prop(&p, 0x6635, 0);                              /* like fo_create: the hierarchy row mirrors these */
    if (!rc) rc = i32_prop(&p, 0x6636, 0);
    if (!rc) rc = put_pc(w, nid, parent, &p);
    pcprops_free(&p);
    if (rc) return rc;
    rc = fo_make_tables(o, nid, parent, 0);
    if (rc) return rc;
    bbuf nm = {0}, cl = {0};
    if (utf8_to_utf16(name, &nm) < 0) { bb_free(&nm); return op_err(OPST_E_ARG, "folder name is not valid UTF-8"); }
    rc = fo_add_row(o, nid, parent, &nm, &cl, NULL);
    bb_free(&nm); bb_free(&cl);
    if (!rc) rc = set_has_sub(o, parent);
    return rc;
}

static int build(opst *p, const char *name) {
    ops o;
    int rc = ops_begin(p, &o);
    if (rc) return rc;
    opw *w = o.w;
    uint8_t key[16], replica[16];
    rc = op_random(key, 16);
    if (!rc) rc = op_random(replica, 16);
    if (!rc) rc = put_template(w, 0x60D, TMPL_60D_COLS, sizeof TMPL_60D_COLS / sizeof *TMPL_60D_COLS, TMPL_60D_RGIB);
    if (!rc) rc = put_template(w, 0x60E, TMPL_60E_COLS, sizeof TMPL_60E_COLS / sizeof *TMPL_60E_COLS, TMPL_60E_RGIB);
    if (!rc) rc = put_template(w, 0x60F, TMPL_60F_COLS, sizeof TMPL_60F_COLS / sizeof *TMPL_60F_COLS, TMPL_60F_RGIB);
    if (!rc) rc = put_template(w, 0x610, TMPL_610_COLS, sizeof TMPL_610_COLS / sizeof *TMPL_610_COLS, TMPL_610_RGIB);
    if (!rc) rc = put_template(w, 0x671, TMPL_671_COLS, sizeof TMPL_671_COLS / sizeof *TMPL_671_COLS, TMPL_671_RGIB);
    if (!rc) rc = put_template(w, 0x692, TMPL_692_COLS, sizeof TMPL_692_COLS / sizeof *TMPL_692_COLS, TMPL_692_RGIB);
    if (!rc) rc = put_template(w, 0x6B6, TMPL_6B6_COLS, sizeof TMPL_6B6_COLS / sizeof *TMPL_6B6_COLS, TMPL_6B6_RGIB);
    if (!rc) rc = put_template(w, 0x6D7, TMPL_6D7_COLS, sizeof TMPL_6D7_COLS / sizeof *TMPL_6D7_COLS, TMPL_6D7_RGIB);
    if (!rc) rc = put_template(w, 0x6F8, TMPL_6F8_COLS, sizeof TMPL_6F8_COLS / sizeof *TMPL_6F8_COLS, TMPL_6F8_RGIB);
    if (!rc) rc = put_template(w, 0x64C, TMPL_64C_COLS, sizeof TMPL_64C_COLS / sizeof *TMPL_64C_COLS, TMPL_64C_RGIB);      /* the outgoing queue */
    if (!rc) rc = put_receive_table(&o);
    static const uint32_t empty_nodes[] = {0x1E1, 0x201, 0x261, 0xE41, 0xEC1, 0xF21};          /* search queues, activity list, outgoing queue: present but empty */
    for (size_t i = 0; i < sizeof empty_nodes / sizeof *empty_nodes && !rc; i++) rc = opw_node_put(w, empty_nodes[i], 0, 0, 0);
    if (!rc) rc = put_empty_idmap(w);
    if (!rc) rc = put_empty_msgindex(w);
    if (!rc) rc = put_empty_namemap(w);
    if (!rc) rc = put_store(w, name, key, replica);
    if (rc) { ops_abort(&o); return rc; }

    /* root folder: its own parent, no row anywhere */
    pcprops rp;
    memset(&rp, 0, sizeof rp);
    rc = pcprops_set(&rp, 0x3001, 0x1F, NULL, 0);
    if (!rc) rc = i32_prop(&rp, 0x3602, 0);
    if (!rc) rc = i32_prop(&rp, 0x3603, 0);
    if (!rc) rc = bool_prop(&rp, 0x360A, 1);
    if (!rc) rc = put_pc(w, OP_NID_ROOT, OP_NID_ROOT, &rp);
    pcprops_free(&rp);
    if (!rc) rc = fo_make_tables(&o, OP_NID_ROOT, OP_NID_ROOT, 0);
    if (!rc) rc = mk_folder(&o, 0x8022, OP_NID_ROOT, name, NULL);                         /* "Top of ..." */
    if (!rc) rc = mk_folder(&o, 0x8042, OP_NID_ROOT, "Search Root", NULL);
    if (!rc) rc = mk_folder(&o, 0x8062, 0x8022, "Deleted Items", "Deleted Items folder");
    if (!rc) { o.deleted = 0x8062; rc = mk_folder(&o, 0x8082, OP_NID_ROOT, "IPM_COMMON_VIEWS", NULL); }
    if (!rc) { wr32(w->hdr + 44 + 4 * 2, 0x404); }                                        /* highest folder index in use */
    if (rc) { ops_abort(&o); return rc; }
    return ops_commit(&o);
}

/* Creates the file (it must not exist). The name is the display name of the store and of its top folder. */
int op_create_file(const char *path, const char *display_name) {
    if (!path || !*path || !display_name || !*display_name) return op_err(OPST_E_ARG, "path and display name are required");
    if (op_file_exists(path)) return op_err(OPST_E_REFUSED, "the file already exists: it is never overwritten");
    size_t n = 0;
    uint8_t *f = skeleton(&n);
    if (!f) return NOMEM;
    int rc = op_file_write_all(path, f, n);
    free(f);
    if (rc) return rc;
    opst *p = NULL;
    rc = opst_open(path, OPST_OPEN_WRITE, &p);
    if (!rc) { rc = build(p, display_name); opst_close(p); }
    if (rc) op_file_remove(path);
    return rc;
}

int opst_create(const char *path_utf8, const char *display_name, opst **out) {
    if (!out) return op_err(OPST_E_ARG, "null argument");
    *out = NULL;
    int rc = op_create_file(path_utf8, display_name);
    if (rc) return rc;
    return opst_open(path_utf8, OPST_OPEN_WRITE, out);
}
