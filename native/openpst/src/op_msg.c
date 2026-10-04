/* op_msg.c - messaging layer and the public API: folders, message lists, messages, recipients, attachments. */
#include "op_internal.h"

#define NID_TYPE_MASK 0x1Fu

/* ---- helpers ------------------------------------------------------------------------------------------------------ */
static void strip_subject_prefix(char *s, size_t *len) {
    /* Outlook stores "\x01\x01" style prefix control characters: Python's rule is "\x01 + one more character removed" */
    if (*len < 2 || (unsigned char)s[0] != 0x01) return;
    size_t skip = 1;
    unsigned char c = (unsigned char)s[1];
    skip += c < 0x80 ? 1 : (c & 0xE0) == 0xC0 ? 2 : (c & 0xF0) == 0xE0 ? 3 : (c & 0xF8) == 0xF0 ? 4 : 1;
    if (skip > *len) skip = *len;
    memmove(s, s + skip, *len - skip + 1);
    *len -= skip;
}

/* text of a table cell as pool offset (0 when absent / not a string) */
static size_t cell_str(optc *tc, const uint8_t *row, int col, oplist *l, unsigned cp, int subject) {
    uint16_t pt; const uint8_t *d; size_t n; void *tf;
    if (!op_tc_cell(tc, row, col, &pt, &d, &n, &tf)) return 0;
    size_t len = 0, off = 0;
    char *s = op_value_to_utf8(pt, d, n, cp, &len);
    free(tf);
    if (!s) return 0;
    if (subject) strip_subject_prefix(s, &len);
    off = op_list_str(l, s, len);
    free(s);
    return off;
}
static int64_t cell_i64(optc *tc, const uint8_t *row, int col, int64_t dflt) {
    uint16_t pt; const uint8_t *d; size_t n; void *tf;
    if (!op_tc_cell(tc, row, col, &pt, &d, &n, &tf)) return dflt;
    int64_t v = op_value_i64(pt, d, n, dflt);
    free(tf);
    return v;
}

static int pc_i32(oppc *pc, uint16_t pid, int64_t dflt, int64_t *out) {
    oppc_prop *pr = op_pc_find(pc, pid);
    if (!pr || op_pc_value(pc, pr) != 0) { *out = dflt; return 0; }
    *out = op_value_i64(pr->ptype, pr->d, pr->n, dflt);
    return 1;
}
static char *pc_str(oppc *pc, uint16_t pid, unsigned cp, size_t *len) {
    oppc_prop *pr = op_pc_find(pc, pid);
    if (!pr || op_pc_value(pc, pr) != 0) return NULL;
    return op_value_to_utf8(pr->ptype, pr->d, pr->n, cp, len);
}

/* ---- folders -------------------------------------------------------------------------------------------------------- */
static int cmp_folder(const void *a, const void *b) {
    const opst_folder_info *x = (const opst_folder_info *)a, *y = (const opst_folder_info *)b;
    int c = op_stricmp_utf8(x->name, y->name);
    if (c) return c;
    return x->nid < y->nid ? -1 : x->nid > y->nid;
}

static size_t table_rows(opst *p, uint32_t nid, int *present) {
    opnode n; optc tc;
    *present = 0;
    if (op_node_get(p, nid, &n) != 0) return 0;
    if (op_tc_open(p, &n, &tc) != 0) return 0;
    size_t r = tc.nrows;
    op_tc_close(&tc);
    *present = 1;
    return r;
}

/* fills `fi` (name into the pool, as an offset) from the folder's property context */
static int folder_fill(opst *p, uint32_t nid, opst_folder_info *fi, oplist *l) {
    opnode n; oppc pc;
    int rc = op_node_get(p, nid, &n);
    if (rc) return rc;
    rc = op_pc_open(p, &n, &pc);
    if (rc) return rc;
    const op_nbt_ent *e = op_nbt_find(p, nid);
    fi->nid = nid;
    fi->parent = e ? e->parent : 0;
    size_t len = 0;
    char *name = pc_str(&pc, 0x3001, 0, &len);
    size_t off = (name && len) ? op_list_str(l, name, len) : op_list_str(l, "(unnamed)", 9);
    free(name);
    fi->name = (const char *)(uintptr_t)off;
    int64_t v;
    pc_i32(&pc, 0x3602, 0, &v); fi->content_count = (int32_t)v;
    pc_i32(&pc, 0x3603, 0, &v); fi->unread_count = (int32_t)v;
    if (!pc_i32(&pc, 0x360A, 0, &v)) {
        int present;
        v = table_rows(p, (nid & ~NID_TYPE_MASK) | 0x0D, &present) > 0;
    }
    fi->has_subfolders = v ? 1 : 0;
    op_pc_close(&pc);
    return 0;
}

int opst_folder_children(opst *p, uint32_t parent, opst_folder_info **out, size_t *count) {
    if (!p || !out || !count) return op_err(OPST_E_ARG, "null argument");
    *out = NULL; *count = 0;
    oplist l;
    if (op_list_init(&l, sizeof(opst_folder_info))) return op_err(OPST_E_NOMEM, "out of memory");
    opnode n; optc tc;
    if (op_node_get(p, (parent & ~NID_TYPE_MASK) | 0x0D, &n) == 0) {
        int rc = op_tc_open(p, &n, &tc);
        if (rc) { op_list_abort(&l); return rc; }
        for (size_t i = 0; i < tc.nrows; i++) {
            opst_folder_info *fi = (opst_folder_info *)op_list_push(&l);
            if (!fi) { op_tc_close(&tc); op_list_abort(&l); return op_err(OPST_E_NOMEM, "out of memory"); }
            /* folder_fill may push strings (not elements), so `fi` stays valid */
            if (folder_fill(p, tc.rowid[i], fi, &l) != 0) l.n--;     /* damaged child: skip */
        }
        op_tc_close(&tc);
    }
    size_t cnt = l.n;
    for (size_t i = 0; i < cnt; i++) { opst_folder_info *fi = OP_LIST_AT(&l, opst_folder_info, i); OP_FIX(&l, fi->name); }
    opst_folder_info *arr = (opst_folder_info *)(l.base + 16);
    op_qsort(arr, cnt, sizeof *arr, cmp_folder);
    *out = (opst_folder_info *)op_list_finish(&l);
    *count = cnt;
    return 0;
}
void opst_free_folders(opst_folder_info *arr) { op_arr_free(arr); }

int opst_folder_info_get(opst *p, uint32_t nid, opst_folder_info *out, char *name_buf, size_t name_cap) {
    if (!p || !out) return op_err(OPST_E_ARG, "null argument");
    oplist l;
    if (op_list_init(&l, sizeof(opst_folder_info))) return op_err(OPST_E_NOMEM, "out of memory");
    opst_folder_info fi;
    memset(&fi, 0, sizeof fi);
    int rc = folder_fill(p, nid, &fi, &l);
    if (rc) { op_list_abort(&l); return rc; }
    const char *name = l.pool + (size_t)(uintptr_t)fi.name;
    *out = fi;
    out->name = name_buf;
    if (name_buf && name_cap) { strncpy(name_buf, name, name_cap - 1); name_buf[name_cap - 1] = 0; }
    op_list_abort(&l);
    return 0;
}

int opst_folder_find(opst *p, uint32_t start, const char *path, uint32_t *nid_out) {
    if (!p || !path || !nid_out) return op_err(OPST_E_ARG, "null argument");
    uint32_t cur = start ? start : OP_NID_ROOT;
    const char *s = path;
    while (*s) {
        while (*s == '/') s++;
        if (!*s) break;
        const char *e = s;
        while (*e && *e != '/') e++;
        size_t plen = (size_t)(e - s);
        char *part = (char *)malloc(plen + 1);
        if (!part) return op_err(OPST_E_NOMEM, "out of memory");
        memcpy(part, s, plen); part[plen] = 0;
        opst_folder_info *kids; size_t nk;
        int rc = opst_folder_children(p, cur, &kids, &nk);
        if (rc) { free(part); return rc; }
        uint32_t found = 0;
        for (size_t i = 0; i < nk; i++) if (op_stricmp_utf8(kids[i].name, part) == 0) { found = kids[i].nid; break; }
        opst_free_folders(kids);
        if (!found) { op_err(OPST_E_NOTFOUND, "folder not found: %s", part); free(part); return OPST_E_NOTFOUND; }
        free(part);
        cur = found;
        s = e;
    }
    *nid_out = cur;
    return 0;
}

/* ---- message lists ----------------------------------------------------------------------------------------------------- */
int opst_messages(opst *p, uint32_t folder, opst_msg_row **out, size_t *count) {
    if (!p || !out || !count) return op_err(OPST_E_ARG, "null argument");
    *out = NULL; *count = 0;
    oplist l;
    if (op_list_init(&l, sizeof(opst_msg_row))) return op_err(OPST_E_NOMEM, "out of memory");
    opnode n; optc tc;
    if (op_node_get(p, (folder & ~NID_TYPE_MASK) | 0x0E, &n) == 0) {
        int rc = op_tc_open(p, &n, &tc);
        if (rc) { op_list_abort(&l); return rc; }
        int c_subj = op_tc_col(&tc, 0x0037), c_from = op_tc_col(&tc, 0x0C1A), c_from2 = op_tc_col(&tc, 0x0042),
            c_to = op_tc_col(&tc, 0x0E04), c_cc = op_tc_col(&tc, 0x0E03), c_sent = op_tc_col(&tc, 0x0039),
            c_recv = op_tc_col(&tc, 0x0E06), c_flags = op_tc_col(&tc, 0x0E07), c_size = op_tc_col(&tc, 0x0E08),
            c_imp = op_tc_col(&tc, 0x0017), c_att = op_tc_col(&tc, 0x0E1B), c_topic = op_tc_col(&tc, 0x0070),
            c_cls = op_tc_col(&tc, 0x001A), c_flag = op_tc_col(&tc, 0x1090);
        for (size_t i = 0; i < tc.nrows; i++) {
            const uint8_t *row = op_tc_row(&tc, i);
            if (!row) continue;
            opst_msg_row r;
            memset(&r, 0, sizeof r);
            r.nid = tc.rowid[i];
            r.flags = (uint32_t)cell_i64(&tc, row, c_flags, 0);
            r.sent = cell_i64(&tc, row, c_sent, 0);
            r.received = cell_i64(&tc, row, c_recv, 0);
            r.size = cell_i64(&tc, row, c_size, 0);
            r.importance = (int32_t)cell_i64(&tc, row, c_imp, 1);
            r.has_attachments = (r.flags & OPST_MSGFLAG_HASATTACH) != 0 || cell_i64(&tc, row, c_att, 0) != 0;
            r.flag_status = (int32_t)cell_i64(&tc, row, c_flag, 0);
            size_t o_subj = cell_str(&tc, row, c_subj, &l, 0, 1);
            size_t o_from = cell_str(&tc, row, c_from, &l, 0, 0);
            if (!o_from) o_from = cell_str(&tc, row, c_from2, &l, 0, 0);
            size_t o_to = cell_str(&tc, row, c_to, &l, 0, 0);
            if (!o_from) o_from = o_to;
            size_t o_cc = cell_str(&tc, row, c_cc, &l, 0, 0);
            size_t o_topic = cell_str(&tc, row, c_topic, &l, 0, 0);
            size_t o_cls = cell_str(&tc, row, c_cls, &l, 0, 0);
            opst_msg_row *dst = (opst_msg_row *)op_list_push(&l);
            if (!dst) { op_tc_close(&tc); op_list_abort(&l); return op_err(OPST_E_NOMEM, "out of memory"); }
            *dst = r;
            dst->subject = (const char *)(uintptr_t)o_subj; dst->sender = (const char *)(uintptr_t)o_from;
            dst->to = (const char *)(uintptr_t)o_to; dst->cc = (const char *)(uintptr_t)o_cc;
            dst->topic = (const char *)(uintptr_t)o_topic; dst->message_class = (const char *)(uintptr_t)o_cls;
        }
        op_tc_close(&tc);
    }
    size_t cnt = l.n;
    for (size_t i = 0; i < cnt; i++) {
        opst_msg_row *r = OP_LIST_AT(&l, opst_msg_row, i);
        OP_FIX(&l, r->subject); OP_FIX(&l, r->sender); OP_FIX(&l, r->to); OP_FIX(&l, r->cc);
        OP_FIX(&l, r->topic); OP_FIX(&l, r->message_class);
    }
    *out = (opst_msg_row *)op_list_finish(&l);
    *count = cnt;
    return 0;
}
void opst_free_messages(opst_msg_row *arr) { op_arr_free(arr); }

/* ---- one message -------------------------------------------------------------------------------------------------------- */
typedef struct { uint16_t pid; char *s; } strent;

struct opst_msg {
    opst     *p;
    uint32_t  nid;
    oppc      pc;
    unsigned  cp;
    strent   *strs;  size_t nstr, capstr;
    char     *body[3];  size_t bodylen[3];  int body_done[3];
    char     *text, *html;  size_t textlen, htmllen;  int text_done, html_done;      /* derived: opst_msg_text / opst_msg_html */
    uint32_t *att_nid;  size_t natt;  int att_loaded;
    oppc     *att_pc;   unsigned char *att_open;
};

int opst_msg_open(opst *p, uint32_t nid, opst_msg **out) {
    if (!p || !out) return op_err(OPST_E_ARG, "null argument");
    *out = NULL;
    opnode n;
    int rc = op_node_get(p, nid, &n);
    if (rc) return rc;
    opst_msg *m = (opst_msg *)calloc(1, sizeof *m);
    if (!m) return op_err(OPST_E_NOMEM, "out of memory");
    rc = op_pc_open(p, &n, &m->pc);
    if (rc) { free(m); return rc; }
    m->p = p; m->nid = nid;
    int64_t cp;
    pc_i32(&m->pc, 0x3FFD, 0, &cp);
    if (!cp) pc_i32(&m->pc, 0x3FDE, 0, &cp);
    m->cp = (unsigned)cp;
    *out = m;
    return 0;
}

void opst_msg_close(opst_msg *m) {
    if (!m) return;
    for (size_t i = 0; i < m->nstr; i++) free(m->strs[i].s);
    free(m->strs);
    for (int k = 0; k < 3; k++) free(m->body[k]);
    free(m->text); free(m->html);
    if (m->att_pc) for (size_t i = 0; i < m->natt; i++) if (m->att_open[i]) op_pc_close(&m->att_pc[i]);
    free(m->att_pc); free(m->att_open); free(m->att_nid);
    op_pc_close(&m->pc);
    free(m);
}

int opst_msg_prop(opst_msg *m, uint16_t pid, uint16_t *ptype, const uint8_t **data, size_t *len) {
    if (!m) return op_err(OPST_E_ARG, "null argument");
    oppc_prop *pr = op_pc_find(&m->pc, pid);
    if (!pr) return op_err(OPST_E_NOTFOUND, "property 0x%04x not present", pid);
    int rc = op_pc_value(&m->pc, pr);
    if (rc) return rc;
    if (ptype) *ptype = pr->ptype;
    if (data) *data = pr->d;
    if (len) *len = pr->n;
    return 0;
}

int64_t opst_msg_i64(opst_msg *m, uint16_t pid, int64_t dflt) {
    if (!m) return dflt;
    int64_t v;
    return pc_i32(&m->pc, pid, dflt, &v) ? v : dflt;
}

const char *opst_msg_str(opst_msg *m, uint16_t pid) {
    if (!m) return NULL;
    for (size_t i = 0; i < m->nstr; i++) if (m->strs[i].pid == pid) return m->strs[i].s;
    size_t len;
    char *s = pc_str(&m->pc, pid, m->cp, &len);
    if (!s) return NULL;
    if (pid == OPST_PID_SUBJECT) strip_subject_prefix(s, &len);
    if (m->nstr == m->capstr) {
        size_t nc = m->capstr ? m->capstr * 2 : 8;
        strent *t = (strent *)realloc(m->strs, nc * sizeof *t);
        if (!t) { free(s); return NULL; }
        m->strs = t; m->capstr = nc;
    }
    m->strs[m->nstr].pid = pid; m->strs[m->nstr].s = s;
    m->nstr++;
    return s;
}

unsigned opst_msg_bodies(opst_msg *m) {
    unsigned r = 0;
    if (!m) return 0;
    if (op_pc_find(&m->pc, OPST_PID_BODY_TEXT)) r |= 1u << OPST_BODY_TEXT;
    if (op_pc_find(&m->pc, OPST_PID_BODY_HTML)) r |= 1u << OPST_BODY_HTML;
    if (op_pc_find(&m->pc, OPST_PID_BODY_RTF)) r |= 1u << OPST_BODY_RTF;
    return r;
}

const char *opst_msg_body(opst_msg *m, opst_body_kind kind, size_t *len) {
    if (len) *len = 0;
    if (!m || kind < 0 || kind > 2) return NULL;
    if (!m->body_done[kind]) {
        m->body_done[kind] = 1;
        uint16_t pid = kind == OPST_BODY_TEXT ? OPST_PID_BODY_TEXT : kind == OPST_BODY_HTML ? OPST_PID_BODY_HTML : OPST_PID_BODY_RTF;
        oppc_prop *pr = op_pc_find(&m->pc, pid);
        if (pr && op_pc_value(&m->pc, pr) == 0) {
            size_t n = 0;
            char *s = NULL;
            if (kind == OPST_BODY_TEXT) {
                s = op_value_to_utf8(pr->ptype, pr->d, pr->n, m->cp, &n);
                if (!s) s = op_ansi_to_utf8(pr->d, pr->n, m->cp, &n);
            } else if (kind == OPST_BODY_HTML) {
                if (pr->ptype == 0x1F) s = op_utf16_to_utf8(pr->d, pr->n, &n);
                else {
                    int64_t cp;
                    pc_i32(&m->pc, 0x3FDE, 0, &cp);
                    s = op_ansi_to_utf8(pr->d, pr->n, cp ? (unsigned)cp : m->cp, &n);
                }
            } else {
                uint8_t *rtf = NULL;
                if (op_lzfu(pr->d, pr->n, &rtf, &n) == 0) s = (char *)rtf;
            }
            if (s) {
                m->body[kind] = s;
                m->bodylen[kind] = n;
                if (kind == OPST_BODY_RTF) s[n] = 0;
            }
        }
    }
    if (len) *len = m->bodylen[kind];
    return m->body[kind];
}

const char *opst_msg_text(opst_msg *m, size_t *len) {
    if (len) *len = 0;
    if (!m) return NULL;
    if (!m->text_done) {
        m->text_done = 1;
        size_t n = 0;
        const char *b = opst_msg_body(m, OPST_BODY_TEXT, &n);
        if (b && n) { m->text = (char *)malloc(n + 1); if (m->text) { memcpy(m->text, b, n + 1); m->textlen = n; } }
        if (!m->text && (b = opst_msg_body(m, OPST_BODY_HTML, &n)) != NULL && n) {
            if (opst_html_to_text(b, n, &m->text, &m->textlen) != 0) m->text = NULL;
        }
        if (!m->text && (b = opst_msg_body(m, OPST_BODY_RTF, &n)) != NULL && n) {
            if (opst_rtf_to_text(b, n, &m->text, &m->textlen) != 0) m->text = NULL;
        }
    }
    if (len) *len = m->textlen;
    return m->text;
}

const char *opst_msg_html(opst_msg *m, size_t *len) {
    if (len) *len = 0;
    if (!m) return NULL;
    if (!m->html_done) {
        m->html_done = 1;
        size_t n = 0;
        const char *b = opst_msg_body(m, OPST_BODY_HTML, &n);
        if (b && n) { m->html = (char *)malloc(n + 1); if (m->html) { memcpy(m->html, b, n + 1); m->htmllen = n; } }
        if (!m->html && (b = opst_msg_body(m, OPST_BODY_RTF, &n)) != NULL && n) {
            if (opst_rtf_to_html(b, n, &m->html, &m->htmllen) != 0) m->html = NULL;
        }
    }
    if (len) *len = m->htmllen;
    return m->html;
}

/* ---- recipients ---------------------------------------------------------------------------------------------------------- */
int opst_msg_recipients(opst_msg *m, opst_recipient **out, size_t *count) {
    if (!m || !out || !count) return op_err(OPST_E_ARG, "null argument");
    *out = NULL; *count = 0;
    oplist l;
    if (op_list_init(&l, sizeof(opst_recipient))) return op_err(OPST_E_NOMEM, "out of memory");
    const opsub *s = op_subs_find(op_heap_subs(&m->pc.heap), OP_NID_RECIP_TBL);
    if (s) {
        opnode n; optc tc;
        n.nid = s->nid; n.bd = s->bd; n.bs = s->bs;
        int rc = op_tc_open(m->p, &n, &tc);
        if (rc) { op_list_abort(&l); return rc; }
        int c_name = op_tc_col(&tc, 0x3001), c_smtp = op_tc_col(&tc, 0x39FE), c_addr = op_tc_col(&tc, 0x3003), c_type = op_tc_col(&tc, 0x0C15);
        for (size_t i = 0; i < tc.nrows; i++) {
            const uint8_t *row = op_tc_row(&tc, i);
            if (!row) continue;
            size_t o_name = cell_str(&tc, row, c_name, &l, m->cp, 0);
            size_t o_mail = cell_str(&tc, row, c_smtp, &l, m->cp, 0);
            if (!o_mail) o_mail = cell_str(&tc, row, c_addr, &l, m->cp, 0);
            opst_recipient *r = (opst_recipient *)op_list_push(&l);
            if (!r) { op_tc_close(&tc); op_list_abort(&l); return op_err(OPST_E_NOMEM, "out of memory"); }
            r->name = (const char *)(uintptr_t)o_name; r->email = (const char *)(uintptr_t)o_mail;
            r->type = (int32_t)cell_i64(&tc, row, c_type, 0);
        }
        op_tc_close(&tc);
    }
    size_t cnt = l.n;
    for (size_t i = 0; i < cnt; i++) { opst_recipient *r = OP_LIST_AT(&l, opst_recipient, i); OP_FIX(&l, r->name); OP_FIX(&l, r->email); }
    *out = (opst_recipient *)op_list_finish(&l);
    *count = cnt;
    return 0;
}
void opst_free_recipients(opst_recipient *arr) { op_arr_free(arr); }

/* ---- attachments ---------------------------------------------------------------------------------------------------------- */
static int att_load(opst_msg *m) {
    if (m->att_loaded) return 0;
    m->att_loaded = 1;
    const opsub *s = op_subs_find(op_heap_subs(&m->pc.heap), OP_NID_ATTACH_TBL);
    if (!s) return 0;
    opnode n; optc tc;
    n.nid = s->nid; n.bd = s->bd; n.bs = s->bs;
    int rc = op_tc_open(m->p, &n, &tc);
    if (rc) return rc;
    const opsubs *subs = op_heap_subs(&m->pc.heap);
    m->att_nid = (uint32_t *)calloc(tc.nrows ? tc.nrows : 1, sizeof(uint32_t));
    if (!m->att_nid) { op_tc_close(&tc); return op_err(OPST_E_NOMEM, "out of memory"); }
    for (size_t i = 0; i < tc.nrows; i++) if (op_subs_find(subs, tc.rowid[i])) m->att_nid[m->natt++] = tc.rowid[i];
    op_tc_close(&tc);
    m->att_pc = (oppc *)calloc(m->natt ? m->natt : 1, sizeof(oppc));
    m->att_open = (unsigned char *)calloc(m->natt ? m->natt : 1, 1);
    if (!m->att_pc || !m->att_open) return op_err(OPST_E_NOMEM, "out of memory");
    return 0;
}

static oppc *att_pc(opst_msg *m, size_t i) {
    if (i >= m->natt) return NULL;
    if (!m->att_open[i]) {
        const opsub *s = op_subs_find(op_heap_subs(&m->pc.heap), m->att_nid[i]);
        if (!s) return NULL;
        opnode n;
        n.nid = s->nid; n.bd = s->bd; n.bs = s->bs;
        if (op_pc_open(m->p, &n, &m->att_pc[i]) != 0) return NULL;
        m->att_open[i] = 1;
    }
    return &m->att_pc[i];
}

int opst_msg_attachments(opst_msg *m, opst_attachment **out, size_t *count) {
    if (!m || !out || !count) return op_err(OPST_E_ARG, "null argument");
    *out = NULL; *count = 0;
    int rc = att_load(m);
    if (rc) return rc;
    oplist l;
    if (op_list_init(&l, sizeof(opst_attachment))) return op_err(OPST_E_NOMEM, "out of memory");
    for (size_t i = 0; i < m->natt; i++) {
        oppc *pc = att_pc(m, i);
        if (!pc) continue;
        opst_attachment a;
        memset(&a, 0, sizeof a);
        a.index = (uint32_t)i; a.nid = m->att_nid[i];
        size_t len;
        char *fn = pc_str(pc, 0x3707, m->cp, &len);
        if (!fn || !len) { free(fn); fn = pc_str(pc, 0x3704, m->cp, &len); }
        if (!fn || !len) { free(fn); fn = pc_str(pc, 0x3001, m->cp, &len); }
        size_t o_fn = (fn && len) ? op_list_str(&l, fn, len) : op_list_str(&l, "attachment", 10);
        free(fn);
        char *mime = pc_str(pc, 0x370E, m->cp, &len);
        size_t o_mime = mime ? op_list_str(&l, mime, len) : 0;
        free(mime);
        char *cid = pc_str(pc, 0x3712, m->cp, &len);
        size_t o_cid = cid ? op_list_str(&l, cid, len) : 0;
        free(cid);
        int64_t v;
        pc_i32(pc, 0x3705, 0, &v); a.method = (int32_t)v;
        pc_i32(pc, 0x7FFE, 0, &v); a.hidden = v != 0;
        if (!pc_i32(pc, 0x0E20, 0, &v)) {
            oppc_prop *pr = op_pc_find(pc, 0x3701);
            v = (pr && op_pc_value(pc, pr) == 0) ? (int64_t)pr->n : 0;
        }
        a.size = v;
        opst_attachment *dst = (opst_attachment *)op_list_push(&l);
        if (!dst) { op_list_abort(&l); return op_err(OPST_E_NOMEM, "out of memory"); }
        *dst = a;
        dst->filename = (const char *)(uintptr_t)o_fn; dst->mime = (const char *)(uintptr_t)o_mime; dst->cid = (const char *)(uintptr_t)o_cid;
    }
    size_t cnt = l.n;
    for (size_t i = 0; i < cnt; i++) {
        opst_attachment *a = OP_LIST_AT(&l, opst_attachment, i);
        OP_FIX(&l, a->filename); OP_FIX(&l, a->mime); OP_FIX(&l, a->cid);
    }
    *out = (opst_attachment *)op_list_finish(&l);
    *count = cnt;
    return 0;
}
void opst_free_attachments(opst_attachment *arr) { op_arr_free(arr); }

int opst_attachment_data(opst_msg *m, uint32_t index, const uint8_t **data, size_t *len) {
    if (!m || !data || !len) return op_err(OPST_E_ARG, "null argument");
    int rc = att_load(m);
    if (rc) return rc;
    oppc *pc = att_pc(m, index);
    if (!pc) return op_err(OPST_E_NOTFOUND, "attachment %u not found", index);
    oppc_prop *pr = op_pc_find(pc, 0x3701);
    if (!pr) { static const uint8_t e[1] = {0}; *data = e; *len = 0; return 0; }
    rc = op_pc_value(pc, pr);
    if (rc) return rc;
    *data = pr->d; *len = pr->n;
    return 0;
}
