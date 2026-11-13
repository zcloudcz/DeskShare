# DeskShare landing site

The GitHub Pages workflow publishes `site/` directly. Commit generated HTML with
source changes. No package installation or build service is required.

```sh
python3 site/_src/build.py
python3 site/_src/check_links.py --downloads
node site/_src/check_behavior.cjs
python3 -m http.server 8765 --directory site
```

Edit `template.html` for structure, `i18n/<code>.json` for copy, and
`../assets/site.css` / `site.js` for shared styling and browser behavior. The
initial role, safety and language strings were imported from
`docs/ux-proposal/strings/core.csv`; dictionaries are the editable source now.
Every language must contain every key, with a nonempty translation. The build
fails on missing keys and long dash characters. Translations need native-speaker
review, especially safety copy.

Czech stays at `/`, English at `/en/`, and other languages at `/<code>/`.
The two existing privacy documents retain their URLs and content; languages
without a privacy translation link to English. The sitemap includes all 19 pages.
Each landing page has canonical, Open Graph and all 17 alternate language links,
plus English as `x-default`.

Language precedence: valid `?lang=` (also saved), then explicit language path.
Only `/` detects a saved `deskshare.lang` choice or the first supported browser
language (primary subtag), falling back to English. The Automatic switcher clears
the saved choice and follows browser preferences immediately. Storage failures
are harmless. Language links work without JavaScript and the root stays Czech.
JavaScript switching removes stale `?lang=` while preserving other parameters.

Downloads use the latest GitHub release URLs. The hero selects Windows, macOS
Apple Silicon or Linux; Intel macOS is available in all downloads. Phones link
to the downloads section instead of suggesting that the web viewer can share
their screen. The helper path always opens the viewer with `?lang=<code>`.

Fonts are self-hosted: existing IBM Plex for Latin, plus SIL OFL Noto subsets for
Arabic, Cyrillic, Chinese, Japanese and Korean. After changing translations run
`python3 site/_src/fetch_fonts.py` to refresh the text subsets (network required).
The normal build is offline. Font licenses are alongside the font files.

Screenshots are in `docs/ux-proposal/impl/site-<code>-<width>x<height>.png`.
They use an explicit language parameter so the headless browser's English locale
does not redirect the Czech screenshot. Capture at 1280x800 and 390x844 for
cs, en, de, ar and ja. Inspect the actual images after capturing.

Verification for this change: all 19 pages and 798 internal references passed;
all six unique release assets returned HTTP 200 with `curl -sIL`. The behavior
check covers query/saved/browser precedence, regional tags, unsupported languages,
blocked storage, explicit paths, Automatic and OS selection. Chromium rendered
all 17 pages at 390 px with both menus open without horizontal overflow. All ten
required screenshots were visually reviewed; missing glyphs found in the first
pass were corrected with the bundled fonts. No app or authentication changes
are part of the landing-site implementation.
