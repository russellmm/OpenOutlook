/*
 * openpst.h - public C API of OpenPST, a from-scratch library for reading (and, in later stages, editing in place)
 * Outlook Unicode PST files (MS-PST, wVer 23).  Plain C11, no third-party dependencies.
 *
 * Design rules (so the API is easy to call from C, C++, C#/.NET P/Invoke, Python ctypes, ...):
 *   - opaque handles, plain structs, fixed-width integer types, no callbacks required;
 *   - all strings are UTF-8, NUL terminated;
 *   - every function returns OPST_OK (0) or a negative OPST_E_* code; opst_last_error() gives a readable message
 *     (per thread, valid until the next call on the same thread);
 *   - arrays/lists returned by the library are released with the matching opst_free_* function;
 *   - times are Windows FILETIME values (100 ns ticks since 1601-01-01 UTC, 0 = not set); opst_filetime_to_unix() converts.
 *   - a handle must not be used from two threads at the same time (different handles are independent).
 */
#ifndef OPENPST_H
#define OPENPST_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32) && defined(OPST_SHARED)
#  ifdef OPST_BUILD
#    define OPST_API __declspec(dllexport)
#  else
#    define OPST_API __declspec(dllimport)
#  endif
#elif defined(__GNUC__) && defined(OPST_SHARED)
#  define OPST_API __attribute__((visibility("default")))
#else
#  define OPST_API
#endif

#define OPST_VERSION_MAJOR 0
#define OPST_VERSION_MINOR 3
#define OPST_VERSION_PATCH 0

/* ---- error codes -------------------------------------------------------------------------------------------- */
enum {
    OPST_OK            = 0,
    OPST_E_IO          = -1,   /* file could not be opened / read / written */
    OPST_E_FORMAT      = -2,   /* not a PST file, or the file is damaged */
    OPST_E_UNSUPPORTED = -3,   /* ANSI (wVer < 23) or 4K-page (wVer >= 36) files, unknown encryption */
    OPST_E_NOTFOUND    = -4,   /* node / folder / message / property does not exist */
    OPST_E_NOMEM       = -5,
    OPST_E_ARG         = -6,   /* bad argument */
    OPST_E_STATE       = -7,   /* operation not allowed in this state (e.g. file opened read-only) */
    OPST_E_REFUSED     = -8    /* operation refused by a safety rule (e.g. changing a special folder) */
};

typedef struct opst opst;          /* an open PST file */
typedef struct opst_msg opst_msg;  /* an open message */

OPST_API const char *opst_version(void);
OPST_API const char *opst_last_error(void);

/* ---- opening ------------------------------------------------------------------------------------------------- */
#define OPST_OPEN_READONLY 0u
#define OPST_OPEN_WRITE    1u    /* read + write; a leftover journal of an interrupted write is rolled back on open */
OPST_API int  opst_open(const char *path_utf8, unsigned flags, opst **out);
OPST_API void opst_close(opst *p);

OPST_API const char *opst_display_name(opst *p);      /* the store's name; owned by the handle */
OPST_API uint32_t    opst_root_folder(opst *p);        /* NID of the root folder (not shown by Outlook) */
OPST_API uint32_t    opst_ipm_root(opst *p);           /* NID of "Top of Outlook data file" (0 when absent) */
OPST_API uint32_t    opst_deleted_items(opst *p);      /* NID of Deleted Items (0 when unknown) */

/* ---- folders ------------------------------------------------------------------------------------------------- */
typedef struct opst_folder_info {
    uint32_t    nid;
    uint32_t    parent;          /* NID of the parent folder */
    const char *name;            /* UTF-8, "(unnamed)" when empty; owned by the array */
    int32_t     content_count;   /* number of messages (from the folder's property context) */
    int32_t     unread_count;
    int32_t     has_subfolders;  /* 0 / 1 */
} opst_folder_info;

/* Children of folder `parent`, sorted case-insensitively by name. Release with opst_free_folders(). */
OPST_API int  opst_folder_children(opst *p, uint32_t parent, opst_folder_info **out, size_t *count);
OPST_API void opst_free_folders(opst_folder_info *arr);
OPST_API int  opst_folder_info_get(opst *p, uint32_t nid, opst_folder_info *out_struct, char *name_buf, size_t name_cap);
/* "Inbox/Projects" style path below `start` (0 = root folder); case-insensitive. */
OPST_API int  opst_folder_find(opst *p, uint32_t start, const char *path_utf8, uint32_t *nid_out);

/* ---- message lists (one row per message of a folder, from its contents table - fast) ---------------------- */
#define OPST_MSGFLAG_READ      0x0001
#define OPST_MSGFLAG_UNSENT    0x0008
#define OPST_MSGFLAG_HASATTACH 0x0010

typedef struct opst_msg_row {
    uint32_t    nid;
    uint32_t    flags;           /* PidTagMessageFlags */
    int64_t     sent;            /* FILETIME or 0 */
    int64_t     received;        /* FILETIME or 0 */
    int64_t     size;            /* PidTagMessageSize */
    int32_t     importance;
    int32_t     has_attachments;
    const char *subject;         /* without Outlook's prefix control characters; owned by the array */
    const char *sender;          /* display name of the sender ("" when unknown) */
    const char *to;              /* display-to */
    const char *cc;
    const char *topic;           /* conversation topic */
    const char *message_class;
    int32_t     flag_status;     /* PidTagFlagStatus: 0 none, 1 complete, 2 flagged */
} opst_msg_row;

OPST_API int  opst_messages(opst *p, uint32_t folder, opst_msg_row **out, size_t *count);
OPST_API void opst_free_messages(opst_msg_row *arr);

/* ---- one message --------------------------------------------------------------------------------------------- */
OPST_API int  opst_msg_open(opst *p, uint32_t nid, opst_msg **out);
OPST_API void opst_msg_close(opst_msg *m);

/* Well-known property ids (PidTag...). Any id of the message's property context can be asked for. */
#define OPST_PID_SUBJECT          0x0037
#define OPST_PID_SENT_NAME        0x0042
#define OPST_PID_TRANSPORT_HDRS   0x007D
#define OPST_PID_SENDER_NAME      0x0C1A
#define OPST_PID_SENDER_EMAIL     0x0C1F
#define OPST_PID_SENDER_SMTP      0x5D01
#define OPST_PID_BCC              0x0E02
#define OPST_PID_CC               0x0E03
#define OPST_PID_TO               0x0E04
#define OPST_PID_RECEIVED         0x0E06
#define OPST_PID_FLAGS            0x0E07
#define OPST_PID_SIZE             0x0E08
#define OPST_PID_SENT             0x0039
#define OPST_PID_BODY_TEXT        0x1000
#define OPST_PID_BODY_RTF         0x1009
#define OPST_PID_BODY_HTML        0x1013
#define OPST_PID_CLASS            0x001A
#define OPST_PID_TOPIC            0x0070

/* Property as UTF-8 text (string properties; other types give NULL). Owned by the message handle. The Outlook subject
   prefix characters are removed for OPST_PID_SUBJECT. */
OPST_API const char *opst_msg_str(opst_msg *m, uint16_t pid);
/* Raw property bytes + MAPI type (e.g. 0x1F string, 0x03 int32, 0x40 time, 0x102 binary). Owned by the message handle. */
OPST_API int         opst_msg_prop(opst_msg *m, uint16_t pid, uint16_t *ptype, const uint8_t **data, size_t *len);
OPST_API int64_t     opst_msg_i64(opst_msg *m, uint16_t pid, int64_t dflt);     /* int16/int32/int64/bool/FILETIME */

typedef enum { OPST_BODY_TEXT = 0, OPST_BODY_HTML = 1, OPST_BODY_RTF = 2 } opst_body_kind;
/* Body as bytes: TEXT/HTML as UTF-8, RTF decompressed (7-bit RTF text). NULL when the body kind is absent. Owned by the message. */
OPST_API const char *opst_msg_body(opst_msg *m, opst_body_kind kind, size_t *len);
OPST_API unsigned    opst_msg_bodies(opst_msg *m);     /* bit mask: 1 << OPST_BODY_xxx for each body present */

/* Plain text of the message for display / indexing: the text body, else the HTML body with tags removed, else the RTF body
   with formatting removed (RTF that encapsulates HTML is de-encapsulated first). NULL when the message has no body at all.
   Owned by the message. */
OPST_API const char *opst_msg_text(opst_msg *m, size_t *len);
/* HTML for display: the HTML body, else the RTF body converted to HTML (formatting, tables, links and PNG/JPEG pictures are
   kept; the result is a complete page). NULL when there is neither an HTML nor an RTF body. Owned by the message. */
OPST_API const char *opst_msg_html(opst_msg *m, size_t *len);

typedef struct opst_recipient {
    const char *name;
    const char *email;
    int32_t     type;            /* 1 To, 2 Cc, 3 Bcc */
} opst_recipient;
OPST_API int  opst_msg_recipients(opst_msg *m, opst_recipient **out, size_t *count);
OPST_API void opst_free_recipients(opst_recipient *arr);

typedef struct opst_attachment {
    uint32_t    index;           /* use with opst_attachment_data() */
    uint32_t    nid;
    const char *filename;
    const char *mime;
    const char *cid;             /* Content-ID for inline images, "" otherwise */
    int64_t     size;
    int32_t     method;          /* 1 = by value (data stored in the file), 5 = embedded message, 6 = OLE, ... */
    int32_t     hidden;          /* PidTagAttachmentHidden */
} opst_attachment;
OPST_API int  opst_msg_attachments(opst_msg *m, opst_attachment **out, size_t *count);
OPST_API void opst_free_attachments(opst_attachment *arr);
OPST_API int  opst_attachment_data(opst_msg *m, uint32_t index, const uint8_t **data, size_t *len);   /* owned by the message */

/* ---- text conversion (usable without a PST file) -------------------------------------------------------------------- */
/* Results are malloc'd UTF-8, NUL terminated; release with opst_free(). `len` is the input length in bytes. */
OPST_API int opst_rtf_to_html(const char *rtf, size_t len, char **out, size_t *out_len);   /* complete HTML page */
OPST_API int opst_rtf_to_text(const char *rtf, size_t len, char **out, size_t *out_len);
OPST_API int opst_html_to_text(const char *html, size_t len, char **out, size_t *out_len); /* tags -> space, entities decoded */

/* ---- search (case- and accent-insensitive) -------------------------------------------------------------------------------- */
/* Query syntax: words and "exact phrases" must all match (in subject, sender, recipients - and in the body with
   OPST_SEARCH_BODY); -word must not match; from:x to:x cc:x subject:x body:x restrict a term to one field; has:attachment;
   is:unread / is:read; after:YYYY-MM-DD / before:YYYY-MM-DD (sent date, else received); folder:NAME (folders whose path
   contains the text). A bad date returns OPST_E_ARG with the reason in opst_last_error(). An empty query finds nothing. */
#define OPST_SEARCH_BODY          0x1u    /* unfielded terms also look into message bodies (slow: opens every candidate message) */
#define OPST_SEARCH_SKIP_DELETED  0x2u    /* leave out Deleted Items and its subfolders */

typedef void (*opst_search_progress)(void *user, size_t folder_index, size_t folder_count, const char *folder_path);

typedef struct opst_search_opts {
    uint32_t       size;          /* set to sizeof(opst_search_opts) */
    uint32_t       flags;         /* OPST_SEARCH_* */
    uint32_t       limit;         /* stop after this many hits; 0 = no limit */
    uint32_t       reserved;
    const uint32_t *folders;      /* only search these folders (NIDs); NULL = all folders */
    size_t         nfolders;
    volatile int32_t *cancel;     /* optional: another thread sets this non-zero to stop; hits found so far are returned */
    opst_search_progress progress;/* optional, called before each folder */
    void          *user;
} opst_search_opts;

typedef struct opst_hit {
    uint32_t    nid;
    uint32_t    folder;           /* NID of the folder holding the message */
    uint32_t    flags;
    int32_t     has_attachments;
    int64_t     sent, received;   /* FILETIME or 0 */
    int64_t     size;
    const char *folder_path;      /* "Inbox/Projects", relative to the root folder; owned by the array */
    const char *subject;
    const char *sender;
    const char *to;
} opst_hit;

/* `opts` may be NULL. Hits come in folder order (depth first, folders sorted by name), then in table order. */
OPST_API int  opst_search(opst *p, const char *query_utf8, const opst_search_opts *opts, opst_hit **out, size_t *count);
OPST_API void opst_free_hits(opst_hit *arr);

/* ---- writing (needs a handle opened with OPST_OPEN_WRITE; close Outlook first; work on a COPY of important files) -------------
 * Every function below is ONE atomic, journalled transaction: either everything it does is written, or the file is unchanged
 * (after a crash the next opst_open(..., OPST_OPEN_WRITE) rolls the interrupted write back). Read handles opened on the same
 * file see the new state after the call returns. Refused operations return OPST_E_REFUSED / OPST_E_ARG with a readable
 * message and change nothing. */
typedef struct opst_purge_stats {
    int32_t folders;             /* folders removed (the folder itself and its subfolders) */
    int32_t messages;            /* messages removed */
} opst_purge_stats;

OPST_API int opst_recovered(opst *p);        /* 1 when a leftover journal was rolled back while opening */
/* 1 when the file was opened READ-ONLY and a journal of an interrupted write exists next to it: what you read may be half written.
   Open the file once with OPST_OPEN_WRITE (it rolls the write back), then read again. */
OPST_API int opst_journal_pending(opst *p);

/* folders; the special folders of the store (root, Top of data file, Inbox, Deleted Items, ...) are protected */
OPST_API int opst_folder_is_protected(opst *p, uint32_t nid);                  /* 1 / 0, or a negative error */
OPST_API int opst_folder_create(opst *p, uint32_t parent, const char *name, const char *container_class /* NULL = "IPF.Note" */, uint32_t *nid_out);
OPST_API int opst_folder_rename(opst *p, uint32_t nid, const char *name);
OPST_API int opst_folder_move(opst *p, uint32_t nid, uint32_t dest_parent);
/* Outlook semantics: the first delete moves the folder to Deleted Items; a folder already inside it is removed for good
   (*permanent = 1, stats filled). */
OPST_API int opst_folder_delete(opst *p, uint32_t nid, int *permanent, opst_purge_stats *stats);
OPST_API int opst_folder_purge(opst *p, uint32_t nid, opst_purge_stats *stats);  /* remove for good, wherever it is */

/* messages (NIDs from opst_messages) */
OPST_API int opst_msgs_move(opst *p, const uint32_t *nids, size_t n, uint32_t dest_folder);
OPST_API int opst_msgs_copy(opst *p, const uint32_t *nids, size_t n, uint32_t dest_folder, uint32_t *new_nids /* n entries, may be NULL */);
OPST_API int opst_msgs_delete(opst *p, const uint32_t *nids, size_t n, size_t *moved, size_t *purged);   /* Outlook semantics */
/* read: -1 unchanged, 0 unread, 1 read.  flag: -1 unchanged, else PidTagFlagStatus (0 none, 1 complete, 2 flagged).
   Updates the messages, their contents-table rows and the unread counts of their folders in one transaction. */
OPST_API int opst_msgs_set_state(opst *p, const uint32_t *nids, size_t n, int read, int flag);
/* ---- importing a message built from plain fields (an EML file, a Graph message, ...) ---------------------------------------------- */
typedef struct opst_import_recipient {
    const char *name;                /* display name (UTF-8), may be empty */
    const char *email;               /* SMTP address */
    int32_t     type;                /* 1 To, 2 Cc, 3 Bcc */
} opst_import_recipient;
typedef struct opst_import_attachment {
    const char    *filename;
    const char    *mime;             /* e.g. "image/png", may be NULL */
    const char    *content_id;       /* Content-ID of an inline picture (without <>), NULL / "" otherwise */
    const uint8_t *data;
    size_t         len;
    int32_t        hidden;
    int64_t        modified;         /* FILETIME, 0 = now */
} opst_import_attachment;
typedef struct opst_import_msg {
    const char *message_class;       /* default "IPM.Note" */
    const char *subject;
    const char *sender_name;
    const char *sender_email;
    const char *body_text;           /* UTF-8, either or both of the bodies */
    const char *body_html;           /* UTF-8 */
    const char *transport_headers;   /* the internet headers, if known */
    const char *message_id;          /* Message-ID header value */
    int64_t     sent;                /* FILETIME, 0 = now */
    int64_t     received;            /* FILETIME, 0 = same as sent */
    int32_t     importance;          /* 0 low, 1 normal, 2 high */
    int32_t     read;                /* 1 = already read */
    const opst_import_recipient  *recipients;  size_t nrecipients;
    const opst_import_attachment *attachments; size_t nattachments;
} opst_import_msg;
/* Files the message into `folder` (one transaction) and returns its NID. */
OPST_API int opst_msg_import(opst *p, uint32_t folder, const opst_import_msg *msg, uint32_t *nid_out);
/* Files n messages into `folder` in ONE transaction (one contents-table rewrite): much faster than n single imports. nids has n entries.
   Everything is held in memory until the commit, so callers should keep a batch to a few dozen MB of payload. */
OPST_API int opst_msgs_import(opst *p, uint32_t folder, const opst_import_msg *msgs, size_t n, uint32_t *nids);

OPST_API int opst_msgs_purge(opst *p, const uint32_t *nids, size_t n);

/* copy messages of `src` (only read) into a folder of ANOTHER file `dst` (must be opened with OPST_OPEN_WRITE): named properties are
   translated through both files' name-to-id maps, attachments are kept, ids / row versions are renewed. Afterwards the destination's
   message index is repaired (opst_fix rule R3). new_nids (n entries, may be NULL) receive the NIDs in the destination. */
OPST_API int opst_msgs_copy_to(opst *src, const uint32_t *nids, size_t n, opst *dst, uint32_t dest_folder, uint32_t *new_nids);

/* fixes known "minor inconsistencies" that SCANPST reports in files edited by Outlook or by copies (see op_fix.c for the rules).
   apply = 0: only count (works on a read-only handle); apply = 1: repair in one transaction (needs OPST_OPEN_WRITE). */
typedef struct opst_fix_report {
    int32_t rows_without_ids;        /* R1: table rows without the 0x0E30/0E33/0E34 id cells */
    int32_t dangling_idmap;          /* R2: ID-map records pointing at deleted nodes */
    int32_t messages_not_indexed;    /* R3: messages missing from the message index (node 0xE01) */
    int32_t row_version_issues;      /* R4: duplicate / too high PidTagLtpRowVer values */
    int32_t nid_mark_issues;         /* R5: header NID high-water marks that are too low (any node type) */
    int32_t rowcell_issues;          /* R6: contents-table rows without the row-only cells 0x0E17 / 0x3013 */
} opst_fix_report;
OPST_API int opst_fix(opst *p, int apply, opst_fix_report *report);

/* read-only scan-style checks (a stand-in for much of SCANPST): block reference counts, header NID high-water marks, folder counts and
   contents rows vs messages, ID map, data-tree blocks, row versions. `text` (may be NULL) receives one line per finding and a summary
   line per check. Works on any handle. */
typedef struct opst_check_report {
    int32_t problems;                /* total number of findings */
    int32_t refs_problems, nids_problems, tables_problems, idmap_problems, xblocks_problems, rowver_problems;
    int32_t subnode_problems, rowcell_problems;   /* attachment sizes and local nid counters; row-only table cells */
} opst_check_report;
OPST_API int opst_check(opst *p, opst_check_report *report, char *text, size_t text_cap);

/* ---- utilities ----------------------------------------------------------------------------------------------- */
OPST_API int64_t opst_filetime_to_unix(int64_t filetime);         /* seconds since 1970-01-01 UTC (floor); 0 -> 0 */
OPST_API int     opst_filetime_to_iso(int64_t filetime, char *buf, size_t cap);   /* "2021-01-13T18:16:03Z" */
OPST_API void    opst_free(void *p);

/* ---- file health (read-only checks, same rules as the Python pstcheck.py) ---------------------------------- */
typedef struct opst_verify_report {
    uint64_t blocks;             /* data blocks examined */
    uint64_t crc_errors;
    uint64_t problems;           /* total number of findings */
} opst_verify_report;
OPST_API int opst_verify(opst *p, opst_verify_report *rep, char *text, size_t text_cap);

#ifdef __cplusplus
}
#endif
#endif /* OPENPST_H */
