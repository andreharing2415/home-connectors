#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs HomeHub as a Windows service, with OpenBao as its local vault.

.DESCRIPTION
    1. Downloads OpenBao, checks it against the release's checksums.txt.
    2. Publishes HomeHub into -InstallDir and writes appsettings.Local.json.
    3. Locks -DataDir to SYSTEM and Administrators.
    4. Registers the HomeHub service (LocalSystem, delayed auto start, restarts on failure) and starts it.
    5. Grants -ClientUser read access to the client token, so a desktop app on this machine can call the hub.

    Re-running it upgrades in place: the vault data, keys and token in -DataDir are kept.

.EXAMPLE
    .\install-hub.ps1 -CatalogPath "$env:LOCALAPPDATA\MyApp\home.json" -CatalogSection "MyApp:Home"
#>
param(
    [Parameter(Mandatory)] [string] $CatalogPath,
    [string] $CatalogSection = "Home",
    [string] $InstallDir = "$env:ProgramFiles\HomeHub",
    [string] $DataDir = "$env:ProgramData\HomeHub",
    [string] $FfmpegPath = "",
    [string] $OpenBaoVersion = "2.7.1",
    [string] $ClientUser = "$env:USERDOMAIN\$env:USERNAME",
    [string] $ImportConsumptionDb = ""
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$service = "HomeHub"

if (-not (Test-Path $CatalogPath)) { throw "Catálogo não encontrado: $CatalogPath" }

# --- ffmpeg: a WinGet link lives in the user's profile; the service needs the real file.
if (-not $FfmpegPath) {
    $cmd = Get-Command ffmpeg -ErrorAction SilentlyContinue
    if ($cmd) { $FfmpegPath = $cmd.Source }
}
if ($FfmpegPath) {
    $item = Get-Item $FfmpegPath
    if ($item.LinkType -and $item.Target) { $FfmpegPath = @($item.Target)[0] }
}

# --- stop the old service before replacing its files
if (Get-Service $service -ErrorAction SilentlyContinue) {
    Stop-Service $service -Force -ErrorAction SilentlyContinue
    Get-Process bao -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$InstallDir*" } | Stop-Process -Force
}

# --- publish
dotnet publish "$repo\src\HomeHub\HomeHub.csproj" -c Release -o $InstallDir --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falhou." }

# --- OpenBao, verified against the release checksums
$baoDir = Join-Path $InstallDir "openbao"
$bao = Join-Path $baoDir "bao.exe"
$installed = if (Test-Path $bao) { (& $bao version) -join " " } else { "" }
if ($installed -notmatch [regex]::Escape("v$OpenBaoVersion")) {
    $tmp = Join-Path ([IO.Path]::GetTempPath()) "openbao-$OpenBaoVersion"
    New-Item -ItemType Directory -Force $tmp | Out-Null
    $zipName = "openbao_${OpenBaoVersion}_windows_amd64.zip"
    $base = "https://github.com/openbao/openbao/releases/download/v$OpenBaoVersion"
    Invoke-WebRequest "$base/$zipName" -OutFile "$tmp\$zipName"
    Invoke-WebRequest "$base/checksums.txt" -OutFile "$tmp\checksums.txt"

    $expected = (Select-String -Path "$tmp\checksums.txt" -Pattern ([regex]::Escape($zipName) + '$')).Line.Split(" ")[0]
    $actual = (Get-FileHash "$tmp\$zipName" -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not $expected -or $expected -ne $actual) { throw "Checksum do OpenBao não confere ($actual vs $expected)." }

    New-Item -ItemType Directory -Force $baoDir | Out-Null
    Expand-Archive "$tmp\$zipName" -DestinationPath $baoDir -Force
    Remove-Item $tmp -Recurse -Force
}
Write-Host "OpenBao: $((& $bao version) -join ' ')"

# --- machine settings
$local = @{
    Hub = @{
        DataDirectory  = $DataDir
        CatalogPath    = (Resolve-Path $CatalogPath).Path
        CatalogSection = $CatalogSection
        FfmpegPath     = $FfmpegPath
        OpenBao        = @{ Executable = $bao }
    }
}
$local | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $InstallDir "appsettings.Local.json") -Encoding utf8

# --- data folder: SYSTEM and Administrators only
New-Item -ItemType Directory -Force $DataDir | Out-Null
icacls $DataDir /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" | Out-Null

if ($ImportConsumptionDb -and (Test-Path $ImportConsumptionDb) -and -not (Test-Path "$DataDir\consumo.db")) {
    Copy-Item $ImportConsumptionDb "$DataDir\consumo.db"
    Write-Host "Histórico de consumo importado de $ImportConsumptionDb"
}

# --- service
$exe = Join-Path $InstallDir "HomeHub.exe"
if (-not (Get-Service $service -ErrorAction SilentlyContinue)) {
    New-Service -Name $service -BinaryPathName "`"$exe`"" -DisplayName "Home Hub" `
        -Description "Hub da casa: conectores, cofre OpenBao e API local." -StartupType AutomaticDelayedStart | Out-Null
}
sc.exe failure $service reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
Start-Service $service

# --- client token: readable by the owner, nobody else outside SYSTEM/Administrators
$token = Join-Path $DataDir "client-token"
for ($i = 0; $i -lt 60 -and -not (Test-Path $token); $i++) { Start-Sleep -Milliseconds 500 }
if (-not (Test-Path $token)) { throw "O hub não gerou o token. Veja o Visualizador de Eventos (origem HomeHub)." }
icacls $DataDir /grant "${ClientUser}:(X)" | Out-Null
icacls $token /grant "${ClientUser}:(R)" | Out-Null

$headers = @{ Authorization = "Bearer $((Get-Content $token -Raw).Trim())" }
for ($i = 0; $i -lt 40; $i++) {
    try { $health = Invoke-RestMethod http://127.0.0.1:8787/api/health -Headers $headers; break } catch { Start-Sleep -Milliseconds 500 }
}
if (-not $health) { throw "O hub não respondeu em http://127.0.0.1:8787." }
Write-Host ("Hub no ar: {0} dispositivos, cofre aberto = {1}" -f $health.value.devices, $health.value.vaultUnsealed)
Write-Host "Token do cliente: $token"
