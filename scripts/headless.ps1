param(
    [string]$Instance = 'dev',
    [ValidateSet('prepare', 'smoke', 'start', 'request', 'stop')][string]$Action = 'smoke',
    [int]$TimeoutSec = 180,
    [string]$WaitFor = 'ai4sts2 initialized',
    [string]$Op = 'ping',
    [string]$Payload = ''
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$props = Get-Content (Join-Path $repo 'local.props') -Raw
if ($props -notmatch '<Sts2Dir>([^<]+)</Sts2Dir>') { throw 'local.props has no Sts2Dir; run scripts/setup.ps1' }
$gameDir = $Matches[1]
$volume = [System.IO.Path]::GetPathRoot($gameDir)
$root = Join-Path $volume "ai4sts2\headless\$Instance"
$game = Join-Path $root 'game'
$user = Join-Path $root 'user'
$roaming = Join-Path $user 'Roaming'
$local = Join-Path $user 'Local'
$logPath = Join-Path $root 'game.log'
$pidPath = Join-Path $root 'pid'
$harness = Join-Path $roaming 'SlayTheSpire2\ai4sts2\harness'

function Link-Tree([string]$src, [string]$dst) {
    New-Item -ItemType Directory -Force $dst | Out-Null
    foreach ($f in Get-ChildItem $src -File) {
        $target = Join-Path $dst $f.Name
        if (-not (Test-Path $target)) { New-Item -ItemType HardLink -Path $target -Target $f.FullName | Out-Null }
    }
    foreach ($d in Get-ChildItem $src -Directory) { Link-Tree $d.FullName (Join-Path $dst $d.Name) }
}

function Prepare {
    New-Item -ItemType Directory -Force $game, $roaming, $local | Out-Null
    foreach ($f in Get-ChildItem $gameDir -File) {
        $target = Join-Path $game $f.Name
        if (-not (Test-Path $target)) { New-Item -ItemType HardLink -Path $target -Target $f.FullName | Out-Null }
    }
    foreach ($d in 'data_sts2_windows_x86_64', 'controller_config') { Link-Tree (Join-Path $gameDir $d) (Join-Path $game $d) }
    $mods = Join-Path $game 'mods'
    if (Test-Path $mods) { Remove-Item $mods -Recurse -Force }
    New-Item -ItemType Directory -Force $mods | Out-Null
    $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam').SteamPath
    $workshop = @((Join-Path $volume 'SteamLibrary\steamapps\workshop\content\2868840\3747602295'), (Join-Path $steam 'steamapps\workshop\content\2868840\3747602295')) | Where-Object { Test-Path (Join-Path $_ 'mod_manifest.json') } | Select-Object -First 1
    if (-not $workshop) { throw 'RitsuLib workshop item 3747602295 not found' }
    Copy-Item $workshop (Join-Path $mods 'STS2-RitsuLib') -Recurse
    $modBuild = Join-Path $PSScriptRoot '..\src\Ai4Sts2\.godot\mono\temp\bin\Release\ai4sts2.dll'
    if (-not (Test-Path $modBuild)) { throw "mod not built: $modBuild" }
    New-Item -ItemType Directory -Force (Join-Path $mods 'ai4sts2') | Out-Null
    Copy-Item $modBuild (Join-Path $mods 'ai4sts2')
    Copy-Item (Join-Path $PSScriptRoot '..\src\Ai4Sts2\ai4sts2.json') (Join-Path $mods 'ai4sts2')
    $profile = Join-Path $roaming 'SlayTheSpire2\default\1'
    New-Item -ItemType Directory -Force $profile | Out-Null
    $source = Get-ChildItem (Join-Path $env:APPDATA 'SlayTheSpire2\steam') -Directory | ForEach-Object { Join-Path $_.FullName 'settings.save' } | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $source) { throw 'no settings.save found under the interactive profile' }
    $settings = Get-Content $source -Raw | ConvertFrom-Json
    $settings.mod_settings = [ordered]@{ mods_enabled = $true }
    foreach ($k in 'volume_master', 'volume_bgm', 'volume_sfx', 'volume_ambience') { if ($null -ne $settings.$k) { $settings.$k = 0 } }
    $settings | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $profile 'settings.save') -Encoding UTF8
    Write-Output "prepared $root"
}

function Stop-Instance {
    if (Test-Path $pidPath) {
        $p = Get-Process -Id ([int](Get-Content $pidPath)) -ErrorAction SilentlyContinue
        if ($p -and $p.Path -eq (Join-Path $game 'SlayTheSpire2.exe')) { $p.Kill(); $p.WaitForExit(10000) | Out-Null }
        Remove-Item $pidPath -Force
    }
}

function Launch {
    Stop-Instance
    if (Test-Path $logPath) { Remove-Item $logPath -Force }
    if (Test-Path $harness) { Remove-Item $harness -Recurse -Force }
    $p = Start-Process -FilePath (Join-Path $game 'SlayTheSpire2.exe') -ArgumentList @('--headless', '--disable-vsync', '--max-fps', '0', '--force-steam=off', '--log-file', "`"$logPath`"") -WorkingDirectory $game -Environment @{ APPDATA = $roaming; LOCALAPPDATA = $local; AI4STS2_HARNESS = '1'; AI4STS2_WORKBENCH = $(if ($Instance.StartsWith('wb')) { '1' } else { '0' }) } -WindowStyle Hidden -PassThru
    Set-Content $pidPath $p.Id
    return $p
}

function Wait-Marker([System.Diagnostics.Process]$p, [string]$marker) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 1
        if ($p.HasExited) { return $false }
        if ((Test-Path $logPath) -and (Select-String -Path $logPath -Pattern ([regex]::Escape($marker)) -Quiet)) { return $true }
    }
    return $false
}

function Smoke {
    $p = Launch
    $found = Wait-Marker $p $WaitFor
    $exited = $p.HasExited
    Stop-Instance
    if (Test-Path $logPath) { Select-String -Path $logPath -Pattern 'ai4sts2|RitsuLib|ModManager|Mod ' | Select-Object -Last 25 | ForEach-Object { $_.Line } }
    if (-not $found) { throw "marker '$WaitFor' not found (exited=$exited)" }
    Write-Output "SMOKE_OK"
}

function Start-Headless {
    $p = Launch
    if (-not (Wait-Marker $p $WaitFor)) { Stop-Instance; throw "marker '$WaitFor' not found" }
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while (-not (Test-Path (Join-Path $harness 'ready')) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
    if (-not (Test-Path (Join-Path $harness 'ready'))) { Stop-Instance; throw 'harness not ready' }
    Write-Output "STARTED pid=$($p.Id)"
}

function Send-Request {
    if (-not (Test-Path (Join-Path $harness 'ready'))) { throw 'instance not running; use -Action start' }
    $id = [guid]::NewGuid().ToString('N')
    $argsElement = if ([string]::IsNullOrWhiteSpace($Payload)) { $null } else { $Payload | ConvertFrom-Json }
    $body = @{ id = $id; op = $Op; args = $argsElement } | ConvertTo-Json -Depth 20 -Compress
    $resultPath = Join-Path $harness "result-$id.json"
    $tmp = Join-Path $harness 'request.json.tmp'
    Set-Content $tmp $body -Encoding UTF8 -NoNewline
    Move-Item $tmp (Join-Path $harness 'request.json') -Force
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 200
        if (Test-Path $resultPath) {
            $raw = Get-Content $resultPath -Raw
            $result = $raw | ConvertFrom-Json
            if ($result.id -eq $id) {
                Remove-Item $resultPath -Force -ErrorAction SilentlyContinue
                Write-Output $raw
                if (-not $result.ok) { throw "request failed: $($result.error)" }
                return
            }
        }
    }
    throw "request timed out after $TimeoutSec s"
}

switch ($Action) {
    'prepare' { Prepare }
    'stop' { Stop-Instance }
    'smoke' { Prepare; Smoke }
    'start' { Prepare; Start-Headless }
    'request' { Send-Request }
}