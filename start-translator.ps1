$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$runtimeRoot = Join-Path $root "runtime"
$translatorExe = Join-Path $runtimeRoot "translator\BookRescueTranslator\BookRescueTranslator.exe"
$argosData = Join-Path $runtimeRoot "argos-data"
$argosCache = Join-Path $runtimeRoot "argos-cache"
$argosConfig = Join-Path $runtimeRoot "argos-config"
$argosPackages = Join-Path $argosData "argos-translate\packages"
$port = 5000

if (-not (Test-Path -LiteralPath $translatorExe)) {
    throw "No se encontro el traductor congelado: $translatorExe"
}

if (-not (Test-Path -LiteralPath $argosPackages)) {
    throw "No se encontraron modelos Argos offline: $argosPackages"
}

New-Item -ItemType Directory -Force -Path $argosCache, $argosConfig | Out-Null

$env:PYTHONUTF8 = "1"
$env:PYTHONIOENCODING = "utf-8"
$env:XDG_DATA_HOME = $argosData
$env:XDG_CACHE_HOME = $argosCache
$env:XDG_CONFIG_HOME = $argosConfig
$env:ARGOS_PACKAGES_DIR = $argosPackages

Start-Process -FilePath $translatorExe -ArgumentList @("--port", "$port") -WindowStyle Hidden
Write-Host "Traductor local iniciado en http://localhost:$port"
