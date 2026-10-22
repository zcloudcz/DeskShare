# Hromadné nasazení DeskShare (MSI)

Pro IT správce: `DeskShare-x64.msi` instaluje DeskShare **pro celý počítač** (`%ProgramFiles%\DeskShare\App`), tiše,
s přednastavenou konfigurací a volitelně se službou pro nepřetržitý přístup (unattended). Najdete ho v každém vydání
na https://github.com/zcloudcz/DeskShare/releases/latest/download/DeskShare-x64.msi.

> MSI je zatím **nepodepsané**. SmartScreen i některá pravidla v Intune/AppLocker podepsaný balíček očekávají, před
> plošným nasazením ho podepište vlastním certifikátem (`signtool sign` na MSI, ideálně i na `DeskShare*.exe`).

Uživatelský instalátor (`DeskShare-win-Setup.exe`, Velopack) zůstává beze změny a aktualizuje se sám. MSI se **neaktualizuje
samo**: novou verzi nasadíte novým MSI (viz [Aktualizace](#aktualizace)).

## Vlastnosti MSI

| Vlastnost | Výchozí | Význam |
|---|---|---|
| `SIGNALING_URL` | `wss://app.deskshare.zcloud.cz/signal` | Adresa signaling serveru (musí být `ws://` nebo `wss://`). |
| `ENABLE_UNATTENDED` | `0` | `1` = nainstaluje službu `DeskShareService` (nepřetržitý přístup, běží jako SYSTEM). |
| `ALLOW_REMOTE_CONTROL` | `0` | Ovládání klávesnicí a myší. |
| `ALLOW_CLIPBOARD` | `0` | Sdílení schránky. |
| `ALLOW_FILES` | `0` | Příjem souborů od diváka. |
| `ALLOW_PRIVACY_SCREEN` | `0` | Privacy screen (lokální monitor černý). |
| `DISABLE_AUTOUPDATE` | `1` | `1` = aplikace se nikdy sama neaktualizuje přes Velopack. |

`ALLOW_*`: `1` = zapnuto a **zamčeno**, `0` = vypnuto a **zamčeno**, jakákoli jiná hodnota (např. `user`) = nenastaveno,
rozhoduje uživatel. Zamčené volby jsou v aplikaci šedé s poznámkou „Set by your administrator“.

## Tichá instalace

```cmd
:: výchozí: bez služby, vše zamčeno na vypnuto, bez autoupdate
msiexec /i DeskShare-x64.msi /qn /l*v C:\Windows\Temp\deskshare-install.log

:: vlastní signaling + služba pro nepřetržitý přístup
msiexec /i DeskShare-x64.msi /qn SIGNALING_URL=wss://signal.firma.cz/signal ENABLE_UNATTENDED=1

:: ovládání povolit, schránku a soubory nechat na uživateli
msiexec /i DeskShare-x64.msi /qn ALLOW_REMOTE_CONTROL=1 ALLOW_CLIPBOARD=user ALLOW_FILES=user
```

Instalace vyžaduje administrátorská práva (SYSTEM z Intune/SCCM/GPO je v pořádku). Zdroj MSI musí být dostupný
SYSTEM účtu (UNC share s právem čtení pro počítače, ne jen pro uživatele).

## Aktualizace

Nainstalujte novější MSI (stejný `UpgradeCode`, starou verzi nahradí automaticky, a to v jedné transakci: při chybě
se vrátí stará verze). Starší verze než nainstalovaná se odmítne.

**Nastavení se pamatují.** Hodnoty vlastností z poslední instalace jsou uložené v `HKLM\SOFTWARE\DeskShare\Msi`. Upgrade,
oprava (`msiexec /fvomus DeskShare-x64.msi`) i znovunasazení bez argumentů je proto zachovají; vlastnost zadaná na příkazové
řádce vždy přepíše uloženou hodnotu.

```cmd
msiexec /i DeskShare-x64-novejsi.msi /qn
msiexec /i DeskShare-x64-novejsi.msi /qn SIGNALING_URL=wss://novy.firma.cz/signal
```

**Změna konfigurace** = spuštění téhož MSI znovu s novými vlastnostmi (`msiexec /i DeskShare-x64.msi /qn SIGNALING_URL=...`).
`policy.json` i uložené hodnoty v registru se přepíšou při každém běhu, ne jen při první instalaci.

**Služba (`ENABLE_UNATTENDED`).** Odebrání služby bez odinstalace aplikace:
`msiexec /i DeskShare-x64.msi REMOVE=Unattended ENABLE_UNATTENDED=0 /qn` (feature `Unattended` zmizí a MSI zavolá
`DeskShare.Service.exe uninstall`). Při upgradu s `ENABLE_UNATTENDED=0` se služba z předchozí verze automaticky neodstraní;
nejdřív proveďte krok výše na staré verzi.

**Selhání je bezpečné.** Při chybě instalace se vrátí předchozí `policy.json` a nová služba (která dřív neexistovala) se odstraní.

## Odinstalace

```cmd
msiexec /x DeskShare-x64.msi /qn
```

Odstraní aplikaci, `policy.json` a (pokud byla nainstalována) službu `DeskShareService`. Ponechá `%ProgramData%\DeskShare`
(ID serveru a tajný klíč, stejně jako při vypnutí služby v aplikaci), aby se po reinstalaci zachovala identita počítače.
Bez původního souboru MSI: `msiexec /x {ProductCode} /qn` (ProductCode je v `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall`,
položka s názvem DeskShare).

## Intune (Win32 app, doporučeno)

Typ „Line-of-business app“ neumí předat vlastní vlastnosti MSI, proto použijte Win32 app.

1. Zabalte MSI nástrojem [Microsoft Win32 Content Prep Tool](https://learn.microsoft.com/mem/intune/apps/apps-win32-prepare):
   `IntuneWinAppUtil.exe -c <složka_s_msi> -s DeskShare-x64.msi -o <výstup>`.
2. Intune > Apps > Windows > Add > **Windows app (Win32)**, nahrajte `.intunewin`.
3. Install command:
   `msiexec /i "DeskShare-x64.msi" /qn SIGNALING_URL=wss://signal.firma.cz/signal ENABLE_UNATTENDED=1`
4. Uninstall command: `msiexec /x "DeskShare-x64.msi" /qn`
5. Install behavior: **System**. Restart behavior: No specific action.
6. Detection rule: **MSI**, Product code = ProductCode z MSI **dané verze** (po nové verzi aktualizujte), nebo robustněji
   rule typu **File**: cesta `%ProgramFiles%\DeskShare\App`, soubor `DeskShare.exe`, metoda *File or folder exists*
   (případně *Version* `>=` požadovaná verze, pokud má detekce vynutit upgrade).
7. Přiřaďte skupině zařízení (Required).

## Group Policy (software installation)

GPO „Software Installation“ neumí předat vlastní vlastnosti ani transformaci vytvořit bez dalšího nástroje. Možnosti:

- **MST transform**: vytvořte transform (např. Orca / `wix`), který změní tabulku `Property` (`SIGNALING_URL`,
  `ENABLE_UNATTENDED`, `ALLOW_*`), a v GPO ho přidejte na záložce *Modifications*. GPO přiřazuje MSI **počítačům**
  (Computer Configuration > Policies > Software Settings > Software installation > New > Package > *Assigned*).
- **Startup script** v GPO (jednodušší, nepotřebuje transform):
  `msiexec /i "\\server\share\DeskShare-x64.msi" /qn SIGNALING_URL=wss://signal.firma.cz/signal ENABLE_UNATTENDED=1`.
  Spouští se jako SYSTEM při startu počítače; skript by měl nejdřív zkontrolovat, zda už není nainstalováno.

## SCCM / PDQ Deploy

Install: `msiexec /i DeskShare-x64.msi /qn <vlastnosti>`, uninstall: `msiexec /x DeskShare-x64.msi /qn`, spustit jako
SYSTEM. Detekce: existence souboru `%ProgramFiles%\DeskShare\App\DeskShare.exe` (SCCM) nebo MSI Product Code (PDQ).

## Co MSI vytvoří

- `%ProgramFiles%\DeskShare\App\` aplikace (self-contained, nevyžaduje .NET) a zástupce v nabídce Start pro všechny uživatele.
- `%ProgramFiles%\DeskShare\App\service\`: instalační zdroj služby. Při `ENABLE_UNATTENDED=1` MSI spustí
  `DeskShare.Service.exe install` (stejná logika jako tlačítko „Enable“ v aplikaci: kopie do `%ProgramFiles%\DeskShare\Service`,
  `%ProgramData%\DeskShare` s právy jen pro SYSTEM a Administrators, SAS politika, potvrzení startu přes `status.json`).
  Pokud instalace služby selže, selže celé MSI a vrátí se zpět. Odinstalace MSI zavolá `DeskShare.Service.exe uninstall`.
- `%ProgramData%\DeskShare.Policy\policy.json`: politika počítače (níže).

### policy.json

Složka `%ProgramData%\DeskShare.Policy` má DACL: SYSTEM a Administrators plný přístup, Users jen čtení (aplikace běžícího
uživatele musí politiku přečíst; `%ProgramData%\DeskShare` tak použít nejde, je jen pro SYSTEM a Administrators).
Aplikace i služba politiku **přijmou jen když je důvěryhodná**: složka a soubor patří SYSTEM/Administrators, složka má
chráněné (nedědičné) DACL, nikdo kromě SYSTEM/Administrators nemá právo zápisu a nejde o junction/symlink. Jinak se politika
ignoruje a do logu přijde varování. Instalace služby navíc takovou cizí složku přejmenuje na `DeskShare.Policy.untrusted-*`.
Při ruční úpravě tedy soubor upravujte jako administrátor a neměňte práva složky.

Hodnota, která je v souboru, **má přednost** před nastavením uživatele i před `appsettings.json` / `unattended.json`
(platí pro aplikaci i pro službu/agenta). Chybějící klíč = rozhoduje uživatel. Poškozený soubor se ignoruje (varování v logu).

```json
{
  "Signaling": { "ServerUrl": "wss://signal.firma.cz/signal" },
  "Security": {
    "EnableRemoteControl": false,
    "EnableClipboard": false,
    "EnableFileReceive": false,
    "EnablePrivacyScreen": false
  },
  "Updates": { "Disabled": true }
}
```

Soubor lze upravit i ručně (jako administrátor) bez přeinstalace; změna se projeví po restartu aplikace / služby
(`Restart-Service DeskShareService`).

## Aktualizace přes Velopack vs. MSI

Aplikace spuštěná z `%ProgramFiles%\DeskShare\App` nikdy nevolá Velopack (ani neprovádí kontrolu aktualizací); `Updates.Disabled`
navíc vypne kontrolu i u jiných instalací na stejném počítači. Instalace přes MSI a přes `DeskShare-win-Setup.exe` na jednom
počítači nemíchejte.
