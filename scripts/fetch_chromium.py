#!/usr/bin/env python3
"""Downloads the pinned chrome-headless-shell (Google's "Chrome for Testing" build) that OpenOutlook bundles for laying out HTML mail.

usage: python scripts/fetch_chromium.py [win64] [linux64] ...      (default: win64 and linux64)

The version is pinned in scripts/chromium-version.txt (change it only together with a test of the reading pane). Each platform is unpacked
to third_party/chromium/<platform>/ (ignored by git); the Desktop project copies the matching folder next to the application at build and
publish time (see OpenOutlook.Desktop.csproj, item "ChromiumShell"). The download is checked against the size the server reports and the
archive is unpacked only when it is a valid zip.
"""
import hashlib, os, shutil, sys, urllib.request, zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
VERSION = open(os.path.join(ROOT, 'scripts', 'chromium-version.txt'), encoding='utf-8').read().strip()
BASE = 'https://storage.googleapis.com/chrome-for-testing-public/%s/%s/chrome-headless-shell-%s.zip'


def fetch(platform):
    dest = os.path.join(ROOT, 'third_party', 'chromium', platform)
    marker = os.path.join(dest, 'VERSION.txt')
    if os.path.exists(marker) and open(marker, encoding='utf-8').read().strip() == VERSION:
        print('%s: %s already present' % (platform, VERSION))
        return
    url = BASE % (VERSION, platform, platform)
    os.makedirs(os.path.join(ROOT, 'third_party', 'chromium'), exist_ok=True)
    zpath = os.path.join(ROOT, 'third_party', 'chromium', 'download-%s.zip' % platform)
    print('%s: downloading %s' % (platform, url))
    with urllib.request.urlopen(url, timeout=120) as r, open(zpath, 'wb') as out:
        expected = int(r.headers.get('Content-Length', '0'))
        sha = hashlib.sha256()
        got = 0
        while True:
            chunk = r.read(1 << 20)
            if not chunk:
                break
            out.write(chunk); sha.update(chunk); got += len(chunk)
    if expected and got != expected:
        raise SystemExit('%s: incomplete download (%d of %d bytes)' % (platform, got, expected))
    if not zipfile.is_zipfile(zpath):
        raise SystemExit('%s: the download is not a zip file' % platform)
    print('%s: %.1f MB, sha256 %s' % (platform, got / 1048576, sha.hexdigest()))
    tmp = dest + '.tmp'
    shutil.rmtree(tmp, ignore_errors=True)
    with zipfile.ZipFile(zpath) as z:
        z.extractall(tmp)
    inner = os.path.join(tmp, 'chrome-headless-shell-%s' % platform)       # the archive has one top folder
    shutil.rmtree(dest, ignore_errors=True)
    shutil.move(inner if os.path.isdir(inner) else tmp, dest)
    shutil.rmtree(tmp, ignore_errors=True)
    os.remove(zpath)
    with open(marker, 'w', encoding='utf-8') as f:
        f.write(VERSION + '\n')
    if platform.startswith('linux') or platform.startswith('mac'):
        exe = os.path.join(dest, 'chrome-headless-shell')
        if os.path.exists(exe):
            os.chmod(exe, 0o755)
    print('%s: ready in %s' % (platform, dest))


if __name__ == '__main__':
    for p in (sys.argv[1:] or ['win64', 'linux64']):
        fetch(p)
