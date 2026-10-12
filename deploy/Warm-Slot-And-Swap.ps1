<#
.SYNOPSIS
    Warms a deployment slot's keyword cache from the database, then swaps it
    into production.

.DESCRIPTION
    Run after the new build has been deployed to the slot. Steps:
      0. Start the slot if it is stopped, then copy production's app settings
         and connection strings onto it (skipped with -SkipSettingsSync), so
         settings only ever need adding to production. Sticky ("deployment
         slot setting") entries are left alone on both slots.
      1. Wait for the slot to answer GET /api/pipelinetest/health.
      2. Wait for GET /api/health/db to report the SSH tunnel + Postgres reachable.
      3. POST /api/search/refresh?wait=false, then poll GET /api/search/status
         until the refresh finishes; fail if it errored or loaded nothing.
      4. az webapp deployment slot swap  (skipped with -NoSwap)

    Requires an authenticated Azure CLI (an AzureCLI@2 task in the release
    pipeline provides this). Exits non-zero on any failure so the release
    stops before production is touched.

.EXAMPLE
    ./Warm-Slot-And-Swap.ps1 -ResourceGroup ShotdeckSearch -WebAppName shotdecksearch -SlotName staging
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ResourceGroup,
    [Parameter(Mandatory)] [string] $WebAppName,
    [string] $SlotName = 'staging',
    # Override when the slot has a custom hostname; defaults to <app>-<slot>.azurewebsites.net
    [string] $SlotBaseUrl,
    [int] $HealthTimeoutSec = 600,
    [int] $RefreshTimeoutSec = 1800,
    [int] $PollIntervalSec = 10,
    [int] $MinFlatCount = 1000,
    [switch] $NoSwap,
    [switch] $SkipSettingsSync
)

$ErrorActionPreference = 'Stop'

if (-not $SlotBaseUrl) { $SlotBaseUrl = "https://$WebAppName-$SlotName.azurewebsites.net" }
$SlotBaseUrl = $SlotBaseUrl.TrimEnd('/')

function Invoke-AzJson {
    param([string[]] $AzArgs)
    $out = az @AzArgs -o json
    if ($LASTEXITCODE -ne 0) { throw "az $($AzArgs[0..3] -join ' ') failed (exit $LASTEXITCODE)." }
    # ForEach-Object unrolls the array on both Windows PowerShell and pwsh.
    @($out | Out-String | ConvertFrom-Json | ForEach-Object { $_ })
}

# Makes the slot's non-sticky entries of one kind ('appsettings' or
# 'connection-string') match production's. Values are passed via a temp file so
# they never appear on a command line or in the log.
function Sync-SlotEntries {
    param([string] $Kind)
    $listArgs = @('webapp', 'config', $Kind, 'list', '--resource-group', $ResourceGroup, '--name', $WebAppName)
    $prod = Invoke-AzJson $listArgs | Where-Object { -not $_.slotSetting }
    $slot = Invoke-AzJson ($listArgs + @('--slot', $SlotName))

    if ($Kind -eq 'appsettings' -and @($prod).Count -eq 0) {
        throw "Production returned no app settings; refusing to sync (it would empty the slot)."
    }

    $prodNames = @($prod | ForEach-Object name)
    $stale = @($slot | Where-Object { -not $_.slotSetting -and $prodNames -notcontains $_.name } | ForEach-Object name)

    if (@($prod).Count -gt 0) {
        # Always a JSON array: az reads a single object as a map of setting names.
        [object[]] $entries = @($prod | ForEach-Object {
            if ($Kind -eq 'appsettings') { [ordered]@{ name = $_.name; value = $_.value } }
            else { [ordered]@{ name = $_.name; value = $_.value; type = $_.type } }
        })
        $file = New-TemporaryFile
        try {
            [IO.File]::WriteAllText($file.FullName, (ConvertTo-Json -InputObject $entries -Depth 5))
            az webapp config $Kind set --resource-group $ResourceGroup --name $WebAppName --slot $SlotName --settings "@$($file.FullName)" -o none
            if ($LASTEXITCODE -ne 0) { throw "Copying $Kind to slot '$SlotName' failed (exit $LASTEXITCODE)." }
        }
        finally { Remove-Item -Path $file.FullName -Force -ErrorAction SilentlyContinue }
    }

    if ($stale.Count -gt 0) {
        az webapp config $Kind delete --resource-group $ResourceGroup --name $WebAppName --slot $SlotName --setting-names @stale -o none
        if ($LASTEXITCODE -ne 0) { throw "Removing stale $Kind from slot '$SlotName' failed (exit $LASTEXITCODE)." }
    }

    Write-Host "    ${Kind}: copied $(@($prod).Count) from production, removed $($stale.Count) not on production$(if ($stale.Count) { ': ' + ($stale -join ', ') })"
}

function Wait-Until {
    param([string] $What, [int] $TimeoutSec, [scriptblock] $Probe)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $lastError = $null
    while ((Get-Date) -lt $deadline) {
        try {
            $result = & $Probe
            if ($result) { return $result }
        }
        catch { $lastError = $_.Exception.Message }
        Start-Sleep -Seconds $PollIntervalSec
    }
    throw "Timed out after ${TimeoutSec}s waiting for $What. Last error: $lastError"
}

Write-Host "Slot URL: $SlotBaseUrl"

# A stopped slot can't warm up, and the swap fails 409 ("one of the slots is in a stopped state").
$state = az webapp show --resource-group $ResourceGroup --name $WebAppName --slot $SlotName --query state -o tsv
if ($LASTEXITCODE -ne 0) { throw "Could not read state of slot '$SlotName' (exit $LASTEXITCODE)." }
if ($state -ne 'Running') {
    Write-Host "Slot '$SlotName' is $state; starting it..."
    az webapp start --resource-group $ResourceGroup --name $WebAppName --slot $SlotName
    if ($LASTEXITCODE -ne 0) { throw "Could not start slot '$SlotName' (exit $LASTEXITCODE)." }
}

if ($SkipSettingsSync) {
    Write-Host "-SkipSettingsSync set; leaving the slot's settings as they are."
} else {
    Write-Host "Copying production settings to slot '$SlotName' (restarts the slot)..."
    Sync-SlotEntries 'appsettings'
    Sync-SlotEntries 'connection-string'
}

Write-Host "1/4 Waiting for slot to come up..."
Wait-Until "slot health" $HealthTimeoutSec {
    $r = Invoke-RestMethod -Uri "$SlotBaseUrl/api/pipelinetest/health" -TimeoutSec 30
    $r.status -eq 'healthy'
} | Out-Null
Write-Host "    slot is up"

Write-Host "2/4 Waiting for SSH tunnel + database..."
$db = Wait-Until "database reachability" $HealthTimeoutSec {
    $r = Invoke-RestMethod -Uri "$SlotBaseUrl/api/health/db" -TimeoutSec 30
    if ($r.databaseReachable) { $r }
}
Write-Host "    db reachable via $($db.databaseTarget) (tunnel endpoint $($db.tunnel.endpoint), usingExistingForward=$($db.tunnel.usingExistingForward))"

Write-Host "3/4 Refreshing keyword cache from database..."
$startedAt = (Get-Date).ToUniversalTime()
$kick = Invoke-RestMethod -Method Post -Uri "$SlotBaseUrl/api/search/refresh?wait=false" -TimeoutSec 60
if (-not $kick.started) { Write-Host "    a refresh was already running; waiting for it" }

$status = Wait-Until "keyword cache refresh" $RefreshTimeoutSec {
    $s = Invoke-RestMethod -Uri "$SlotBaseUrl/api/search/status" -TimeoutSec 30
    if (-not $s.isRefreshRunning -and $s.lastRefreshEndAt -and ([datetime]$s.lastRefreshEndAt).ToUniversalTime() -ge $startedAt.AddSeconds(-5)) { $s }
}

if (-not $status.lastRefreshSucceeded) {
    throw "Keyword cache refresh FAILED on slot '$SlotName': $($status.lastRefreshError)"
}
if ($status.flatCount -lt $MinFlatCount) {
    throw "Keyword cache refresh loaded only $($status.flatCount) keywords (< $MinFlatCount); refusing to swap."
}
Write-Host "    refresh ok: flatCount=$($status.flatCount) sources=$($status.sourceKeys) categories=$($status.categoryCount) synonyms=$($status.synonymCount) unwanted=$($status.unwantedWordsCount)"

if ($NoSwap) {
    Write-Host "4/4 -NoSwap set; slot is warm, not swapping."
    exit 0
}

Write-Host "4/4 Swapping slot '$SlotName' into production..."
az webapp deployment slot swap --resource-group $ResourceGroup --name $WebAppName --slot $SlotName --target-slot production
if ($LASTEXITCODE -ne 0) { throw "Slot swap failed (exit $LASTEXITCODE)." }

$prod = Invoke-RestMethod -Uri "https://$WebAppName.azurewebsites.net/api/search/status" -TimeoutSec 60
Write-Host "    production now serving flatCount=$($prod.flatCount) (lastRefreshEndAt=$($prod.lastRefreshEndAt))"
Write-Host "Done."
