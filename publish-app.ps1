$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "BookRescue.App\BookRescue.App.csproj"
$publish = Join-Path $root "installer\BookRescue"
$runtimeSource = Join-Path $root "runtime"
$runtimeDest = Join-Path $publish "runtime"
$requiredRuntimeFolders = @("tessdata", "translator", "argos-data", "ai", "typst", "python312", "document-ai-venv", "document-ai-models", "libreoffice")

if (-not (Test-Path -LiteralPath $runtimeSource)) {
    $current = Get-Item -LiteralPath $root
    while ($null -ne $current) {
        $candidate = Join-Path $current.FullName "runtime"
        if ((Test-Path -LiteralPath $candidate) -and (Test-Path -LiteralPath (Join-Path $candidate "tessdata"))) {
            $runtimeSource = $candidate
            break
        }

        $current = $current.Parent
    }
}

Set-Location -LiteralPath $root

dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -o $publish

New-Item -ItemType Directory -Force -Path $runtimeDest | Out-Null

$libreOfficeRuntime = Join-Path $runtimeSource "libreoffice"
$installedLibreOffice = Join-Path $env:ProgramFiles "LibreOffice"
if (-not (Test-Path -LiteralPath $libreOfficeRuntime) -and (Test-Path -LiteralPath $installedLibreOffice)) {
    Write-Host "Copiando LibreOffice al runtime offline..."
    Copy-Item -LiteralPath $installedLibreOffice -Destination $libreOfficeRuntime -Recurse -Force
}

foreach ($folder in $requiredRuntimeFolders) {
    $source = Join-Path $runtimeSource $folder
    $destination = Join-Path $runtimeDest $folder

    if (-not (Test-Path -LiteralPath $source)) {
        throw "Falta runtime offline requerido: $source"
    }

    if (Test-Path -LiteralPath $destination) {
        $runtimeDestFull = [System.IO.Path]::GetFullPath($runtimeDest)
        $destinationFull = [System.IO.Path]::GetFullPath($destination)
        if (-not $destinationFull.StartsWith($runtimeDestFull, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Ruta de destino insegura: $destinationFull"
        }

        Remove-Item -LiteralPath $destination -Recurse -Force
    }

    Copy-Item -LiteralPath $source -Destination $destination -Recurse -Force
}

Write-Host "BookRescue publicado con dependencias offline en: $publish"
