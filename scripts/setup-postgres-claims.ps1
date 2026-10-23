# Points the signaling server's "My devices" storage at PostgreSQL: sets ConnectionStrings__DeskShare (asked for,
# not echoed) and Accounts__Storage=Postgres on the App Service. Creates nothing; the database and login must exist
# (the server creates its device_claims table on first use).
# Needs: pwsh 7+, az CLI signed in to the subscription with rg-agentwall.
# Example: Host=187.127.83.154;Port=6432;Database=deskshare;Username=...;Password=...;Ssl Mode=Require;No Reset On Close=true
# (No Reset On Close: PgBouncer resets server connections itself; Npgsql's DISCARD ALL breaks transaction pooling.)

$ErrorActionPreference = "Stop"
$rg = "rg-agentwall"; $app = "deskshare-signaling"

$cs = Read-Host "Connection string" -MaskInput
if ($cs -notmatch 'Host=' -or $cs -notmatch 'Database=') { throw "That does not look like an Npgsql connection string." }

# Through a file: the ";" in the connection string would break the az.cmd command line.
$file = New-TemporaryFile
try {
    @(@{ name = "ConnectionStrings__DeskShare"; value = $cs; slotSetting = $false },
      @{ name = "Accounts__Storage"; value = "Postgres"; slotSetting = $false }) | ConvertTo-Json | Set-Content $file
    az webapp config appsettings set -g $rg -n $app -o none --settings "@$file"
    if ($LASTEXITCODE -ne 0) { throw "az failed" }
}
finally { Remove-Item $file }

Write-Host "Set. The app restarts with the new settings."
