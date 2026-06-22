# BookRescue Test Plan

## Baseline checks

- `dotnet build .\BookRescue.App\BookRescue.App.csproj -c Release -v minimal`
- Confirm Git remains clean except for intentional source changes.
- Confirm the portable executable is not replaced during development tests.

## Mode regression matrix

For each mode, verify output suffixes and selected TXT/PDF/DOCX/ePub/CSV files:

- `TextOnly`: no rescued images in final documents.
- `TextAndPhotos`: text and relevant images are present.
- `VisualElementsOnly`: no plain body text; visual manifest and selected formats are generated.
- `PerfectHeavy`: PDF/DOCX plus JSON report and required diagnostics.

## Heavy-mode acceptance

- Use approved real and synthetic samples.
- Record OCR title/subtitle, region counts, warnings, and final rendered PDF comparison.
- Verify diagram lines are preserved and damage does not erase technical content.
- Treat diagnostic-canvas similarity as supplemental, not final-output acceptance.

## Artifact policy

Save full logs and generated evidence under `verification`. Report only command, result, warning/error counts, key metrics, and exact output paths.
