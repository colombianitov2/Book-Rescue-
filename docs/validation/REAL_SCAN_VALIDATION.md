# Real Scan Validation

This workflow is for judging reconstruction quality with real scanned or damaged pages. It does not replace the portable smoke matrix; the smoke matrix only confirms that the portable app can run each mode and generate structurally valid outputs.

## Recommended test set

Use small, legally safe samples:

- Clean text page.
- Damaged text page.
- Page with a photo or diagram.
- Table-heavy page.
- Old cover or colored page.

## Modes to run

Run each sample through:

- Extraer solo texto.
- Texto y fotos.
- Solo tablas y gráficos.
- Reconstrucción perfecta pesada.

## What to check

- OCR readability: spelling, line breaks, accents, and whether damaged text remains understandable.
- Layout order: headings, paragraphs, columns, captions, and reading order.
- Photos and diagrams: whether visual regions are preserved and placed coherently.
- Tables: whether rows, columns, and labels survive without being destroyed.
- Heavy mode report: whether the JSON/report exists and contains useful structure diagnostics.
- Final rendered PDF: judge the final output, not only the diagnostic canvas.

## Acceptance criteria

A real scan pass requires:

- Conversion completes without crash.
- TXT is readable enough for manual review.
- Requested PDF/DOCX outputs open structurally and are not blank.
- Important visual content is preserved in visual modes.
- Tables remain understandable in table-heavy samples.
- Heavy mode creates its JSON/report and improves or explains structure handling.
- No new installer, runtime duplicate, or portable file replacement occurs during validation.

## Outputs to save

Save the following under a local ignored validation output folder:

- Input filename and mode used.
- Command or UI settings used.
- TXT/PDF/DOCX/JSON outputs.
- stdout/stderr or app logs.
- Short manual notes with pass/fail and observed defects.

## Manual comparison

Compare the original scan against:

- final rendered PDF;
- DOCX opened in Word-compatible viewer;
- extracted TXT;
- heavy-mode JSON/report when applicable.

Record issues as concrete observations, for example: missing diagram, broken table order, unreadable OCR segment, blank PDF page, or wrong reading order.
