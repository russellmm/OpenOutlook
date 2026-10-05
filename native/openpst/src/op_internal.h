/* op_internal.h - internal declarations shared by the OpenPST source files (not part of the public API). */
#ifndef OP_INTERNAL_H
#define OP_INTERNAL_H

#if !defined(_WIN32)
#  ifndef _GNU_SOURCE
#    define _GNU_SOURCE 1
#  endif
#  ifndef _FILE_OFFSET_BITS
#    define _FILE_OFFSET_BITS 64
#  endif
#endif

#if defined(_WIN32) && !defined(_CRT_RAND_S)
#  define _CRT_RAND_S                /* rand_s() for op_random() */
#endif

#include "openpst.h"
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#if defined(_MSC_VER)
#  define OP_TLS __declspec(thread)
#  define OP_PRINTF(a, b)
#elif defined(__GNUC__)
#  define OP_TLS __thread
#  define OP_PRINTF(a, b) __attribute__((format(printf, a, b)))
#else
#  define OP_TLS _Thread_local
#  define OP_PRINTF(a, b)
#endif

/* qsort that tolerates a NULL base with no (or one) element (UBSAN: null passed to a nonnull parameter) */
static inline void op_qsort(void *b, size_t n, size_t sz, int (*c)(const void *, const void *)) { if (n > 1) qsort(b, n, sz, c); }

/* ---- little-endian readers ---------------------------------------------------------------------------------- */
static inline uint16_t op_u16(const uint8_t *p) { return (uint16_t)(p[0] | (p[1] << 8)); }
static inline uint32_t op_u32(const uint8_t *p) {
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
}
static inline uint64_t op_u64(const uint8_t *p) { return (uint64_t)op_u32(p) | ((uint64_t)op_u32(p + 4) << 32); }

/* ---- errors ------------------------------------------------------------------------------------------------- */
int op_err(int code, const char *fmt, ...) OP_PRINTF(2, 3);   /* records the message, returns `code` */

/* ---- platform file access ------------------------------------------------------------------------------------ */
typedef struct opfile opfile;
int      op_file_open(const char *path_utf8, int writable, opfile **out);
void     op_file_close(opfile *f);
uint64_t op_file_size(opfile *f);
int      op_file_pread(opfile *f, void *buf, size_t n, uint64_t off);    /* 0 on success, OPST_E_IO otherwise */

/* writing (file must have been opened writable) */
int      op_file_pwrite(opfile *f, const void *buf, size_t n, uint64_t off);   /* extends the file when needed */
int      op_file_sync(opfile *f);                                              /* flush to stable storage */
int      op_file_truncate(opfile *f, uint64_t size);
int      op_file_exists(const char *path_utf8);
int      op_file_remove(const char *path_utf8);
int      op_file_write_all(const char *path_utf8, const void *buf, size_t n);  /* create / replace, flushed */
int      op_file_read_all(const char *path_utf8, uint8_t **buf, size_t *n);    /* malloc'd; OPST_E_IO when missing */
int      op_random(void *buf, size_t n);          /* OS random bytes (OPST_TEST_RANDOM=1 gives a fixed stream for tests) */

/* ANSI code page -> UTF-8 using the operating system (iconv / MultiByteToWideChar). Returns malloc'd string or NULL. */
char *op_os_ansi_to_utf8(const uint8_t *s, size_t n, unsigned codepage, size_t *outlen);

/* ---- utilities ------------------------------------------------------------------------------------------------ */
uint32_t op_crc(const uint8_t *p, size_t n);
char    *op_utf16_to_utf8(const uint8_t *s, size_t nbytes, size_t *outlen);          /* malloc'd, NUL terminated */
char    *op_ansi_to_utf8(const uint8_t *s, size_t n, unsigned codepage, size_t *outlen);
uint32_t op_lower(uint32_t cp);                                                       /* simple case folding */
int      op_stricmp_utf8(const char *a, const char *b);                               /* case-insensitive compare */
int      op_ci_prefix(const char *s, size_t n, const char *lit_lowercase);             /* ASCII case-insensitive prefix test */
char    *op_fold_text(const char *s, size_t n, size_t *outlen);                       /* search folding (accents, case) */
int      op_lzfu(const uint8_t *in, size_t n, uint8_t **out, size_t *outlen);         /* compressed RTF -> bytes */

/* A growing array of fixed-size elements plus a string pool. The elements hold string offsets (cast to pointers)
   while being built; op_list_finish() turns them into real pointers and gives out one block. */
typedef struct oplist {
    uint8_t *base;               /* header + elements */
    size_t   n, cap, esz;
    char    *pool;
    size_t   plen, pcap;
} oplist;
int    op_list_init(oplist *l, size_t esz);
void  *op_list_push(oplist *l);                       /* zeroed new element or NULL */
size_t op_list_str(oplist *l, const char *s, size_t n);   /* copies, returns the offset; 0 is "" */
#define OP_LIST_AT(l, T, i) ((T *)((l)->base + 16 + (i) * (l)->esz))
void  *op_list_finish(oplist *l);                     /* detach; elements start at the returned pointer */
void   op_list_abort(oplist *l);
void   op_arr_free(void *arr);
/* convert an offset stored in a pointer field to a real pointer */
#define OP_FIX(l, field) ((field) = (const char *)((l)->pool + (size_t)(uintptr_t)(field)))

/* ---- NDB layer ----------------------------------------------------------------------------------------------- */
typedef struct { uint32_t nid; uint32_t parent; uint64_t bd, bs; } op_nbt_ent;
typedef struct { uint64_t bid; uint64_t ib; uint16_t cb; } op_bbt_ent;

struct opst {
    opfile     *f;
    char       *path;
    uint64_t    fsize;
    unsigned    ver;
    int         crypt;
    uint8_t     header[564];
    op_nbt_ent *nbt;  size_t nnbt, capnbt;
    op_bbt_ent *bbt;  size_t nbbt, capbbt;
    uint64_t    page_crc_errors;
    char       *display_name;
    uint32_t    ipm_root, deleted;
    unsigned    store_cp;
    int         writable;
    int         reconciled;      /* the allocation maps were reconciled with the B-trees by an earlier committed transaction of this handle */
    int         recovered;           /* a leftover journal was rolled back when the file was opened */
    int         journal_pending;     /* read-only open and a journal of an interrupted write exists: the file may be inconsistent */
    struct opw *w;               /* the current write transaction, if any */
};

typedef struct { uint8_t *p; size_t n; } opbuf;
typedef struct { opbuf *b; size_t n; } opblocks;          /* decrypted data blocks of a node */
typedef struct { uint32_t nid; uint64_t bd, bs; } opsub;
typedef struct { opsub *e; size_t n; } opsubs;
typedef struct { uint32_t nid; uint64_t bd, bs; } opnode;

const op_nbt_ent *op_nbt_find(opst *p, uint32_t nid);
const op_bbt_ent *op_bbt_find(opst *p, uint64_t bid);
int  op_node_get(opst *p, uint32_t nid, opnode *out);
int  op_read_raw(opst *p, uint64_t bid, opbuf *out);                /* raw (still encrypted) block bytes, malloc'd */
int  op_blocks_load(opst *p, uint64_t bid, opblocks *out);          /* all data blocks (XBLOCK expanded), decrypted */
void op_blocks_free(opblocks *bl);
int  op_subs_load(opst *p, uint64_t bsub, opsubs *out);
void op_subs_free(opsubs *s);
const opsub *op_subs_find(const opsubs *s, uint32_t nid);
int  op_data_concat(const opblocks *bl, uint8_t **out, size_t *n);  /* one malloc'd buffer */

/* ---- LTP layer ----------------------------------------------------------------------------------------------- */
typedef struct {
    opst     *p;
    opnode    node;
    opblocks  bl;
    uint8_t   client_sig;
    uint32_t  user_root;
    opsubs    subs;  int subs_loaded;
} opheap;

int  op_heap_open(opst *p, const opnode *node, opheap *h);
void op_heap_close(opheap *h);
int  op_heap_get(const opheap *h, uint32_t hid, const uint8_t **d, size_t *n);
typedef int (*op_bth_cb)(void *ctx, const uint8_t *key, const uint8_t *val);
int  op_bth_walk(const opheap *h, uint32_t hid, unsigned *cbkey, unsigned *cbent, op_bth_cb cb, void *ctx);
/* Value reference: HID into heap (pointer into the heap) or NID of a subnode (then *tofree is a malloc'd copy). */
int  op_heap_resolve(opheap *h, uint32_t hnid, const uint8_t **d, size_t *n, void **tofree);
const opsubs *op_heap_subs(opheap *h);

typedef struct {
    uint16_t pid, ptype;
    uint32_t hnid;               /* raw 4 bytes (inline value or HNID) */
    uint8_t  inl[4];             /* the same 4 bytes, for inline values */
    const uint8_t *d;  size_t n;
    int      state;              /* 0 unresolved, 1 resolved, 2 resolved+owned (d is malloc'd) */
} oppc_prop;
typedef struct {
    opheap     heap;
    oppc_prop *props;  size_t n;
} oppc;
int  op_pc_open(opst *p, const opnode *node, oppc *pc);
void op_pc_close(oppc *pc);
oppc_prop *op_pc_find(oppc *pc, uint16_t pid);
int  op_pc_value(oppc *pc, oppc_prop *pr);              /* resolve lazily; 0 on success */

typedef struct { uint16_t pid, ptype, ibdata; uint8_t cbdata, ibit; } optc_col;
typedef struct {
    opheap      heap;
    optc_col   *cols;  size_t ncols;
    uint16_t    rgib[4];
    uint32_t    rowsize;
    size_t      nrows;
    uint32_t   *rowid;
    uint32_t   *rowidx;
    opblocks    rowblocks;  int rows_in_sub;
    const uint8_t *rowheap;  size_t rowheap_n;
} optc;
int  op_tc_open(opst *p, const opnode *node, optc *tc);
void op_tc_close(optc *tc);
const uint8_t *op_tc_row(const optc *tc, size_t i);      /* pointer to row data (rowsize bytes) or NULL */
int  op_tc_col(const optc *tc, uint16_t pid);            /* column index or -1 */
/* cell: returns 1 when present. *tofree must be freed with free() by the caller (may be NULL). */
int  op_tc_cell(optc *tc, const uint8_t *row, int col, uint16_t *ptype, const uint8_t **d, size_t *n, void **tofree);

/* decode helpers for a value of the given MAPI type into UTF-8 (malloc'd), NULL for non-string types */
char *op_value_to_utf8(uint16_t ptype, const uint8_t *d, size_t n, unsigned codepage, size_t *outlen);
int64_t op_value_i64(uint16_t ptype, const uint8_t *d, size_t n, int64_t dflt);

#define OP_NID_STORE      0x21u
#define OP_NID_ROOT       0x122u
#define OP_NID_RECIP_TBL  0x692u
#define OP_NID_ATTACH_TBL 0x671u

#endif
