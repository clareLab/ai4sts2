$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$dir = Join-Path $root ".local\archive\$stamp"
New-Item -ItemType Directory -Force $dir | Out-Null
python (Join-Path $PSScriptRoot 'monitor.py') --export $dir --status --embed -1 | Out-Null
Copy-Item (Join-Path $root 'metrics\runs.jsonl') (Join-Path $dir 'data\runs.jsonl')
$zip = "$dir.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $dir '*') -DestinationPath $zip
Write-Output "archived $zip"
