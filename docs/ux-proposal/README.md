# DeskShare — UX návrh pro Martina

- [PROPOSAL.md](PROPOSAL.md): český návrh, role a slovník, průchody, bezpečnost, detekce jazyků, implementace a odhady.
- [RESEARCH.md](RESEARCH.md): srovnání sedmi produktů, oficiální zdroje, deset principů a plán ověření s lidmi.
- [strings/core.csv](strings/core.csv): 42 textů × 17 jazyků v UTF-8; všechny překlady vyžadují rodilou revizi.
- [mockups/index.html](mockups/index.html): rozcestník sedmi HTML a PNG maket (CS, DE, AR/RTL).

## Opakované sestavení

Z kořene repozitáře:

```bash
python docs/ux-proposal/strings/build.py
python docs/ux-proposal/mockups/build.py
```

Pro každý HTML soubor kromě indexu spusťte (nahraďte oba absolutní názvy):

```bash
/home/zahy/.cache/ms-playwright/chromium-1254/chrome-linux64/chrome --headless --no-sandbox --disable-gpu --hide-scrollbars --window-size=1280,800 --virtual-time-budget=1500 --screenshot=/ABS/mockups/home-ar.png file:///ABS/mockups/home-ar.html
```

Arabské písmo je přiloženo lokálně: Noto Sans Arabic, [SIL Open Font License](mockups/fonts/OFL.txt), [zdroj](https://github.com/google/fonts/tree/main/ofl/notosansarabic). Makety nepoužívají vzdálené služby a neodesílají údaje. Ostatní písma využívají systémový fallback. Ikony jsou vložené SVG přímo v HTML.

## Provedená kontrola

13. 11. 2026: všech sedm PNG bylo vykresleno headless Chromium a jednotlivě vizuálně zkontrolováno. Opraveny chybějící arabské glyfy/ikony a přetečení obsahu. Ověřen rozměr všech PNG 1280 × 800; CSV má 18 sloupců, 42 jedinečných klíčů, žádné prázdné překlady a shodné pojmenované parametry. Změny pouze v této složce, žádný commit.

Makety jsou statické návrhy; klikání, skutečné souhlasy, detekce jazyků ani čtečky nebyly implementovány. Přepínač v maketách ukazuje aktuální jazyk; varianty otevřete z rozcestníku. Bez uživatelských testů a rodilé jazykové revize. Původní snímky ve `current/` zůstaly zachované.
