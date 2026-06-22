# BookRescue Roadmap

## Phase 1: reproducible baseline

- Keep source, portable package, and verification artifacts clearly separated.
- Synchronize the portable executable with an approved commit.
- Add CI build validation without packaging the offline runtime.
- Record dependency and model licenses/checksums.

## Phase 2: regression safety

- Add automated tests for mode selection, naming, library persistence, and output manifests.
- Create a small approved corpus covering text, photos, tables, diagrams, and damaged covers.
- Compare rendered final outputs, not only internal diagnostic canvases.

## Phase 3: heavy-mode stabilization

- Decide whether heavy mode targets an editable editorial reconstruction or a visual facsimile.
- Make document-AI fallbacks explicit in reports and UI status.
- Protect technical content before tuning crack, stain, and noise detection.

## Phase 4: isolated experiments

- Benchmark Surya and PaddleOCR/PP-Structure as alternative layout providers.
- Compare Marker, deepdoctection, and OCRmyPDF without coupling them to the production pipeline.
- Integrate only after measurable improvement on the approved corpus.
