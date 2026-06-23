param(
    [string]$PortableRoot = "D:\Proyectos de desarrollo de Software\BookRescue",
    [string]$SourceRoot = "D:\Proyectos de desarrollo de Software\BookRescue_migrado\Extractor de texto\BookRescue",
    [string]$InputPath = "",
    [string]$OutputRoot = ""
)

$ErrorActionPreference = "Stop"

function Resolve-ExistingSourceRoot {
    param([string]$RequestedSourceRoot)

    if (Test-Path -LiteralPath $RequestedSourceRoot) {
        return $RequestedSourceRoot
    }

    $knownMigratedRoot = "D:\Proyectos de desarrollo de Software\BookRescue\_migrado\Extractor de texto\BookRescue"
    if (Test-Path -LiteralPath $knownMigratedRoot) {
        return $knownMigratedRoot
    }

    return $RequestedSourceRoot
}

function Test-PdfSignature {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        if ($stream.Length -lt 5) { return $false }
        $buffer = [byte[]]::new(5)
        [void]$stream.Read($buffer, 0, 5)
        return ([System.Text.Encoding]::ASCII.GetString($buffer) -eq "%PDF-")
    }
    finally {
        $stream.Dispose()
    }
}

function Test-DocxOpenXml {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
        $zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
        try {
            return ($null -ne $zip.GetEntry("word/document.xml"))
        }
        finally {
            $zip.Dispose()
        }
    }
    catch {
        return $false
    }
}

function Test-JsonFile {
    param([string]$Path)

    try {
        Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -ErrorAction Stop | Out-Null
        return $true
    }
    catch {
        return $false
    }
}

function Format-CommandForLog {
    param([string]$Exe, [string[]]$Arguments)

    $quotedArgs = foreach ($arg in $Arguments) {
        if ($arg -match "\s") { '"' + $arg + '"' } else { $arg }
    }

    return '"' + $Exe + '" ' + ($quotedArgs -join " ")
}

function ConvertTo-ProcessArgumentString {
    param([string[]]$Arguments)

    $quotedArgs = foreach ($arg in $Arguments) {
        if ($arg -eq "") {
            '""'
        }
        elseif ($arg -match '[\s"]') {
            '"' + ($arg -replace '"', '\"') + '"'
        }
        else {
            $arg
        }
    }

    return ($quotedArgs -join " ")
}

$SourceRoot = Resolve-ExistingSourceRoot -RequestedSourceRoot $SourceRoot
if ([string]::IsNullOrWhiteSpace($InputPath)) {
    $InputPath = Join-Path $SourceRoot "smoke-test\sample_scan.png"
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $PortableRoot ("verification\portable-mode-matrix-{0}" -f (Get-Date -Format "yyyyMMdd-HHmmss"))
}

$portableExe = Join-Path $PortableRoot "BookRescue.App.exe"
$runtimePath = Join-Path $PortableRoot "runtime"
$installerPath = Join-Path $PortableRoot "installer"

if (-not (Test-Path -LiteralPath $PortableRoot)) { throw "PortableRoot not found: $PortableRoot" }
if (-not (Test-Path -LiteralPath $portableExe)) { throw "Portable executable not found: $portableExe" }
if (-not (Test-Path -LiteralPath $runtimePath)) { throw "Runtime folder not found: $runtimePath" }
if (-not (Test-Path -LiteralPath $InputPath)) { throw "Input file not found: $InputPath" }

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

$runtimeFileCountBefore = (Get-ChildItem -LiteralPath $runtimePath -Recurse -File -ErrorAction SilentlyContinue | Measure-Object).Count
$installerExistedBefore = Test-Path -LiteralPath $installerPath
$portableVersion = (Get-Item -LiteralPath $portableExe).VersionInfo.ProductVersion

$modes = @(
    [pscustomobject]@{ Label = "Extraer solo texto"; Arg = "text-only" },
    [pscustomobject]@{ Label = "Texto y fotos"; Arg = "text-and-photos" },
    [pscustomobject]@{ Label = "Solo tablas y gr$([char]0x00E1)ficos"; Arg = "solo-tablas-y-graficos" },
    [pscustomobject]@{ Label = "Reconstrucci$([char]0x00F3)n perfecta pesada"; Arg = "perfect-heavy" }
)

$results = New-Object System.Collections.Generic.List[object]

foreach ($mode in $modes) {
    $modeOutputRoot = Join-Path $OutputRoot $mode.Arg
    New-Item -ItemType Directory -Force -Path $modeOutputRoot | Out-Null

    $stdoutPath = Join-Path $modeOutputRoot "stdout.log"
    $stderrPath = Join-Path $modeOutputRoot "stderr.log"
    $exitCodePath = Join-Path $modeOutputRoot "exit-code.txt"
    $commandPath = Join-Path $modeOutputRoot "command.txt"

    $arguments = @(
        "--convert", $InputPath,
        "--out", $modeOutputRoot,
        "--ocr", "eng",
        "--lang", "es",
        "--mode", $mode.Arg,
        "--formats", "pdf,docx",
        "--no-ai",
        "--no-translate"
    )

    Set-Content -LiteralPath $commandPath -Encoding UTF8 -Value (Format-CommandForLog -Exe $portableExe -Arguments $arguments)

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $portableExe
    $startInfo.Arguments = ConvertTo-ProcessArgumentString -Arguments $arguments
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true

    $process = [System.Diagnostics.Process]::Start($startInfo)
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()

    Set-Content -LiteralPath $stdoutPath -Encoding UTF8 -Value $stdout
    Set-Content -LiteralPath $stderrPath -Encoding UTF8 -Value $stderr
    Set-Content -LiteralPath $exitCodePath -Encoding ASCII -Value $process.ExitCode

    $generatedFiles = Get-ChildItem -LiteralPath $modeOutputRoot -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notin @("stdout.log", "stderr.log", "exit-code.txt", "command.txt") } |
        Sort-Object FullName

    $pdfFiles = @($generatedFiles | Where-Object { $_.Extension -eq ".pdf" })
    $docxFiles = @($generatedFiles | Where-Object { $_.Extension -eq ".docx" })
    $jsonFiles = @($generatedFiles | Where-Object { $_.Extension -eq ".json" })
    $txtFiles = @($generatedFiles | Where-Object { $_.Extension -eq ".txt" })
    $csvFiles = @($generatedFiles | Where-Object { $_.Extension -eq ".csv" })
    $epubFiles = @($generatedFiles | Where-Object { $_.Extension -eq ".epub" })

    $allGeneratedFilesNonEmpty = ($generatedFiles.Count -gt 0) -and (-not ($generatedFiles | Where-Object { $_.Length -le 0 }))
    $pdfSignaturesValid = ($pdfFiles.Count -eq 0) -or (-not ($pdfFiles | Where-Object { -not (Test-PdfSignature -Path $_.FullName) }))
    $docxPackagesValid = ($docxFiles.Count -eq 0) -or (-not ($docxFiles | Where-Object { -not (Test-DocxOpenXml -Path $_.FullName) }))
    $jsonFilesValid = ($jsonFiles.Count -eq 0) -or (-not ($jsonFiles | Where-Object { -not (Test-JsonFile -Path $_.FullName) }))
    $heavyJsonExists = if ($mode.Arg -eq "perfect-heavy") { $jsonFiles.Count -gt 0 } else { $true }

    $modePassed = (
        $process.ExitCode -eq 0 -and
        $allGeneratedFilesNonEmpty -and
        $pdfSignaturesValid -and
        $docxPackagesValid -and
        $jsonFilesValid -and
        $heavyJsonExists
    )

    $results.Add([pscustomobject]@{
        Mode = $mode.Label
        Arg = $mode.Arg
        Passed = $modePassed
        ExitCode = $process.ExitCode
        OutputFolder = $modeOutputRoot
        Stdout = $stdoutPath
        Stderr = $stderrPath
        TxtCount = $txtFiles.Count
        PdfCount = $pdfFiles.Count
        DocxCount = $docxFiles.Count
        JsonCount = $jsonFiles.Count
        CsvCount = $csvFiles.Count
        EpubCount = $epubFiles.Count
        NonEmptyOutputs = $allGeneratedFilesNonEmpty
        PdfSignaturesValid = $pdfSignaturesValid
        DocxPackagesValid = $docxPackagesValid
        JsonFilesValid = $jsonFilesValid
        HeavyJsonExists = $heavyJsonExists
        Files = @($generatedFiles | ForEach-Object {
            [pscustomobject]@{
                RelativePath = $_.FullName.Substring($modeOutputRoot.Length + 1)
                SizeBytes = $_.Length
            }
        })
    })
}

$runtimeFileCountAfter = (Get-ChildItem -LiteralPath $runtimePath -Recurse -File -ErrorAction SilentlyContinue | Measure-Object).Count
$runtimeUntouched = ($runtimeFileCountBefore -eq $runtimeFileCountAfter)
$installerExistsAfter = Test-Path -LiteralPath $installerPath
$installerCreated = (-not $installerExistedBefore) -and $installerExistsAfter
$allModesPassed = -not ($results | Where-Object { -not $_.Passed })
$overallPassed = $allModesPassed -and $runtimeUntouched -and (-not $installerCreated)

$summaryPath = Join-Path $OutputRoot "mode-matrix-summary.txt"
$summaryLines = New-Object System.Collections.Generic.List[string]
$summaryLines.Add("BookRescue portable smoke matrix")
$summaryLines.Add("PortableRoot: $PortableRoot")
$summaryLines.Add("PortableExe: $portableExe")
$summaryLines.Add("PortableVersion: $portableVersion")
$summaryLines.Add("SourceRoot: $SourceRoot")
$summaryLines.Add("InputPath: $InputPath")
$summaryLines.Add("OutputRoot: $OutputRoot")
$summaryLines.Add("RuntimeFileCountBefore: $runtimeFileCountBefore")
$summaryLines.Add("RuntimeFileCountAfter: $runtimeFileCountAfter")
$summaryLines.Add("RuntimeUntouched: $runtimeUntouched")
$summaryLines.Add("InstallerExistedBefore: $installerExistedBefore")
$summaryLines.Add("InstallerExistsAfter: $installerExistsAfter")
$summaryLines.Add("InstallerCreated: $installerCreated")
$summaryLines.Add("OverallPassed: $overallPassed")
$summaryLines.Add("")

foreach ($result in $results) {
    $summaryLines.Add("MODE: $($result.Mode) [$($result.Arg)]")
    $summaryLines.Add("  pass: $($result.Passed)")
    $summaryLines.Add("  exitCode: $($result.ExitCode)")
    $summaryLines.Add("  outputFolder: $($result.OutputFolder)")
    $summaryLines.Add("  stdout: $($result.Stdout)")
    $summaryLines.Add("  stderr: $($result.Stderr)")
    $summaryLines.Add("  counts: txt=$($result.TxtCount), pdf=$($result.PdfCount), docx=$($result.DocxCount), json=$($result.JsonCount), csv=$($result.CsvCount), epub=$($result.EpubCount)")
    $summaryLines.Add("  checks: nonEmpty=$($result.NonEmptyOutputs), pdfSignatures=$($result.PdfSignaturesValid), docxOpenXml=$($result.DocxPackagesValid), jsonValid=$($result.JsonFilesValid), heavyJson=$($result.HeavyJsonExists)")
    foreach ($file in $result.Files) {
        $summaryLines.Add("  file: $($file.RelativePath) | $($file.SizeBytes) bytes")
    }
    $summaryLines.Add("")
}

Set-Content -LiteralPath $summaryPath -Encoding UTF8 -Value $summaryLines

Write-Host "BookRescue portable smoke matrix"
Write-Host "Output: $OutputRoot"
foreach ($result in $results) {
    $status = if ($result.Passed) { "PASS" } else { "FAIL" }
    Write-Host ("{0}: {1} (exit {2}, txt={3}, pdf={4}, docx={5}, json={6})" -f $status, $result.Mode, $result.ExitCode, $result.TxtCount, $result.PdfCount, $result.DocxCount, $result.JsonCount)
}
Write-Host "Runtime untouched: $runtimeUntouched"
Write-Host "Installer created: $installerCreated"
Write-Host "Summary: $summaryPath"

if (-not $overallPassed) {
    exit 1
}

exit 0
