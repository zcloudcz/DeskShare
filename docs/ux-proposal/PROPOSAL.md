# DeskShare: pomoc bez technických pojmů

**Rozhodnutí pro Martina:** jeden úvod, dvě velké volby, žádné „Server Mode“. Člověk potřebující pomoc vidí své číslo, kód a jasné zastavení. Pomáhající vyplní dvě pole. Modrá #0b72d9, světlé pozadí, text alespoň 18 px, ovládací prvky alespoň 48 px.

Jde pouze o návrh; aplikace se nemění. [Výzkum a zdroje](RESEARCH.md), [tabulka překladů](strings/core.csv), [přehled maket](mockups/index.html). Makety jsou statické a obsahují smyšlené údaje.

## 1. Úvod a společná struktura

- **Potřebuji pomoc** — „Ukážu svou obrazovku někomu, komu důvěřuji.“ Otevře přípravu sdílení, ještě nic nesdílí.
- **Chci pomoct někomu** — „Zadám číslo a kód od druhého člověka.“
- Vpravo nahoře **Jazyk / Čeština**, vždy dostupné; žádné vlajky. V nabídce názvy jazyků jejich vlastním písmem a „Automaticky“.
- Dole **Rozšířené**: historie, diagnostika, vlastní připojovací služba, účet, soubory/schránka (v UI psát „Kopírování textu“), výběr obrazovky, trvalý přístup. Základ funguje bez přihlášení.
- Web viewer má přímo druhou roli; odkaz „Potřebuji pomoc“ vede ke stažení aplikace, protože viewer nesdílí lokální plochu. Web není plnohodnotná náhrada desktopového sdílení.
- Web nabídky: „Potřebujete pomoc s počítačem?“ → Stáhnout DeskShare; „Pomáháte někomu?“ → Připojit v prohlížeči. Technologie a benchmarky až níže.
- Mobil stejné role; iPhone v aktuálním `mobile/lib/app.dart` neumí sdílení a neumožňuje ovládání: vysvětlení místo falešného aktivního tlačítka. Android žádá systémový souhlas se záznamem obrazovky; ovládání navíc vyžaduje přístupnost. Nezaměňovat tyto souhlasy.

## 2. Slovník pro všechny platformy

| Dnešní výraz | EN | CS |
|---|---|---|
| Server Mode / screen sharing server | I need help | Potřebuji pomoc |
| Client Mode / remote desktop client | Help someone | Chci pomoct někomu |
| Server ID (vlastní) | Your number | Vaše číslo |
| Server ID (vstup) | Their number | Číslo druhého člověka |
| Passkey / Connection Passkey | Code | Kód |
| Start Server | Share my screen | Sdílet mou obrazovku |
| Starting… | Preparing to share… | Připravujeme sdílení… |
| Stop Server | Stop sharing | Zastavit sdílení |
| Connect to Server | Connect | Připojit se |
| Disconnect (pomáhající) | Leave | Odejít |
| Server Status / Running | Ready to connect | Připraveno k připojení |
| Stopped | Your screen is not being shared | Vaše obrazovka se nesdílí |
| Active Connections | People connected | Připojení lidé |
| Uptime | Sharing duration | Doba sdílení |
| Enable control | Ask to control | Požádat o ovládání |
| Deny / Allow | Do not allow / Allow control | Nepovolit / Povolit ovládání |
| Disable control (host) | Stop control | Zastavit ovládání |
| Trust connected client permanently | Allow future access without a code | Povolit příští přístup bez kódu |
| Permanent password | Password for access without you present | Heslo pro přístup bez vaší přítomnosti |
| Allow clipboard sharing | Allow copying text between devices | Povolit kopírování textu mezi zařízeními |
| Allow receiving files | Allow receiving files | Povolit příjem souborů |
| Saved Connections | Saved devices | Uložená zařízení |
| Manual Connection | Connect with a number and code | Připojení pomocí čísla a kódu |
| WebRTC state / RTT / bitrate / route | Connection details | Podrobnosti připojení |
| Fullscreen / Hide panels | Full screen / Hide tools | Celá obrazovka / Skrýt nástroje |

„Sdílíte obrazovku“ až při skutečném přenosu; běžící služba bez připojeného člověka = „Připraveno k připojení“. Doba sdílení a počet jsou v podrobnostech, ne dominantní informace.

## 3. Tok člověka, který potřebuje pomoc

1. **Potřebuji pomoc** → vysvětlení „Druhý člověk uvidí obsah vaší obrazovky. Zavřete soukromá okna.“ Varování: „Zavolal vám někdo nečekaně? Nic nesdílejte. Zavěste a zavolejte známému člověku nebo firmě na ověřené číslo. Nikomu neukazujte bankovnictví ani nesdělujte hesla.“ Tlačítka **Zpět** / **Sdílet mou obrazovku**. Bez předvoleného „příště nezobrazovat“.
2. Příprava → **Připraveno k připojení**. Nadiktovat **Vaše číslo 609 374 915**, potom **Kód VY8–5L5–6RW**. Samostatné Kopírovat; čísla číst jednotlivě v trojicích, kód po znacích. Velká písmena, dobře rozlišitelné znaky fontu, LTR i v arabštině. „VY8: vé, ypsilon, osm“ je český příklad pomůcky; lokální hláskování ověřit s mluvčími, nevymýšlet univerzální abecedu.
3. **Dnešní limit 45 s:** „Nový kód za 32 s. Když se změní, přečtěte nový. Vaše číslo zůstává stejné.“ Žádný červený blikající odpočet. Při rotaci nahradit celý kód atomicky a oznámit „Kód se změnil“. Nekopírovat starý kód. Při chybě pomáhající vidí „Kód už neplatí. Požádejte o nový kód.“ Zachovat vyplněné číslo, nesmazat formulář. Neslibovat překryv platnosti ani pozastavení rotace. Změna kódu sama neukončuje už navázané spojení; ověřit integračním scénářem.
4. Po připojení **Sdílíte obrazovku** a „Druhý člověk vidí vaši obrazovku. Ovládání není povoleno.“ V čekání psát „Zatím není nikdo připojený“. V návrhu povolit možnost žádat o ovládání, ne ovládání samotné; dnešní checkbox je ve výchozím stavu vypnutý, tedy jde i o změnu konfigurace.
5. Žádost: **Povolit ovládání?** „Druhý člověk chce používat vaši myš a klávesnici. Povolte to jen člověku, kterého znáte a od kterého čekáte pomoc.“ **Nepovolit** / **Povolit ovládání**. Ukázat identifikátor spojení, jméno jen pokud je dostupné, označit jako neověřené. Nepředstírat znalost identity. Bezpečné tlačítko má výchozí fokus; Escape/zavření/odchod protistrany/30s timeout = odmítnutí (timeout již v desktopu existuje). Fokus uzavřít do dialogu a poté vrátit.
6. Po schválení „Ovládání je povoleno“ + **Zastavit ovládání**; tato akce zachová obraz. Stálé **Zastavit sdílení** okamžitě ukončí všechna spojení tohoto sdílení, odebere ovládání a zastaví nabízení dalších připojení. Bez potvrzovací překážky. Potvrzení výsledku: „Sdílení skončilo. Vaše obrazovka se už nesdílí.“

**Později:** bezpečnostně posoudit delší, uživatelem obnovovaný kód (např. několik minut), případně pozvánku. Nezkracovat na 6 číslic pouhou změnou UI: mění se entropie, autentizace a ochrana proti hádání. Explicitní schválení prvního zobrazení obrazovky je další doporučená změna protokolu/toku; dnes kód opravňuje k zobrazení a samostatně se schvaluje ovládání. Makety zobrazují tento dnešní model.

## 4. Tok pomáhajícího

1. **Chci pomoct někomu** (desktop/mobil), nebo otevřít web viewer.
2. Dvě pole: **Číslo druhého člověka** (příklad `123 456 789`), **Kód** (příklad `ABC–234–XYZ`). Popisek zůstává viditelný; příklad není náhradou popisku. Přijímat mezery/pomlčky a malá písmena dle současné normalizace. Jen číslo má numerickou klávesnici; kód je alfanumerický, bez autocorrect. Heslo pro trvalý přístup je oddělená volba v Rozšířené, ne třetí matoucí pole.
3. **Připojit se** → „Připojujeme vás…“, poté obraz. Chyby: „Číslo jsme nenašli. Zkontrolujte ho s druhým člověkem.“ / „Připojení se nezdařilo. Zkontrolujte internet a zkuste to znovu.“ Bez automatického opakování starého kódu.
4. Výchozí prohlížení, **Požádat o ovládání**, „Čekáme na povolení“. Neposílat vstupy před potvrzením. Zamítnutí: „Ovládání nebylo povoleno. Obrazovku můžete dál sledovat.“
5. **Odejít** ukončí vlastní spojení; host může stále sdílet s dalšími. Host při posledním odchodu vidí „Pomáhající se odpojil. Chcete dál čekat?“ a hlavní Zastavit sdílení.

Trvalý přístup: Rozšířené → vysvětlení „Toto zařízení se příště připojí bez nového kódu. Zapněte jen pro vlastní nebo spravované zařízení.“ Samostatné potvrzení, přehled povolených zařízení a odebrání. Uložení čísla do historie nesmí samo povolit přístup. Výchozí stav vypnuto.

## 5. Jazyky: přesné rozhodovací pravidlo KELIQO

Podpora přesně **cs sk en de pl uk fr es it pt ru tr ar zh ja ko id**. Pořadí tabulky CSV je záměrně `key,en,cs,sk,de,pl,uk,fr,es,it,pt,ru,tr,ar,zh,ja,ko,id` dle zadání. KELIQO zde přebíráme podle pravidla vlastníka; jeho zdrojový kód nebyl vstupem výzkumu.

Společně: platná explicitní volba > systém/prohlížeč > en. Na webu platné `?lang=` > uložená volba > automatika. Tag normalizovat (`de-DE`→`de`, `pt-BR`→`pt`, `zh-Hant`→`zh`); ignorovat neplatnou hodnotu a pokračovat dalším zdrojem. Z preferovaného seznamu zvolit první podporovaný jazyk; bez shody en. Čínština má zatím jednu zjednodušenou verzi, ne lokalizaci tradičního písma. Přepnutí „Automaticky“ smaže override. Ruční změnu uložit a odstranit/přepsat starý parametr v URL, aby nevyhrával dál. Detekce jazyka nikdy neodvozuje polohu z IP.

| Platforma | Detekce a uložení | Ruční přepnutí |
|---|---|---|
| Desktop | Uložený language override, jinak OS UI culture (`CurrentUICulture`, nikoli číselná regionální kultura); nastavit při startu. | Pravý horní roh všech obrazovek; uložit do uživatelského nastavení. Změna bez přerušení relace. |
| Web viewer | `URLSearchParams` → `localStorage['deskshare.lang']` → `navigator.languages` (fallback `navigator.language`) → en. `localStorage` může selhat; catch a pokračovat v paměti. | Hlavička před připojením i v nástrojích při připojení; aktualizovat `html.lang` a `dir`. |
| Site | Explicitní `?lang`, jazyková cesta `/xx/` a uložená volba mají přednost; přímý `/de/` respektovat jako explicitní odkaz. Na neutrálním `/` server vyhodnotí uloženou cookie → `Accept-Language` dle q → en. Statický hosting bez těchto možností použije JS: localStorage → navigator → en a `location.replace('/xx/'+suffix)`. | Hlavička i patička, skutečné odkazy. Při změně uložit cookie/localStorage. Server localStorage nevidí. Žádné opakované přesměrování jazykových cest. |
| Flutter | Uložený override → seřazené `platformDispatcher.locales` → en, změny zařízení sledovat jen v automatice. | Hlavička úvodu a Nastavení; uložit do SharedPreferences. |

Site: dnes je český obsah na `/` a anglický `/en/`. Navržená migrace `/` na neutrální rozcestník, čeština `/cs/`; zachovat staré odkazy včetně privacy, aktualizovat canonical/hreflang/x-default, používat dočasné jazykové redirecty (ne permanentní podle prohlížeče). Bez JS ukázat anglický rozcestník se všemi jazyky. Serverové redirecty cacheovat bezpečně s odpovídajícím Vary nebo bez veřejné cache. Jazyk vieweru na jiné subdoméně předat přes `?lang`, nečekat společný localStorage.

RTL: pouze ar → pravolevé rozložení; logické start/end a inline-start/end, pořadí podle směru čtení. Čísla zařízení/kódy vždy LTR a izolované (`bdi dir=ltr`, Avalonia samostatný LTR prvek, Flutter Directionality). Zachovat pořadí znaků i při kopírování, do schránky nepřidávat bidi řídicí znaky. Nepřeklápět značku, čísla ani obsah sdílené obrazovky. Texty neřetězit, používat celé věty s pojmenovanými parametry. Čas řešit lokálním formátovačem, countdown bez nutnosti pluralizace.

Příklady akceptace: `?lang=sk` + uložené de + prohlížeč cs → sk; uložené ar + OS en → ar/RTL; automatika `[sv-SE,de-AT]` → de; pouze sv → en; `pt-BR`→pt; přímé `/fr/` z německého prohlížeče zůstane fr; zakázaný storage neblokuje start.

## 6. Implementace a odhad

Odhady v člověkodnech pro jednoho vývojáře znalého projektu, bez uživatelského testování a čekání na rodilé překladatele. Celkem **14–22 dní** s rezervou na rozdíly OS; nejde o hotovou implementaci.

| Oblast | Doporučení | Odhad |
|---|---|---|
| Avalonia | RESX: výchozí EN + 16 satelitních sad, notifikující lokalizační služba a binding helper pro živé přepnutí. Zahrnout `.axaml.cs`, SafetyPromptDialog, Dialogs, Controls, Windows, menu i chyby. `FlowDirection`, platformní oprávnění a dialogy zkontrolovat na 3 OS. | 4–6 dní |
| Web viewer | JSON slovníky podle klíčů CSV; `t(key, params)` s EN fallbackem, `textContent` místo HTML interpolace. Pokrýt HTML, client.js, statusy a přístupné názvy. Locale modul před prvním vykreslením. | 2–3 dny |
| Flutter | `flutter gen-l10n`, EN template ARB + 16 ARB; metadata parametrů, supportedLocales, localeResolutionCallback, delegates a Directionality. Přeložit sender/viewer/settings včetně chyb; systémové dialogy respektují OS. | 2–3 dny |
| Site | Statické `/xx/` ze společné šablony/slovníků; žádných 17 ručně rozcházejících se kopií. Přeložit navigaci, stažení, metadata, bezpečnost a privacy s právní revizí samostatně. | 2–3 dny |
| Sdílené texty a QA | Import CSV → platformní formáty, kontrola úplnosti klíčů/parametrů, fallbacků, RTL, delších textů, čteček, zoomu a oprávnění. | 4–7 dní |

**Dnes večer (4–6 h):** CS/EN přejmenování hlavních voleb, oprava „Starting…“, výrazné číslo/kód, trvalé zastavení, skrytí diagnostiky a rizikových voleb, jasnější existující bezpečnostní texty. Začít lokalizační službou pro jádro desktop/web a detekcí. Před vydáním ověřit dva reálné počítače. Neslibovat kompletní čtyři platformy a 17 jazyků za večer.

**Další iterace:** plná extrakce textů, 17 jazyků na všech plochách, ověření mobilních oprávnění, audit trvalého přístupu, prodloužení kódu a samostatný souhlas se zobrazením po změně toku. CSV je překladový návrh, nikoli schválený jazykový balík: **všechny jazyky jsou vytvořené AI a potřebují rodilou revizi**, včetně přirozenosti CS/SK; zvláště bezpečnostní texty a AR/RTL. DE/AR makety nejsou důkazem produkční připravenosti.

## 7. Malé opravy při implementaci

- `MainWindow.axaml.cs / StartServer_Click`: po úspěchu zůstává disabled „Starting…“; nahradit dvě start/stop tlačítka jedním stavovým ovládáním a zastavením, při chybě vrátit přípravu.
- Dvě různá hlášení expirace (pevné „expires in 45 seconds“ a živý odpočet) sjednotit podle skutečného ValidTo.
- Běžící desktop se nevejde do 900px snímku; základní údaje a Stop mají být vidět bez scrollování. Rozšířené níže.
- Nezobrazovat disabled prázdné akce Disconnect/Control/Ctrl+Alt+Del na nepřipojeném webu; statistiky až po připojení v podrobnostech.
- Sjednotit šířku desktopových rolí; nezačínat prázdnou obří kartou uložených připojení.
- Dvě heslová pole v klientu rozdělit na základní kód a oddělený rozšířený přístup; nepřerušit stávající autentizační větve.
- Zakázaná nastavení vysvětlit „Změníte po zastavení sdílení“, nezanechat nečitelný šedý text.
- Zpětná vazba Kopírovat → Zkopírováno; při odmítnutí schránky nabídnout ruční označení.
- Oprávnění popsat podle skutečného stavu, ne jako „ovládání dostupné“ před schválením. Při odpojení resetovat oprávnění.
- Sjednotit DeskShare místo RemoteDesktop WebClient; odstranit rozbitý symbol v titulku.
- Otestovat číslo po kolizi registrace (může se změnit), zavření host okna, výpadek sítě a zrušení systémového sdílení. Nepravdivé „už se nesdílí“ je blokující chyba.

## 8. Kritéria před vydáním

Klávesnice projde všechny akce, fokus je vidět, kontrast textů alespoň 4.5:1; modrá #0b72d9 s bílou je přibližně 4.8:1. 200% zoom/velká systémová písma nesmí schovat zastavení. V mobilu cíle alespoň 48 dp. Screen reader nečte countdown každou sekundu: samostatný oznamovaný stav jen při obnově kódu. Ověřit CS/SK skloňování, DE dlouhé popisky a AR fokus/kopírování. CSV kontrolovat na 18 sloupců, jedinečné klíče, úplnost všech 17 jazyků a shodu parametrů. Makety ukazují zamýšlenou hierarchii, nejsou funkční aplikací ani důkazem těchto testů.
