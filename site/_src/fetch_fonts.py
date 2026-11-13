"""Refresh self-hosted, text-subset Noto fonts after changing dictionaries.

Only this optional maintenance script needs network access; build.py is offline.
Google Fonts text subsets keep the download small and cover all dictionary text.
"""
import json
from pathlib import Path
import re
from urllib.parse import urlencode
from urllib.request import Request, urlopen

SRC = Path(__file__).resolve().parent
FONTS = SRC.parent / 'assets' / 'fonts'
text = ''.join(''.join(json.loads(p.read_text()).values()) for p in (SRC / 'i18n').glob('*.json'))
text += 'Čeština Slovenčina English Deutsch Polski Українська Français Español Italiano Português Русский Türkçe العربية 中文 日本語 한국어 Bahasa Indonesia'
sets = [
    ('Arabic', 'Noto Sans Arabic', lambda c: '\u0600' <= c <= '\u06ff', 'U+0600-06FF'),
    ('CJK', 'Noto Sans SC', lambda c: '\u3000' <= c <= '\u9fff' or '\uff00' <= c <= '\uffef', 'U+3000-9FFF,U+FF00-FFEF'),
    ('Korean', 'Noto Sans KR', lambda c: '\uac00' <= c <= '\ud7af', 'U+AC00-D7AF'),
    ('Cyrillic', 'Noto Sans', lambda c: '\u0400' <= c <= '\u052f', 'U+0400-052F'),
]
css = ['/* Noto fonts, SIL OFL 1.1. Self-hosted text subsets; refresh with _src/fetch_fonts.py. */']
for name, family, include, ranges in sets:
    chars = ''.join(sorted({c for c in text if include(c)}))
    # Japanese kana need Japanese glyphs; shared Han characters use SC as a fallback.
    url = 'https://fonts.googleapis.com/css2?' + urlencode({'family':family+':wght@400..600','text':chars,'display':'swap'})
    source = urlopen(Request(url, headers={'User-Agent':'Mozilla/5.0'})).read().decode()
    font_url = re.search(r'url\((https:[^)]+)\)', source)[1]
    filename = f'noto-{name.lower()}.ttf'
    (FONTS / filename).write_bytes(urlopen(font_url).read())
    css.append(f'@font-face {{ font-family: "DeskShare {name}"; font-style: normal; font-weight: 400 600; font-display: swap; src: url("{filename}"); unicode-range: {ranges}; }}')
# Use a Japanese subset for kana and Japanese Han forms on Japanese pages.
chars = ''.join(sorted({c for c in text if '\u3000' <= c <= '\u9fff' or '\uff00' <= c <= '\uffef'}))
url = 'https://fonts.googleapis.com/css2?' + urlencode({'family':'Noto Sans JP:wght@400..600','text':chars,'display':'swap'})
source = urlopen(url).read().decode()
(FONTS / 'noto-japanese.ttf').write_bytes(urlopen(re.search(r'url\((https:[^)]+)\)',source)[1]).read())
css.append('@font-face { font-family: "DeskShare Japanese"; font-style: normal; font-weight: 400 600; font-display: swap; src: url("noto-japanese.ttf"); unicode-range: U+3000-9FFF,U+FF00-FFEF; }')
(FONTS / 'international.css').write_text('\n'.join(css)+'\n')
