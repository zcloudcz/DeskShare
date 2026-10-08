# Go-live checklist (deskshare.zcloud.cz)

One-time steps to take DeskShare from this repo to production. Run from the repo root.
Everything after step 1 is idempotent and safe to re-run.

## 1. Purge FFmpeg DLLs from git history (before the first push)

The old `src/DeskShare.Desktop/ffmpeg/*.dll` (143 MB) were tracked. Rewrite history once:

```powershell
pip install git-filter-repo
git filter-repo --invert-paths --path src/DeskShare.Desktop/ffmpeg --force
```

## 2. Public GitHub repo

```powershell
# repo already exists (created 2026-10-08, empty)
git remote add origin https://github.com/zcloudcz/DeskShare.git
git push -u origin main
gh api -X POST repos/zcloudcz/DeskShare/pages -f build_type=workflow
```

`build-test.yml` runs on push; `pages.yml` publishes `site/` to GitHub Pages.

## 3. Azure App Service for the signaling server (done 2026-10-08)

Web app `deskshare-signaling` in the existing Linux B1 plan `agentwall-plan` (rg-agentwall).
CI deploys with GitHub OIDC: Entra app `deskshare-github-deploy` has a federated credential for
`repo:zcloudcz/DeskShare:ref:refs/heads/main` and the Website Contributor role on the web app;
repo secrets `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`. SCM basic auth is
disabled (default), so publish profiles do not work and are not needed.

Manual deploy without CI:

```powershell
dotnet publish src/SignalingServer/DeskShare.SignalingServer.csproj -c Release -o publish
Copy-Item src/WebClient publish/WebClient -Recurse
Compress-Archive publish/* sig.zip -Force
az webapp deploy -g rg-agentwall -n deskshare-signaling --src-path sig.zip --type zip --clean true --restart true
```

Check: `https://deskshare-signaling.azurewebsites.net/health` → 200.

## 4. DNS (Wedos, zone zcloud.cz)

| Name | Type | Value |
|---|---|---|
| `deskshare` | CNAME | `zcloudcz.github.io` |
| `app.deskshare` | CNAME | `deskshare-signaling.azurewebsites.net` |
| `asuid.app.deskshare` | TXT | output of `az webapp show -g rg-agentwall -n deskshare-signaling --query customDomainVerificationId -o tsv` |

## 5. Custom domains + TLS

```powershell
gh api -X PUT repos/zcloudcz/DeskShare/pages -f cname=deskshare.zcloud.cz -F https_enforced=true
az webapp config hostname add -g rg-agentwall --webapp-name deskshare-signaling --hostname app.deskshare.zcloud.cz
az webapp config ssl create -g rg-agentwall -n deskshare-signaling --hostname app.deskshare.zcloud.cz
```

## 6. First release

```powershell
git tag v1.0.0
git push origin v1.0.0
```

`release.yml` builds the WPF app, packs it with Velopack and publishes
`DeskShare-win-Setup.exe`, `DeskShare-win-Portable.zip` and `releases.win.json` to the GitHub
Release. The landing page links to `releases/latest/download/...`, so nothing else to update.

## Known gaps after go-live

- No TURN server (App Service has no UDP): peers behind symmetric NAT cannot connect.
  Fix later with coturn on a small VM or Azure Container Instance and `Turn:*` / `IceServers` config.
- Installer is not code-signed → SmartScreen warning (the landing page explains it).
- `/register` is unauthenticated (anyone can overwrite a ServerId registration) — see MEMORY.md audit list.

## 7. macOS signing + notarization (Apple Developer account)

`release.yml` builds the Avalonia viewer for Apple Silicon on a macOS runner. Without secrets the build is
unsigned (Gatekeeper: right-click → Open, or `xattr -dr com.apple.quarantine DeskShare.app`). To sign and
notarize, set these repo secrets once:

| Secret | Value |
|---|---|
| `APPLE_CERT_P12_BASE64` | `base64 -i certs.p12` of a .p12 exported from Keychain Access that contains **both** "Developer ID Application" and "Developer ID Installer" certificates (with private keys). Create them at developer.apple.com → Certificates. |
| `APPLE_CERT_PASSWORD` | password chosen when exporting the .p12 |
| `APPLE_ID` | Apple ID e-mail of the developer account |
| `APPLE_TEAM_ID` | 10-character Team ID (developer.apple.com → Membership) |
| `APPLE_APP_PASSWORD` | app-specific password generated at appleid.apple.com (Sign-In and Security → App-Specific Passwords) |

```powershell
gh secret set APPLE_CERT_P12_BASE64 -R zcloudcz/DeskShare < certs.p12.b64
gh secret set APPLE_CERT_PASSWORD -R zcloudcz/DeskShare
gh secret set APPLE_ID -R zcloudcz/DeskShare
gh secret set APPLE_TEAM_ID -R zcloudcz/DeskShare
gh secret set APPLE_APP_PASSWORD -R zcloudcz/DeskShare
```

The workflow imports the certificate into a temporary keychain, derives the identities with
`security find-identity`, stores notarytool credentials and passes them to `vpk pack`
(`--signAppIdentity`, `--signInstallIdentity`, `--notaryProfile`). Without the Installer certificate only the
portable `.app` zip is produced (`--noInst`).

Release assets: `DeskShare-osx-Portable.zip` (+ `DeskShare-osx-Setup.pkg` once the Installer certificate is
present), `DeskShare.AppImage`, `releases.osx.json`, `releases.linux.json`. Linux (AppImage, x64) needs nothing extra. Both non-Windows builds are viewer-only betas built without a
test machine; `MainWindow` disables "Start Server" on macOS (no capture implementation yet).
