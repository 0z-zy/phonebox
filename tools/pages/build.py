#!/usr/bin/env python3
"""Assembles the static web demo (GitHub Pages) from the real panel and settings page:  python tools/pages/build.py _site"""
import shutil, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WWW = ROOT / 'android' / 'app' / 'src' / 'main' / 'assets' / 'www'
AGENT = ROOT / 'agent' / 'PhoneBox.Agent'
PAGES = Path(__file__).resolve().parent


def panel_html(relay=False):
    """The panel's index.html with the demo API loaded first (a classic script, before the ES modules)."""
    html = (WWW / 'index.html').read_text(encoding='utf-8')
    tag = '<script type="module" src="main.js"></script>'
    assert tag in html
    return html.replace(tag, f'<script src="../demo.js" data-panel{" data-relay" if relay else ""}></script>\n' + tag)


def settings_html(relay=False):
    """The real settings page with the demo API loaded before its script."""
    html = (AGENT / 'settings.html').read_text(encoding='utf-8')
    assert '<script>' in html
    return (html.replace('<script>', f'<script src="../demo.js"{" data-relay" if relay else ""}></script>\n<script>', 1)
            .replace('href="/favicon.ico"', 'href="../favicon.ico"').replace('src="/favicon.ico"', 'src="../favicon.ico"'))


def build(out):
    if out.exists(): shutil.rmtree(out)
    (out / 'panel').mkdir(parents=True)
    (out / 'settings').mkdir()
    for name in ('index.html', 'demo.js'): shutil.copy(PAGES / name, out / name)
    for name in ('widgets.json', 'templates.json'): shutil.copy(AGENT / name, out / name)
    shutil.copy(AGENT / 'phonebox.ico', out / 'favicon.ico')
    shutil.copytree(WWW, out / 'panel', dirs_exist_ok=True, ignore=shutil.ignore_patterns('index.html'))
    (out / 'panel' / 'index.html').write_text(panel_html(), encoding='utf-8')
    (out / 'settings' / 'index.html').write_text(settings_html(), encoding='utf-8')
    print(f'web demo written to {out}')


if __name__ == '__main__':
    build(Path(sys.argv[1] if len(sys.argv) > 1 else '_site').resolve())
