/* op_inflate.c - a small RFC 1950/1951 (zlib / deflate) decoder, needed to read the compressed data blocks of 4K-page PST/OST files.
 * Canonical-Huffman decoding in the style of Mark Adler's public domain "puff.c": symbols are decoded bit by bit from the code-length counts,
 * which is slow but small and has no tables to build. Input is untrusted: every read and write is bounds-checked. */
#include "op_internal.h"

#define MAXBITS 15
#define MAXLCODES 286
#define MAXDCODES 30
#define FIXLCODES 288

typedef struct {
    const uint8_t *in; size_t inlen, incnt;
    int bitbuf, bitcnt;
    uint8_t *out; size_t outcap, outcnt;
} zstate;

typedef struct { uint16_t count[MAXBITS + 1]; uint16_t symbol[FIXLCODES]; } huffman;

static int z_bits(zstate *s, int need) {
    long val = s->bitbuf;
    while (s->bitcnt < need) {
        if (s->incnt >= s->inlen) return -1;
        val |= (long)s->in[s->incnt++] << s->bitcnt;
        s->bitcnt += 8;
    }
    s->bitbuf = (int)(val >> need);
    s->bitcnt -= need;
    return (int)(val & ((1L << need) - 1));
}

static int z_decode(zstate *s, const huffman *h) {
    int code = 0, first = 0, index = 0;
    for (int len = 1; len <= MAXBITS; len++) {
        int b = z_bits(s, 1);
        if (b < 0) return -1;
        code |= b;
        int count = h->count[len];
        if (code - count < first) return h->symbol[index + (code - first)];
        index += count;
        first += count;
        first <<= 1;
        code <<= 1;
    }
    return -2;                                         /* ran out of codes */
}

static int z_construct(huffman *h, const short *length, int n) {
    short offs[MAXBITS + 1];
    for (int len = 0; len <= MAXBITS; len++) h->count[len] = 0;
    for (int sym = 0; sym < n; sym++) h->count[length[sym]]++;
    if (h->count[0] == n) return 0;                    /* no codes: complete (and unusable) */
    int left = 1;
    for (int len = 1; len <= MAXBITS; len++) {
        left <<= 1;
        left -= h->count[len];
        if (left < 0) return left;                     /* over-subscribed */
    }
    offs[1] = 0;
    for (int len = 1; len < MAXBITS; len++) offs[len + 1] = (short)(offs[len] + h->count[len]);
    for (int sym = 0; sym < n; sym++) if (length[sym] != 0) h->symbol[offs[length[sym]]++] = (uint16_t)sym;
    return left;
}

static int z_codes(zstate *s, const huffman *lencode, const huffman *distcode) {
    static const short lens[29] = {3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258};
    static const short lext[29] = {0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0};
    static const short dists[30] = {1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577};
    static const short dext[30] = {0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13};
    for (;;) {
        int symbol = z_decode(s, lencode);
        if (symbol < 0) return -1;
        if (symbol < 256) {
            if (s->outcnt >= s->outcap) return -1;
            s->out[s->outcnt++] = (uint8_t)symbol;
        } else if (symbol == 256) {
            return 0;
        } else {
            symbol -= 257;
            if (symbol >= 29) return -1;
            int e = z_bits(s, lext[symbol]);
            if (e < 0) return -1;
            int len = lens[symbol] + e;
            symbol = z_decode(s, distcode);
            if (symbol < 0 || symbol >= MAXDCODES) return -1;
            e = z_bits(s, dext[symbol]);
            if (e < 0) return -1;
            size_t dist = (size_t)dists[symbol] + (size_t)e;
            if (dist > s->outcnt || s->outcnt + (size_t)len > s->outcap) return -1;
            for (int i = 0; i < len; i++, s->outcnt++) s->out[s->outcnt] = s->out[s->outcnt - dist];
        }
    }
}

static int z_stored(zstate *s) {
    s->bitbuf = 0; s->bitcnt = 0;
    if (s->incnt + 4 > s->inlen) return -1;
    unsigned len = s->in[s->incnt] | ((unsigned)s->in[s->incnt + 1] << 8);
    unsigned nlen = s->in[s->incnt + 2] | ((unsigned)s->in[s->incnt + 3] << 8);
    s->incnt += 4;
    if (len != (~nlen & 0xFFFFu)) return -1;
    if (s->incnt + len > s->inlen || s->outcnt + len > s->outcap) return -1;
    memcpy(s->out + s->outcnt, s->in + s->incnt, len);
    s->incnt += len; s->outcnt += len;
    return 0;
}

static int z_fixed(zstate *s) {
    static int built;
    static huffman lencode, distcode;
    if (!built) {
        short lengths[FIXLCODES];
        int sym;
        for (sym = 0; sym < 144; sym++) lengths[sym] = 8;
        for (; sym < 256; sym++) lengths[sym] = 9;
        for (; sym < 280; sym++) lengths[sym] = 7;
        for (; sym < FIXLCODES; sym++) lengths[sym] = 8;
        z_construct(&lencode, lengths, FIXLCODES);
        for (sym = 0; sym < MAXDCODES; sym++) lengths[sym] = 5;
        z_construct(&distcode, lengths, MAXDCODES);
        built = 1;
    }
    return z_codes(s, &lencode, &distcode);
}

static int z_dynamic(zstate *s) {
    static const short order[19] = {16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15};
    short lengths[MAXLCODES + MAXDCODES];
    huffman lencode, distcode;
    int nlen = z_bits(s, 5), ndist = z_bits(s, 5), ncode = z_bits(s, 4);
    if (nlen < 0 || ndist < 0 || ncode < 0) return -1;
    nlen += 257; ndist += 1; ncode += 4;
    if (nlen > MAXLCODES || ndist > MAXDCODES) return -1;
    int index;
    for (index = 0; index < ncode; index++) {
        int v = z_bits(s, 3);
        if (v < 0) return -1;
        lengths[order[index]] = (short)v;
    }
    for (; index < 19; index++) lengths[order[index]] = 0;
    if (z_construct(&lencode, lengths, 19) != 0) return -1;
    index = 0;
    while (index < nlen + ndist) {
        int symbol = z_decode(s, &lencode);
        if (symbol < 0) return -1;
        if (symbol < 16) {
            lengths[index++] = (short)symbol;
        } else {
            int len = 0, rep;
            if (symbol == 16) {
                if (index == 0) return -1;
                len = lengths[index - 1];
                rep = z_bits(s, 2);
                if (rep < 0) return -1;
                rep += 3;
            } else if (symbol == 17) {
                rep = z_bits(s, 3);
                if (rep < 0) return -1;
                rep += 3;
            } else {
                rep = z_bits(s, 7);
                if (rep < 0) return -1;
                rep += 11;
            }
            if (index + rep > nlen + ndist) return -1;
            while (rep--) lengths[index++] = (short)len;
        }
    }
    if (lengths[256] == 0) return -1;                  /* no end-of-block code */
    int err = z_construct(&lencode, lengths, nlen);
    if (err && (err < 0 || nlen - lencode.count[0] != 1)) return -1;
    err = z_construct(&distcode, lengths + nlen, ndist);
    if (err && (err < 0 || ndist - distcode.count[0] != 1)) return -1;
    return z_codes(s, &lencode, &distcode);
}

/* zlib stream (2-byte header, deflate data, Adler-32 ignored) -> exactly `want` bytes; 0 on success */
int op_zinflate(const uint8_t *in, size_t n, uint8_t *out, size_t want) {
    if (n < 2 || (in[0] & 0x0F) != 8 || ((in[0] << 8) | in[1]) % 31 != 0 || (in[1] & 0x20)) return -1;
    zstate s;
    memset(&s, 0, sizeof s);
    s.in = in + 2; s.inlen = n - 2;
    s.out = out; s.outcap = want;
    int last;
    do {
        last = z_bits(&s, 1);
        int type = z_bits(&s, 2);
        if (last < 0 || type < 0) return -1;
        int err = type == 0 ? z_stored(&s) : type == 1 ? z_fixed(&s) : type == 2 ? z_dynamic(&s) : -1;
        if (err) return -1;
    } while (!last);
    return s.outcnt == want ? 0 : -1;
}
