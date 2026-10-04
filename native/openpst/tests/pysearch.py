#!/usr/bin/env python3
"""Reference for opst_search: prints the hits of pstsearch.Searcher as TSV (nid, folder nid, folder path, subject), the same format
as `openpst FILE searchdump QUERY [--body]`.   usage: pysearch.py REPO_DIR FILE.pst QUERY [--body]"""
import sys
sys.path.insert(0, sys.argv[1])
import pstsearch

body = '--body' in sys.argv
s = pstsearch.Searcher(sys.argv[2])
try:
    for h in s.search(sys.argv[3], body=body, include_deleted=True):
        print('%d\t%d\t%s\t%s' % (h.nid, h.folder, h.folder_path.replace('\t', ' '), h.subject.replace('\t', ' ').replace('\n', ' ').replace('\r', ' ')))
finally:
    s.close()
