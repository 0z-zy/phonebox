#!/usr/bin/env python3
"""Assembles the static web demo (GitHub Pages) from the real panel and settings page:  python tools/pages/build.py _site"""
import shutil, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WWW = ROOT / 'android' / 'app' / 'src' / 'main' / 'assets' / 'www'
AGENT = ROOT / 'agent' / 'PhoneBox.Agent'
PAGES = Path(__file__).resolve().parent
out = Path(sys.argv[1] if len(sys.argv) > 1 else '_site').resolve()

if out.exists(): shutil.rmtree(out)
(out / 'panel').mkdir(parents=True)
(out / 'settings').mkdir()

for name in ('index.html', 'demo.js'): shutil.copy(PAGES / name, out / name)
for name in ('widgets.json', 'templates.json'): shutil.copy(AGENT / name, out / name)
shutil.copy(AGENT / 'phonebox.ico', out / 'favicon.ico')

# panel: the real assets (ES modules), with the demo API loaded first as a classic script
shutil.copytree(WWW, out / 'panel', dirs_exist_ok=True, ignore=shutil.ignore_patterns('index.html'))
html = (WWW / 'index.html').read_text(encoding='utf-8')
tag = '<script type="module" src="main.js"></script>'
assert tag in html
(out / 'panel' / 'index.html').write_text(html.replace(tag, '<script src="../demo.js" data-panel></script>\n' + tag), encoding='utf-8')

# settings page: the real one, with the demo API loaded before its script
html = (AGENT / 'settings.html').read_text(encoding='utf-8')
assert '<script>' in html
html = html.replace('<script>', '<script src="../demo.js"></script>\n<script>', 1).replace('href="/favicon.ico"', 'href="../favicon.ico"').replace('src="/favicon.ico"', 'src="../favicon.ico"')
(out / 'settings' / 'index.html').write_text(html, encoding='utf-8')
print(f'web demo written to {out}')
