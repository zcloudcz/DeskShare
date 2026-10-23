# Accounts (ZCLOUD Login, "My devices")

Optional. Anonymous use (Server ID + passkey or permanent password) is unchanged and stays the default.
Signing in adds "My devices": a user binds the Server IDs of their own computers to their ZCLOUD account and
sees them listed with online status. Connecting still needs the passkey or password (the end-to-end binding
of the session is derived from it). Two-factor sign-in comes from Entra (Conditional Access / user flow),
not from DeskShare code.

## How it works

| Piece | What it does |
|---|---|
| Signaling server | `Accounts:Enabled=true` turns on JwtBearer validation of Entra External ID access tokens and maps `GET /accounts/config` (public), `GET /me/devices`, `POST /me/devices/claim`, `DELETE /me/devices/{serverId}`. Off: all of them answer 404. |
| Claim proof | `HMAC-SHA256(SHA-256(ownerSecret), "DeskShare-claim-v1|serverId|accountKey|unixSeconds|nonce")`, base64. The server already holds `SHA-256(ownerSecret)` per Server ID from `/register`, so only a running device can be added, and only by whoever holds its owner secret. Timestamp window 5 min, nonce used once. See `src/Core/Auth/DeviceClaimProof.cs`. |
| Account key | `oid` claim of the access token, else `sub`. Returned by `GET /me/devices` so clients prove for exactly that value. |
| Storage | Same switch as sessions: `Storage:Type=AzureTableStorage` uses table `DeviceClaims` (survives restarts), otherwise in memory. |
| Desktop app | MSAL.NET, system browser, loopback redirect `http://localhost`. "Sign in" top right, Server Mode "Add this computer to my devices", Client Mode "My devices". Unattended agent: "Add to my devices" in the Unattended access card runs `DeskShare.Service.exe claim-proof` elevated (the agent's owner secret is admin-only and never leaves the helper). |
| Web viewer | `accounts.js` loads `vendor/msal-browser.min.js` only when `/accounts/config` answers. Redirect sign-in, token cache in `sessionStorage`. |

## 1. App registrations (External ID tenant)

Run in PowerShell, signed in to the External ID tenant (not the Azure subscription tenant):

```powershell
az login --tenant 37a15516-3c13-4bc8-a606-63226859b29a --allow-no-subscriptions

# --- API (the signaling server) ---
$api = az ad app create --display-name "DeskShare API" --sign-in-audience AzureADMyOrg | ConvertFrom-Json
$apiAppId = $api.appId
# External ID accepts api://<appId> as App ID URI without a verified domain.
az ad app update --id $apiAppId --identifier-uris "api://$apiAppId"
$scopeId = [guid]::NewGuid().ToString()
$body = @{
  api = @{
    requestedAccessTokenVersion = 2   # v2 tokens: aud = API client id, iss = https://<tenant>.ciamlogin.com/<tenant>/v2.0
    oauth2PermissionScopes = @(@{
      id = $scopeId; value = "access_as_user"; type = "User"; isEnabled = $true
      adminConsentDisplayName = "Access DeskShare as the signed-in user"
      adminConsentDescription = "Lets DeskShare list and manage the user's devices."
      userConsentDisplayName = "Access DeskShare"; userConsentDescription = "List and manage your DeskShare devices."
    })
  }
} | ConvertTo-Json -Depth 5 -Compress
$body | Out-File -Encoding utf8 api-scope.json
az rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications/$($api.id)" --headers "Content-Type=application/json" --body "@api-scope.json"
Remove-Item api-scope.json
az ad sp create --id $apiAppId

# --- Web viewer (SPA) ---
$web = az ad app create --display-name "DeskShare Web" --sign-in-audience AzureADMyOrg | ConvertFrom-Json
$spa = @{ spa = @{ redirectUris = @(
  "https://app.deskshare.zcloud.cz/webclient/index.html",
  "http://localhost:5151/webclient/index.html",
  "https://localhost:5152/webclient/index.html") } } | ConvertTo-Json -Depth 4 -Compress
$spa | Out-File -Encoding utf8 web-spa.json
az rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications/$($web.id)" --headers "Content-Type=application/json" --body "@web-spa.json"
Remove-Item web-spa.json
az ad sp create --id $web.appId

# --- Desktop app (public client, loopback) ---
$desk = az ad app create --display-name "DeskShare Desktop" --sign-in-audience AzureADMyOrg `
  --public-client-redirect-uris "http://localhost" --is-fallback-public-client true | ConvertFrom-Json
az ad sp create --id $desk.appId

# --- Let both clients call the API (+ openid/offline_access) and pre-consent (customers cannot consent themselves) ---
$graph = "00000003-0000-0000-c000-000000000000"
foreach ($client in @($web.appId, $desk.appId)) {
  az ad app permission add --id $client --api $apiAppId --api-permissions "$scopeId=Scope"
  az ad app permission add --id $client --api $graph --api-permissions "37f7f235-527c-4136-accd-4a02d197296e=Scope" "7427e0e9-2fba-42fe-b0c0-848c9e6a8182=Scope"  # openid, offline_access
  az ad app permission grant --id $client --api $apiAppId --scope access_as_user
  az ad app permission grant --id $client --api $graph --scope "openid offline_access"
}

# --- Add both clients to the zcloud_signin user flow ---
$flow = (az rest --method GET --uri "https://graph.microsoft.com/beta/identity/authenticationEventsFlows" | ConvertFrom-Json).value |
  Where-Object displayName -eq "zcloud_signin"
foreach ($client in @($web.appId, $desk.appId)) {
  $link = @{ "@odata.type" = "#microsoft.graph.authenticationConditionApplication"; appId = $client } | ConvertTo-Json -Compress
  $link | Out-File -Encoding utf8 flow-app.json
  az rest --method POST --uri "https://graph.microsoft.com/beta/identity/authenticationEventsFlows/$($flow.id)/conditions/applications/includeApplications" `
    --headers "Content-Type=application/json" --body "@flow-app.json"
}
Remove-Item flow-app.json

"Audience=$apiAppId  Scope=api://$apiAppId/access_as_user  ClientIdWeb=$($web.appId)  ClientIdDesktop=$($desk.appId)"
```

MFA: in the Entra admin center of the External ID tenant, Conditional Access policy "Require MFA" targeting
"DeskShare Web" and "DeskShare Desktop" (email OTP / SMS as configured for the tenant). Portal only, no code.

## 2. Signaling server settings (App Service)

```powershell
az account set --subscription <subscription of rg-agentwall>
az webapp config appsettings set -g rg-agentwall -n deskshare-signaling -o none --settings `
  Accounts__Enabled=true `
  Accounts__Authority=https://zcloudcz.ciamlogin.com/37a15516-3c13-4bc8-a606-63226859b29a `
  Accounts__Audience=<apiAppId> `
  Accounts__Scope=api://<apiAppId>/access_as_user `
  Accounts__ClientIdWeb=<web appId> `
  Accounts__ClientIdDesktop=<desktop appId>
```

Production uses `Storage:Type=InMemory` today (`appsettings.Production.json`): claims are then lost on every restart.
For durable "My devices" set `Storage__Type=AzureTableStorage` and `Storage__AzureTableStorage__ConnectionString`
(this also moves sessions to the table backend, see `AzureTableSessionStorage`).

Optional: `RateLimiting__Accounts__PermitLimit` / `__WindowSeconds` (default 30 per 60 s per account, per IP when anonymous).

## 3. What to verify after the registrations exist

1. `GET https://app.deskshare.zcloud.cz/accounts/config` returns the authority, both client ids and the scope.
2. Web viewer: "Sign in" appears top right, redirects to zcloudcz.ciamlogin.com, MFA prompt (if the policy is on),
   back on the viewer the account name shows and "My devices" says "No devices yet". No CSP errors in the console.
3. Desktop: "Sign in" opens the browser, the app shows the account name. Start the server, "Add this computer to my
   devices" succeeds; Client Mode lists it as Online; clicking it fills the Server ID; connecting still asks for the passkey.
4. Unattended (Windows, service installed): "Add to my devices" shows a UAC prompt and adds the agent's Server ID (unattended).
5. A second account cannot add the same running computer (409), and "Remove" frees it.
6. Access token check if anything fails with 401: decode it (jwt.ms) and compare `iss` (one of the two ciamlogin hosts
   + `/v2.0`), `aud` (= API appId; if it is `api://...`, `requestedAccessTokenVersion` is not 2) and `scp` (`access_as_user`).

## Mobile app (not done, out of scope)

The Flutter app would need: an `/accounts/config` check, sign-in with the desktop-style public client (or its own
registration with a custom-scheme redirect, added to the user flow), "My devices" list via `GET /me/devices`, and,
to add the phone itself as a host (if it ever shares its screen), the claim proof from `DeviceClaimProof` over its own
owner secret (`tests/js/claim-vector.mjs` pins the exact bytes for a non-.NET implementation).
