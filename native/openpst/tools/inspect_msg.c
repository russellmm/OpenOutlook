/* inspect_msg.c - developer tool: prints the internal structure of one message node (property context, subnodes, recipient and
 * attachment tables, attachment property contexts). Used to model the message builder on what Outlook itself writes.
 *   inspect_msg FILE.pst NID */
#include "../src/op_internal.h"

static const char *ptype_name(unsigned t) {
    switch (t) {
    case 2: return "i16"; case 3: return "i32"; case 4: return "f32"; case 5: return "f64"; case 6: return "cur"; case 7: return "apptime";
    case 0xA: return "err"; case 0xB: return "bool"; case 0x14: return "i64"; case 0x1E: return "str8"; case 0x1F: return "str16";
    case 0x40: return "time"; case 0x48: return "guid"; case 0xD: return "obj"; case 0x102: return "bin"; case 0x1003: return "mv-i32";
    case 0x101F: return "mv-str16"; case 0x1102: return "mv-bin"; default: return "?";
    }
}

static void print_pc(opst *p, const opnode *n, const char *indent) {
    oppc pc;
    int rc = op_pc_open(p, n, &pc);
    if (rc) { printf("%s(not a property context: %s)\n", indent, opst_last_error()); return; }
    printf("%sproperty context nid 0x%x: %zu properties, heap blocks %zu\n", indent, n->nid, pc.n, pc.heap.bl.n);
    for (size_t i = 0; i < pc.n; i++) {
        oppc_prop *pr = &pc.props[i];
        const char *where = (pr->hnid & 0x1F) == 0 && pr->hnid ? "heap" : pr->hnid ? "SUBNODE" : "inline";
        if (pr->ptype == 3 || pr->ptype == 2 || pr->ptype == 0xB || pr->ptype == 0xA || pr->ptype == 4) where = "inline";
        printf("%s  %04x %-8s hnid=%08x %-7s", indent, pr->pid, ptype_name(pr->ptype), pr->hnid, where);
        if (op_pc_value(&pc, pr) == 0) {
            printf(" len=%zu", pr->n);
            if (pr->ptype == 0x1F || pr->ptype == 0x1E) {
                size_t l; char *s = op_value_to_utf8(pr->ptype, pr->d, pr->n, 1252, &l);
                if (s) { printf(" \"%.60s\"", s); free(s); }
            } else if (pr->ptype == 3 || pr->ptype == 0xB) printf(" = %lld", (long long)op_value_i64(pr->ptype, pr->d, pr->n, 0));
            else if ((pr->ptype == 0x102 || pr->ptype == 0x14) && pr->n <= 32) { printf(" "); for (size_t k = 0; k < pr->n; k++) printf("%02x", pr->d[k]); }
        }
        printf("\n");
    }
    op_pc_close(&pc);
}

static void print_tc(opst *p, const opnode *n, const char *indent) {
    optc tc;
    int rc = op_tc_open(p, n, &tc);
    if (rc) { printf("%s(not a table context: %s)\n", indent, opst_last_error()); return; }
    printf("%stable context nid 0x%x: %zu columns, %zu rows, rowsize %u, rgib %u/%u/%u/%u, rows %s\n", indent, n->nid, tc.ncols, tc.nrows,
           tc.rowsize, tc.rgib[0], tc.rgib[1], tc.rgib[2], tc.rgib[3], tc.rows_in_sub ? "in subnode" : "in heap");
    for (size_t i = 0; i < tc.ncols; i++)
        printf("%s  col %2zu: %04x %-8s ibData=%3u cb=%u ibit=%u\n", indent, i, tc.cols[i].pid, ptype_name(tc.cols[i].ptype), tc.cols[i].ibdata, tc.cols[i].cbdata, tc.cols[i].ibit);
    for (size_t r = 0; r < tc.nrows && r < 3; r++) printf("%s  row %zu: id=0x%x index=%u\n", indent, r, tc.rowid[r], tc.rowidx[r]);
    if (getenv("INSPECT_ROW")) {                                  /* INSPECT_ROW=0x<rowid>: every cell of that row */
        uint32_t want = (uint32_t)strtoul(getenv("INSPECT_ROW"), NULL, 0);
        for (size_t r = 0; r < tc.nrows; r++) {
            if (tc.rowid[r] != want) continue;
            const uint8_t *row = op_tc_row(&tc, r);
            for (size_t c = 0; c < tc.ncols; c++) {
                uint16_t pt; const uint8_t *d; size_t n; void *tf;
                if (!op_tc_cell(&tc, row, (int)c, &pt, &d, &n, &tf)) continue;
                printf("%s    cell %04x %-8s len=%zu", indent, tc.cols[c].pid, ptype_name(pt), n);
                if (pt == 0x1F || pt == 0x1E) { size_t l; char *u = op_value_to_utf8(pt, d, n, 1252, &l); if (u) { printf(" \"%.50s\"", u); free(u); } }
                else if (pt == 3 || pt == 0xB) printf(" = %lld", (long long)op_value_i64(pt, d, n, 0));
                else if (pt == 0x40 && n >= 8) printf(" = %llx", (unsigned long long)op_u64(d));
                else if ((pt == 0x102 || pt == 0x14) && n <= 32) { printf(" "); for (size_t k = 0; k < n; k++) printf("%02x", d[k]); }
                printf("\n");
                free(tf);
            }
        }
    }
    op_tc_close(&tc);
}

int main(int argc, char **argv) {
    if (argc < 3) { fprintf(stderr, "usage: inspect_msg FILE NID\n"); return 2; }
    opst *p;
    if (opst_open(argv[1], OPST_OPEN_READONLY, &p)) { fprintf(stderr, "%s\n", opst_last_error()); return 1; }
    uint32_t nid = (uint32_t)strtoul(argv[2], NULL, 0);
    opnode n;
    if (op_node_get(p, nid, &n)) { fprintf(stderr, "%s\n", opst_last_error()); return 1; }
    printf("node 0x%x: bd=0x%llx bs=0x%llx parent=0x%x\n", nid, (unsigned long long)n.bd, (unsigned long long)n.bs, op_nbt_find(p, nid)->parent);
    if ((nid & 0x1F) == 0x0E || (nid & 0x1F) == 0x0D || (nid & 0x1F) == 0x0F) { print_tc(p, &n, ""); opst_close(p); return 0; }
    print_pc(p, &n, "");
    opsubs subs;
    if (op_subs_load(p, n.bs, &subs) == 0) {
        printf("subnodes: %zu\n", subs.n);
        for (size_t i = 0; i < subs.n; i++) {
            opnode sn = {subs.e[i].nid, subs.e[i].bd, subs.e[i].bs};
            opblocks bl;
            size_t total = 0;
            if (op_blocks_load(p, sn.bd, &bl) == 0) { for (size_t k = 0; k < bl.n; k++) total += bl.b[k].n; op_blocks_free(&bl); }
            printf("  sub nid 0x%x (type 0x%x) bd=0x%llx bs=0x%llx data bytes=%zu\n", sn.nid, sn.nid & 0x1F, (unsigned long long)sn.bd, (unsigned long long)sn.bs, total);
        }
        for (size_t i = 0; i < subs.n; i++) {
            unsigned type = subs.e[i].nid & 0x1F;
            opnode sn = {subs.e[i].nid, subs.e[i].bd, subs.e[i].bs};
            if (subs.e[i].nid == OP_NID_RECIP_TBL || subs.e[i].nid == OP_NID_ATTACH_TBL) print_tc(p, &sn, "  ");
            else if (type == 5) {
                printf("  attachment 0x%x:\n", sn.nid);
                print_pc(p, &sn, "    ");
            }
        }
        op_subs_free(&subs);
    }
    opst_close(p);
    return 0;
}
