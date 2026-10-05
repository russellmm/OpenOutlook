"""pio.py - tiny portability shim so the PST tools run unchanged on Windows.
Import it before any code that calls os.pread / os.pwrite / os.open (it patches the os module in place).
On Linux/macOS it does nothing."""
import os

if not hasattr(os, 'pread'):
    def _pread(fd, n, offset):
        os.lseek(fd, offset, os.SEEK_SET)
        out = bytearray()
        while len(out) < n:
            chunk = os.read(fd, min(n - len(out), 1 << 24))
            if not chunk:
                break
            out += chunk
        return bytes(out)

    def _pwrite(fd, data, offset):
        os.lseek(fd, offset, os.SEEK_SET)
        view = memoryview(data)
        done = 0
        while done < len(view):
            done += os.write(fd, view[done:done + (1 << 24)])
        return done

    os.pread = _pread
    os.pwrite = _pwrite

if hasattr(os, 'O_BINARY'):
    _orig_open = os.open

    def _open(path, flags, mode=0o777, **kw):
        return _orig_open(path, flags | os.O_BINARY, mode)

    os.open = _open
