[CmdletBinding()]
param(
    [switch]$Apply
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$targets = @(
    "BookRescue.App\bin",
    "BookRescue.App\obj",
    "_tmpWriterTest",
    "ai-process-cleanup-test",
    "table-format-lab",
    "typst-smoke-test"
)

$rootFull = [System.IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
$totalBytes = 0L

foreach ($relativePath in $targets) {
    $target = [System.IO.Path]::GetFullPath((Join-Path $root $relativePath))
    if (-not $target.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe cleanup path: $target"
    }

    $gitPath = $relativePath.Replace('\', '/')
    $trackedFiles = @(git -C $root ls-files -- $gitPath)
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to verify tracked files for: $gitPath"
    }
    if ($trackedFiles.Count -gt 0) {
        throw "Cleanup target contains tracked files: $gitPath"
    }

    if (-not (Test-Path -LiteralPath $target)) {
        continue
    }

    $bytes = (Get-ChildItem -LiteralPath $target -Recurse -File -Force -ErrorAction SilentlyContinue |
        Measure-Object -Property Length -Sum).Sum
    $totalBytes += [long]($bytes ?? 0)

    if ($Apply) {
        Remove-Item -LiteralPath $target -Recurse -Force
        Write-Host "Removed: $target"
    }
    else {
        Write-Host "Would remove: $target"
    }
}

$mode = if ($Apply) { "APPLIED" } else { "DRY RUN" }
Write-Host "$mode - candidate bytes: $totalBytes"
Write-Host "Protected by policy: runtime, installer, smoke-test, verification, source files, and portable package."
