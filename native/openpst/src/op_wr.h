/* op_wr.h - internal declarations of the writing side (transactions, allocation, B-trees, heap/table builders, node editing).
 * Not part of the public API. Port of pstwrite.py / pstedit.py. */
#ifndef OP_WR_H
#define OP_WR_H

#include "op_internal.h"

#define OPW_AMAP0        0x4400u
#define OPW_SECT         253952u          /* bytes covered by one AMap section */
#define OPW_SLOTS        3968u            /* 64-byte slots per section */
#define OPW_GROW         16u              /* sections appended when the file is full (about 4 MB) */
#define OPW_MAX_SECTIONS 65536u           /* about 16 GB */
#define OPW_FPMAP_FIRST  8192u
#define OPW_FPMAP_EVERY  31744u
#define OPW_DLIST_IB     0x4200u
#define OP_BLOCKMAX      8176u
#define OP_MAXALLOC      3580u
#define OP_BIGDISPLAY    2047u                          /* display-to / display-cc (0x0E04 / 0x0E03) over this many bytes: a subnode in the message AND the contents-table cell, one shared data tree (SCANPST re-creates the row of such a message otherwise: it has been seen from 2,048 bytes up) */

/* ---- growable byte buffer -------------------------------------------------------------------------------------------- */
typedef struct { uint8_t *p; size_t n, cap; int bad; } bbuf;
void bb_free(bbuf *b);
int  bb_put(bbuf *b, const void *d, size_t n);
int  bb_zero(bbuf *b, size_t n);
int  bb_u8(bbuf *b, unsigned v);
int  bb_u16(bbuf *b, unsigned v);
int  bb_u32(bbuf *b, uint32_t v);
int  bb_u64(bbuf *b, uint64_t v);
static inline void wr16(uint8_t *p, unsigned v) { p[0] = (uint8_t)v; p[1] = (uint8_t)(v >> 8); }
static inline void wr32(uint8_t *p, uint32_t v) { p[0] = (uint8_t)v; p[1] = (uint8_t)(v >> 8); p[2] = (uint8_t)(v >> 16); p[3] = (uint8_t)(v >> 24); }
static inline void wr64(uint8_t *p, uint64_t v) { wr32(p, (uint32_t)v); wr32(p + 4, (uint32_t)(v >> 32)); }

/* ---- the writer / transaction ---------------------------------------------------------------------------------------- */
typedef struct { uint64_t slot; uint8_t d[64]; } ovl_ent;

typedef struct opw {
    opst     *p;
    uint8_t   hdr[564];
    uint64_t  bid_next_p, bid_next_b, eof, amap_last, cb_amap_free, eof_start;
    uint32_t  unique;
    uint64_t  nbt_bid, nbt_ib, bbt_bid, bbt_ib;
    /* overlay of pending 64-byte slots */
    ovl_ent  *ov;  size_t nov, capov;
    uint32_t *ovidx;  size_t ovidxcap;
    /* allocation maps */
    uint32_t  nsec;
    uint8_t **amaps;  size_t capamaps;          /* 496-byte bitmaps, NULL = not loaded */
    uint8_t  *dirty;                            /* per section */
    int32_t  *freecnt;                          /* per section: upper bound for the longest free run, -1 = unknown */
    int64_t   free_delta;
    int       reconciled;
    uint64_t  reconciled_fixes;
    uint8_t   mpbb_r[256];                      /* encoding permutation (encryption method 1) */
    int       crypt;
} opw;

/* begin / end of a transaction on a handle opened with OPST_OPEN_WRITE; commit refreshes the reader's caches */
const uint8_t *op_mpbb_table(void);                /* op_ndb.c */
int op_reload(opst *p);
int op_journal_recover(const char *path, int *recovered);
int  op_txn_begin(opst *p, opw **out);          /* the handle keeps one transaction object; returns it */
int  op_txn_commit(opst *p);
void op_txn_abort(opst *p);                     /* throws away everything pending */

int  opw_read(opw *w, uint64_t off, size_t n, uint8_t *buf);
int  opw_write(opw *w, uint64_t off, const void *data, size_t n);        /* off and n multiples of 64 */
int  opw_alloc(opw *w, size_t size, size_t align, uint64_t *ib);
int  opw_free(opw *w, uint64_t ib, size_t size);
int  opw_grow(opw *w, unsigned add);
int  opw_is_allocated(opw *w, uint64_t ib, size_t size);
uint64_t opw_alloc_page_bid(opw *w);
int  opw_validate(opw *w, bbuf *report, size_t *nproblems);

/* B-trees: nbt != 0 -> node tree (32-byte leaf entries, key = nid), else block tree (24-byte entries, key = bid) */
int  bt_get(opw *w, int nbt, uint64_t key, uint8_t *ent, int *found);
int  bt_put(opw *w, int nbt, uint64_t key, const uint8_t *ent);
int  bt_delete(opw *w, int nbt, uint64_t key, int *found);
typedef int (*bt_cb)(void *ctx, const uint8_t *ent);                    /* non-zero stops and is returned */
int  bt_items(opw *w, int nbt, bt_cb cb, void *ctx);

/* blocks */
typedef struct { uint64_t bid, ib; unsigned cb, cref; } bbt_e;
int  opw_bbt_entry(opw *w, uint64_t bid, bbt_e *out);
int  opw_read_block_raw(opw *w, uint64_t bid, opbuf *out);                    /* the stored bytes (still encrypted) */
int  opw_add_block(opw *w, const uint8_t *data, size_t cb, int internal, unsigned cref, uint64_t *bid);
int  opw_xblock(opw *w, int level, const uint64_t *ids, size_t n, uint32_t total, uint64_t *bid);
int  opw_add_ref(opw *w, uint64_t bid);
int  opw_release(opw *w, uint64_t bid);
int  opw_bbt_drop(opw *w, uint64_t bid);                       /* BBT entry removed and its space freed; children untouched */
int  opw_tree_bytes(opw *w, uint64_t bid, uint64_t *out, int depth);
int  opw_bbt_set_cref(opw *w, uint64_t bid, unsigned cref);
typedef struct { uint64_t *orphan; size_t norphan, caporphan; uint64_t *fix_bid; unsigned *fix_cref; size_t nfix, capfix; } refsfix;
int  refs_collect(opw *w, refsfix *out);                       /* op_check.c: orphan blocks and blocks whose reference count is wrong */
void refsfix_free(refsfix *r);

/* nodes */
typedef struct { uint64_t bd, bs; uint32_t parent; } nbt_e;
int  opw_node(opw *w, uint32_t nid, nbt_e *out);                              /* OPST_E_NOTFOUND when absent */
int  opw_node_put(opw *w, uint32_t nid, uint64_t bd, uint64_t bs, uint32_t parent);
int  opw_node_del(opw *w, uint32_t nid);

/* ---- decoded node data ------------------------------------------------------------------------------------------------ */
typedef struct { opbuf *b; size_t n; } hblocks;                      /* data blocks of a node (decrypted), each malloc'd */
void hb_free(hblocks *h);
int  hb_clone(const hblocks *src, hblocks *dst);
int  opw_leaf_blocks(opw *w, uint64_t bid, hblocks *out);            /* expands XBLOCKs; decrypts */
int  opw_put_blocks(opw *w, const hblocks *blocks, uint64_t *top);   /* store as data tree (Editor.put_blocks) */
int  opw_put_bytes(opw *w, const uint8_t *d, size_t n, uint64_t *top);   /* single block data tree helper (n <= 8176) */
typedef struct { uint32_t nid; uint64_t bd, bs; } wsub;
typedef struct { wsub *e; size_t n; } wsubs;
void wsubs_free(wsubs *s);
int  opw_subnodes(opw *w, uint64_t bs, wsubs *out);                   /* single SLBLOCK only; sorted by nid */
int  opw_put_subnodes(opw *w, const wsub *e, size_t n, uint64_t *bs); /* new SLBLOCK from (sorted) entries; *bs = 0 when n == 0 */
typedef void (*patch_fn)(hblocks *blocks, void *ctx);
int  opw_clone_node(opw *w, uint64_t bd, uint64_t bs, patch_fn patch, void *ctx, uint64_t *nbd, uint64_t *nbs);

/* ---- heap-on-node ---------------------------------------------------------------------------------------------------- */
int  heap_loc(const hblocks *h, uint32_t hid, size_t *blk, size_t *s, size_t *e);
int  heap_get(const hblocks *h, uint32_t hid, const uint8_t **d, size_t *n);      /* pointer into the block */
unsigned heap_client(const hblocks *h);
uint32_t heap_root(const hblocks *h);
int  heap_is_heap(const hblocks *h);
/* walks a BTH; cb(key, value) for each leaf record in order */
typedef int (*bth_cb)(void *ctx, const uint8_t *key, const uint8_t *val);
int  heap_bth_walk(const hblocks *h, uint32_t hdr_hid, unsigned *cbk, unsigned *cbe, bth_cb cb, void *ctx);
int  heap_find_record(const hblocks *h, uint32_t bth_hid, const uint8_t *key, size_t *blk, size_t *off);   /* off of the value */

int  heap_grow_item(hblocks *h, uint32_t hid, const uint8_t *data, size_t n);      /* 1 = done, 0 = no room, <0 = error */
int  heap_append_item(hblocks *h, const uint8_t *data, size_t n, uint32_t *hid);
int  heap_bth_repoint(hblocks *h, uint32_t bth_hid, uint32_t old_hid, uint32_t new_hid);   /* 1 = found */
int  heap_bth_insert(hblocks *h, uint32_t key, uint32_t val);                      /* message index BTH at heap item 0x40 */
unsigned heap_fill_level(size_t free_bytes);

/* heap builder: items are collected per block, finalize() lays out the blocks (page maps, fill levels) */
typedef struct { uint8_t *p; size_t n; } hitem;
typedef struct { hitem *it; size_t n, cap; size_t used; } hbblk;
typedef struct { unsigned client; hbblk *blk; size_t nblk, capblk; } hbuild;
int  hbuild_init(hbuild *b, unsigned client);
void hbuild_free(hbuild *b);
int  hbuild_alloc(hbuild *b, const uint8_t *data, size_t n, uint32_t *hid);        /* data NULL = n zero bytes */
int  hbuild_set(hbuild *b, uint32_t hid, const uint8_t *data, size_t n);           /* same size as the placeholder */
int  hbuild_finalize(hbuild *b, uint32_t user_root, hblocks *out);
typedef struct { const uint8_t *k; const uint8_t *v; } kv;
/* BTH from sorted records; when hdr_hid is non-zero it is the already allocated 8-byte header placeholder, otherwise one is allocated */
int  hbuild_patch_bth_leaf(hbuild *b, uint32_t hid, const kv *recs, size_t n, unsigned cbkey, unsigned cbent);
int  build_bth(hbuild *b, unsigned cbkey, unsigned cbent, const kv *recs, size_t n, uint32_t hdr_hid, uint32_t *out_hdr_hid);

/* ---- property contexts (heap-stored values only) --------------------------------------------------------------------- */
typedef struct { uint16_t pid, ptype; bbuf v; uint32_t ext_nid; } pcprop;      /* ext_nid != 0: the value is in the subnode with that nid (v is unused) */
typedef struct { pcprop *p; size_t n, cap; } pcprops;
void pcprops_free(pcprops *p);
int  pcprops_get(opw *w, uint32_t nid, pcprops *out);
int  pcprops_get_ex(opw *w, uint32_t nid, pcprops *out, int lenient);     /* lenient 1: properties stored in subnodes are skipped; 2: kept as references (ext_nid), so pc_store can write the context back */
pcprop *pcprops_find(pcprops *p, unsigned pid);
int  pcprops_del(pcprops *p, unsigned pid);
int  pcprops_set(pcprops *p, unsigned pid, unsigned ptype, const uint8_t *v, size_t n);
int  pc_build(const pcprops *p, hblocks *out);
int  pc_store(opw *w, uint32_t nid, const pcprops *p);

/* ---- table contexts ------------------------------------------------------------------------------------------------ */
typedef struct { uint16_t pid, ptype, ibd; uint8_t cbd, ibit; } tcol;
typedef struct {
    uint32_t rowid;
    uint8_t *present;                         /* per column: 0/1 */
    bbuf    *cell;                            /* per column, valid when present */
} tcrow;
typedef struct {
    tcol    *cols;  size_t ncols;
    uint16_t rgib[4];
    tcrow   *rows;  size_t nrows, caprows;
    uint32_t rows_nid;                        /* subnode that held the row matrix (0 = heap) */
} tctx;
void tc_free(tctx *t);
int  tc_col(const tctx *t, unsigned pid);                      /* column index or -1 */
int  tc_find(const tctx *t, uint32_t rowid);                   /* row index or -1 */
int  tc_remove(tctx *t, uint32_t rowid);
int  tc_add_row(tctx *t, uint32_t rowid);                      /* appends an empty row; index = nrows - 1 */
int  tc_set_cell(tctx *t, size_t row, int col, const uint8_t *d, size_t n);
int  tc_copy_row(tctx *dst, const tctx *src, size_t srow, uint32_t newid);           /* cells matched by property id */
int  tc_build(const tctx *t, hblocks *heap, hblocks *rowblocks, uint32_t *rows_nid);
/* a cell value too large for the heap (more than OP_MAXALLOC bytes) lives in a subnode of the table node; tc_build_ex allocates the local NID and hands the data back */
typedef struct { uint32_t nid; uint8_t *p; size_t n; uint32_t rowid; uint16_t pid; } tcbig;     /* rowid / pid: the cell's row and property, to find the message property that holds the same value */
int  tc_build_ex(const tctx *t, opw *w, hblocks *heap, hblocks *rowblocks, uint32_t *rows_nid, tcbig **big, size_t *nbig);   /* w and big may be NULL: large cells are then an error */
void tcbig_free(tcbig *b, size_t n);
int  tcbig_put(opw *w, const tcbig *b, size_t n, wsub *out);                  /* stores the data trees; out[i] = {nid, top, 0} */
int  tc_clear_rows(tctx *t);
int  tc_row_pidvals(const tctx *t, size_t row, pcprops *out);          /* copies of all cells of a row keyed by property id */
int  tc_add_by_pid(tctx *t, uint32_t rowid, const pcprops *vals);      /* new row from pid -> value (unknown columns dropped) */

/* ---- editor: load / store a table context node ---------------------------------------------------------------------- */
int  ed_load_tc(opw *w, uint32_t nid, tctx *out);
int  ed_store_tc(opw *w, uint32_t nid, tctx *tc);

/* ---- operations (port of pstops.py / pstidmap.py / pstfolders.py) --------------------------------------------------- */
typedef struct idmap idmap;
typedef struct { const uint8_t *topic; size_t topic_len; const uint32_t *nids; size_t n; } idxgroup;   /* messages without a relative in the message index */
typedef struct { uint32_t nid; uint32_t parent; } nidpair;
typedef struct ops {
    opst  *p;
    opw   *w;
    idmap *im;                         /* loaded lazily */
    int    im_loaded;
    uint32_t deleted;                  /* NID of Deleted Items (0 = unknown) */
} ops;

int  ops_begin(opst *p, ops *o);                       /* starts a transaction */
int  ops_commit(ops *o);                               /* flushes the ID map, commits, refreshes the reader */
void ops_abort(ops *o);
idmap *ops_idmap(ops *o, int *rc);
int  idmap_present(idmap *m);
int  idmap_add(idmap *m, const uint8_t guid[16], uint32_t nid);
int  idmap_zero_nid(idmap *m, uint32_t nid);
int  idmap_flush(ops *o);
void idmap_free(idmap *m);
size_t idmap_count(idmap *m);
int  idmap_get(idmap *m, size_t i, uint8_t guid[16], uint32_t *nid);
int  idmap_lookup(idmap *m, const uint8_t guid[16], uint32_t *nid);

uint32_t ops_next_row_ver(ops *o);
int  ops_counts(ops *o, uint32_t folder, int d_cnt, int d_unread);
int  ops_unread(const pcprops *vals);
int  ops_set_msg_state(ops *o, const uint32_t *nids, size_t n, int read, int flag);
int  ops_import_msgs(ops *o, uint32_t folder, const opst_import_msg *msgs, size_t n, uint32_t *nids);
int  ops_move_msgs(ops *o, const uint32_t *nids, size_t n, uint32_t dest, size_t *moved);
int  ops_copy_msgs(ops *o, const uint32_t *nids, size_t n, uint32_t dest, int share, int ids, int fresh, uint32_t *new_nids);
int  ops_purge_msgs(ops *o, const uint32_t *nids, size_t n);
int  ops_delete_msgs(ops *o, const uint32_t *nids, size_t n, size_t *moved, size_t *purged);
typedef struct { uint32_t key, nid; } keyednid;                      /* message -> key of its bucket in the message index */
int  msg_conv_key(opw *w, uint32_t nid, uint32_t *key);               /* key from the conversation GUID; 0 = message has no conversation index */
int  ops_note_max_message_nid(ops *o, const nidpair *pairs, size_t npairs, const idxgroup *groups, size_t ngroups, const keyednid *keyed, size_t nkeyed);
int  ops_bump_hwm(ops *o, const uint32_t *nids, size_t n);
int  opw_amap_state(opw *w, uint64_t *unalloc, uint64_t *computed_free, uint64_t *header_free);
int  opw_amap_repair(opw *w);
int  ops_pc_add(ops *o, uint32_t nid, unsigned pid, int delta, int *found);
int  ops_hier_add(ops *o, uint32_t folder, int d_cnt, int d_unread);

/* name-to-id map (op_npm.c) */
typedef struct namemap namemap;
typedef struct { uint8_t guid[16]; int has_guid; int is_string; uint32_t num; char *name; } nm_name;   /* name: UTF-8, malloc'd by nm_name_of */
int  nm_load(opw *w, namemap **out);
void nm_free(namemap *m);
int  nm_present(namemap *m);
size_t nm_count(const namemap *m);
int  nm_name_of(const namemap *m, unsigned propid, nm_name *out);              /* 1 found, 0 absent, <0 error */
int  nm_add(namemap *m, const nm_name *n, unsigned *propid);                   /* existing id or a new one */
int  nm_save(ops *o, namemap *m);
size_t nm_subnids(const namemap *m, uint32_t *out, size_t cap);

/* read-only access to a handle's file through the writer machinery (used for the source of a cross-file copy) */
int  op_ro_begin(opst *p, opw **out);
void op_ro_end(opw *w);
int  ops_begin_ro(opst *p, ops *o);
void ops_end_ro(ops *o);
int  op_path_same(const char *a, const char *b);                               /* platform: do both paths name the same file? */

/* fixer and cross-file copy (op_fix.c, op_xcopy.c) */
int  fix_replica_blob(ops *o, bbuf *out, int *found);
int  fix_run(ops *o, int apply, opst_fix_report *rep);
int  xc_copy(ops *dst, opst *src, const uint32_t *nids, size_t n, uint32_t dest, uint32_t *new_nids);

int  fo_create(ops *o, uint32_t parent, const char *name, const char *cls, uint32_t *nid);
long utf8_to_utf16(const char *s, bbuf *out);                                  /* op_folders.c: UTF-8 -> UTF-16LE, code points or -1 */
int  set_has_sub(ops *o, uint32_t nid);
int  fo_make_tables(ops *o, uint32_t nid, uint32_t parent, int only_missing);
int  fo_add_row(ops *o, uint32_t nid, uint32_t parent, const bbuf *nm, const bbuf *cl, const pcprops *mirror);
int  op_create_file(const char *path, const char *display_name, const char *top_name);            /* op_create.c */
int  fo_rename(ops *o, uint32_t nid, const char *name);
/* every cell a contents row must carry: for each column of the table the row lacks, the message's own value, else the default SCANPST writes
   (0x0E03 empty, 0x1080 -1, 0x300B a 16-byte key). apply = 0 only counts. Returns the number of cells added / missing, or a negative error. */
int  row_fill_missing(tctx *tc, size_t r, pcprops *mp, int apply);
int  row_fill_node(opw *w, tctx *tc, uint32_t rowid);
int  row_conv_key(const tctx *tc, uint32_t rowid, uint32_t *key);
int  row_sync_conv_id(opw *w, tctx *tc, uint32_t rowid);
uint32_t conv_key_of_guid(const uint8_t *g);
int  ops_set_i32(ops *o, uint32_t nid, unsigned pid, uint32_t v);
int  fo_repair_all(ops *o, int apply, int *found, void (*say)(void *, const char *), void *ctx);
int  fo_move(ops *o, uint32_t nid, uint32_t dest, int *moved);
int  fo_purge(ops *o, uint32_t nid, opst_purge_stats *st);
int  fo_delete(ops *o, uint32_t nid, int *permanent, opst_purge_stats *st);
int  fo_is_protected(ops *o, uint32_t nid, int *prot);

#endif
