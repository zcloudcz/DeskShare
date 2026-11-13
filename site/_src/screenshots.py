"""Capture the requested language/viewport matrix against a running local server."""
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
import subprocess
import tempfile

REPO = Path(__file__).resolve().parents[2]
CHROME = Path.home() / '.cache/ms-playwright/chromium-1254/chrome-linux64/chrome'
OUT = REPO / 'docs/ux-proposal/impl'
OUT.mkdir(parents=True, exist_ok=True)

def capture(item):
    lang, width, height = item
    path = '/' if lang == 'cs' else f'/{lang}/'
    target = OUT / f'site-{lang}-{width}x{height}.png'
    with tempfile.TemporaryDirectory(prefix='deskshare-screenshot-') as profile:
        subprocess.run([str(CHROME), '--headless', '--no-sandbox', '--disable-gpu', '--hide-scrollbars',
            '--no-first-run', f'--user-data-dir={profile}', f'--window-size={width},{height}',
            '--force-device-scale-factor=1', '--virtual-time-budget=3000', f'--screenshot={target}',
            f'http://localhost:8765{path}?lang={lang}'], check=True, capture_output=True)
    return target.relative_to(REPO)

if __name__ == '__main__':
    matrix = [(lang, w, h) for lang in ['cs', 'en', 'de', 'ar', 'ja'] for w, h in [(1280, 800), (390, 844)]]
    with ThreadPoolExecutor(max_workers=3) as pool:
        for target in pool.map(capture, matrix):
            print(target)
