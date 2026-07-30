<#
.SYNOPSIS
    Venta / Echo self-hosted installer - Windows.

.DESCRIPTION
    Windows counterpart to deploy/install.sh. Produces the same deployment from the same
    deploy/compose.yaml:

      infrastructure   PostgreSQL, Redis, RabbitMQ, ScyllaDB (optional), MinIO (optional)
      services         Identity, Guild, Messaging, Social, Federation, Bots, Import,
                       Isle (optional) and the Echo gateway
      edge             Caddy in front of everything for TLS termination, with automatic
                       Let's Encrypt issuance and renewal
      lifecycle        a Scheduled Task at system startup plus docker restart policies, so
                       the stack comes back after a reboot, and a `ventactl` helper

    The containers are Linux containers: Docker Desktop must be running in its default
    (Linux/WSL2) mode, or Docker Engine must be reachable inside WSL2.

    Re-run it at any time. Secrets already in deploy\.env are preserved - rotating the
    federation keypair would invalidate every peering you have already established.

.EXAMPLE
    .\Install-VentaStack.ps1

.EXAMPLE
    .\Install-VentaStack.ps1 -NonInteractive -Domain chat.example.com -AcmeEmail admin@example.com

.NOTES
    Run from an elevated PowerShell session.
#>

[CmdletBinding()]
param(
    [string]$Domain,
    [string]$StorageDomain,
    [string]$InstanceName,
    [string]$AcmeEmail,
    [ValidateSet('letsencrypt', 'local', 'external-proxy')]
    [string]$TlsMode,
    [ValidateSet('registry', 'build')]
    [string]$ImageSource,
    [string]$ImagePrefix = 'ghcr.io/alpinebits-ch',
    [string]$ImageTag = 'latest',
    [switch]$ExternalPostgres,
    [string]$DbHost,
    [string]$DbPort,
    [string]$DbUser,
    [string]$DbPassword,
    [ValidateSet('yes', 'no')] [string]$Scylla,
    [ValidateSet('yes', 'no')] [string]$Isle,
    [switch]$ExternalStorage,
    [switch]$NonInteractive,
    [switch]$Reconfigure,
    [switch]$SkipDependencies,
    [switch]$NoStart,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ── Paths ────────────────────────────────────────────────────────────────────────────
$ScriptDir    = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot     = Split-Path -Parent $ScriptDir
$EnvFile      = Join-Path $ScriptDir '.env'
$GeneratedDir = Join-Path $ScriptDir 'generated'
$ComposeFile  = Join-Path $ScriptDir 'compose.yaml'
$ProjectName  = 'venta'
$VentaCtl     = Join-Path $ScriptDir 'ventactl.ps1'
$TaskName     = 'VentaStack'

# ── Output helpers ───────────────────────────────────────────────────────────────────
function Write-Log  { param([string]$Message) Write-Host "==> " -ForegroundColor Cyan -NoNewline; Write-Host $Message }
function Write-Ok   { param([string]$Message) Write-Host " ok " -ForegroundColor Green -NoNewline; Write-Host $Message }
function Write-Warn { param([string]$Message) Write-Host "  ! " -ForegroundColor Yellow -NoNewline; Write-Host $Message }
function Write-Step { param([string]$Message) Write-Host ""; Write-Host $Message -ForegroundColor White -BackgroundColor DarkBlue }
function Stop-WithError { param([string]$Message) Write-Host "error: " -ForegroundColor Red -NoNewline; Write-Host $Message; exit 1 }

function Read-Answer {
    param([string]$Prompt, [string]$Default = '')
    if ($NonInteractive) { return $Default }
    $suffix = if ($Default) { " [$Default]" } else { '' }
    $answer = Read-Host -Prompt "? $Prompt$suffix"
    if ([string]::IsNullOrWhiteSpace($answer)) { return $Default }
    return $answer
}

function Read-Secret {
    param([string]$Prompt)
    if ($NonInteractive) { return '' }
    $secure = Read-Host -Prompt "? $Prompt" -AsSecureString
    return [Runtime.InteropServices.Marshal]::PtrToStringAuto(
        [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
}

function Read-YesNo {
    param([string]$Prompt, [string]$Default = 'n')
    $answer = Read-Answer -Prompt "$Prompt (y/n)" -Default $Default
    if ($answer -match '^(y|yes|true)$') { return 'yes' } else { return 'no' }
}

# Takes a value supplied on the command line when there is one, otherwise asks for it.
# (Windows PowerShell 5.1 parses `SomeCommand (if ...)` but then tries to run `if` as a
# command at run time, so an if-expression cannot be passed as an argument directly.)
function Get-Setting {
    param([string]$Preset, [string]$Prompt, [string]$Default = '', [switch]$Secret)
    if (-not [string]::IsNullOrWhiteSpace($Preset)) { return $Preset }
    if ($Secret) { return (Read-Secret -Prompt $Prompt) }
    return (Read-Answer -Prompt $Prompt -Default $Default)
}

# Values are written to a .env that both this script and docker compose parse, so drop
# the characters that would make either interpretation ambiguous.
function Format-EnvValue { param([string]$Value) if ($null -eq $Value) { return '' } return ($Value -replace '["\\$`]', '') }

function New-Secret {
    param([int]$Bytes = 24)
    $buffer = [byte[]]::new($Bytes)
    # RandomNumberGenerator.Fill() is .NET Core only - Windows PowerShell 5.1 runs on
    # .NET Framework, where it silently leaves the buffer zeroed under a non-terminating
    # error and every "generated" secret comes out identical.
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($buffer) } finally { $rng.Dispose() }
    return -join ($buffer | ForEach-Object { $_.ToString('x2') })
}

function ConvertTo-Base64File {
    param([string]$Path)
    return [Convert]::ToBase64String([IO.File]::ReadAllBytes($Path))
}

# Deliberately a simple function using $args: an advanced function would try to bind
# "-d"/"--remove-orphans" as its own parameters and fail. Docker's output goes straight
# to the host and the caller checks $LASTEXITCODE.
function Invoke-Compose {
    & docker compose -p $ProjectName --project-directory $ScriptDir -f $ComposeFile --env-file $EnvFile @args
}

function Test-Administrator {
    $identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# ── Uninstall ────────────────────────────────────────────────────────────────────────
if ($Uninstall) {
    Write-Step ' Uninstalling '
    if (Test-Path $EnvFile) { Invoke-Compose down | Out-Null }
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-Ok 'stack stopped and the startup task removed'
    Write-Warn "Data volumes were kept. Remove them with: docker volume rm (docker volume ls -q -f name=${ProjectName}_)"
    exit 0
}

Write-Host @'
 ┌──────────────────────────────────────────────────────┐
 │        Venta / Echo  ·  self-hosted installer        │
 │       federated chat stack · Windows edition         │
 └──────────────────────────────────────────────────────┘
'@ -ForegroundColor Cyan

if (-not (Test-Administrator)) {
    Stop-WithError 'run this installer from an elevated PowerShell session (Run as Administrator)'
}

# =====================================================================================
# 1. Host dependencies
# =====================================================================================
Write-Step ' 1/9  Host dependencies '

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    if ($SkipDependencies) { Stop-WithError 'docker is not installed' }
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        Write-Log 'installing Docker Desktop via winget'
        winget install --id Docker.DockerDesktop --silent --accept-package-agreements --accept-source-agreements
        Write-Warn 'Docker Desktop was installed. Start it once (and sign in if prompted), then re-run this script.'
        exit 0
    }
    Stop-WithError 'docker is not installed and winget is unavailable - install Docker Desktop manually'
}

& docker compose version *> $null
if ($LASTEXITCODE -ne 0) { Stop-WithError "the 'docker compose' plugin (v2) is required" }

& docker info *> $null
if ($LASTEXITCODE -ne 0) {
    Stop-WithError 'the Docker daemon is not reachable - start Docker Desktop and re-run'
}

# The stack is Linux-only (postgres, scylla, the .NET runtime images). Docker Desktop in
# Windows-container mode silently fails much later, at the first image pull.
$dockerOs = (& docker version --format '{{.Server.Os}}' 2>$null)
if ($dockerOs -and $dockerOs -ne 'linux') {
    Stop-WithError "Docker is in '$dockerOs' container mode - switch to Linux containers and re-run"
}
Write-Ok "docker $(& docker version --format '{{.Server.Version}}' 2>$null) ready (linux containers)"

# openssl is needed for the Ed25519 federation keypair; .NET cannot export Ed25519 in
# PKIX text form, which is exactly the format Federation.Application imports. Docker
# Desktop always ships a Linux container runtime, so a throwaway alpine/openssl container
# stands in when no native openssl is on PATH.
$OpenSslNative = [bool](Get-Command openssl -ErrorAction SilentlyContinue)
if ($OpenSslNative) { Write-Ok 'openssl found on PATH' }
else { Write-Log 'no native openssl - key material will be generated inside a container' }

# =====================================================================================
# 2. Existing configuration
# =====================================================================================
Write-Step ' 2/9  Configuration '

$Config = @{}

function Import-EnvFile {
    param([string]$Path)
    $result = @{}
    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line -match '^\s*#' -or $line -notmatch '=') { continue }
        $key   = $line.Substring(0, $line.IndexOf('=')).Trim()
        $value = $line.Substring($line.IndexOf('=') + 1).Trim()
        if ($value.Length -ge 2 -and $value.StartsWith('"') -and $value.EndsWith('"')) {
            $value = $value.Substring(1, $value.Length - 2)
        }
        if ($key) { $result[$key] = $value }
    }
    return $result
}

function Get-Config {
    param([string]$Key, [string]$Default = '')
    if ($Config.ContainsKey($Key) -and -not [string]::IsNullOrEmpty($Config[$Key])) { return $Config[$Key] }
    return $Default
}

$ReuseEnv = $false
if ((Test-Path $EnvFile) -and -not $Reconfigure) {
    $ReuseEnv = $true
    Write-Log 'found an existing deploy\.env - reusing it (pass -Reconfigure to start over)'
    $Config = Import-EnvFile -Path $EnvFile
}

if (-not $ReuseEnv) {
    $Config['INSTANCE_NAME'] = Format-EnvValue (
        Get-Setting -Preset $InstanceName -Prompt 'Instance display name (shown to federated peers)' -Default 'Venta')

    $Config['INSTANCE_DOMAIN'] = Format-EnvValue (
        Get-Setting -Preset $Domain -Prompt 'Public hostname for the API (blank for a LAN-only install)' -Default '')

    if ($TlsMode) {
        $Config['TLS_MODE'] = $TlsMode
    }
    elseif (-not $Config['INSTANCE_DOMAIN']) {
        $Config['TLS_MODE'] = 'local'
    }
    else {
        Write-Host ''
        Write-Host '  How should TLS be handled?'
        Write-Host "    1) Bundled Caddy with automatic Let's Encrypt certificates (recommended)"
        Write-Host '    2) I already run my own reverse proxy in front of this host'
        Write-Host '    3) No TLS - plain HTTP on the LAN (development only)'
        switch (Read-Answer 'Selection' '1') {
            '2'     { $Config['TLS_MODE'] = 'external-proxy' }
            '3'     { $Config['TLS_MODE'] = 'local' }
            default { $Config['TLS_MODE'] = 'letsencrypt' }
        }
    }

    switch ($Config['TLS_MODE']) {
        { $_ -in 'letsencrypt', 'external-proxy' } {
            if (-not $Config['INSTANCE_DOMAIN']) { Stop-WithError "-Domain is required for TLS mode '$($Config['TLS_MODE'])'" }
            $Config['STORAGE_DOMAIN'] = Format-EnvValue (
                Get-Setting -Preset $StorageDomain -Prompt 'Public hostname for attachments/avatars' `
                            -Default "storage.$($Config['INSTANCE_DOMAIN'])")
            $Config['INSTANCE_URL']       = "https://$($Config['INSTANCE_DOMAIN'])"
            $Config['STORAGE_PUBLIC_URL'] = "https://$($Config['STORAGE_DOMAIN'])"
        }
        'local' {
            # Not localhost: every service resolves INSTANCE_URL from *inside* its own
            # container to fetch Identity's OpenID metadata, and "localhost" there is the
            # container itself.
            $lanIp = (Get-NetIPConfiguration |
                      Where-Object { $_.IPv4DefaultGateway -and $_.NetAdapter.Status -eq 'Up' } |
                      Select-Object -First 1 -ExpandProperty IPv4Address |
                      Select-Object -First 1 -ExpandProperty IPAddress)
            if (-not $lanIp) { $lanIp = '127.0.0.1' }
            $Config['INSTANCE_DOMAIN']    = Format-EnvValue (Read-Answer 'Address other machines reach this host on' $lanIp)
            $Config['STORAGE_DOMAIN']     = $Config['INSTANCE_DOMAIN']
            $Config['INSTANCE_URL']       = "http://$($Config['INSTANCE_DOMAIN']):8080"
            $Config['STORAGE_PUBLIC_URL'] = "http://$($Config['INSTANCE_DOMAIN']):9000"
        }
    }

    $Config['ACME_EMAIL'] = ''
    if ($Config['TLS_MODE'] -eq 'letsencrypt') {
        $Config['ACME_EMAIL'] = Format-EnvValue (
            Get-Setting -Preset $AcmeEmail -Prompt "Contact e-mail for Let's Encrypt" `
                        -Default "admin@$($Config['INSTANCE_DOMAIN'])")
    }

    # --- Database ---
    $Config['USE_EXTERNAL_DB'] = if ($ExternalPostgres) { 'yes' } else { Read-YesNo 'Use an existing external PostgreSQL server?' 'n' }
    if ($Config['USE_EXTERNAL_DB'] -eq 'yes') {
        $Config['DATABASE_HOSTNAME'] = Format-EnvValue (Get-Setting -Preset $DbHost -Prompt 'PostgreSQL host')
        $Config['DATABASE_PORT']     = Format-EnvValue (Get-Setting -Preset $DbPort -Prompt 'PostgreSQL port' -Default '5432')
        $Config['DATABASE_USERNAME'] = Format-EnvValue (Get-Setting -Preset $DbUser -Prompt 'PostgreSQL user (must be allowed to CREATE DATABASE)' -Default 'postgres')
        $Config['DATABASE_PASSWORD'] = Format-EnvValue (Get-Setting -Preset $DbPassword -Prompt 'PostgreSQL password' -Secret)
        if (-not $Config['DATABASE_HOSTNAME']) { Stop-WithError '-DbHost is required with -ExternalPostgres' }
    }
    else {
        $Config['DATABASE_HOSTNAME'] = 'postgres'
        $Config['DATABASE_PORT']     = '5432'
        $Config['DATABASE_USERNAME'] = 'postgres'
        $Config['DATABASE_PASSWORD'] = New-Secret 24
    }

    # --- Message store ---
    $Config['USE_SCYLLA'] = if ($Scylla) { $Scylla } else { Read-YesNo 'Enable the ScyllaDB message store? (needs ~4 GB RAM; Postgres is used otherwise)' 'y' }

    # --- Object storage ---
    $Config['USE_EXTERNAL_STORAGE'] = if ($ExternalStorage) { 'yes' } else { Read-YesNo 'Use external S3-compatible storage instead of the bundled MinIO?' 'n' }
    if ($Config['USE_EXTERNAL_STORAGE'] -eq 'yes') {
        $Config['BUCKET_NAME']             = Format-EnvValue (Read-Answer 'Bucket name' 'echo-chat')
        $Config['ACCESS_KEY_ID']           = Format-EnvValue (Read-Answer 'S3 access key id' '')
        $Config['SECRET_ACCESS_KEY']       = Format-EnvValue (Read-Secret 'S3 secret access key')
        $Config['STORAGE_SERVICE_URL']     = Format-EnvValue (Read-Answer 'S3 endpoint URL' 'https://storage.googleapis.com')
        $Config['STORAGE_PUBLIC_URL']      = Format-EnvValue (Read-Answer 'Public base URL objects are served from' $Config['STORAGE_SERVICE_URL'])
        $Config['STORAGE_USE_SERVICE_URL'] = 'true'
        $Config['STORAGE_REGION']          = Format-EnvValue (Read-Answer 'Region' 'us-east-1')
    }
    else {
        $Config['BUCKET_NAME']             = 'echo-chat'
        $Config['ACCESS_KEY_ID']           = "venta_$(New-Secret 6)"
        $Config['SECRET_ACCESS_KEY']       = New-Secret 24
        $Config['STORAGE_SERVICE_URL']     = 'http://minio:9000'
        $Config['STORAGE_USE_SERVICE_URL'] = 'true'
        $Config['STORAGE_REGION']          = 'us-east-1'
    }

    # --- Optional modules ---
    $Config['ENABLE_ISLE']       = if ($Isle) { $Isle } else { Read-YesNo 'Enable the Isle game-server integration service?' 'n' }
    $Config['ISLE_IP_ADDRESS']   = '10.0.0.0'
    $Config['ISLE_BRIDGE_PORT']  = '8080'
    $Config['ISLE_RCON_PORT']    = '8888'
    $Config['ISLE_RCON_PASSWORD'] = ''
    if ($Config['ENABLE_ISLE'] -eq 'yes') {
        $Config['ISLE_IP_ADDRESS']    = Format-EnvValue (Read-Answer 'Isle dedicated-server address' '10.0.0.0')
        $Config['ISLE_BRIDGE_PORT']   = Format-EnvValue (Read-Answer 'IsleBridge plugin HTTP port' '8080')
        $Config['ISLE_RCON_PORT']     = Format-EnvValue (Read-Answer 'Isle RCON port' '8888')
        $Config['ISLE_RCON_PASSWORD'] = Format-EnvValue (Read-Secret 'Isle RCON password')
    }

    # --- Secrets ---
    $Config['REDIS_PASSWORD']        = New-Secret 24
    $Config['RABBITMQ_USERNAME']     = 'venta'
    $Config['RABBITMQ_PASSWORD']     = New-Secret 24
    $Config['SCYLLA_PASSWORD']       = New-Secret 20
    $Config['IDENTITY_KEY_PASSWORD'] = New-Secret 32
}

# Anything the steps below read must exist, including in a .env written by an older
# installer or edited by hand.
$Defaults = @{
    INSTANCE_NAME = 'Venta'; INSTANCE_DOMAIN = ''; TLS_MODE = 'local'; ACME_EMAIL = ''
    INSTANCE_URL = 'http://127.0.0.1:8080'; STORAGE_DOMAIN = ''; STORAGE_PUBLIC_URL = 'http://127.0.0.1:9000'
    USE_EXTERNAL_DB = 'no'; DATABASE_HOSTNAME = 'postgres'; DATABASE_PORT = '5432'
    DATABASE_USERNAME = 'postgres'; DATABASE_PASSWORD = (New-Secret 24)
    USE_SCYLLA = 'no'; SCYLLA_PASSWORD = (New-Secret 20)
    USE_EXTERNAL_STORAGE = 'no'; BUCKET_NAME = 'echo-chat'
    ACCESS_KEY_ID = "venta_$(New-Secret 6)"; SECRET_ACCESS_KEY = (New-Secret 24)
    STORAGE_SERVICE_URL = 'http://minio:9000'; STORAGE_USE_SERVICE_URL = 'true'; STORAGE_REGION = 'us-east-1'
    REDIS_PASSWORD = (New-Secret 24); RABBITMQ_USERNAME = 'venta'; RABBITMQ_PASSWORD = (New-Secret 24)
    IDENTITY_KEY_PASSWORD = (New-Secret 32); AUTH_REQUIRE_USER_EMAIL_VERIFICATION = 'false'
    ENABLE_ISLE = 'no'; ISLE_IP_ADDRESS = '10.0.0.0'; ISLE_BRIDGE_PORT = '8080'
    ISLE_RCON_PORT = '8888'; ISLE_RCON_PASSWORD = ''
    MICROSOFT_GRAPH_CLIENT_ID = ''; MICROSOFT_GRAPH_CLIENT_SECRET = ''
    CLOUDFLARE_APP_ID = 'mock_app_id'; CLOUDFLARE_API_TOKEN = 'mock_tocken'
    FIREBASE_SERVICE_ACCOUNT_JSON_BASE_64 = ''; GOOGLE_SERVICE_ACCOUNT_JSON_BASE_64 = ''
    APNS_BUNDLE_ID = 'gg.venta.mobile'; APNS_KEY_ID = ''; APNS_TEAM_ID = ''
    APNS_AUTH_KEY_BASE_64 = ''; APNS_USE_SANDBOX = 'true'
    STEAM_WEB_API_KEY = ''; DISCORD_IMPORT_BOT_TOKEN = ''; DISCORD_IMPORT_CLIENT_ID = ''
    SENTRY_URL = ''; PERSONAL_ACCESS_TOKEN = ''
    FEDERATION_PRIVATE_KEY_BASE_64 = ''; FEDERATION_PUBLIC_KEY_BASE_64 = ''; IDENTITY_SIGNING_CERT = ''
}
foreach ($key in $Defaults.Keys) {
    if (-not $Config.ContainsKey($key) -or [string]::IsNullOrEmpty($Config[$key])) { $Config[$key] = $Defaults[$key] }
}

$Config['IMAGE_SOURCE'] = if ($ImageSource) { $ImageSource } else { Get-Config 'IMAGE_SOURCE' 'registry' }
$Config['IMAGE_PREFIX'] = if ($ImagePrefix -ne 'ghcr.io/alpinebits-ch') { $ImagePrefix } else { Get-Config 'IMAGE_PREFIX' $ImagePrefix }
$Config['IMAGE_TAG']    = if ($ImageTag -ne 'latest') { $ImageTag } else { Get-Config 'IMAGE_TAG' 'latest' }

# =====================================================================================
# 3. Cryptographic material
# =====================================================================================
Write-Step ' 3/9  Keys and certificates '

New-Item -ItemType Directory -Force -Path $GeneratedDir | Out-Null

function Invoke-OpenSsl {
    param([string[]]$OpenSslArgs, [string]$WorkDir)
    if ($OpenSslNative) {
        & openssl @OpenSslArgs
        if ($LASTEXITCODE -ne 0) { Stop-WithError "openssl failed: $($OpenSslArgs -join ' ')" }
        return
    }
    # /work is the generated directory; every path passed in is relative to it.
    & docker run --rm -v "${WorkDir}:/work" -w /work alpine/openssl @OpenSslArgs
    if ($LASTEXITCODE -ne 0) { Stop-WithError "containerised openssl failed: $($OpenSslArgs -join ' ')" }
}

# --- Ed25519 federation keypair ------------------------------------------------------
# Federation.Application signs and verifies with NSec, importing both halves as
# KeyBlobFormat.Pkix*Text - i.e. PEM text, base64-encoded once more for transport in the
# environment. Random bytes (what the previous installer produced) are rejected outright
# by Key.Import, so every outbound event would throw on signing.
if (-not $Config['FEDERATION_PRIVATE_KEY_BASE_64'] -or -not $Config['FEDERATION_PUBLIC_KEY_BASE_64']) {
    $privPath = Join-Path $GeneratedDir 'federation-ed25519.key.pem'
    $pubPath  = Join-Path $GeneratedDir 'federation-ed25519.pub.pem'
    if ($OpenSslNative) {
        Invoke-OpenSsl @('genpkey', '-algorithm', 'ed25519', '-out', $privPath) $GeneratedDir
        Invoke-OpenSsl @('pkey', '-in', $privPath, '-pubout', '-out', $pubPath) $GeneratedDir
    }
    else {
        Invoke-OpenSsl @('genpkey', '-algorithm', 'ed25519', '-out', 'federation-ed25519.key.pem') $GeneratedDir
        Invoke-OpenSsl @('pkey', '-in', 'federation-ed25519.key.pem', '-pubout', '-out', 'federation-ed25519.pub.pem') $GeneratedDir
    }
    $Config['FEDERATION_PRIVATE_KEY_BASE_64'] = ConvertTo-Base64File $privPath
    $Config['FEDERATION_PUBLIC_KEY_BASE_64']  = ConvertTo-Base64File $pubPath
    Write-Ok 'generated a new Ed25519 federation identity'
}
else {
    Write-Ok 'kept the existing federation keypair (peers stay valid)'
}

# --- Identity token-signing certificate ----------------------------------------------
# In Production, Identity.Application loads a PKCS#12 bundle from IDENTITY_SIGNING_CERT
# for OpenIddict signing + encryption. Without one it falls back to OpenIddict's
# development certificate, which is regenerated on every container start - so every
# access token issued before a restart stops validating.
if (-not $Config['IDENTITY_SIGNING_CERT']) {
    $pfxPath = Join-Path $GeneratedDir 'identity.p12'
    $subject = "CN=$(if ($Config['INSTANCE_DOMAIN']) { $Config['INSTANCE_DOMAIN'] } else { 'venta' }) Identity Signing"
    $rsa  = [System.Security.Cryptography.RSA]::Create(4096)
    $req  = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
        $subject, $rsa,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $cert = $req.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1), [DateTimeOffset]::UtcNow.AddYears(10))
    [IO.File]::WriteAllBytes($pfxPath, $cert.Export(
        [System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx,
        $Config['IDENTITY_KEY_PASSWORD']))
    $rsa.Dispose(); $cert.Dispose()
    $Config['IDENTITY_SIGNING_CERT'] = ConvertTo-Base64File $pfxPath
    Write-Ok 'generated a persistent OpenIddict signing certificate (10 year validity)'
}
else {
    Write-Ok 'kept the existing Identity signing certificate'
}

# =====================================================================================
# 4. Derived settings
# =====================================================================================
Write-Step ' 4/9  Deployment layout '

$profileList = @()
if ($Config['USE_EXTERNAL_DB']      -ne 'yes')         { $profileList += 'pg-local' }
if ($Config['USE_EXTERNAL_STORAGE'] -ne 'yes')         { $profileList += 'storage-local' }
if ($Config['USE_SCYLLA']           -eq 'yes')         { $profileList += 'scylla' }
if ($Config['ENABLE_ISLE']          -eq 'yes')         { $profileList += 'isle' }
if ($Config['TLS_MODE']             -eq 'letsencrypt') { $profileList += 'caddy' }
$Config['COMPOSE_PROFILES'] = $profileList -join ','
$Config['USE_SCYLLA_DB']    = if ($Config['USE_SCYLLA'] -eq 'yes') { 'true' } else { 'false' }

# Port publishing and in-network name resolution differ per TLS mode - see the same
# switch in install.sh for the reasoning.
switch ($Config['TLS_MODE']) {
    'letsencrypt' {
        $Config['GATEWAY_BIND'] = '127.0.0.1:8080'; $Config['MINIO_BIND'] = '127.0.0.1:9000'
        $Config['HAIRPIN_HOST_ENTRY'] = 'venta-hairpin.invalid:127.0.0.1'
    }
    'external-proxy' {
        $Config['GATEWAY_BIND'] = '127.0.0.1:8080'; $Config['MINIO_BIND'] = '127.0.0.1:9000'
        $Config['HAIRPIN_HOST_ENTRY'] = "$($Config['INSTANCE_DOMAIN']):host-gateway"
    }
    default {
        $Config['GATEWAY_BIND'] = '0.0.0.0:8080'; $Config['MINIO_BIND'] = '0.0.0.0:9000'
        $Config['HAIRPIN_HOST_ENTRY'] = 'venta-hairpin.invalid:127.0.0.1'
    }
}

# =====================================================================================
# 5. Write deploy\.env
# =====================================================================================
Write-Step ' 5/9  Writing deploy\.env '

$stamp = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
$envLines = @"
# =====================================================================================
#  Venta stack configuration - generated by deploy\Install-VentaStack.ps1 on $stamp
#  Contains secrets. Keep it out of version control.
#  Re-run the installer after editing, or apply with: ventactl up
# =====================================================================================

COMPOSE_PROJECT_NAME=$ProjectName
COMPOSE_PROFILES="$($Config['COMPOSE_PROFILES'])"

# -- Identity of this instance --------------------------------------------------------
INSTANCE_NAME="$($Config['INSTANCE_NAME'])"
INSTANCE_DOMAIN="$($Config['INSTANCE_DOMAIN'])"
INSTANCE_URL="$($Config['INSTANCE_URL'])"
INSTANCE_VERSION="1.0.0"
TLS_MODE="$($Config['TLS_MODE'])"
ACME_EMAIL="$($Config['ACME_EMAIL'])"
STORAGE_DOMAIN="$($Config['STORAGE_DOMAIN'])"
ASPNETCORE_ENVIRONMENT="Production"

# -- Images ---------------------------------------------------------------------------
IMAGE_SOURCE="$($Config['IMAGE_SOURCE'])"
IMAGE_PREFIX="$($Config['IMAGE_PREFIX'])"
IMAGE_TAG="$($Config['IMAGE_TAG'])"

# -- Networking -----------------------------------------------------------------------
HTTP_BIND="0.0.0.0:80"
HTTPS_BIND="0.0.0.0:443"
GATEWAY_BIND="$($Config['GATEWAY_BIND'])"
MINIO_BIND="$($Config['MINIO_BIND'])"
MINIO_CONSOLE_BIND="127.0.0.1:9001"
RABBITMQ_MGMT_BIND="127.0.0.1:15672"
HAIRPIN_HOST_ENTRY="$($Config['HAIRPIN_HOST_ENTRY'])"

# -- PostgreSQL -----------------------------------------------------------------------
USE_EXTERNAL_DB="$($Config['USE_EXTERNAL_DB'])"
DATABASE_HOSTNAME="$($Config['DATABASE_HOSTNAME'])"
DATABASE_PORT="$($Config['DATABASE_PORT'])"
DATABASE_USERNAME="$($Config['DATABASE_USERNAME'])"
DATABASE_PASSWORD="$($Config['DATABASE_PASSWORD'])"

# -- Redis ----------------------------------------------------------------------------
REDIS_HOST="redis"
REDIS_PORT="6379"
REDIS_USERNAME=""
REDIS_PASSWORD="$($Config['REDIS_PASSWORD'])"

# -- RabbitMQ -------------------------------------------------------------------------
RABBITMQ_HOST="rabbitmq"
RABBITMQ_PORT="5672"
RABBITMQ_USERNAME="$($Config['RABBITMQ_USERNAME'])"
RABBITMQ_PASSWORD="$($Config['RABBITMQ_PASSWORD'])"

# -- ScyllaDB (message store) ---------------------------------------------------------
USE_SCYLLA="$($Config['USE_SCYLLA'])"
USE_SCYLLA_DB="$($Config['USE_SCYLLA_DB'])"
SCYLLA_HOST="scylladb"
SCYLLA_PORT="9042"
SCYLLA_USERNAME="cassandra"
SCYLLA_PASSWORD="$($Config['SCYLLA_PASSWORD'])"
SCYLLA_SMP="1"

# -- Object storage -------------------------------------------------------------------
USE_EXTERNAL_STORAGE="$($Config['USE_EXTERNAL_STORAGE'])"
BUCKET_NAME="$($Config['BUCKET_NAME'])"
ACCESS_KEY_ID="$($Config['ACCESS_KEY_ID'])"
SECRET_ACCESS_KEY="$($Config['SECRET_ACCESS_KEY'])"
STORAGE_PUBLIC_URL="$($Config['STORAGE_PUBLIC_URL'])"
STORAGE_SERVICE_URL="$($Config['STORAGE_SERVICE_URL'])"
STORAGE_USE_SERVICE_URL="$($Config['STORAGE_USE_SERVICE_URL'])"
STORAGE_REGION="$($Config['STORAGE_REGION'])"

# -- Auth -----------------------------------------------------------------------------
AUTH_REQUIRE_USER_EMAIL_VERIFICATION="$($Config['AUTH_REQUIRE_USER_EMAIL_VERIFICATION'])"
IS_USER_HASH_GENERATION_ENABLED="true"
IDENTITY_KEY_PASSWORD="$($Config['IDENTITY_KEY_PASSWORD'])"
IDENTITY_SIGNING_CERT="$($Config['IDENTITY_SIGNING_CERT'])"
ACCOUNT_DELETION_GRACE_PERIOD_SECONDS="2592000"
ACCOUNT_DELETION_SWEEP_INTERVAL_SECONDS="300"

# -- Federation (Ed25519, PEM text, base64-encoded) -----------------------------------
FEDERATION_PRIVATE_KEY_BASE_64="$($Config['FEDERATION_PRIVATE_KEY_BASE_64'])"
FEDERATION_PUBLIC_KEY_BASE_64="$($Config['FEDERATION_PUBLIC_KEY_BASE_64'])"

# -- Transactional e-mail (Microsoft Graph) -------------------------------------------
# Set both, then flip AUTH_REQUIRE_USER_EMAIL_VERIFICATION to "true" to require
# verified addresses at sign-up.
MICROSOFT_GRAPH_CLIENT_ID="$($Config['MICROSOFT_GRAPH_CLIENT_ID'])"
MICROSOFT_GRAPH_CLIENT_SECRET="$($Config['MICROSOFT_GRAPH_CLIENT_SECRET'])"

# -- Voice / video (Cloudflare Calls SFU) ---------------------------------------------
CLOUDFLARE_APP_ID="$($Config['CLOUDFLARE_APP_ID'])"
CLOUDFLARE_API_TOKEN="$($Config['CLOUDFLARE_API_TOKEN'])"

# -- Push notifications ---------------------------------------------------------------
FIREBASE_SERVICE_ACCOUNT_JSON_BASE_64="$($Config['FIREBASE_SERVICE_ACCOUNT_JSON_BASE_64'])"
GOOGLE_SERVICE_ACCOUNT_JSON_BASE_64="$($Config['GOOGLE_SERVICE_ACCOUNT_JSON_BASE_64'])"
APNS_BUNDLE_ID="$($Config['APNS_BUNDLE_ID'])"
APNS_KEY_ID="$($Config['APNS_KEY_ID'])"
APNS_TEAM_ID="$($Config['APNS_TEAM_ID'])"
APNS_AUTH_KEY_BASE_64="$($Config['APNS_AUTH_KEY_BASE_64'])"
APNS_USE_SANDBOX="$($Config['APNS_USE_SANDBOX'])"

# -- Steam login ----------------------------------------------------------------------
STEAM_PUBLIC_BASE_URL="$($Config['INSTANCE_URL'])"
STEAM_PUBLIC_CALLBACK_PATH="/api/v1/identity/authentication/steam/callback"
STEAM_CLIENT_RETURN_URL="venta://steam-auth"
STEAM_WEB_API_KEY="$($Config['STEAM_WEB_API_KEY'])"

# -- Discord import -------------------------------------------------------------------
DISCORD_IMPORT_BOT_TOKEN="$($Config['DISCORD_IMPORT_BOT_TOKEN'])"
DISCORD_IMPORT_CLIENT_ID="$($Config['DISCORD_IMPORT_CLIENT_ID'])"
DISCORD_IMPORT_PUBLIC_BASE_URL="$($Config['INSTANCE_URL'])"
DISCORD_IMPORT_PUBLIC_CALLBACK_PATH="/api/v1/imports/discord/callback"
DISCORD_IMPORT_CLIENT_RETURN_URL="venta://discord-import"

# -- The Isle integration -------------------------------------------------------------
ENABLE_ISLE="$($Config['ENABLE_ISLE'])"
ISLE_IP_ADDRESS="$($Config['ISLE_IP_ADDRESS'])"
ISLE_BRIDGE_PORT="$($Config['ISLE_BRIDGE_PORT'])"
ISLE_RCON_PORT="$($Config['ISLE_RCON_PORT'])"
ISLE_RCON_PASSWORD="$($Config['ISLE_RCON_PASSWORD'])"

# -- Misc -----------------------------------------------------------------------------
SENTRY_URL="$($Config['SENTRY_URL'])"
PERSONAL_ACCESS_TOKEN="$($Config['PERSONAL_ACCESS_TOKEN'])"
"@

# UTF-8 without BOM: docker compose treats a leading BOM as part of the first key name.
[IO.File]::WriteAllText($EnvFile, ($envLines -replace "`r`n", "`n"), (New-Object Text.UTF8Encoding $false))

# Secrets file: strip inheritance and leave only SYSTEM and the local Administrators group.
$acl = Get-Acl $EnvFile
$acl.SetAccessRuleProtection($true, $false)
foreach ($rule in @($acl.Access)) { $acl.RemoveAccessRule($rule) | Out-Null }
foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
    $account = (New-Object Security.Principal.SecurityIdentifier($sid))
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
        $account, 'FullControl', 'Allow')))
}
Set-Acl -Path $EnvFile -AclObject $acl
Write-Ok "wrote $EnvFile"

# =====================================================================================
# 6. Reverse proxy configuration
# =====================================================================================
Write-Step ' 6/9  Reverse proxy '

$caddyfilePath = Join-Path $GeneratedDir 'Caddyfile'
if ($Config['TLS_MODE'] -eq 'letsencrypt') {
    $caddyfile = @"
# Generated by deploy\Install-VentaStack.ps1 - edited copies are overwritten on re-run.
{
	email $($Config['ACME_EMAIL'])
}

$($Config['INSTANCE_DOMAIN']) {
	encode zstd gzip

	request_body {
		max_size 100MB
	}

	# WebSockets (the /api/v1/ws/hub SignalR hub and the Discord-compatible bot gateway
	# at /api/discord/v10/gateway) are upgraded transparently by reverse_proxy.
	reverse_proxy echo:8080 {
		header_up X-Forwarded-Proto https
		flush_interval -1
	}
}

$($Config['STORAGE_DOMAIN']) {
	encode zstd gzip

	request_body {
		max_size 500MB
	}

	# Attachment URLs are path-style: {STORAGE_PUBLIC_URL}/{bucket}/{key}
	reverse_proxy minio:9000
}
"@
    # LF line endings: Caddy is fine with CRLF, but the file is read inside a Linux container.
    [IO.File]::WriteAllText($caddyfilePath, ($caddyfile -replace "`r`n", "`n"), (New-Object Text.UTF8Encoding $false))
    Write-Ok "wrote $caddyfilePath (Let's Encrypt, HTTP-01 on :80)"

    foreach ($port in 80, 443) {
        if (-not (Get-NetFirewallRule -DisplayName "Venta HTTP $port" -ErrorAction SilentlyContinue)) {
            New-NetFirewallRule -DisplayName "Venta HTTP $port" -Direction Inbound -Protocol TCP `
                -LocalPort $port -Action Allow -Profile Any | Out-Null
        }
    }
    Write-Ok 'opened inbound TCP 80/443 in Windows Firewall'
}
else {
    [IO.File]::WriteAllText($caddyfilePath, '', (New-Object Text.UTF8Encoding $false))
    if ($Config['TLS_MODE'] -eq 'external-proxy') {
        Write-Host ''
        Write-Host '  Point your own reverse proxy at this host:'
        Write-Host ''
        Write-Host "    https://$($Config['INSTANCE_DOMAIN'])   ->  http://127.0.0.1:8080     (must forward WebSocket upgrades)"
        Write-Host "    https://$($Config['STORAGE_DOMAIN'])  ->  http://127.0.0.1:9000"
        Write-Host ''
        Write-Host '  Forward the usual X-Forwarded-For / -Proto / -Host headers, and allow request'
        Write-Host '  bodies of at least 100 MB (500 MB on the storage host).'
    }
    else {
        Write-Warn 'no TLS: federation with other instances requires a public HTTPS endpoint'
        foreach ($port in 8080, 9000) {
            if (-not (Get-NetFirewallRule -DisplayName "Venta LAN $port" -ErrorAction SilentlyContinue)) {
                New-NetFirewallRule -DisplayName "Venta LAN $port" -Direction Inbound -Protocol TCP `
                    -LocalPort $port -Action Allow -Profile Private, Domain | Out-Null
            }
        }
    }
}

# =====================================================================================
# 7. ventactl + startup task
# =====================================================================================
Write-Step ' 7/9  Lifecycle management '

$ventactlBody = @'
<#
    Venta stack control wrapper - generated by deploy\Install-VentaStack.ps1
    Usage: .\ventactl.ps1 <command> [service]

      up | start        start the stack (also run at boot by the VentaStack task)
      stop              stop containers, keep them defined
      down              stop and remove containers (volumes are kept)
      restart [svc]     restart everything or one service
      ps | status       container status
      logs [svc]        follow logs
      update            pull/build the current images and restart
      backup [dir]      copy .env and dump the built-in PostgreSQL
      federation-doc    print this instance's public federation document
      config            render the fully-resolved compose configuration
#>
param(
    [Parameter(Position = 0)][string]$Command = 'help',
    [Parameter(Position = 1, ValueFromRemainingArguments = $true)][string[]]$Rest
)

$ErrorActionPreference = 'Stop'
$DeployDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$EnvFile   = Join-Path $DeployDir '.env'
$Project   = 'venta'

# COMPOSE_PROFILES decides which optional services exist at all, so load the env file for
# every command, not just the ones that read other settings.
$Settings = @{}
foreach ($line in Get-Content -LiteralPath $EnvFile) {
    if ($line -match '^\s*#' -or $line -notmatch '=') { continue }
    $k = $line.Substring(0, $line.IndexOf('=')).Trim()
    $v = $line.Substring($line.IndexOf('=') + 1).Trim()
    if ($v.Length -ge 2 -and $v.StartsWith('"') -and $v.EndsWith('"')) { $v = $v.Substring(1, $v.Length - 2) }
    if ($k) { $Settings[$k] = $v; [Environment]::SetEnvironmentVariable($k, $v) }
}

# Simple function on purpose: an advanced one would try to bind "-d" and "--tail" as its
# own parameters instead of passing them through to docker.
function dc {
    & docker compose -p $Project --project-directory $DeployDir `
        -f (Join-Path $DeployDir 'compose.yaml') --env-file $EnvFile @args
}

switch ($Command) {
    { $_ -in 'up', 'start' } { dc up -d --remove-orphans }
    'stop'                   { dc stop }
    'down'                   { dc down }
    'restart'                { if ($Rest) { dc restart @Rest } else { dc restart } }
    { $_ -in 'ps', 'status' } { dc ps }
    'logs'                   { if ($Rest) { dc logs -f --tail=200 @Rest } else { dc logs -f --tail=200 } }
    'config'                 { dc config }
    'update' {
        if ($Settings['IMAGE_SOURCE'] -eq 'build') {
            Push-Location (Split-Path -Parent $DeployDir)
            try { git pull --ff-only } catch { } finally { Pop-Location }
            dc build --pull
        }
        else { dc pull }
        dc up -d --remove-orphans
    }
    'backup' {
        $out = if ($Rest) { $Rest[0] } else { Join-Path $env:ProgramData 'venta\backups' }
        New-Item -ItemType Directory -Force -Path $out | Out-Null
        $stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')
        Copy-Item $EnvFile (Join-Path $out "env-$stamp.bak")
        if ($Settings['USE_EXTERNAL_DB'] -eq 'yes') {
            Write-Host "external database: dump it yourself against $($Settings['DATABASE_HOSTNAME'])"
        }
        else {
            dc exec -T postgres pg_dumpall -U $Settings['DATABASE_USERNAME'] |
                Set-Content -Encoding utf8 (Join-Path $out "postgres-$stamp.sql")
        }
        Write-Host "backup written to $out"
    }
    'federation-doc' { Invoke-RestMethod "$($Settings['INSTANCE_URL'])/.well-known/federation" | ConvertTo-Json -Depth 5 }
    default { Get-Help $MyInvocation.MyCommand.Path -Detailed }
}
'@
[IO.File]::WriteAllText($VentaCtl, $ventactlBody, (New-Object Text.UTF8Encoding $false))
Write-Ok "installed $VentaCtl"

# Docker Desktop can take a while after logon before the engine accepts connections, and
# a plain "run at startup" action would fire into a dead socket - so the task retries.
$startupScript = Join-Path $ScriptDir 'start-on-boot.ps1'
$startupBody = @"
`$ErrorActionPreference = 'SilentlyContinue'
for (`$i = 0; `$i -lt 60; `$i++) {
    & docker info *> `$null
    if (`$LASTEXITCODE -eq 0) { break }
    Start-Sleep -Seconds 10
}
& '$VentaCtl' up
"@
[IO.File]::WriteAllText($startupScript, $startupBody, (New-Object Text.UTF8Encoding $false))

$action    = New-ScheduledTaskAction -Execute 'powershell.exe' `
    -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$startupScript`""
$trigger   = New-ScheduledTaskTrigger -AtStartup
$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero)

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
    -Principal $principal -Settings $settings -Force | Out-Null
Write-Ok "registered the '$TaskName' scheduled task (starts the stack at boot)"

$dockerService = Get-Service com.docker.service -ErrorAction SilentlyContinue
if ($dockerService -and $dockerService.StartType -eq 'Manual') {
    Write-Warn 'the Docker Desktop service starts manually - set it to Automatic, or the boot task will time out'
}

# =====================================================================================
# 8. Images and boot
# =====================================================================================
Write-Step ' 8/9  Images '

$env:COMPOSE_PROFILES = $Config['COMPOSE_PROFILES']

if ($Config['IMAGE_SOURCE'] -eq 'build') {
    $licence = Join-Path $RepoRoot 'Messaging.Application\Credentials\sixlabors.lic'
    if (-not (Test-Path $licence)) {
        Write-Warn 'Messaging.Application\Credentials\sixlabors.lic is missing - a source build of the'
        Write-Warn 'Messaging service needs a SixLabors ImageSharp license file there.'
    }
    Write-Log 'building images from source (this takes a while)'
    Invoke-Compose build --pull
    if ($LASTEXITCODE -ne 0) { Stop-WithError 'image build failed' }
}
else {
    Write-Log "pulling images from $($Config['IMAGE_PREFIX']) (tag: $($Config['IMAGE_TAG']))"
    Invoke-Compose pull
    if ($LASTEXITCODE -ne 0) {
        Write-Warn 'pull failed (private or unpublished registry?) - falling back to a source build'
        $Config['IMAGE_SOURCE'] = 'build'
        (Get-Content -LiteralPath $EnvFile) -replace '^IMAGE_SOURCE=.*', 'IMAGE_SOURCE="build"' |
            Set-Content -LiteralPath $EnvFile -Encoding utf8
        Invoke-Compose build --pull
        if ($LASTEXITCODE -ne 0) { Stop-WithError 'image build failed' }
    }
}
Write-Ok 'images ready'

Write-Step ' 9/9  Boot '

if ($NoStart) {
    Write-Warn "-NoStart given; run .\ventactl.ps1 up when you are ready"
    exit 0
}

Write-Log 'starting the stack'
Invoke-Compose up -d --remove-orphans
if ($LASTEXITCODE -ne 0) { Stop-WithError 'the stack failed to start - see: .\ventactl.ps1 logs' }

# Waiting on the gateway is enough of a smoke test: it only reports healthy once its own
# Wolverine host, database and Redis connection are up, and it actively health-checks
# every downstream service through YARP.
Write-Log 'waiting for the gateway to report healthy (up to 5 minutes)'
$deadline  = (Get-Date).AddMinutes(5)
$gatewayOk = $false
while ((Get-Date) -lt $deadline) {
    $containerId = (& docker compose -p $ProjectName --project-directory $ScriptDir -f $ComposeFile --env-file $EnvFile ps -q echo 2>$null)
    if ($containerId) {
        $state = (& docker inspect -f '{{.State.Health.Status}}' $containerId 2>$null)
        if ($state -eq 'healthy') { $gatewayOk = $true; break }
    }
    Start-Sleep -Seconds 5
}
if ($gatewayOk) { Write-Ok 'gateway healthy' }
else { Write-Warn 'the gateway did not report healthy in time - check: .\ventactl.ps1 logs echo' }

# =====================================================================================
# Summary
# =====================================================================================
function Test-Endpoint {
    param([string]$Url, [string]$Label)
    try {
        Invoke-WebRequest -Uri $Url -TimeoutSec 15 -UseBasicParsing | Out-Null
        Write-Host ('  {0} {1,-34} {2}' -f [char]0x2713, $Label, $Url) -ForegroundColor Green
    }
    catch {
        Write-Host ('  . {0,-34} {1} (not answering yet)' -f $Label, $Url) -ForegroundColor Yellow
    }
}

Write-Host ''
Write-Host 'Installation summary' -ForegroundColor White
Write-Host "  instance          $($Config['INSTANCE_NAME'])"
Write-Host "  public URL        $($Config['INSTANCE_URL'])"
Write-Host "  attachments       $($Config['STORAGE_PUBLIC_URL'])"
Write-Host "  TLS               $($Config['TLS_MODE'])"
Write-Host "  images            $($Config['IMAGE_SOURCE']) ($($Config['IMAGE_PREFIX']):$($Config['IMAGE_TAG']))"
Write-Host "  profiles          $(if ($Config['COMPOSE_PROFILES']) { $Config['COMPOSE_PROFILES'] } else { '<none>' })"
Write-Host "  configuration     $EnvFile"

Write-Host ''
Write-Host 'Endpoint checks' -ForegroundColor White
Test-Endpoint "$($Config['INSTANCE_URL'])/health"                           'gateway health'
Test-Endpoint "$($Config['INSTANCE_URL'])/.well-known/openid-configuration" 'OpenID discovery'
Test-Endpoint "$($Config['INSTANCE_URL'])/.well-known/federation"           'federation document'

Write-Host ''
Write-Host 'Federating with another instance' -ForegroundColor White
Write-Host "  Your public key and capabilities are published at"
Write-Host "      $($Config['INSTANCE_URL'])/.well-known/federation"
Write-Host "  Start a handshake with a peer (admin token required):"
Write-Host "      POST $($Config['INSTANCE_URL'])/api/v1/admin/federation/initiate  {`"host`":`"https://peer.example.com`"}"
Write-Host "  Then review and approve inbound requests:"
Write-Host "      GET  $($Config['INSTANCE_URL'])/api/v1/admin/federation/instances"
Write-Host "      POST $($Config['INSTANCE_URL'])/api/v1/admin/federation/<id>/approve"
Write-Host ''
Write-Host 'Day to day' -ForegroundColor White
Write-Host '  .\ventactl.ps1 status | logs [service] | restart [service] | update | backup'
Write-Host ''

if ($Config['TLS_MODE'] -eq 'letsencrypt') {
    Write-Host "Certificates are issued and renewed by Caddy; make sure $($Config['INSTANCE_DOMAIN']) and" -ForegroundColor DarkGray
    Write-Host "$($Config['STORAGE_DOMAIN']) resolve to this host's public IP and that ports 80/443 are reachable." -ForegroundColor DarkGray
    Write-Host ''
}

Write-Ok 'done'
