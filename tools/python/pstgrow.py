#!/usr/bin/env python3
"""pstgrow.py FILE N   - append N empty sections (N x 253,952 bytes) to a PST. WRITES: work on a copy.
Normally the writer grows the file by itself when it runs out of space; this is for testing and pre-allocation."""
import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pstwrite as W

def main(a):
    if len(a) != 2:
        print(__doc__); return 2
    w = W.PSTWriter(a[0])
    try:
        first = w.grow(int(a[1]))
        w.commit()
        print('grew from %d to %d sections; file is now %d bytes' % (first, w.nsec, w.eof))
        p = w.validate(); print('validate: %d problems' % len(p), p[:5])
    except Exception:
        w.rollback_pending(); raise
    finally:
        w.close()
    return 0

if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
