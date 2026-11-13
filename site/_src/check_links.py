"""Check every generated/local HTML link and fragment; optionally probe release assets."""
import argparse
from concurrent.futures import ThreadPoolExecutor
from html.parser import HTMLParser
from pathlib import Path
import subprocess
from urllib.parse import unquote, urlsplit

SITE = Path(__file__).resolve().parents[1]
class Page(HTMLParser):
    def __init__(self, source):
        super().__init__()
        self.links, self.ids = [], set()
        self.feed(source)
    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if 'id' in attrs:
            self.ids.add(attrs['id'])
        for key in ('href', 'src'):
            if key in attrs:
                self.links.append(attrs[key])

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--downloads', action='store_true')
    args = parser.parse_args()
    pages = {p: Page(p.read_text()) for p in SITE.rglob('*.html') if '_src' not in p.parts}
    errors, downloads = [], set()
    count = 0
    for source, page in pages.items():
        for link in page.links:
            url = urlsplit(link)
            if '/releases/latest/download/' in link:
                downloads.add(link)
            if url.scheme or url.netloc:
                continue
            target = SITE / unquote(url.path).lstrip('/') if url.path.startswith('/') else source.parent / unquote(url.path)
            if not url.path:
                target = source
            elif target.is_dir():
                target /= 'index.html'
            count += 1
            if not target.is_file():
                errors.append(f'{source.relative_to(SITE)}: missing {link}')
            elif url.fragment and target in pages and unquote(url.fragment) not in pages[target].ids:
                errors.append(f'{source.relative_to(SITE)}: missing fragment {link}')
    if args.downloads:
        def probe(url):
            result = subprocess.run(['curl', '-sIL', '--max-time', '60', '-o', '/dev/null', '-w', '%{http_code}', url], capture_output=True, text=True)
            return url, result.stdout.strip()
        with ThreadPoolExecutor(max_workers=6) as pool:
            for url, status in pool.map(probe, sorted(downloads)):
                print(f'{status} {url}')
                if status != '200':
                    errors.append(f'Download returned {status}: {url}')
    print(f'Checked {len(pages)} pages, {count} local references, {len(downloads)} unique release assets.')
    for error in errors:
        print(error)
    raise SystemExit(bool(errors))

if __name__ == '__main__':
    main()
