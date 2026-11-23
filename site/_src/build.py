"""Build static landing pages. Python standard library only."""
import json
from html import escape
from pathlib import Path
from string import Template

SRC = Path(__file__).resolve().parent
SITE = SRC.parent
BASE = 'https://deskshare.zcloud.cz'
NAMES = dict(zip('cs sk en de pl uk fr es it pt ru tr ar zh ja ko id'.split(),
    ['Čeština', 'Slovenčina', 'English', 'Deutsch', 'Polski', 'Українська', 'Français', 'Español', 'Italiano', 'Português', 'Русский', 'Türkçe', 'العربية', '中文', '日本語', '한국어', 'Bahasa Indonesia']))

def path(lang):
    return '/' if lang == 'cs' else f'/{lang}/'

def jsonld(lang, data):
    """Schema.org @graph built only from facts stated on the site."""
    org = {'@type': 'Organization', '@id': 'https://zcloud.cz/#organization', 'name': 'ZCLOUD', 'url': 'https://zcloud.cz/',
        'identifier': {'@type': 'PropertyValue', 'propertyID': 'IČO', 'value': '07783809'},
        'address': {'@type': 'PostalAddress', 'addressLocality': 'Boskovice', 'addressCountry': 'CZ'},
        'founder': {'@type': 'Person', '@id': 'https://zcloud.cz/#martin-zahalka', 'name': 'Martin Zahálka', 'email': 'mailto:martin@zcloud.cz'}}
    app = {'@type': 'SoftwareApplication', '@id': f'{BASE}/#software', 'name': 'DeskShare', 'url': BASE + path(lang),
        'applicationCategory': 'UtilitiesApplication', 'operatingSystem': 'Windows, macOS, Linux',
        'description': f"{data['home.need_hint']} {data['tech_body']}", 'inLanguage': lang, 'isAccessibleForFree': True,
        'offers': {'@type': 'Offer', 'price': '0', 'priceCurrency': 'CZK'},
        'downloadUrl': 'https://github.com/zcloudcz/DeskShare/releases/latest', 'codeRepository': 'https://github.com/zcloudcz/DeskShare',
        'publisher': {'@id': org['@id']}, 'author': {'@id': org['founder']['@id']}}
    site = {'@type': 'WebSite', '@id': f'{BASE}/#website', 'name': 'DeskShare', 'url': BASE + '/', 'inLanguage': list(NAMES),
        'publisher': {'@id': org['@id']}}
    text = json.dumps({'@context': 'https://schema.org', '@graph': [app, org, site]}, ensure_ascii=False, indent=1)
    return '<script type="application/ld+json">\n' + text.replace('</', '<\\/') + '\n</script>'

def build():
    template = Template((SRC / 'template.html').read_text())
    dictionaries = {lang: json.loads((SRC / 'i18n' / f'{lang}.json').read_text()) for lang in NAMES}
    expected = set(dictionaries['en'])
    for lang, data in dictionaries.items():
        assert set(data) == expected, f'Incomplete dictionary: {lang}'
        assert all(isinstance(v, str) and v.strip() and '\u2014' not in v for v in data.values()), lang
        values = {k.replace('.', '_'): escape(v, quote=True) for k, v in data.items()}
        links = ''.join(f'<a href="{path(code)}" lang="{code}" hreflang="{code}" data-lang="{code}"{(" aria-current=\"page\"" if code == lang else "")}>{name}</a>' for code, name in NAMES.items())
        switcher = f'<details class="languages"><summary>{escape(data["language.label"])}: {NAMES[lang]}</summary><nav aria-label="{escape(data["language.label"])}"><a href="{path(lang)}" data-lang="auto">{escape(data["language.auto"])}</a>{links}</nav></details>'
        alternates = '\n'.join(f'<link rel="alternate" hreflang="{code}" href="{BASE}{path(code)}">' for code in NAMES)
        values.update(lang=lang, dir='rtl' if lang == 'ar' else 'ltr', path=path(lang), canonical=BASE+path(lang),
            alternates=alternates+f'\n<link rel="alternate" hreflang="x-default" href="{BASE}/en/">', switcher=switcher,
            release='https://github.com/zcloudcz/DeskShare/releases/latest/download', viewer_url=f'https://app.deskshare.zcloud.cz/webclient/index.html?lang={lang}',
            jsonld=jsonld(lang, data),
            privacy_url='/zasady-ochrany-osobnich-udaju/' if lang == 'cs' else '/en/privacy/')
        target = SITE / path(lang).lstrip('/') / 'index.html'
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(template.substitute(values))
    paths = [path(lang) for lang in NAMES] + ['/zasady-ochrany-osobnich-udaju/', '/en/privacy/']
    (SITE / 'sitemap.xml').write_text('<?xml version="1.0" encoding="UTF-8"?>\n<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">\n' + '\n'.join(f'  <url><loc>{BASE}{p}</loc></url>' for p in paths) + '\n</urlset>\n')
    for llms in ('llms.txt', 'en/llms.txt'):  # hand-written, kept in sync with the pages (see README)
        text = (SITE / llms).read_text()
        assert text.startswith('# DeskShare') and '\u2014' not in text, llms
    print(f'Built {len(NAMES)} landing pages and sitemap.')

if __name__ == '__main__':
    build()
