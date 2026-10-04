/* op_platform.c - error reporting, file access and OS code page conversion (POSIX and Windows). */
#include "op_internal.h"

#ifdef _WIN32
#  ifndef _CRT_RAND_S
#    define _CRT_RAND_S
#  endif
#  define WIN32_LEAN_AND_MEAN
#  include <windows.h>
#else
#  include <errno.h>
#  include <fcntl.h>
#  include <sys/stat.h>
#  include <sys/types.h>
#  include <unistd.h>
#  include <iconv.h>
#endif

static OP_TLS char g_err[320];

int op_err(int code, const char *fmt, ...) {
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(g_err, sizeof g_err, fmt, ap);
    va_end(ap);
    return code;
}

const char *opst_last_error(void) { return g_err; }
const char *opst_version(void) { return "0.3.0"; }

struct opfile {
#ifdef _WIN32
    HANDLE h;
#else
    int fd;
#endif
    uint64_t size;
};

uint64_t op_file_size(opfile *f) { return f->size; }

int op_random_os(void *buf, size_t n);
/* OPST_TEST_RANDOM=1: a fixed byte stream (xorshift32, seed 0x12345678, top byte of each step) so that two implementations can be
   compared byte for byte; the Python side patches os.urandom with the same generator (tests/pytestrandom.py). */
int op_random(void *buf, size_t n) {
    static uint32_t st = 0x12345678u;
    const char *e = getenv("OPST_TEST_RANDOM");
    if (e && *e == '1') {
        uint8_t *b = (uint8_t *)buf;
        for (size_t i = 0; i < n; i++) { st ^= st << 13; st ^= st >> 17; st ^= st << 5; b[i] = (uint8_t)(st >> 24); }
        return 0;
    }
    return op_random_os(buf, n);
}

#ifdef _WIN32
int op_file_open(const char *path, int writable, opfile **out) {
    int n = MultiByteToWideChar(CP_UTF8, 0, path, -1, NULL, 0);
    if (n <= 0) return op_err(OPST_E_ARG, "path is not valid UTF-8");
    wchar_t *w = (wchar_t *)malloc((size_t)n * sizeof(wchar_t));
    if (!w) return op_err(OPST_E_NOMEM, "out of memory");
    MultiByteToWideChar(CP_UTF8, 0, path, -1, w, n);
    HANDLE h = CreateFileW(w, writable ? (GENERIC_READ | GENERIC_WRITE) : GENERIC_READ,
                           FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    free(w);
    if (h == INVALID_HANDLE_VALUE) return op_err(OPST_E_IO, "cannot open '%s' (error %lu)", path, GetLastError());
    LARGE_INTEGER sz;
    if (!GetFileSizeEx(h, &sz)) { CloseHandle(h); return op_err(OPST_E_IO, "cannot get the size of '%s'", path); }
    opfile *f = (opfile *)calloc(1, sizeof *f);
    if (!f) { CloseHandle(h); return op_err(OPST_E_NOMEM, "out of memory"); }
    f->h = h; f->size = (uint64_t)sz.QuadPart;
    *out = f;
    return 0;
}
void op_file_close(opfile *f) { if (f) { CloseHandle(f->h); free(f); } }
int op_file_pread(opfile *f, void *buf, size_t n, uint64_t off) {
    uint8_t *b = (uint8_t *)buf;
    while (n) {
        OVERLAPPED ov;
        memset(&ov, 0, sizeof ov);
        ov.Offset = (DWORD)(off & 0xFFFFFFFFu);
        ov.OffsetHigh = (DWORD)(off >> 32);
        DWORD want = n > 0x40000000u ? 0x40000000u : (DWORD)n, got = 0;
        if (!ReadFile(f->h, b, want, &got, &ov) || got == 0) return op_err(OPST_E_IO, "read error at offset %llu", (unsigned long long)off);
        b += got; off += got; n -= got;
    }
    return 0;
}
static wchar_t *utf8_to_wide(const char *path) {
    int n = MultiByteToWideChar(CP_UTF8, 0, path, -1, NULL, 0);
    if (n <= 0) return NULL;
    wchar_t *w = (wchar_t *)malloc((size_t)n * sizeof(wchar_t));
    if (w) MultiByteToWideChar(CP_UTF8, 0, path, -1, w, n);
    return w;
}
int op_file_pwrite(opfile *f, const void *buf, size_t n, uint64_t off) {
    const uint8_t *b = (const uint8_t *)buf;
    uint64_t end = off + n;
    while (n) {
        OVERLAPPED ov;
        memset(&ov, 0, sizeof ov);
        ov.Offset = (DWORD)(off & 0xFFFFFFFFu);
        ov.OffsetHigh = (DWORD)(off >> 32);
        DWORD want = n > 0x40000000u ? 0x40000000u : (DWORD)n, put = 0;
        if (!WriteFile(f->h, b, want, &put, &ov) || put == 0) return op_err(OPST_E_IO, "write error at offset %llu (error %lu)", (unsigned long long)off, GetLastError());
        b += put; off += put; n -= put;
    }
    if (end > f->size) f->size = end;
    return 0;
}
int op_file_sync(opfile *f) { return FlushFileBuffers(f->h) ? 0 : op_err(OPST_E_IO, "cannot flush the file (error %lu)", GetLastError()); }
int op_file_truncate(opfile *f, uint64_t size) {
    LARGE_INTEGER li;
    li.QuadPart = (LONGLONG)size;
    if (!SetFilePointerEx(f->h, li, NULL, FILE_BEGIN) || !SetEndOfFile(f->h)) return op_err(OPST_E_IO, "cannot resize the file (error %lu)", GetLastError());
    f->size = size;
    return 0;
}
int op_file_exists(const char *path) {
    wchar_t *w = utf8_to_wide(path);
    if (!w) return 0;
    DWORD a = GetFileAttributesW(w);
    free(w);
    return a != INVALID_FILE_ATTRIBUTES;
}
int op_file_remove(const char *path) {
    wchar_t *w = utf8_to_wide(path);
    if (!w) return op_err(OPST_E_ARG, "path is not valid UTF-8");
    BOOL ok = DeleteFileW(w);
    free(w);
    return ok ? 0 : op_err(OPST_E_IO, "cannot delete '%s' (error %lu)", path, GetLastError());
}
int op_file_write_all(const char *path, const void *buf, size_t n) {
    wchar_t *w = utf8_to_wide(path);
    if (!w) return op_err(OPST_E_ARG, "path is not valid UTF-8");
    HANDLE h = CreateFileW(w, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    free(w);
    if (h == INVALID_HANDLE_VALUE) return op_err(OPST_E_IO, "cannot create '%s' (error %lu)", path, GetLastError());
    const uint8_t *b = (const uint8_t *)buf;
    int rc = 0;
    while (n && !rc) {
        DWORD want = n > 0x40000000u ? 0x40000000u : (DWORD)n, put = 0;
        if (!WriteFile(h, b, want, &put, NULL) || put == 0) rc = op_err(OPST_E_IO, "cannot write '%s'", path);
        b += put; n -= put;
    }
    if (!rc && !FlushFileBuffers(h)) rc = op_err(OPST_E_IO, "cannot flush '%s'", path);
    CloseHandle(h);
    return rc;
}
int op_file_read_all(const char *path, uint8_t **buf, size_t *n) {
    opfile *f;
    int rc = op_file_open(path, 0, &f);
    if (rc) return rc;
    size_t sz = (size_t)op_file_size(f);
    *buf = (uint8_t *)malloc(sz ? sz : 1);
    if (!*buf) { op_file_close(f); return op_err(OPST_E_NOMEM, "out of memory"); }
    rc = sz ? op_file_pread(f, *buf, sz, 0) : 0;
    op_file_close(f);
    if (rc) { free(*buf); *buf = NULL; return rc; }
    *n = sz;
    return 0;
}
int op_path_same(const char *a, const char *b) {
    wchar_t *wa = utf8_to_wide(a), *wb = utf8_to_wide(b);
    int same = 0;
    if (wa && wb) {
        wchar_t fa[1024], fb[1024];
        DWORD na = GetFullPathNameW(wa, 1024, fa, NULL), nb = GetFullPathNameW(wb, 1024, fb, NULL);
        same = na > 0 && na < 1024 && nb > 0 && nb < 1024 && _wcsicmp(fa, fb) == 0;
    }
    free(wa); free(wb);
    return same;
}
int op_random_os(void *buf, size_t n) {
    uint8_t *b = (uint8_t *)buf;
    while (n) {
        unsigned int r;
        if (rand_s(&r) != 0) return op_err(OPST_E_IO, "no random numbers available");
        for (int k = 0; k < 4 && n; k++, n--) { *b++ = (uint8_t)(r & 0xFF); r >>= 8; }
    }
    return 0;
}
char *op_os_ansi_to_utf8(const uint8_t *s, size_t n, unsigned cp, size_t *outlen) {
    if (n == 0) { char *e = (char *)malloc(1); if (e) e[0] = 0; if (outlen) *outlen = 0; return e; }
    int wn = MultiByteToWideChar(cp, 0, (const char *)s, (int)n, NULL, 0);
    if (wn <= 0) return NULL;
    wchar_t *w = (wchar_t *)malloc((size_t)wn * sizeof(wchar_t));
    if (!w) return NULL;
    MultiByteToWideChar(cp, 0, (const char *)s, (int)n, w, wn);
    char *r = op_utf16_to_utf8((const uint8_t *)w, (size_t)wn * 2, outlen);
    free(w);
    return r;
}
#else
int op_file_open(const char *path, int writable, opfile **out) {
    int fd = open(path, (writable ? O_RDWR : O_RDONLY)
#ifdef O_CLOEXEC
                  | O_CLOEXEC
#endif
                  );
    if (fd < 0) return op_err(OPST_E_IO, "cannot open '%s': %s", path, strerror(errno));
    struct stat st;
    if (fstat(fd, &st) != 0) { close(fd); return op_err(OPST_E_IO, "cannot stat '%s': %s", path, strerror(errno)); }
    opfile *f = (opfile *)calloc(1, sizeof *f);
    if (!f) { close(fd); return op_err(OPST_E_NOMEM, "out of memory"); }
    f->fd = fd; f->size = (uint64_t)st.st_size;
    *out = f;
    return 0;
}
void op_file_close(opfile *f) { if (f) { close(f->fd); free(f); } }
int op_file_pread(opfile *f, void *buf, size_t n, uint64_t off) {
    uint8_t *b = (uint8_t *)buf;
    while (n) {
        ssize_t r = pread(f->fd, b, n, (off_t)off);
        if (r < 0 && errno == EINTR) continue;
        if (r <= 0) return op_err(OPST_E_IO, "read error at offset %llu", (unsigned long long)off);
        b += r; off += (uint64_t)r; n -= (size_t)r;
    }
    return 0;
}
int op_file_pwrite(opfile *f, const void *buf, size_t n, uint64_t off) {
    const uint8_t *b = (const uint8_t *)buf;
    uint64_t end = off + n;
    while (n) {
        ssize_t r = pwrite(f->fd, b, n, (off_t)off);
        if (r < 0 && errno == EINTR) continue;
        if (r <= 0) return op_err(OPST_E_IO, "write error at offset %llu: %s", (unsigned long long)off, strerror(errno));
        b += r; off += (uint64_t)r; n -= (size_t)r;
    }
    if (end > f->size) f->size = end;
    return 0;
}
int op_file_sync(opfile *f) { return fsync(f->fd) == 0 ? 0 : op_err(OPST_E_IO, "cannot flush the file: %s", strerror(errno)); }
int op_file_truncate(opfile *f, uint64_t size) {
    if (ftruncate(f->fd, (off_t)size) != 0) return op_err(OPST_E_IO, "cannot resize the file: %s", strerror(errno));
    f->size = size;
    return 0;
}
int op_file_exists(const char *path) { struct stat st; return stat(path, &st) == 0; }
int op_file_remove(const char *path) { return unlink(path) == 0 ? 0 : op_err(OPST_E_IO, "cannot delete '%s': %s", path, strerror(errno)); }
int op_file_write_all(const char *path, const void *buf, size_t n) {
    int fd = open(path, O_WRONLY | O_CREAT | O_TRUNC, 0644);
    if (fd < 0) return op_err(OPST_E_IO, "cannot create '%s': %s", path, strerror(errno));
    const uint8_t *b = (const uint8_t *)buf;
    int rc = 0;
    while (n && !rc) {
        ssize_t r = write(fd, b, n);
        if (r < 0 && errno == EINTR) continue;
        if (r <= 0) rc = op_err(OPST_E_IO, "cannot write '%s': %s", path, strerror(errno));
        else { b += r; n -= (size_t)r; }
    }
    if (!rc && fsync(fd) != 0) rc = op_err(OPST_E_IO, "cannot flush '%s': %s", path, strerror(errno));
    close(fd);
    return rc;
}
int op_file_read_all(const char *path, uint8_t **buf, size_t *n) {
    opfile *f;
    int rc = op_file_open(path, 0, &f);
    if (rc) return rc;
    size_t sz = (size_t)op_file_size(f);
    *buf = (uint8_t *)malloc(sz ? sz : 1);
    if (!*buf) { op_file_close(f); return op_err(OPST_E_NOMEM, "out of memory"); }
    rc = sz ? op_file_pread(f, *buf, sz, 0) : 0;
    op_file_close(f);
    if (rc) { free(*buf); *buf = NULL; return rc; }
    *n = sz;
    return 0;
}
int op_path_same(const char *a, const char *b) {
    char ra[4096], rb[4096];
    if (!realpath(a, ra) || !realpath(b, rb)) return strcmp(a, b) == 0;
    return strcmp(ra, rb) == 0;
}
int op_random_os(void *buf, size_t n) {
    FILE *f = fopen("/dev/urandom", "rb");
    if (!f) return op_err(OPST_E_IO, "no random numbers available");
    size_t r = fread(buf, 1, n, f);
    fclose(f);
    return r == n ? 0 : op_err(OPST_E_IO, "no random numbers available");
}
char *op_os_ansi_to_utf8(const uint8_t *s, size_t n, unsigned cp, size_t *outlen) {
    char name[24];
    if (cp >= 28591 && cp <= 28599) snprintf(name, sizeof name, "ISO-8859-%u", cp - 28590);
    else if (cp == 28605) snprintf(name, sizeof name, "ISO-8859-15");
    else if (cp == 20866) snprintf(name, sizeof name, "KOI8-R");
    else if (cp == 21866) snprintf(name, sizeof name, "KOI8-U");
    else if (cp == 51932) snprintf(name, sizeof name, "EUC-JP");
    else if (cp == 51949) snprintf(name, sizeof name, "EUC-KR");
    else if (cp == 54936) snprintf(name, sizeof name, "GB18030");
    else if (cp == 10000) snprintf(name, sizeof name, "MACINTOSH");
    else snprintf(name, sizeof name, "CP%u", cp);
    iconv_t cd = iconv_open("UTF-8", name);
    if (cd == (iconv_t)-1) return NULL;
    size_t cap = n * 4 + 8, used = 0;
    char *out = (char *)malloc(cap);
    if (!out) { iconv_close(cd); return NULL; }
    char *ip = (char *)s;
    size_t il = n;
    while (il > 0) {
        char *op = out + used;
        size_t ol = cap - used - 1;
        size_t r = iconv(cd, &ip, &il, &op, &ol);
        used = (size_t)(op - out);
        if (r == (size_t)-1) {
            if (errno == E2BIG) {
                cap *= 2;
                char *t = (char *)realloc(out, cap);
                if (!t) { free(out); iconv_close(cd); return NULL; }
                out = t;
            } else {                       /* invalid byte: U+FFFD, skip it */
                if (used + 4 >= cap) { cap *= 2; char *t = (char *)realloc(out, cap); if (!t) { free(out); iconv_close(cd); return NULL; } out = t; }
                out[used++] = (char)0xEF; out[used++] = (char)0xBF; out[used++] = (char)0xBD;
                ip++; il--;
            }
        }
    }
    iconv_close(cd);
    out[used] = 0;
    if (outlen) *outlen = used;
    return out;
}
#endif
