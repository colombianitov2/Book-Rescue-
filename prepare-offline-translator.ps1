$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$runtimeRoot = Join-Path $root "runtime"
$translatorExe = Join-Path $runtimeRoot "translator\BookRescueTranslator\BookRescueTranslator.exe"
$argosData = Join-Path $runtimeRoot "argos-data"
$argosCache = Join-Path $runtimeRoot "argos-cache"
$argosConfig = Join-Path $runtimeRoot "argos-config"
$argosPackages = Join-Path $argosData "argos-translate\packages"
$port = 5099

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

Write-Host "Validando traductor offline incluido..."
$existingTranslatorProcessIds = @(
    Get-Process -Name "BookRescueTranslator" -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty Id
)

$process = Start-Process -FilePath $translatorExe `
    -ArgumentList @("--port", "$port") `
    -WindowStyle Hidden `
    -PassThru

$exitCode = 1

try {
    $deadline = (Get-Date).AddMinutes(2)
    do {
        Start-Sleep -Seconds 2
        try {
            $languages = Invoke-RestMethod -Uri "http://localhost:$port/languages" -Method Get -TimeoutSec 5
            if ($languages) {
                $translation = Invoke-RestMethod `
                    -Uri "http://localhost:$port/translate" `
                    -Method Post `
                    -Body @{
                        q = "The old book was rescued locally."
                        source = "en"
                        target = "es"
                        format = "text"
                    } `
                    -TimeoutSec 20

                Write-Host "Traductor offline listo."
                Write-Host "Prueba: $($translation.translatedText)"
                $exitCode = 0
                break
            }
        }
        catch {
            Write-Host "Esperando traductor..."
        }
    } while ((Get-Date) -lt $deadline)

    if ($exitCode -ne 0) {
        throw "No se pudo confirmar el traductor offline en http://localhost:$port"
    }
}
finally {
    if ($process) {
        $process.Refresh()
        if (-not $process.HasExited) {
            $process.Kill($true)
            $process.WaitForExit(5000)
        }
    }

    Get-Process -Name "BookRescueTranslator" -ErrorAction SilentlyContinue |
        Where-Object { $existingTranslatorProcessIds -notcontains $_.Id } |
        Stop-Process -Force
}

exit $exitCode
