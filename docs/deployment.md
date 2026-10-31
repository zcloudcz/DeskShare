# DeskShare bulk deployment (MSI)

For IT admins: `DeskShare-x64.msi` installs DeskShare **machine-wide** (`%ProgramFiles%\DeskShare\App`), silently,
with preconfigured settings and optionally with a service for unattended access. You can find it in every release
at https://github.com/zcloudcz/DeskShare/releases/latest/download/DeskShare-x64.msi.

> The MSI is **not signed** yet. SmartScreen and some Intune/AppLocker rules expect a signed package, so before
> rolling it out widely, sign it with your own certificate (`signtool sign` on the MSI, ideally on `DeskShare*.exe` too).

The per-user installer (`DeskShare-win-Setup.exe`, Velopack) stays unchanged and updates itself. The MSI **does not update
itself**: roll out a new version with a new MSI (see [Updates](#updates)).

## MSI properties

| Property | Default | Meaning |
|---|---|---|
| `SIGNALING_URL` | `wss://app.deskshare.zcloud.cz/signal` | Signaling server address (must be `ws://` or `wss://`). |
| `ENABLE_UNATTENDED` | `0` | `1` = installs the `DeskShareService` service (unattended access, runs as SYSTEM). |
| `ALLOW_REMOTE_CONTROL` | `0` | Keyboard and mouse control. |
| `ALLOW_CLIPBOARD` | `0` | Clipboard sharing. |
| `ALLOW_FILES` | `0` | Receiving files from the viewer. |
| `ALLOW_PRIVACY_SCREEN` | `0` | Privacy screen (local monitor goes black). |
| `DISABLE_AUTOUPDATE` | `1` | `1` = the app never updates itself via Velopack. |

`ALLOW_*`: `1` = enabled and **locked**, `0` = disabled and **locked**, any other value (e.g. `user`) = not set,
the user decides. Locked options are grayed out in the app with the note “Set by your administrator”.

## Silent installation

```cmd
:: default: no service, everything locked to off, no autoupdate
msiexec /i DeskShare-x64.msi /qn /l*v C:\Windows\Temp\deskshare-install.log

:: custom signaling + service for unattended access
msiexec /i DeskShare-x64.msi /qn SIGNALING_URL=wss://signal.firma.cz/signal ENABLE_UNATTENDED=1

:: allow control, leave clipboard and files up to the user
msiexec /i DeskShare-x64.msi /qn ALLOW_REMOTE_CONTROL=1 ALLOW_CLIPBOARD=user ALLOW_FILES=user
```

Installation requires administrator rights (SYSTEM from Intune/SCCM/GPO is fine). The MSI source must be accessible
to the SYSTEM account (a UNC share with read permission for computers, not just for users).

## Updates

Install a newer MSI (same `UpgradeCode`, it replaces the old version automatically, in a single transaction: on error
the old version is restored). A version older than the installed one is rejected.

**Settings are remembered.** Property values from the last installation are stored in `HKLM\SOFTWARE\DeskShare\Msi`. An upgrade,
a repair (`msiexec /fvomus DeskShare-x64.msi`) and a redeployment without arguments therefore preserve them; a property given on the command
line always overrides the stored value.

```cmd
msiexec /i DeskShare-x64-novejsi.msi /qn
msiexec /i DeskShare-x64-novejsi.msi /qn SIGNALING_URL=wss://novy.firma.cz/signal
```

**Changing the configuration** = running the same MSI again with new properties (`msiexec /i DeskShare-x64.msi /qn SIGNALING_URL=...`).
`policy.json` and the stored registry values are overwritten on every run, not just on the first installation.

**Service (`ENABLE_UNATTENDED`).** Removing the service without uninstalling the app:
`msiexec /i DeskShare-x64.msi REMOVE=Unattended ENABLE_UNATTENDED=0 /qn` (the `Unattended` feature disappears and the MSI calls
`DeskShare.Service.exe uninstall`). During an upgrade with `ENABLE_UNATTENDED=0` the service from the previous version is not removed automatically;
run the step above on the old version first.

**Failure is safe.** If the installation fails, the previous `policy.json` is restored and the new service (which did not exist before) is removed.

## Uninstallation

```cmd
msiexec /x DeskShare-x64.msi /qn
```

Removes the app, `policy.json` and (if it was installed) the `DeskShareService` service. Keeps `%ProgramData%\DeskShare`
(server ID and secret key, same as when the service is disabled in the app), so the computer identity is preserved after reinstalling.
Without the original MSI file: `msiexec /x {ProductCode} /qn` (ProductCode is in `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall`,
the entry named DeskShare).

## Intune (Win32 app, recommended)

The “Line-of-business app” type cannot pass custom MSI properties, so use a Win32 app.

1. Package the MSI with the [Microsoft Win32 Content Prep Tool](https://learn.microsoft.com/mem/intune/apps/apps-win32-prepare):
   `IntuneWinAppUtil.exe -c <folder_with_msi> -s DeskShare-x64.msi -o <output>`.
2. Intune > Apps > Windows > Add > **Windows app (Win32)**, upload the `.intunewin`.
3. Install command:
   `msiexec /i "DeskShare-x64.msi" /qn SIGNALING_URL=wss://signal.firma.cz/signal ENABLE_UNATTENDED=1`
4. Uninstall command: `msiexec /x "DeskShare-x64.msi" /qn`
5. Install behavior: **System**. Restart behavior: No specific action.
6. Detection rule: **MSI**, Product code = the ProductCode from the MSI of the **given version** (update it after a new version), or more robustly
   a rule of type **File**: path `%ProgramFiles%\DeskShare\App`, file `DeskShare.exe`, method *File or folder exists*
   (optionally *Version* `>=` the required version, if the detection should force an upgrade).
7. Assign to a device group (Required).

## Group Policy (software installation)

The GPO “Software Installation” cannot pass custom properties, nor create a transform without an additional tool. Options:

- **MST transform**: create a transform (e.g. Orca / `wix`) that changes the `Property` table (`SIGNALING_URL`,
  `ENABLE_UNATTENDED`, `ALLOW_*`), and add it in the GPO on the *Modifications* tab. The GPO assigns the MSI to **computers**
  (Computer Configuration > Policies > Software Settings > Software installation > New > Package > *Assigned*).
- **Startup script** in the GPO (simpler, needs no transform):
  `msiexec /i "\\server\share\DeskShare-x64.msi" /qn SIGNALING_URL=wss://signal.firma.cz/signal ENABLE_UNATTENDED=1`.
  It runs as SYSTEM at computer startup; the script should first check whether it is already installed.

## SCCM / PDQ Deploy

Install: `msiexec /i DeskShare-x64.msi /qn <properties>`, uninstall: `msiexec /x DeskShare-x64.msi /qn`, run as
SYSTEM. Detection: existence of the file `%ProgramFiles%\DeskShare\App\DeskShare.exe` (SCCM) or the MSI Product Code (PDQ).

## What the MSI creates

- `%ProgramFiles%\DeskShare\App\` the app (self-contained, does not require .NET) and a Start menu shortcut for all users.
- `%ProgramFiles%\DeskShare\App\service\`: the service installation source. With `ENABLE_UNATTENDED=1` the MSI runs
  `DeskShare.Service.exe install` (same logic as the “Enable” button in the app: copy to `%ProgramFiles%\DeskShare\Service`,
  `%ProgramData%\DeskShare` with permissions only for SYSTEM and Administrators, SAS policy, start confirmation via `status.json`).
  If the service installation fails, the whole MSI fails and is rolled back. Uninstalling the MSI calls `DeskShare.Service.exe uninstall`.
- `%ProgramData%\DeskShare.Policy\policy.json`: machine policy (below).

### policy.json

The `%ProgramData%\DeskShare.Policy` folder has a DACL: SYSTEM and Administrators full access, Users read-only (the app of the logged-in
user must be able to read the policy; `%ProgramData%\DeskShare` therefore cannot be used, it is only for SYSTEM and Administrators).
The app and the service **accept the policy only when it is trusted**: the folder and file belong to SYSTEM/Administrators, the folder has a
protected (non-inherited) DACL, nobody except SYSTEM/Administrators has write permission, and it is not a junction/symlink. Otherwise the policy is
ignored and a warning is logged. The service installation also renames such a foreign folder to `DeskShare.Policy.untrusted-*`.
When editing manually, therefore, edit the file as an administrator and do not change the folder permissions.

A value that is in the file **takes precedence** over the user setting and over `appsettings.json` / `unattended.json`
(applies to the app as well as to the service/agent). A missing key = the user decides. A corrupted file is ignored (warning in the log).

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

The file can also be edited manually (as an administrator) without reinstalling; the change takes effect after the app / service restarts
(`Restart-Service DeskShareService`).

## Updates via Velopack vs. MSI

An app launched from `%ProgramFiles%\DeskShare\App` never calls Velopack (nor does it check for updates); `Updates.Disabled`
additionally turns off the check for other installations on the same computer too. Do not mix installations via MSI and via `DeskShare-win-Setup.exe` on one
computer.
