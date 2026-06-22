# BookRescue Architecture

## Scope

BookRescue is a Windows WPF application targeting `net10.0-windows`. `MainWindowViewModel` coordinates the UI and delegates conversions to `BookConversionPipeline`.

## Active conversion flow

1. `BookConversionPipeline` validates PDF/image input and creates an isolated run folder.
2. `PDFtoImage` rasterizes PDFs; `ImageRestorationService` restores pages with OpenCV.
3. `OcrExtractionService` runs Tesseract and returns text with line/word coordinates.
4. `ImageRegionRescueService` preserves visual regions.
5. Standard modes use `PdfEditorialWriter` and `DocxEditorialWriter`.
6. Heavy mode optionally applies `DocumentAiStructureService` (Docling/RapidOCR) and `LocalAiDocumentAssistant` (text-only OpenHermes fallback), then creates diagnostics through `HeavyLayoutAnalyzer`.
7. `LocalLibraryStore` persists completed conversions under `%LOCALAPPDATA%\BookRescue\library.json`.

## Reconstruction modes

- `TextOnly`: organized text without images.
- `TextAndPhotos`: organized text plus rescued images; default mode.
- `VisualElementsOnly`: tables, diagrams, figures, and other visual elements without plain text.
- `PerfectHeavy`: document-AI analysis, damage diagnostics, editorial PDF/DOCX, and JSON report.

## Runtime boundary

The Git repository contains source only. The portable package keeps OCR data, translation models, local AI, Docling, Python, Typst, and LibreOffice under the external `runtime` folder.

## Known architecture debt

- `PerfectPdfReconstructor`, `PerfectDocxReconstructor`, `PdfCloneWriter`, and `DocxCloneWriter` are not connected to the active pipeline.
- Heavy-mode similarity measures a diagnostic canvas, not the rendered final PDF.
- Temporary run data is cleaned only after successful conversion.
