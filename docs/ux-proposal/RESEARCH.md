# DeskShare: výzkum srozumitelné vzdálené pomoci

Stav průzkumu: 13. 11. 2026. Desk research oficiální dokumentace a audit dodaných obrazovek/kódu; nejde o uživatelské testování. Počty níže jsou naše počty hlavních kroků po otevření aplikace, bez instalace a přihlášení. Produkty mají více verzí a odlišné mobilní možnosti. Krátké anglické názvy jsou doslovné; ostatní text je shrnutí.

## Srovnání

| Nástroj / zdroj | Role, kód a přibližné kroky | Souhlas, ukončení, poučení pro DeskShare |
|---|---|---|
| [Windows Quick Assist](https://support.microsoft.com/en-us/windows/apps/solve-pc-problems-remotely-using-quick-assist) | „Help someone“, „Code from assistant“. Pomáhající vytvoří a sdělí 6 číslic. Příjemce zadá kód → Submit → Allow (3 kroky), případně čtvrtý souhlas s ovládáním. | Sdílení a ovládání mají oddělený souhlas. „Request control“, „Cancel control“, „Leave“. Převzít role, nikoli obrátit současný směr předávání kódů DeskShare. |
| [TeamViewer QuickSupport](https://www.teamviewer.com/en/solutions/use-cases/quicksupport/) | Samostatná aplikace pro příjem pomoci; dokumentuje sdílení vlastního ID nebo připojení pomocí dodaného session code. Po otevření zhruba 2–3 hlavní úkony podle varianty. | Neplést starší ID/password tok s novým session-code tokem. Malá aplikace s jediným účelem snižuje rozhodování. Tento zdroj nepotvrzuje jednotnou délku/životnost kódu ani univerzální text souhlasu; nepřebírat neověřená čísla. |
| [AnyDesk: accepting the connection](https://support.anydesk.com/docs/accepting-the-connection) | Žádost zobrazuje jméno, ID/alias protistrany. Příjemce zkontroluje oprávnění → Accept (2 kroky v této fázi). Zdroj nepopisuje celý instalační tok. | Oprávnění jsou vidět před přijetím. Jméno je nastavitelné uživatelem, proto samo neprokazuje totožnost. Profily oprávnění jsou pro náš základní tok příliš složité. |
| [Chrome Remote Desktop](https://support.google.com/chrome/answer/1649523?hl=en) | Remote Support: „Get Support“ / „Give Support“. Příjemce Generate Code → sdílí kód → zkontroluje e-mail pomáhajícího → Share (4 úkony). Pomáhající zadá kód → Connect (2). | Kód je jednorázový; potvrzení pokračování sdílení každých 30 minut. Stop Sharing zůstává dostupné. Zdroj nespecifikuje diktování po skupinách, proto to je náš návrh, nikoli citace. |
| [RustDesk Client](https://rustdesk.com/docs/en/client/), [nastavení souhlasu](https://rustdesk.com/docs/en/self-host/client-configuration/advanced-settings/) | Vedle sebe vlastní ID, One Time Password a vstup pro ID druhého počítače. Pomáhající zadá ID → připojí → ověří heslem/souhlasem (zhruba 3 úkony dle nastavení). | Existuje password, click a password-click režim. Inspirace: výrazné vlastní číslo. Nepřebírat technický Network/server obsah na úvod. |
| [Zoho Assist: join](https://www.zoho.com/assist/help/remote-support/join-session.html), [živá připojovací stránka](https://assist.zoho.com/login/join.jsp) | Pozvánka nebo session ID od technika. Browser: otevřít stránku → zadat jméno/ID a Join → Agree and Download → spustit soubor (4 kroky). | Na připojovací stránce varování před cizími lidmi a sdílením hesel/bankovních údajů. Převzít konkrétnost varování a alternativu odkazu; instalaci jasně odlišit od připojení. |
| [Apple FaceTime: remote control](https://support.apple.com/en-ie/guide/facetime/fctmebd8481a/mac) | Pomoc uvnitř hovoru jeden na jednoho, bez diktování ID. Z již sdílené obrazovky přibližně žádost → Allow (2 úkony). | Oddělené povolení ovládání; některé citlivé operace jsou omezené. Výsledná stránka během průzkumu vracela 429, dostupný byl indexovaný oficiální výňatek; přesné tlačítko Stop a kroky ověřit před implementací. Omezení Apple neslibovat v DeskShare. |

### Telefon, podvody a kódy

[Microsoft Security: zneužití Quick Assist](https://www.microsoft.com/en-us/security/blog/2024/05/15/threat-actors-misusing-quick-assist-in-social-engineering-attacks-leading-to-ransomware/) popisuje podvodné hovory vydávající se za podporu. Doporučuje spojení jen při vlastním vyžádání pomoci. Z toho plyne varování před nečekaným volajícím a ověření známým číslem; úspěšné spojení není ověření identity.

Čistě číselný šestimístný kód Quick Assist je jednodušší diktovat než devět smíšených znaků DeskShare. To je hypotéza k ověření, ne výsledek měření. Žádný z použitých zdrojů nedokládá, že právě 45 s je vhodná doba pro seniory. Doporučujeme zachovat nynější autentizaci, členit číslo i kód 3–3–3 a nabídnout kopírování; změnu abecedy/platnosti řešit samostatně se zachováním bezpečnosti.

## 10 principů pro návrh

1. Pojmenovat úmysl člověka: potřebuji pomoc / chci pomoct někomu.
2. Jeden hlavní další krok na obrazovku, dvě role na úvodu.
3. Základní pomoc bez účtu; účet, historii a konfiguraci odsunout.
4. Vlastní číslo a kód oddělit, zvětšit, seskupit a dát každému popisek i Kopírovat.
5. Obnova kódu není chyba uživatele; napsat, co udělat dál, bez alarmu.
6. Vidět obrazovku a ovládat zařízení jsou odlišná oprávnění.
7. Zamítnutí musí být stejně dostupné jako povolení; zavření znamená zamítnutí.
8. Trvalý přístup je samostatné vědomé nastavení, nikdy předvolená součást pomoci.
9. Ukončení sdílení je stále viditelné, okamžité a popsáno důsledkem.
10. Překlady, RTL, klávesnice a velké cíle navrhovat současně s českou obrazovkou.

## Ověření s lidmi před vydáním

6–8 účastníků včetně seniorů, rodičů a klientů malé firmy; ve dvojici po telefonu, bez vysvětlování pojmů. Úkol: vyžádat pomoc, nadiktovat údaje, odmítnout a pak povolit ovládání, ukončit pomoc; zopakovat při obnově kódu. Zaznamenat chyby znaků, čas do prvního obrazu a to, zda člověk chápe oprávnění. Cíl: alespoň 80 % bez zásahu moderátora, všichni najdou zastavení do 5 s a rozliší obraz/ovládání. Pro lokalizace přidat rodilé mluvčí de/ar a kontrolu RTL. Cíle jsou navržená kritéria, nikoli hotové výsledky.
