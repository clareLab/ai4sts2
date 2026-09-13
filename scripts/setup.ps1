$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam').SteamPath
$libraries = @($steam)
$vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
if (Test-Path $vdf) {
    $libraries += Select-String -Path $vdf -Pattern '"path"\s+"([^"]+)"' | ForEach-Object { $_.Matches[0].Groups[1].Value.Replace('\', '\') }
}
$game = $libraries | ForEach-Object { Join-Path $_ 'steamapps\common\Slay the Spire 2' } | Where-Object { Test-Path (Join-Path $_ 'data_sts2_windows_x86_64\sts2.dll') } | Select-Object -First 1
if (-not $game) { throw 'Slay the Spire 2 install not found in any Steam library' }
$game = [System.IO.Path]::GetFullPath($game).TrimEnd("\").Replace("\", "/")
$props = "<Project>`n  <PropertyGroup>`n    <Sts2Dir>$game</Sts2Dir>`n  </PropertyGroup>`n</Project>`n"
Set-Content -Path (Join-Path $root 'local.props') -Value $props -NoNewline
Write-Output "Sts2Dir=$game"
