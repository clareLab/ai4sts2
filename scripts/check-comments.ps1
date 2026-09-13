$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$files = Get-ChildItem -Path (Join-Path $root 'src'), (Join-Path $root 'tests') -Recurse -Include *.cs -File | Where-Object { $_.FullName -notmatch '(\\|/)(bin|obj)(\\|/)' }
$hits = @()
foreach ($f in $files) {
    $n = 0
    foreach ($line in Get-Content $f.FullName) {
        $n++
        if ($line -match '(^|[^:"])//' -or $line -match '/\*') { $hits += "$($f.FullName):$n`: $($line.Trim())" }
    }
}
if ($hits.Count -gt 0) { $hits | ForEach-Object { Write-Output $_ }; throw "$($hits.Count) comment(s) found" }
Write-Output "no comments in $($files.Count) files"
