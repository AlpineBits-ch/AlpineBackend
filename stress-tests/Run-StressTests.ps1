#Requires -Version 5.1
<#
.SYNOPSIS
  Interactive runner for the Echo stress-test suite.
  Targets either the local k8s cluster (auto-discovers echo-proxy) or a staging environment.

.DESCRIPTION
  Prompts for: environment, test selection, load profile, and account-setup strategy.
  State (guild/channel IDs) is persisted to .stress-state.json so subsequent runs can
  skip user provisioning with SKIP_PROVISION=true.

.EXAMPLE
  # Run from any directory:
  & .\stress-tests\Run-StressTests.ps1

  # Or from inside stress-tests/:
  .\Run-StressTests.ps1
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
#  Paths
# ---------------------------------------------------------------------------
$Root      = $PSScriptRoot
$StateFile = Join-Path $Root '.stress-state.json'

# ---------------------------------------------------------------------------
#  Test catalogue  (relative paths from $Root)
# ---------------------------------------------------------------------------
$Catalogue = [ordered]@{
    websocket = @(
        [pscustomobject]@{ id='a1'; file='a-websocket/01-connection-ramp.js'; label='Connection Ramp'    }
        [pscustomobject]@{ id='a2'; file='a-websocket/02-idle-sustain.js';    label='Idle Sustain'       }
        [pscustomobject]@{ id='a3'; file='a-websocket/03-fanout-storm.js';    label='Fanout Storm'       }
        [pscustomobject]@{ id='a4'; file='a-websocket/04-typing-storm.js';    label='Typing Storm'       }
        [pscustomobject]@{ id='a5'; file='a-websocket/05-voice-churn.js';     label='Voice Churn'        }
    )
    consistency = @(
        [pscustomobject]@{ id='b1'; file='b-consistency/01-event-propagation.js'; label='Event Propagation' }
        [pscustomobject]@{ id='b2'; file='b-consistency/02-read-your-writes.js';  label='Read-Your-Writes'  }
        [pscustomobject]@{ id='b3'; file='b-consistency/03-outbox-lag.js';        label='Outbox Lag'        }
        [pscustomobject]@{ id='b4'; file='b-consistency/04-ghost-presence.js';    label='Ghost Presence'    }
    )
    performance = @(
        [pscustomobject]@{ id='c1'; file='c-performance/01-yarp-throughput.js';     label='YARP Throughput'     }
        [pscustomobject]@{ id='c2'; file='c-performance/02-db-pool-exhaustion.js';  label='DB Pool Exhaustion'  }
        [pscustomobject]@{ id='c3'; file='c-performance/03-rate-limiter.js';        label='Rate Limiter'        }
        [pscustomobject]@{ id='c4'; file='c-performance/04-write-amplification.js'; label='Write Amplification' }
    )
}

# ---------------------------------------------------------------------------
#  Load profiles  (config file paths are relative to $Root)
# ---------------------------------------------------------------------------
$Profiles = [ordered]@{
    '1k'   = [pscustomobject]@{ config = 'options/1k.json';   label = '1 000 users  - dev / laptop-friendly'           }
    '10k'  = [pscustomobject]@{ config = 'options/10k.json';  label = '10 000 users - moderate load'                   }
    '100k' = [pscustomobject]@{ config = 'options/100k.json'; label = '100 000 users - full scale (needs k6 operator)'  }
}

# ---------------------------------------------------------------------------
#  UI helpers
# ---------------------------------------------------------------------------
function Write-Banner([string]$Text) {
    $bar = '-' * 62
    Write-Host ''
    Write-Host $bar          -ForegroundColor DarkCyan
    Write-Host "  $Text"    -ForegroundColor Cyan
    Write-Host $bar          -ForegroundColor DarkCyan
    Write-Host ''
}

function Read-Menu {
    param(
        [string]   $Prompt,
        [string[]] $Items
    )
    Write-Host $Prompt -ForegroundColor Yellow
    for ($i = 0; $i -lt $Items.Count; $i++) {
        Write-Host ("  [{0}] {1}" -f ($i + 1), $Items[$i])
    }
    $n = 0
    do {
        $raw = Read-Host "  Choice (1-$($Items.Count))"
        $ok  = [int]::TryParse($raw.Trim(), [ref]$n) -and $n -ge 1 -and $n -le $Items.Count
        if (-not $ok) { Write-Warning "  Enter a number between 1 and $($Items.Count)." }
    } while (-not $ok)
    return ($n - 1)   # 0-based
}

function Read-YesNo([string]$Prompt) {
    do {
        $r = (Read-Host "$Prompt [y/n]").Trim().ToLower()
    } while ($r -notin @('y', 'n', 'yes', 'no'))
    return $r -in @('y', 'yes')
}

function Read-NonEmpty([string]$Prompt) {
    do {
        $v = (Read-Host $Prompt).Trim()
        if (-not $v) { Write-Warning '  Value cannot be empty.' }
    } while (-not $v)
    return $v
}

# ---------------------------------------------------------------------------
#  State persistence
# ---------------------------------------------------------------------------
function Load-State {
    if (Test-Path $StateFile) {
        try { return (Get-Content $StateFile -Raw | ConvertFrom-Json) }
        catch { Write-Warning "  Could not parse $StateFile - ignoring." }
    }
    return $null
}

function Save-State {
    param([string]$GuildId, [string]$ChannelId, [string]$EnvName, [string]$BaseUrl)
    @{
        guildId     = $GuildId
        channelId   = $ChannelId
        environment = $EnvName
        baseUrl     = $BaseUrl
        savedAt     = (Get-Date -Format 'o')
    } | ConvertTo-Json | Set-Content -Encoding UTF8 $StateFile
    Write-Host "  State saved -> $StateFile" -ForegroundColor DarkGray
}

# ---------------------------------------------------------------------------
#  Local k8s: discover echo-proxy address
# ---------------------------------------------------------------------------
function Get-LocalBaseUrl {
    if (-not (Get-Command kubectl -ErrorAction SilentlyContinue)) {
        Write-Warning '  kubectl not found - cannot auto-discover the cluster address.'
        return (Read-NonEmpty '  Enter local base URL (e.g. http://localhost:8080)')
    }

    Write-Host '  Querying kubectl for echo-proxy in namespace echo-stress...' -ForegroundColor DarkGray

    # LoadBalancer external IP
    $ip = "$(kubectl get svc echo-proxy -n echo-stress `
            -o jsonpath='{.status.loadBalancer.ingress[0].ip}' 2>$null)".Trim()

    # LoadBalancer hostname (kind / cloud)
    if (-not $ip) {
        $ip = "$(kubectl get svc echo-proxy -n echo-stress `
                -o jsonpath='{.status.loadBalancer.ingress[0].hostname}' 2>$null)".Trim()
    }

    if ($ip) {
        $url = "http://$ip"
        Write-Host "  Discovered LoadBalancer address: $url" -ForegroundColor Green
        return $url
    }

    # NodePort fallback (minikube / k3d without metallb)
    $nodePort = "$(kubectl get svc echo-proxy -n echo-stress `
                  -o jsonpath='{.spec.ports[0].nodePort}' 2>$null)".Trim()
    if ($nodePort) {
        $minikubeIp = "$(minikube ip 2>$null)".Trim()
        $h   = if ($minikubeIp) { $minikubeIp } else { 'localhost' }
        $url = "http://${h}:${nodePort}"
        Write-Host "  Using NodePort: $url" -ForegroundColor Green
        return $url
    }

    Write-Warning '  Could not auto-discover echo-proxy address.'
    Write-Host '  Tip: kubectl port-forward svc/echo-proxy 8080:80 -n echo-stress' -ForegroundColor DarkGray
    return (Read-NonEmpty '  Enter local base URL (e.g. http://localhost:8080)')
}

# ---------------------------------------------------------------------------
#  k6 runner detection  (local binary or Docker fallback)
# ---------------------------------------------------------------------------
function Get-K6Runner {
    if (Get-Command k6 -ErrorAction SilentlyContinue) {
        $ver = (k6 version 2>&1 | Select-Object -First 1)
        Write-Host "  Runner: local k6  ($ver)" -ForegroundColor DarkGray
        return 'local'
    }
    if (Get-Command docker -ErrorAction SilentlyContinue) {
        Write-Host '  k6 not in PATH - will use Docker image grafana/k6:0.52.0' -ForegroundColor Yellow
        return 'docker'
    }
    Write-Error ("  Neither k6 nor docker found.`n" +
                 '  Install k6    : https://grafana.com/docs/k6/latest/set-up/install-k6/' + "`n" +
                 '  Install Docker: https://docs.docker.com/get-docker/')
}

# ---------------------------------------------------------------------------
#  Run a single k6 test
# ---------------------------------------------------------------------------
function Invoke-K6Test {
    param(
        [string]    $Runner,
        [string]    $TestFile,     # relative to $Root
        [string]    $ConfigFile,   # relative to $Root
        [string]    $Label,
        [string]    $BaseUrl,
        [hashtable] $ExtraEnv
    )

    Write-Host ''
    Write-Host "  >> $Label" -ForegroundColor Cyan
    Write-Host ("     file   : {0}" -f $TestFile)
    Write-Host ("     config : {0}" -f $ConfigFile)
    Write-Host ("     target : {0}" -f $BaseUrl)

    # Build -e flag list
    $envFlags = [System.Collections.Generic.List[string]]::new()
    $envFlags.Add('-e'); $envFlags.Add("BASE_URL=$BaseUrl")
    foreach ($kv in $ExtraEnv.GetEnumerator()) {
        $envFlags.Add('-e'); $envFlags.Add("$($kv.Key)=$($kv.Value)")
    }

    # Absolute paths avoid CWD ambiguity (Push-Location doesn't reliably update
    # the Win32 process directory inherited by child processes like k6)
    $absTest   = Join-Path $Root $TestFile
    $absConfig = Join-Path $Root $ConfigFile

    $exitCode = 0

    if ($Runner -eq 'local') {
        & k6 run @envFlags --config $absConfig $absTest
        $exitCode = $LASTEXITCODE
    }
    else {
        # Docker: replace localhost with host.docker.internal for container networking
        $dockerUrl = $BaseUrl -replace 'localhost', 'host.docker.internal'
        if ($dockerUrl -ne $BaseUrl) {
            Write-Host '     note   : localhost -> host.docker.internal (Docker networking)' -ForegroundColor DarkGray
            $idx = $envFlags.IndexOf("BASE_URL=$BaseUrl")
            if ($idx -ge 0) { $envFlags[$idx] = "BASE_URL=$dockerUrl" }
        }
        # Docker needs forward-slash paths; test/config are relative inside the container
        $mountPath    = $Root -replace '\\', '/'
        $relTest      = $TestFile   -replace '\\', '/'
        $relConfig    = $ConfigFile -replace '\\', '/'
        & docker run --rm `
            --add-host 'host.docker.internal:host-gateway' `
            -v "${mountPath}:/tests" `
            -w /tests `
            grafana/k6:0.52.0 `
            run @envFlags --config "./$relConfig" "./$relTest"
        $exitCode = $LASTEXITCODE
    }

    if ($exitCode -ne 0) {
        Write-Host ("  [!!] {0}  (exit {1} - threshold(s) likely crossed)" -f $Label, $exitCode) -ForegroundColor Red
    }
    else {
        Write-Host ("  [ok] {0}" -f $Label) -ForegroundColor Green
    }

    return $exitCode
}

# ===========================================================================
#  MAIN
# ===========================================================================
Write-Banner 'Echo Stress-Test Runner'

# -- Step 1 : Target environment --------------------------------------------
$envIdx = Read-Menu `
    'Select target environment:' `
    @('Local k8s cluster  (auto-discover echo-proxy)',
      'Staging            (enter base URL)')

$envName = ''
$baseUrl = ''
if ($envIdx -eq 0) {
    $envName = 'local-k8s'
    $baseUrl = Get-LocalBaseUrl
}
else {
    $envName = 'staging'
    $baseUrl = Read-NonEmpty '  Staging base URL (e.g. https://api.staging.example.com)'
}

$baseUrl = $baseUrl.TrimEnd('/')
Write-Host ''
Write-Host "  Target: $baseUrl" -ForegroundColor Green

# -- Step 2 : Test selection ------------------------------------------------
$allTests = @()
foreach ($cat in $Catalogue.Values) { foreach ($t in $cat) { $allTests += $t } }

$catLabels = $Catalogue.Keys | ForEach-Object {
    $count = $Catalogue[$_].Count
    "$_  ($count tests)"
}

$selIdx = Read-Menu `
    'Select test set:' `
    (@("All tests  ($($allTests.Count))") + $catLabels + @('Pick one test'))

$testsToRun = @()
if ($selIdx -eq 0) {
    $testsToRun = $allTests
}
elseif ($selIdx -le $Catalogue.Count) {
    $catKey     = @($Catalogue.Keys)[$selIdx - 1]
    $testsToRun = $Catalogue[$catKey]
}
else {
    $pickLabels = $allTests | ForEach-Object { "[$($_.id)]  $($_.label)" }
    $pickIdx    = Read-Menu 'Select test:' $pickLabels
    $testsToRun = @($allTests[$pickIdx])
}

$selectedNames = ($testsToRun | ForEach-Object { $_.label }) -join ', '
Write-Host ("  Selected {0} test(s): {1}" -f $testsToRun.Count, $selectedNames) -ForegroundColor Green

# -- Step 3 : Load profile --------------------------------------------------
$profileLabels = $Profiles.Values | ForEach-Object { $_.label }
$profileIdx    = Read-Menu 'Select load profile:' $profileLabels
$profileKey    = @($Profiles.Keys)[$profileIdx]
$profile       = $Profiles[$profileKey]

if ($profileKey -eq '100k') {
    Write-Host ''
    Write-Host '  NOTE: 100k profile requires k6 operator with >=20 pods, Redis Cluster,' -ForegroundColor Yellow
    Write-Host '        3+ ScyllaDB nodes, and RabbitMQ federation. See options/100k.json.' -ForegroundColor Yellow
}
Write-Host ("  Profile: {0}  (config: {1})" -f $profileKey, $profile.config) -ForegroundColor Green

# -- Step 4 : Account / guild setup -----------------------------------------
$state     = Load-State
$guildId   = ''
$channelId = ''
$skipProv  = $false

Write-Host ''

if ($state -and $state.guildId -and $state.channelId) {
    Write-Host '  Saved state found:' -ForegroundColor DarkGray
    Write-Host ("    Guild ID  : {0}" -f $state.guildId)
    Write-Host ("    Channel ID: {0}" -f $state.channelId)
    Write-Host ("    Env       : {0}  |  {1}" -f $state.environment, $state.baseUrl)
    Write-Host ("    Saved at  : {0}" -f $state.savedAt)
    Write-Host ''

    $setupIdx = Read-Menu `
        'Account / guild setup:' `
        @('Reuse saved guild & channel  (SKIP_PROVISION=true - fastest)',
          'Fresh setup                  (re-register users, recreate guild)',
          'Enter different IDs manually')

    switch ($setupIdx) {
        0 {
            $guildId   = $state.guildId
            $channelId = $state.channelId
            $skipProv  = $true
            Write-Host ("  Reusing: guild={0}  channel={1}" -f $guildId, $channelId) -ForegroundColor Green
        }
        1 {
            Write-Host '  Fresh setup - users will be provisioned during the first test run.' -ForegroundColor Yellow
        }
        2 {
            $guildId   = Read-NonEmpty '  TEST_GUILD_ID'
            $channelId = Read-NonEmpty '  TEST_CHANNEL_ID'
            $skipProv  = $true
        }
    }
}
else {
    Write-Host '  No saved state found.' -ForegroundColor DarkGray
    $setupIdx = Read-Menu `
        'Account / guild setup:' `
        @('Fresh setup  (register users, create guild - runs once before VUs start)',
          'Enter existing IDs manually  (SKIP_PROVISION=true)')

    if ($setupIdx -eq 1) {
        $guildId   = Read-NonEmpty '  TEST_GUILD_ID'
        $channelId = Read-NonEmpty '  TEST_CHANNEL_ID'
        $skipProv  = $true
    }
}

# -- Step 5 : Runner --------------------------------------------------------
$runner = Get-K6Runner

# -- Step 6 : Summary & confirmation ----------------------------------------
Write-Banner 'Run Summary'

Write-Host ("  Environment  : {0}" -f $envName)
Write-Host ("  Base URL     : {0}" -f $baseUrl)
Write-Host ("  Load profile : {0}  ({1})" -f $profileKey, $profile.config)
Write-Host ("  Runner       : {0}" -f $runner)

$provisionDesc = if ($skipProv) {
    "skip provisioning  (SKIP_PROVISION=true, guild=$guildId)"
} elseif ($guildId) {
    "manual IDs (guild=$guildId)"
} else {
    "fresh setup (register $profileKey users)"
}
Write-Host ("  Setup        : {0}" -f $provisionDesc)
Write-Host '  Tests        :'
foreach ($t in $testsToRun) {
    Write-Host ("    * [{0}]  {1}  ({2})" -f $t.id, $t.label, $t.file)
}
Write-Host ''

if (-not (Read-YesNo 'Proceed?')) {
    Write-Host '  Aborted.' -ForegroundColor Yellow
    exit 0
}

# -- Step 7 : Execute -------------------------------------------------------
Write-Banner "Running $($testsToRun.Count) test(s)"

$extraEnv = @{}
if ($skipProv) {
    $extraEnv['SKIP_PROVISION']  = 'true'
    $extraEnv['TEST_GUILD_ID']   = $guildId
    $extraEnv['TEST_CHANNEL_ID'] = $channelId
}

$results = @()

foreach ($test in $testsToRun) {
    $code = Invoke-K6Test `
        -Runner     $runner `
        -TestFile   $test.file `
        -ConfigFile $profile.config `
        -Label      $test.label `
        -BaseUrl    $baseUrl `
        -ExtraEnv   $extraEnv

    $results += [pscustomobject]@{ label = $test.label; exitCode = $code; pass = ($code -eq 0) }
}

# -- Step 8 : Offer to save state after fresh/manual run --------------------
if (-not $skipProv -and $results.Count -gt 0) {
    Write-Host ''
    if (Read-YesNo 'Save guild/channel IDs for future runs?') {
        if (-not $guildId)   { $guildId   = Read-NonEmpty '  TEST_GUILD_ID used in this run'   }
        if (-not $channelId) { $channelId = Read-NonEmpty '  TEST_CHANNEL_ID used in this run' }
        if ($guildId -and $channelId) {
            Save-State -GuildId $guildId -ChannelId $channelId -EnvName $envName -BaseUrl $baseUrl
        }
    }
}

# -- Step 9 : Final summary -------------------------------------------------
Write-Banner 'Results'

foreach ($r in $results) {
    $icon  = if ($r.pass) { '[ok]' } else { '[!!]' }
    $color = if ($r.pass) { 'Green' } else { 'Red' }
    Write-Host ("  {0}  {1}" -f $icon, $r.label) -ForegroundColor $color
}

$passed = ($results | Where-Object { $_.pass }).Count
$failed = ($results | Where-Object { -not $_.pass }).Count

Write-Host ''
$summaryColor = if ($failed -eq 0) { 'Green' } else { 'Yellow' }
Write-Host ("  Passed: {0}/{1}   Failed: {2}/{1}" -f $passed, $results.Count, $failed) -ForegroundColor $summaryColor

if ($failed -gt 0) {
    Write-Host '  (Non-zero exit usually means one or more k6 thresholds were crossed.)' -ForegroundColor DarkGray
}

exit $failed
