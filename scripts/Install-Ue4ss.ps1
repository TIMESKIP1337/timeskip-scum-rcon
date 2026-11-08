<#
.SYNOPSIS
    Installs UE4SS from its official GitHub release into a SCUM dedicated server, after checking the file.

.DESCRIPTION
    ScumRcon's game-side mod runs inside UE4SS (https://github.com/UE4SS-RE/RE-UE4SS, MIT licence), which is
    not part of this repository. This script
      1. downloads one pinned release file from the official repository (or uses -ZipPath),
      2. refuses to continue unless its SHA-256 equals the pinned value,
      3. refuses zip entries that would be written outside the target folder,
      4. copies it next to SCUMServer.exe without touching your mods or your UE4SS-settings.ini.

    It never overwrites an existing UE4SS unless you pass -Force (replaced files are backed up first).

.PARAMETER ServerBinaries
    The folder that contains SCUMServer.exe, normally <server>\SCUM\Binaries\Win64.

.PARAMETER ZipPath
    Use an already downloaded zip instead of downloading (the hash is still checked).

.PARAMETER Sha256
    Expected SHA-256 of the zip. The default belongs to the default -Tag/-Asset. If you choose another
    release, take the value from the release page (GitHub shows it next to each file) and pass it here.

.EXAMPLE
    .\scripts\Install-Ue4ss.ps1 -ServerBinaries 'D:\SCUM-Server\SCUM\Binaries\Win64'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$ServerBinaries,
    [string]$Tag    = 'experimental2',
    [string]$Asset  = 'UE4SS_v3.0.1-1217-g4fb9b615.zip',
    [string]$Sha256 = 'fbdacd0fae434e23d53b1181f6b085219d7cbf7e2cc8a13a00303f842c38194f',
    [string]$ZipPath,
    [switch]$Force,
    [switch]$KeepOfficialSettings
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

function Fail([string]$Message) { Write-Host "ERROR: $Message" -ForegroundColor Red; exit 1 }

# ── Target folder ─────────────────────────────────────────────────────────────────────────────
if (-not (Test-Path -LiteralPath $ServerBinaries -PathType Container)) { Fail "Folder not found: $ServerBinaries" }
$target = (Resolve-Path -LiteralPath $ServerBinaries).Path.TrimEnd('\')
if (-not (Test-Path -LiteralPath (Join-Path $target 'SCUMServer.exe'))) {
    Fail "SCUMServer.exe is not in $target. Pass the folder <server>\SCUM\Binaries\Win64."
}

$existing = @('dwmapi.dll', 'ue4ss\UE4SS.dll') | Where-Object { Test-Path -LiteralPath (Join-Path $target $_) }
if ($existing -and -not $Force) {
    Fail ("UE4SS seems to be installed already ($($existing -join ', ')). Nothing was changed. " +
          'Use -Force to replace the UE4SS program files (your mods and settings are kept, replaced files are backed up).')
}

# ── Get the zip and check it ──────────────────────────────────────────────────────────────────
$work = Join-Path ([IO.Path]::GetTempPath()) ('ScumRcon-ue4ss-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null

try {
    if ($ZipPath) {
        if (-not (Test-Path -LiteralPath $ZipPath -PathType Leaf)) { Fail "Zip not found: $ZipPath" }
        $zip = (Resolve-Path -LiteralPath $ZipPath).Path
    }
    else {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $url = "https://github.com/UE4SS-RE/RE-UE4SS/releases/download/$Tag/$Asset"
        $zip = Join-Path $work 'ue4ss.zip'
        Write-Host "Downloading $url"
        Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    }

    $actual = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Sha256.Trim().ToLowerInvariant()) {
        Fail "SHA-256 mismatch. Expected $Sha256 but the file is $actual. Nothing was installed."
    }
    Write-Host "SHA-256 matches the pinned value ($actual)." -ForegroundColor Green

    # ── Look inside before extracting ─────────────────────────────────────────────────────────
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName
            if ($name.StartsWith('/') -or $name.StartsWith('\') -or $name -match '^[A-Za-z]:' -or
                ($name -split '[\\/]') -contains '..') {
                Fail "The zip contains an unsafe path ($name). Nothing was installed."
            }
        }
    }
    finally { $archive.Dispose() }

    $extracted = Join-Path $work 'x'
    Expand-Archive -LiteralPath $zip -DestinationPath $extracted

    $proxy = Get-ChildItem -LiteralPath $extracted -Recurse -File -Filter 'dwmapi.dll' |
             Sort-Object { $_.FullName.Length } | Select-Object -First 1
    if (-not $proxy) { Fail 'dwmapi.dll is not in the zip, so this is not a UE4SS game release. Nothing was installed.' }
    $root = $proxy.DirectoryName
    if (-not (Test-Path -LiteralPath (Join-Path $root 'ue4ss\UE4SS.dll'))) {
        Fail 'ue4ss\UE4SS.dll is not next to dwmapi.dll in the zip. Nothing was installed.'
    }

    # ── Copy ──────────────────────────────────────────────────────────────────────────────────
    $backup = Join-Path $target ('ue4ss-backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    $copied = 0; $kept = 0; $backedUp = 0
    $settingsFresh = $false

    foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File) {
        $relative = $file.FullName.Substring($root.Length).TrimStart('\')
        $destination = Join-Path $target $relative
        $isSettings = ($relative -ieq 'ue4ss\UE4SS-settings.ini')
        $isUserData = ($relative -like 'ue4ss\Mods\*') -or $isSettings

        if ($isSettings -and -not (Test-Path -LiteralPath $destination)) { $settingsFresh = $true }

        if (Test-Path -LiteralPath $destination) {
            if ($isUserData) { $kept++; continue }                 # mods and settings are never replaced
            $saved = Join-Path $backup $relative
            New-Item -ItemType Directory -Force -Path (Split-Path $saved) | Out-Null
            Copy-Item -LiteralPath $destination -Destination $saved
            $backedUp++
        }
        New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
        $copied++
    }

    # A NEW settings file is replaced by the one tested with SCUM: with the release's own defaults (eight
    # extra engine hooks switched on) a SCUM server crashed when the first player joined. An existing
    # settings file of yours is never touched.
    $usedRecommended = $false
    if ($settingsFresh -and -not $KeepOfficialSettings) {
        $recommended = Join-Path $PSScriptRoot '..\extras\ue4ss\UE4SS-settings.scum-server.ini'
        $settingsPath = Join-Path $target 'ue4ss\UE4SS-settings.ini'
        if (Test-Path -LiteralPath $recommended) {
            Copy-Item -LiteralPath $settingsPath -Destination ($settingsPath + '.official') -Force
            Copy-Item -LiteralPath $recommended -Destination $settingsPath -Force
            $usedRecommended = $true
        }
        else {
            Write-Host "WARNING: $recommended not found, the release's default settings were kept." -ForegroundColor Yellow
        }
    }

    Write-Host ''
    Write-Host "Installed UE4SS into $target" -ForegroundColor Green
    Write-Host "  files copied: $copied, kept (your mods/settings): $kept, replaced and backed up: $backedUp"
    if ($backedUp -gt 0) { Write-Host "  backup: $backup" }
    if ($usedRecommended) {
        Write-Host '  settings: the SCUM-tested ue4ss\UE4SS-settings.ini was installed (the release default is saved as UE4SS-settings.ini.official)'
    }
    Write-Host ''
    Write-Host 'Next: copy mods\ScumRcon to ue4ss\Mods\ScumRcon, start the server and look for'
    Write-Host '"[ScumRcon] RCON bridge active" in ue4ss\UE4SS.log.'
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
