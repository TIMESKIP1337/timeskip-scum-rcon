# Tests scripts\Install-Ue4ss.ps1 with small fake zips (no network, no real UE4SS). Exit code 1 if a check fails.
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot '..\..\scripts\Install-Ue4ss.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) ('ue4ss-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $root | Out-Null
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function New-Zip([string]$path, [hashtable]$entries) {
    $fs = [IO.File]::Create($path)
    $za = New-Object IO.Compression.ZipArchive($fs, [IO.Compression.ZipArchiveMode]::Create)
    foreach ($k in $entries.Keys) {
        $e = $za.CreateEntry($k); $w = New-Object IO.StreamWriter($e.Open()); $w.Write($entries[$k]); $w.Dispose()
    }
    $za.Dispose(); $fs.Dispose()
}
function New-Server([string]$name) {
    $d = Join-Path $root $name; New-Item -ItemType Directory $d | Out-Null
    Set-Content (Join-Path $d 'SCUMServer.exe') 'x'; $d
}
function Run($serverDir, $zip, $sha, [switch]$Force, [switch]$KeepOfficial) {
    $args = @('-NoProfile','-ExecutionPolicy','Bypass','-File',$script,'-ServerBinaries',$serverDir,'-ZipPath',$zip,'-Sha256',$sha)
    if ($Force) { $args += '-Force' }
    if ($KeepOfficial) { $args += '-KeepOfficialSettings' }
    $out = & powershell @args 2>&1 | Out-String
    [pscustomobject]@{ Code = $LASTEXITCODE; Out = $out }
}
$results = @()
function Check($name, $cond) { $script:results += [pscustomobject]@{ Test = $name; Pass = [bool]$cond } }

$good = Join-Path $root 'good.zip'
New-Zip $good @{ 'dwmapi.dll'='proxy-v1'; 'ue4ss/UE4SS.dll'='core-v1'; 'ue4ss/UE4SS-settings.ini'='default'; 'ue4ss/Mods/mods.txt'='Keybinds : 1'; 'ue4ss/Mods/Sample/enabled.txt'='' }
$sha = (Get-FileHash $good -Algorithm SHA256).Hash.ToLower()

# 1 fresh install
$s1 = New-Server 'fresh'
$r = Run $s1 $good $sha
Check 'fresh install exits 0' ($r.Code -eq 0)
Check 'fresh install copies dwmapi + UE4SS.dll + mods' ((Get-Content "$s1\dwmapi.dll") -eq 'proxy-v1' -and (Test-Path "$s1\ue4ss\UE4SS.dll") -and (Test-Path "$s1\ue4ss\Mods\Sample\enabled.txt"))

$recommended = Join-Path $PSScriptRoot '..\..\extras\ue4ss\UE4SS-settings.scum-server.ini'
Check 'fresh install uses the SCUM-tested settings' ((Get-FileHash "$s1\ue4ss\UE4SS-settings.ini").Hash -eq (Get-FileHash $recommended).Hash)
Check 'the release default settings are kept as .official' ((Get-Content "$s1\ue4ss\UE4SS-settings.ini.official") -eq 'default')
Check 'the SCUM-tested settings keep the risky hooks off' ((Select-String -Path $recommended -Pattern '^HookEngineTick\s*=\s*0' -Quiet) -and (Select-String -Path $recommended -Pattern '^HookInitGameState\s*=\s*0' -Quiet))

# 1b -KeepOfficialSettings
$s1b = New-Server 'keepofficial'
$r = Run $s1b $good $sha -KeepOfficial
Check '-KeepOfficialSettings keeps the release settings' ($r.Code -eq 0 -and (Get-Content "$s1b\ue4ss\UE4SS-settings.ini") -eq 'default' -and -not (Test-Path "$s1b\ue4ss\UE4SS-settings.ini.official"))

# 2 second run without -Force
$r = Run $s1 $good $sha
Check 'second run without -Force refuses' ($r.Code -eq 1 -and $r.Out -match 'installed already')

# 3 -Force keeps user data, replaces + backs up program files
$s3 = New-Server 'upgrade'
New-Item -ItemType Directory "$s3\ue4ss\Mods" -Force | Out-Null
Set-Content "$s3\dwmapi.dll" 'proxy-OLD'; Set-Content "$s3\ue4ss\UE4SS.dll" 'core-OLD'
Set-Content "$s3\ue4ss\UE4SS-settings.ini" 'my-settings'; Set-Content "$s3\ue4ss\Mods\mods.txt" 'ScumRcon : 1'
$r = Run $s3 $good $sha -Force
Check '-Force exits 0' ($r.Code -eq 0)
Check '-Force replaces dwmapi' ((Get-Content "$s3\dwmapi.dll") -eq 'proxy-v1')
Check '-Force keeps my settings' ((Get-Content "$s3\ue4ss\UE4SS-settings.ini") -eq 'my-settings')
Check '-Force does not add a .official copy next to my settings' (-not (Test-Path "$s3\ue4ss\UE4SS-settings.ini.official"))
Check '-Force keeps my mods.txt' ((Get-Content "$s3\ue4ss\Mods\mods.txt") -eq 'ScumRcon : 1')
Check '-Force adds missing mod files' (Test-Path "$s3\ue4ss\Mods\Sample\enabled.txt")
$bk = Get-ChildItem $s3 -Directory -Filter 'ue4ss-backup-*' | Select-Object -First 1
Check '-Force backs up the old dwmapi' ($bk -and ((Get-Content (Join-Path $bk.FullName 'dwmapi.dll')) -eq 'proxy-OLD'))

# 4 wrong hash
$s4 = New-Server 'badhash'
$r = Run $s4 $good ('0' * 64)
Check 'wrong hash refuses' ($r.Code -eq 1 -and $r.Out -match 'mismatch')
Check 'wrong hash installs nothing' (-not (Test-Path "$s4\dwmapi.dll"))

# 5 wrong folder
$wrong = Join-Path $root 'nothing'; New-Item -ItemType Directory $wrong | Out-Null
$r = Run $wrong $good $sha
Check 'folder without SCUMServer.exe refuses' ($r.Code -eq 1 -and $r.Out -match 'SCUMServer.exe')

# 6 path traversal in the zip
$evil = Join-Path $root 'evil.zip'
New-Zip $evil @{ 'dwmapi.dll'='p'; 'ue4ss/UE4SS.dll'='c'; '../escaped.txt'='bad' }
$esha = (Get-FileHash $evil -Algorithm SHA256).Hash.ToLower()
$s6 = New-Server 'evil'
$r = Run $s6 $evil $esha
Check 'zip with ".." refuses' ($r.Code -eq 1 -and $r.Out -match 'unsafe path')
Check 'zip with ".." writes nothing' (-not (Test-Path "$s6\dwmapi.dll") -and -not (Test-Path (Join-Path $root 'escaped.txt')))

# 7 not a UE4SS zip
$other = Join-Path $root 'other.zip'
New-Zip $other @{ 'readme.txt'='hi' }
$osha = (Get-FileHash $other -Algorithm SHA256).Hash.ToLower()
$s7 = New-Server 'other'
$r = Run $s7 $other $osha
Check 'zip without dwmapi.dll refuses' ($r.Code -eq 1 -and $r.Out -match 'dwmapi.dll is not in the zip')

# 8 zip with the files one folder deeper
$deep = Join-Path $root 'deep.zip'
New-Zip $deep @{ 'UE4SS_pkg/dwmapi.dll'='proxy-deep'; 'UE4SS_pkg/ue4ss/UE4SS.dll'='core-deep' }
$dsha = (Get-FileHash $deep -Algorithm SHA256).Hash.ToLower()
$s8 = New-Server 'deep'
$r = Run $s8 $deep $dsha
Check 'files one folder deeper are found' ($r.Code -eq 0 -and (Get-Content "$s8\dwmapi.dll") -eq 'proxy-deep')

# temp folder cleaned up
Check 'no temp work folders left behind' (-not (Get-ChildItem ([IO.Path]::GetTempPath()) -Directory -Filter 'ScumRcon-ue4ss-*' -ErrorAction SilentlyContinue))

$results | Format-Table -AutoSize | Out-String
"Passed: $(($results | Where-Object Pass).Count) / $($results.Count)"
Remove-Item $root -Recurse -Force
if ($results | Where-Object { -not $_.Pass }) { exit 1 }
