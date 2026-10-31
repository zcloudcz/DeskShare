# Creates the Android upload keystore (or reuses an existing one) and stores it in the GitHub secrets that
# .github/workflows/mobile-release.yml reads. The password is typed here only; it goes straight to GitHub.
# Back up the .jks file and its password: Play accepts updates only signed with this key (a lost upload key
# can be reset through Play support, but that takes days).
param(
    [string]$Keystore = (Join-Path $HOME '.deskshare\deskshare-upload.jks'),
    [string]$Repo = 'zcloudcz/DeskShare'
)
$ErrorActionPreference = 'Stop'
$alias = 'upload'

$keytool = @(
    $(if ($env:JAVA_HOME) { Join-Path $env:JAVA_HOME 'bin\keytool.exe' }),
    'C:\Program Files\Android\Android Studio\jbr\bin\keytool.exe'
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $keytool) { throw 'keytool not found (install Android Studio or set JAVA_HOME).' }
gh auth status *> $null
if ($LASTEXITCODE -ne 0) { throw 'gh is not logged in (run: gh auth login).' }

$password = Read-Host 'Keystore password (min. 6 characters)' -AsSecureString
$plain = [System.Net.NetworkCredential]::new('', $password).Password
if ($plain.Length -lt 6) { throw 'The password must have at least 6 characters.' }

if (Test-Path $Keystore) {
    Write-Host "Using the existing keystore $Keystore"
} else {
    New-Item -ItemType Directory -Force (Split-Path $Keystore) | Out-Null
    # PKCS12: the key password is the keystore password.
    & $keytool -genkeypair -keystore $Keystore -storetype PKCS12 -alias $alias -keyalg RSA -keysize 4096 `
        -validity 10000 -storepass $plain -dname 'CN=DeskShare, O=zcloud.cz, C=CZ'
    if ($LASTEXITCODE -ne 0) { throw 'keytool failed.' }
    Write-Host "Created $Keystore"
}
# Fails here (not in CI) when the password does not open the keystore.
& $keytool -list -keystore $Keystore -storepass $plain -alias $alias *> $null
if ($LASTEXITCODE -ne 0) { throw "The password does not open $Keystore (or alias '$alias' is missing)." }

[Convert]::ToBase64String([IO.File]::ReadAllBytes($Keystore)) | gh secret set ANDROID_KEYSTORE_BASE64 --repo $Repo
$plain | gh secret set ANDROID_KEYSTORE_PASSWORD --repo $Repo
$plain | gh secret set ANDROID_KEY_PASSWORD --repo $Repo
$alias | gh secret set ANDROID_KEY_ALIAS --repo $Repo
Write-Host 'GitHub secrets set. Now back up the keystore file and its password.'
