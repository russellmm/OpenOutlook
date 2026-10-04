/* op_api_w.c - public write API: each function is one transaction (begin, operate, commit or roll back). */
#include "op_wr.h"

static int begin(opst *p, ops *o) {
    if (!p) return op_err(OPST_E_ARG, "null argument");
    if (!p->writable) return op_err(OPST_E_STATE, "the file was opened read-only (use OPST_OPEN_WRITE)");
    return ops_begin(p, o);
}
static int finish(ops *o, int rc) {
    if (rc) { ops_abort(o); return rc; }
    return ops_commit(o);
}

int opst_recovered(opst *p) { return p ? p->recovered : 0; }
int opst_journal_pending(opst *p) { return p ? p->journal_pending : 0; }

int opst_folder_is_protected(opst *p, uint32_t nid) {
    if (!p) return op_err(OPST_E_ARG, "null argument");
    ops o;
    int rc = p->writable ? ops_begin(p, &o) : ops_begin_ro(p, &o), prot = 0;
    if (rc) return rc;
    rc = fo_is_protected(&o, nid, &prot);
    if (p->writable) ops_abort(&o); else ops_end_ro(&o);          /* read only: nothing to commit */
    return rc ? rc : prot;
}

/* the GUI rule: special folders are refused up front, with their name in the message */
static int guard(ops *o, uint32_t nid) {
    int prot = 0;
    int rc = fo_is_protected(o, nid, &prot);
    if (rc) return rc;
    if (prot) return op_err(OPST_E_REFUSED, "folder 0x%x is a special folder and cannot be changed", nid);
    return 0;
}

int opst_folder_create(opst *p, uint32_t parent, const char *name, const char *cls, uint32_t *nid_out) {
    if (!name) return op_err(OPST_E_ARG, "null argument");
    ops o;
    int rc = begin(p, &o);
    if (rc) return rc;
    uint32_t nid = 0;
    rc = fo_create(&o, parent, name, cls ? cls : "IPF.Note", &nid);
    rc = finish(&o, rc);
    if (!rc && nid_out) *nid_out = nid;
    return rc;
}

int opst_folder_rename(opst *p, uint32_t nid, const char *name) {
    if (!name) return op_err(OPST_E_ARG, "null argument");
    ops o;
    int rc = begin(p, &o);
    if (rc) return rc;
    rc = guard(&o, nid);
    if (!rc) rc = fo_rename(&o, nid, name);
    return finish(&o, rc);
}

int opst_folder_move(opst *p, uint32_t nid, uint32_t dest) {
    ops o;
    int rc = begin(p, &o);
    if (rc) return rc;
    rc = guard(&o, nid);
    if (!rc) rc = fo_move(&o, nid, dest, NULL);
    return finish(&o, rc);
}

int opst_folder_delete(opst *p, uint32_t nid, int *permanent, opst_purge_stats *st) {
    ops o;
    int rc = begin(p, &o);
    if (rc) return rc;
    rc = guard(&o, nid);
    if (!rc) rc = fo_delete(&o, nid, permanent, st);
    return finish(&o, rc);
}

int opst_folder_purge(opst *p, uint32_t nid, opst_purge_stats *st) {
    ops o;
    int rc = begin(p, &o);
    if (rc) return rc;
    rc = guard(&o, nid);
    if (!rc) rc = fo_purge(&o, nid, st);
    return finish(&o, rc);
}

int opst_msgs_move(opst *p, const uint32_t *nids, size_t n, uint32_t dest) {
    if (!nids && n) return op_err(OPST_E_ARG, "null argument");
    ops o;
    int rc = begin(p, &o);
    if (rc) return rc;
    nbt_e e;
    if ((dest & 0x1F) != 2 || opw_node(o.w, dest, &e) != 0) { ops_abort(&o); return op_err(OPST_E_ARG, "destination 0x%x is not a folder", dest); }
    rc = ops_move_msgs(&o, nids, n, dest, NULL);
    return finish(&o, rc);
}

int opst_msgs_copy(opst *p, const uint32_t *nids, size_t n, uint32_t dest, uint32_t *new_nids) {
    if (!nids && n) return op_err(OPST_E_ARG, "null argument");
    ops o;
    int rc = begin(p, &o);
    if (rc) return rc;
    nbt_e e;
    if ((dest & 0x1F) != 2 || opw_node(o.w, dest, &e) != 0) { ops_abort(&o); return op_err(OPST_E_ARG, "destination 0x%x is not a folder", dest); }
    rc = ops_copy_msgs(&o, nids, n, dest, 0, 1, 0, new_nids);
    return finish(&o, rc);
}

int opst_msgs_delete(opst *p, const uint32_t *nids, size_t n, size_t *moved, size_t *purged) {
    if (!nids && n) return op_err(OPST_E_ARG, "null argument");
    ops o;
    int rc = begin(p, &o);
    if (rc) return rc;
    rc = ops_delete_msgs(&o, nids, n, moved, purged);
    return finish(&o, rc);
}

int opst_msgs_purge(opst *p, const uint32_t *nids, size_t n) {
    if (!nids && n) return op_err(OPST_E_ARG, "null argument");
    ops o;
    int rc = begin(p, &o);
    if (rc) return rc;
    rc = ops_purge_msgs(&o, nids, n);
    return finish(&o, rc);
}
