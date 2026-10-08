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

## 3. Azure App Service for the signaling server

Reuses the existing Linux B1 plan `agentwall-plan` (no extra cost).

```powershell
az webapp create -g rg-agentwall -p agentwall-plan -n deskshare-signaling --runtime "DOTNETCORE:8.0"
az webapp config set -g rg-agentwall -n deskshare-signaling --web-sockets-enabled true --always-on true --http20-enabled true
az webapp update -g rg-agentwall -n deskshare-signaling --https-only true
az webapp config appsettings set -g rg-agentwall -n deskshare-signaling --settings ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_FORWARDEDHEADERS_ENABLED=true
az webapp deployment list-publishing-profiles -g rg-agentwall -n deskshare-signaling --xml | gh secret set AZURE_WEBAPP_PUBLISH_PROFILE -R zcloudcz/DeskShare
gh workflow run deploy-signaling.yml -R zcloudcz/DeskShare
```

Check: `https://deskshare-signaling.azurewebsites.net/health` (returns 400 until the custom
domain below is bound, because `AllowedHosts` in `appsettings.Production.json` lists only
`app.deskshare.zcloud.cz` and `deskshare-signaling.azurewebsites.net`).

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
