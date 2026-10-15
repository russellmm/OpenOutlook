#!/usr/bin/env python3
"""Checks docs/EDGE_CASES.md against the tests: every cited Class.Method exists and every test class is cited.
usage: python scripts/check-edge-cases.py   (exit 1 when something is missing)"""
import re, glob, os, sys
os.chdir(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
src = {}
for f in glob.glob('tests/**/*.cs', recursive=True):
    g = f.replace(os.sep, '/')
    if '/bin/' in g or '/obj/' in g: continue
    src[os.path.basename(f)[:-3]] = open(f, encoding='utf-8').read()
doc = open('docs/EDGE_CASES.md', encoding='utf-8').read()
bad = []
for cls, meth in re.findall(r'(\w+Tests|UnitTest1)\.(\w+|\*)', doc):
    if cls not in src: bad.append(cls); continue
    if meth != '*' and not re.search(r'\b' + meth + r'\(', src[cls]): bad.append(cls + '.' + meth)
for meth in re.findall(r'`\.(\w+)`', doc):
    if not any(re.search(r'\b' + meth + r'\(', s) for s in src.values()): bad.append('.' + meth)
used = set(re.findall(r'(\w+Tests|UnitTest1)\.', doc))
uncited = sorted(k for k in src if (k.endswith('Tests') or k == 'UnitTest1') and k not in used)
if bad: print('cited but not found:', ', '.join(sorted(set(bad))))
if uncited: print('test classes not in EDGE_CASES.md:', ', '.join(uncited))
if not bad and not uncited: print('EDGE_CASES.md: every cited test exists and every test class is cited.')
sys.exit(1 if bad or uncited else 0)
