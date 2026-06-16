using System.Text;
using BookRescue.App.Models;
using OpenCvSharp;
using PDFtoImage;

namespace BookRescue.App.Services;

public sealed class BookConversionPipeline
{
    private readonly TessdataBootstrapper _tessdata;
    private readonly ImageRestorationService _imageRestoration;
    private readonly OcrExtractionService _ocr;
    private readonly HeadingOcrRefiner _headingOcrRefiner;
    private readonly LanguageDetectionService _languageDetection;
    private readonly LibreTranslateService _translation;
    private readonly PdfCloneWriter _pdfWriter;
    private readonly DocxCloneWriter _docxWriter;
    private readonly PdfEditorialWriter _pdfEditorialWriter = new();
    private readonly DocxEditorialWriter _docxEditorialWriter = new();
    private readonly PerfectHeavyReconstructionService _perfectHeavy = new();
    private readonly EpubCloneWriter _epubWriter;
    private readonly CsvOutputWriter _csvWriter;
    private readonly ImageRegionRescueService _imageRegionRescue;
    private readonly LocalAiDocumentAssistant _localAi;
    private readonly DocumentAiStructureService _documentAi;
    private readonly LibreOfficeDocumentService _libreOffice = new();

    public BookConversionPipeline(
        TessdataBootstrapper tessdata,
        ImageRestorationService imageRestoration,
        OcrExtractionService ocr,
        LanguageDetectionService languageDetection,
        LibreTranslateService translation,
        PdfCloneWriter pdfWriter,
        DocxCloneWriter docxWriter,
        EpubCloneWriter epubWriter,
        CsvOutputWriter csvWriter,
        ImageRegionRescueService imageRegionRescue,
        LocalAiDocumentAssistant localAi,
        DocumentAiStructureService documentAi)
    {
        _tessdata = tessdata;
        _imageRestoration = imageRestoration;
        _ocr = ocr;
        _headingOcrRefiner = new HeadingOcrRefiner(ocr);
        _languageDetection = languageDetection;
        _translation = translation;
        _pdfWriter = pdfWriter;
        _docxWriter = docxWriter;
        _epubWriter = epubWriter;
        _csvWriter = csvWriter;
        _imageRegionRescue = imageRegionRescue;
        _localAi = localAi;
        _documentAi = documentAi;
    }

    public async Task<BookConversionResult> ConvertAsync(
        string inputPath,
        string outputRoot,
        string ocrLanguages,
        string outputLanguage,
        bool enableTranslation,
        bool useLocalAiAssistance,
        OutputProfileOptions outputProfiles,
        string? translationEndpoint,
        string? translationApiKey,
        IProgress<ConversionProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("El archivo de entrada no existe.", inputPath);
        }

        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            throw new InvalidOperationException("Debes indicar una carpeta destino.");
        }

        outputLanguage = NormalizeLanguage(outputLanguage, "es");
        if (!outputProfiles.HasAnySelected)
        {
            throw new InvalidOperationException("Elige al menos un formato de salida.");
        }

        var reconstructionMode = outputProfiles.ReconstructionMode;
        var isTextOnly = reconstructionMode == OutputReconstructionMode.TextOnly;
        var isVisualElementsOnly = reconstructionMode == OutputReconstructionMode.VisualElementsOnly;
        var isPerfectHeavy = reconstructionMode == OutputReconstructionMode.PerfectHeavy;
        var allowDocumentAi = useLocalAiAssistance && isPerfectHeavy;
        var allowLocalTextAi = useLocalAiAssistance && isPerfectHeavy;
        var aiWillRun = (allowDocumentAi && _documentAi.IsAvailable) || (allowLocalTextAi && _localAi.IsAvailable);
        var willCreatePrintView = outputProfiles.Pdf && outputProfiles.Word && _libreOffice.IsAvailable && !isPerfectHeavy;
        var selectedOutputCount = CountSelectedOutputs(outputProfiles) + (isPerfectHeavy ? 1 : 0) + (willCreatePrintView ? 1 : 0);
        const double prepareWeight = 4d;
        const double extractWeight = 13d;
        const double detectWeight = 2d;
        const double outputWeight = 8d;
        var aiWeight = aiWillRun ? 30d : 0d;
        var translationWeight = enableTranslation ? 10d : 0d;
        var pageProcessingWeight = Math.Max(35d, 100d - prepareWeight - extractWeight - aiWeight - translationWeight - detectWeight - outputWeight);
        var progressScale = 100d / (prepareWeight + extractWeight + pageProcessingWeight + aiWeight + detectWeight + translationWeight + outputWeight);

        var prepareEnd = ScaleProgress(prepareWeight, progressScale);
        var extractStart = prepareEnd;
        var extractEnd = ScaleProgress(prepareWeight + extractWeight, progressScale);
        var pageStart = extractEnd;
        var pageEnd = ScaleProgress(prepareWeight + extractWeight + pageProcessingWeight, progressScale);
        var aiStart = pageEnd;
        var aiEnd = ScaleProgress(prepareWeight + extractWeight + pageProcessingWeight + aiWeight, progressScale);
        var detectStart = aiEnd;
        var detectEnd = ScaleProgress(prepareWeight + extractWeight + pageProcessingWeight + aiWeight + detectWeight, progressScale);
        var translationStart = detectEnd;
        var translationEnd = ScaleProgress(prepareWeight + extractWeight + pageProcessingWeight + aiWeight + detectWeight + translationWeight, progressScale);
        var outputStart = translationEnd;

        progress?.Report(new ConversionProgressUpdate(1, "Preparando rescate..."));
        await _tessdata.EnsureLanguagePacksAsync(ocrLanguages, cancellationToken: cancellationToken);

        Directory.CreateDirectory(outputRoot);
        var runFolder = CreateRunFolder(inputPath, outputRoot);
        var internalFolder = Path.Combine(runFolder, "_procesamiento_interno");
        var sourcePagesFolder = Path.Combine(internalFolder, "source-pages");
        var restoredPagesFolder = Path.Combine(internalFolder, "restored-pages");
        var rescuedImagesFolder = Path.Combine(runFolder, "imagenes_rescatadas");
        Directory.CreateDirectory(internalFolder);
        Directory.CreateDirectory(sourcePagesFolder);
        Directory.CreateDirectory(restoredPagesFolder);
        Directory.CreateDirectory(rescuedImagesFolder);
        HideDirectory(internalFolder);

        progress?.Report(new ConversionProgressUpdate(prepareEnd, "Extrayendo páginas del libro..."));
        var pages = await ExtractPagesAsync(inputPath, sourcePagesFolder, outputProfiles.MaximumQuality, extractStart, extractEnd - extractStart, progress, cancellationToken);
        if (pages.Count == 0)
        {
            throw new InvalidOperationException("No se encontraron páginas para procesar.");
        }

        var pageParallelism = SelectPageProcessingParallelism(pages.Count, isPerfectHeavy);
        progress?.Report(new ConversionProgressUpdate(extractEnd, $"{GetModeProgressName(reconstructionMode)}. Reconstruyendo con {pageParallelism} proceso(s)..."));
        var ocrPageArray = new OcrPageResult[pages.Count];
        var finalizedPageArray = new BookPageInfo[pages.Count];
        var rescuedImagesByPage = new IReadOnlyList<RescuedImageInfo>[pages.Count];
        var perPageWeight = (pageEnd - pageStart) / Math.Max(1, pages.Count);
        var completedPages = 0;

        await Parallel.ForEachAsync(
            Enumerable.Range(0, pages.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = pageParallelism,
                CancellationToken = cancellationToken
            },
            (i, token) =>
            {
                token.ThrowIfCancellationRequested();
                var basePage = pages[i];
                var pageNumber = i + 1;

                progress?.Report(new ConversionProgressUpdate(
                    pageStart + (Volatile.Read(ref completedPages) * perPageWeight),
                    $"Reconstruyendo página {pageNumber}/{pages.Count}..."));

                var restoredPath = Path.Combine(restoredPagesFolder, $"page_{pageNumber:D4}.png");
                var (pixelWidth, pixelHeight) = _imageRestoration.Restore(basePage.OriginalImagePath, restoredPath);

                var finalPage = new BookPageInfo
                {
                    OriginalImagePath = basePage.OriginalImagePath,
                    RestoredImagePath = restoredPath,
                    WidthPoints = basePage.WidthPoints,
                    HeightPoints = basePage.HeightPoints,
                    PixelWidth = pixelWidth,
                    PixelHeight = pixelHeight,
                    PageIndex = i
                };

                var ocrResult = _ocr.Extract(restoredPath, ocrLanguages);
                var pageImages = isTextOnly
                    ? Array.Empty<RescuedImageInfo>()
                    : _imageRegionRescue.RescuePageImages(finalPage, ocrResult, rescuedImagesFolder, pageNumber);

                finalizedPageArray[i] = finalPage;
                ocrPageArray[i] = ocrResult;
                rescuedImagesByPage[i] = pageImages;

                var done = Interlocked.Increment(ref completedPages);
                progress?.Report(new ConversionProgressUpdate(
                    pageStart + (done * perPageWeight),
                    $"Páginas reconstruidas {done}/{pages.Count}..."));

                return ValueTask.CompletedTask;
            });

        var finalizedPages = finalizedPageArray
            .Where(page => page is not null)
            .Cast<BookPageInfo>()
            .ToList();
        var ocrPages = ocrPageArray
            .Where(page => page is not null)
            .Cast<OcrPageResult>()
            .ToList();
        var rescuedImages = rescuedImagesByPage
            .Where(images => images is not null)
            .SelectMany(images => images!)
            .OrderBy(image => image.PageNumber)
            .ThenBy(image => image.Y)
            .ThenBy(image => image.X)
            .ToList();

        if (finalizedPages.Count != pages.Count || ocrPages.Count != pages.Count)
        {
            throw new InvalidOperationException("No se pudieron reconstruir todas las páginas.");
        }

        if (isPerfectHeavy)
        {
            for (var i = 0; i < ocrPages.Count; i++)
            {
                ocrPages[i] = _headingOcrRefiner.Refine(finalizedPages[i], ocrPages[i], ocrLanguages);
            }
        }

        progress?.Report(new ConversionProgressUpdate(pageEnd, "Páginas reconstruidas..."));
        var originalPageTexts = ocrPages
            .Select((ocrPage, index) => TextCleanupService.BuildOrderedPageText(finalizedPages[index], ocrPage))
            .ToList();

        var organizedPageTexts = isVisualElementsOnly
            ? Enumerable.Range(0, originalPageTexts.Count).Select(_ => string.Empty).ToList()
            : originalPageTexts.ToList();
        var documentAiApplied = false;
        if (allowDocumentAi && _documentAi.IsAvailable)
        {
            progress?.Report(new ConversionProgressUpdate(aiStart, "Analizando estructura inteligente del libro..."));
            var analysis = await _documentAi.AnalyzeAsync(inputPath, runFolder, finalizedPages, rescuedImagesFolder, cancellationToken);
            if (analysis is not null)
            {
                AddUniqueImages(rescuedImages, analysis.RescuedImages);
            }

            if (analysis?.HasUsableText == true)
            {
                for (var i = 0; i < organizedPageTexts.Count && i < analysis.PageTexts.Count; i++)
                {
                    if (ShouldUseDocumentAiText(organizedPageTexts[i], analysis.PageTexts[i]))
                    {
                        organizedPageTexts[i] = analysis.PageTexts[i];
                        documentAiApplied = true;
                    }
                    else if (ShouldSuppressLowQualityFallbackText(organizedPageTexts[i], analysis.PageTexts[i]))
                    {
                        organizedPageTexts[i] = string.Empty;
                    }
                }

                progress?.Report(new ConversionProgressUpdate(aiStart + ((aiEnd - aiStart) * 0.75d), "Estructura inteligente aplicada..."));
            }
        }

        if (!documentAiApplied && allowLocalTextAi && _localAi.IsAvailable)
        {
            var aiPageWeight = (aiEnd - aiStart) / Math.Max(1, organizedPageTexts.Count);
            for (var i = 0; i < organizedPageTexts.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new ConversionProgressUpdate(aiStart + (i * aiPageWeight), $"Organizando contenido {i + 1}/{organizedPageTexts.Count}..."));
                organizedPageTexts[i] = await _localAi.ImprovePageTextAsync(organizedPageTexts[i], cancellationToken);
            }

            progress?.Report(new ConversionProgressUpdate(aiEnd, "Contenido organizado con asistencia local..."));
        }
        else if (documentAiApplied)
        {
            progress?.Report(new ConversionProgressUpdate(aiEnd, "Contenido organizado con inteligencia documental..."));
        }

        var normalizedDetected = "visual";
        if (!isVisualElementsOnly)
        {
            var allText = string.Join(Environment.NewLine + Environment.NewLine, organizedPageTexts);
            progress?.Report(new ConversionProgressUpdate(detectStart, "Detectando idioma de origen..."));

            var detectedLanguage = await _languageDetection.DetectAsync(allText, translationEndpoint, translationApiKey, cancellationToken);
            normalizedDetected = NormalizeLanguage(detectedLanguage, "auto");
            progress?.Report(new ConversionProgressUpdate(detectEnd, "Idioma detectado..."));
        }
        else
        {
            progress?.Report(new ConversionProgressUpdate(detectEnd, "Elementos visuales clasificados..."));
        }

        var translatedPageTexts = new List<string>(ocrPages.Count);
        var translationApplied = false;
        var translatorAvailable = false;

        if (enableTranslation && !isVisualElementsOnly && normalizedDetected != outputLanguage)
        {
            progress?.Report(new ConversionProgressUpdate(translationStart, "Preparando traducción..."));
            translatorAvailable = await _translation.CanReachServerAsync(translationEndpoint, cancellationToken);

            if (!translatorAvailable)
            {
                progress?.Report(new ConversionProgressUpdate(translationEnd, "No se pudo traducir; se guardará el texto original."));
            }
        }

        for (var i = 0; i < ocrPages.Count; i++)
        {
            var original = organizedPageTexts[i];
            if (!enableTranslation || !translatorAvailable || string.IsNullOrWhiteSpace(original))
            {
                translatedPageTexts.Add(original);
                continue;
            }

            if (normalizedDetected == outputLanguage)
            {
                translatedPageTexts.Add(original);
                continue;
            }

            progress?.Report(new ConversionProgressUpdate(translationStart + (i * (translationEnd - translationStart) / Math.Max(1, ocrPages.Count)), $"Traduciendo página {i + 1}/{ocrPages.Count}..."));
            var translated = await _translation.TranslateAsync(
                original,
                normalizedDetected,
                outputLanguage,
                translationEndpoint,
                translationApiKey,
                cancellationToken);

            translatedPageTexts.Add(translated);
            translationApplied |= !translated.Equals(original, StringComparison.Ordinal);
        }

        if (enableTranslation)
        {
            progress?.Report(new ConversionProgressUpdate(translationEnd, translationApplied ? "Traducción terminada..." : "Texto listo sin traducción..."));
        }

        var baseOutputName = Path.GetFileNameWithoutExtension(inputPath);
        var modeSuffix = GetModeOutputSuffix(reconstructionMode);
        var modeName = GetModeProgressName(reconstructionMode);
        var textOutputPath = Path.Combine(runFolder, $"{baseOutputName}_{modeSuffix}.txt");
        var outputOcrPages = isVisualElementsOnly ? CreateBlankOcrPages(ocrPages.Count) : ocrPages;
        var reconstructedPdfPath = string.Empty;
        var reconstructedDocxPath = string.Empty;
        var reconstructedEpubPath = string.Empty;
        var csvPath = string.Empty;
        var reportPath = string.Empty;
        var outputStep = outputWeight * progressScale / Math.Max(1, selectedOutputCount);
        var outputTasks = new List<Task>();
        var completedOutputs = 0;
        var lastOutputProgress = outputStart;
        var outputProgressLock = new object();

        void ReportOutputProgress(double percent, string message)
        {
            lock (outputProgressLock)
            {
                if (percent < lastOutputProgress)
                {
                    percent = lastOutputProgress;
                }
                else
                {
                    lastOutputProgress = percent;
                }

                progress?.Report(new ConversionProgressUpdate(percent, message));
            }
        }

        if (isPerfectHeavy)
        {
            reconstructedPdfPath = outputProfiles.Pdf
                ? Path.Combine(runFolder, $"{baseOutputName}_reconstruccion_perfecta_pesada.pdf")
                : string.Empty;
            reconstructedDocxPath = outputProfiles.Word
                ? Path.Combine(runFolder, $"{baseOutputName}_reconstruccion_perfecta_pesada.docx")
                : string.Empty;
            reportPath = Path.Combine(runFolder, $"{baseOutputName}_reporte_reconstruccion_perfecta_pesada.json");
            var selectedHeavyOutputs = 1 + (outputProfiles.Pdf ? 1 : 0) + (outputProfiles.Word ? 1 : 0);

            outputTasks.Add(Task.Run(() =>
            {
                ReportOutputProgress(outputStart, "Reconstrucción perfecta pesada: creando salidas fieles...");
                _perfectHeavy.WriteOutputs(
                    reconstructedPdfPath,
                    reconstructedDocxPath,
                    reportPath,
                    outputProfiles.Pdf,
                    outputProfiles.Word,
                    finalizedPages,
                    ocrPages,
                    translatedPageTexts,
                    rescuedImages,
                    cancellationToken);
                var done = Interlocked.Add(ref completedOutputs, selectedHeavyOutputs);
                ReportOutputProgress(outputStart + (done * outputStep), "Reconstrucción perfecta pesada lista...");
            }, cancellationToken));
        }
        else if (outputProfiles.Pdf)
        {
            reconstructedPdfPath = Path.Combine(runFolder, $"{baseOutputName}_{modeSuffix}.pdf");
            outputTasks.Add(Task.Run(() =>
            {
                ReportOutputProgress(outputStart, "Creando PDF...");
                if (isTextOnly)
                {
                    _pdfEditorialWriter.WritePdf(reconstructedPdfPath, finalizedPages, outputOcrPages, translatedPageTexts, rescuedImages, includeImages: false);
                }
                else
                {
                    _pdfEditorialWriter.WritePdf(reconstructedPdfPath, finalizedPages, outputOcrPages, translatedPageTexts, rescuedImages, includeImages: true);
                }

                var done = Interlocked.Increment(ref completedOutputs);
                ReportOutputProgress(outputStart + (done * outputStep), "PDF listo...");
            }, cancellationToken));
        }

        Task? wordTask = null;
        if (!isPerfectHeavy && outputProfiles.Word)
        {
            reconstructedDocxPath = Path.Combine(runFolder, $"{baseOutputName}_{modeSuffix}.docx");
            wordTask = Task.Run(() =>
            {
                ReportOutputProgress(outputStart, "Creando Word...");
                if (isTextOnly)
                {
                    _docxEditorialWriter.WriteDocx(reconstructedDocxPath, finalizedPages, outputOcrPages, translatedPageTexts, rescuedImages, includeImages: false);
                }
                else
                {
                    _docxEditorialWriter.WriteDocx(reconstructedDocxPath, finalizedPages, outputOcrPages, translatedPageTexts, rescuedImages, includeImages: true);
                }

                var done = Interlocked.Increment(ref completedOutputs);
                ReportOutputProgress(outputStart + (done * outputStep), "Word listo...");
            }, cancellationToken);
            outputTasks.Add(wordTask);
        }

        if (outputProfiles.Epub)
        {
            reconstructedEpubPath = Path.Combine(runFolder, $"{baseOutputName}_{modeSuffix}.epub");
            outputTasks.Add(Task.Run(async () =>
            {
                ReportOutputProgress(outputStart, "Creando ePub...");
                await _epubWriter.WriteAsync(reconstructedEpubPath, baseOutputName, finalizedPages, translatedPageTexts, rescuedImages, cancellationToken);
                var done = Interlocked.Increment(ref completedOutputs);
                ReportOutputProgress(outputStart + (done * outputStep), "ePub listo...");
            }, cancellationToken));
        }

        if (outputProfiles.Csv)
        {
            csvPath = Path.Combine(runFolder, $"{baseOutputName}_{modeSuffix}.csv");
            outputTasks.Add(Task.Run(async () =>
            {
                ReportOutputProgress(outputStart, "Creando CSV...");
                if (isVisualElementsOnly)
                {
                    var visualManifestPages = BuildVisualElementsManifestPages(rescuedImages, pages.Count);
                    await _csvWriter.WriteAsync(csvPath, visualManifestPages, visualManifestPages, cancellationToken);
                }
                else
                {
                    await _csvWriter.WriteAsync(csvPath, organizedPageTexts, translatedPageTexts, cancellationToken);
                }
                var done = Interlocked.Increment(ref completedOutputs);
                ReportOutputProgress(outputStart + (done * outputStep), "CSV listo...");
            }, cancellationToken));
        }

        await Task.WhenAll(outputTasks);

        if (willCreatePrintView && !string.IsNullOrWhiteSpace(reconstructedDocxPath))
        {
            if (wordTask is not null)
            {
                await wordTask;
            }

            ReportOutputProgress(lastOutputProgress, "Preparando vista de impresión...");
            var libreOfficePdfPath = Path.Combine(runFolder, $"{baseOutputName}_{modeSuffix}_vista_word.pdf");
            await _libreOffice.TryExportPdfAsync(reconstructedDocxPath, libreOfficePdfPath, cancellationToken);
            var done = Interlocked.Increment(ref completedOutputs);
            ReportOutputProgress(outputStart + (done * outputStep), "Vista de impresión lista...");
        }

        var textPayload = isVisualElementsOnly
            ? BuildVisualElementsManifest(rescuedImages)
            : string.Join(Environment.NewLine + Environment.NewLine, translatedPageTexts);
        await File.WriteAllTextAsync(textOutputPath, textPayload, Encoding.UTF8, cancellationToken);

        var libraryRecord = new ConvertedBookRecord
        {
            SourcePath = inputPath,
            SourceFileName = Path.GetFileName(inputPath),
            OutputFolder = runFolder,
            ReconstructionMode = modeName,
            RestoredImagesFolder = rescuedImagesFolder,
            TextPath = textOutputPath,
            ReconstructedPdfPath = reconstructedPdfPath,
            ReconstructedDocxPath = reconstructedDocxPath,
            ReconstructedEpubPath = reconstructedEpubPath,
            CsvPath = csvPath,
            ReportPath = reportPath,
            ExtractedTextPath = textOutputPath,
            OcrLanguages = ocrLanguages,
            Status = "Completado",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        DeleteDirectoryQuietly(internalFolder);

        progress?.Report(new ConversionProgressUpdate(100, "Conversión completada."));

        return new BookConversionResult
        {
            LibraryRecord = libraryRecord,
            DetectedLanguage = normalizedDetected,
            TranslationApplied = translationApplied
        };
    }

    private static async Task<List<BookPageInfo>> ExtractPagesAsync(
        string inputPath,
        string sourcePagesFolder,
        bool maximumQuality,
        double progressStart,
        double progressWeight,
        IProgress<ConversionProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(inputPath).ToLowerInvariant();

        if (extension == ".pdf")
        {
            return await ExtractPdfPagesAsync(inputPath, sourcePagesFolder, maximumQuality, progressStart, progressWeight, progress, cancellationToken);
        }

        if (IsSupportedImage(extension))
        {
            progress?.Report(new ConversionProgressUpdate(progressStart + (progressWeight * 0.5d), "Preparando imagen..."));
            return ExtractImageAsSinglePage(inputPath, sourcePagesFolder);
        }

        throw new NotSupportedException("Formato no soportado todavía. Usa PDF o imágenes (JPG, PNG, TIFF, BMP, WEBP).");
    }

    private static async Task<List<BookPageInfo>> ExtractPdfPagesAsync(
        string inputPath,
        string sourcePagesFolder,
        bool maximumQuality,
        double progressStart,
        double progressWeight,
        IProgress<ConversionProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var pages = new List<BookPageInfo>();

        await using var pdfStream = File.OpenRead(inputPath);
        var pageCount = Conversion.GetPageCount(pdfStream, leaveOpen: true);

        var renderDpi = SelectRenderDpi(pageCount, maximumQuality);
        var renderOptions = new RenderOptions(Dpi: renderDpi, WithAnnotations: true, WithFormFill: true);

        for (var i = 0; i < pageCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageNumber = i + 1;
            progress?.Report(new ConversionProgressUpdate(progressStart + (i * progressWeight / Math.Max(1, pageCount)), $"Rasterizando página {pageNumber}/{pageCount}..."));

            pdfStream.Position = 0;
            var pageSize = Conversion.GetPageSize(pdfStream, page: i, leaveOpen: true);

            var sourceImagePath = Path.Combine(sourcePagesFolder, $"page_{pageNumber:D4}.png");
            pdfStream.Position = 0;
            Conversion.SavePng(sourceImagePath, pdfStream, page: i, leaveOpen: true, options: renderOptions);

            var (pixelWidth, pixelHeight) = ReadImageSize(sourceImagePath);
            var widthPoints = pageSize.Width > 0 ? pageSize.Width : ConvertPixelToPoints(pixelWidth, 300f);
            var heightPoints = pageSize.Height > 0 ? pageSize.Height : ConvertPixelToPoints(pixelHeight, 300f);

            pages.Add(new BookPageInfo
            {
                OriginalImagePath = sourceImagePath,
                RestoredImagePath = string.Empty,
                WidthPoints = widthPoints,
                HeightPoints = heightPoints,
                PixelWidth = pixelWidth,
                PixelHeight = pixelHeight,
                PageIndex = i
            });
        }

        return pages;
    }

    private static List<BookPageInfo> ExtractImageAsSinglePage(string inputPath, string sourcePagesFolder)
    {
        var extension = Path.GetExtension(inputPath).ToLowerInvariant();
        var outputFile = Path.Combine(sourcePagesFolder, $"page_0001{extension}");
        File.Copy(inputPath, outputFile, overwrite: true);

        var (pixelWidth, pixelHeight) = ReadImageSize(outputFile);

        return
        [
            new BookPageInfo
            {
                OriginalImagePath = outputFile,
                RestoredImagePath = string.Empty,
                WidthPoints = ConvertPixelToPoints(pixelWidth, 300f),
                HeightPoints = ConvertPixelToPoints(pixelHeight, 300f),
                PixelWidth = pixelWidth,
                PixelHeight = pixelHeight,
                PageIndex = 0
            }
        ];
    }

    private static float ConvertPixelToPoints(int pixels, float dpi)
    {
        if (dpi <= 0f || float.IsNaN(dpi) || float.IsInfinity(dpi))
        {
            dpi = 300f;
        }

        return pixels * 72f / dpi;
    }

    private static (int width, int height) ReadImageSize(string imagePath)
    {
        using var image = Cv2.ImRead(imagePath, ImreadModes.Unchanged);
        if (image.Empty())
        {
            throw new InvalidOperationException($"No se pudo leer la imagen: {imagePath}");
        }

        return (image.Width, image.Height);
    }

    private static bool IsSupportedImage(string extension)
    {
        return extension is ".jpg" or ".jpeg" or ".png" or ".tif" or ".tiff" or ".bmp" or ".webp";
    }

    private static int SelectRenderDpi(int pageCount, bool maximumQuality)
    {
        if (maximumQuality)
        {
            return pageCount switch
            {
                <= 30 => 320,
                <= 100 => 260,
                <= 220 => 220,
                _ => 180
            };
        }

        return pageCount switch
        {
            <= 30 => 240,
            <= 100 => 200,
            <= 220 => 170,
            _ => 150
        };
    }

    private static int SelectPageProcessingParallelism(int pageCount, bool isPerfectHeavy)
    {
        if (pageCount <= 1)
        {
            return 1;
        }

        var hardware = HardwareCapabilityService.Current;
        var processorTarget = Math.Max(1, hardware.LogicalProcessors - 1);
        var memoryTarget = hardware.TotalMemoryGb switch
        {
            >= 28 => 10,
            >= 20 => 8,
            >= 16 => 6,
            >= 12 => 4,
            _ => 2
        };

        if (isPerfectHeavy)
        {
            var heavyMemoryTarget = hardware.TotalMemoryGb switch
            {
                >= 28 => 4,
                >= 20 => 3,
                >= 16 => 2,
                _ => 1
            };
            return Math.Clamp(Math.Min(processorTarget, heavyMemoryTarget), 1, Math.Min(4, pageCount));
        }

        return Math.Clamp(Math.Min(processorTarget, memoryTarget), 1, Math.Min(10, pageCount));
    }

    private static int CountSelectedOutputs(OutputProfileOptions outputProfiles)
    {
        var count = 0;
        if (outputProfiles.Pdf)
        {
            count++;
        }

        if (outputProfiles.Word)
        {
            count++;
        }

        if (outputProfiles.Epub)
        {
            count++;
        }

        if (outputProfiles.Csv)
        {
            count++;
        }

        return Math.Max(1, count);
    }

    private static string GetModeProgressName(OutputReconstructionMode mode)
    {
        return mode switch
        {
            OutputReconstructionMode.PerfectHeavy => "Reconstrucción perfecta pesada",
            OutputReconstructionMode.VisualElementsOnly => "Solo tablas y gráficos",
            OutputReconstructionMode.TextAndPhotos => "Texto y fotos",
            _ => "Extraer solo texto"
        };
    }

    private static string GetModeOutputSuffix(OutputReconstructionMode mode)
    {
        return mode switch
        {
            OutputReconstructionMode.PerfectHeavy => "reconstruccion_perfecta_pesada",
            OutputReconstructionMode.VisualElementsOnly => "solo_tablas_y_graficos",
            OutputReconstructionMode.TextAndPhotos => "texto_y_fotos",
            _ => "solo_texto"
        };
    }

    private static string BuildVisualElementsManifest(IReadOnlyList<RescuedImageInfo> rescuedImages)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Elementos visuales rescatados");
        builder.AppendLine("Modo: Solo tablas y gráficos");
        builder.AppendLine();

        if (rescuedImages.Count == 0)
        {
            builder.AppendLine("No se detectaron tablas, gráficos, diagramas ni recortes visuales.");
            return builder.ToString();
        }

        foreach (var image in rescuedImages
                     .OrderBy(image => image.PageNumber)
                     .ThenBy(image => image.Y)
                     .ThenBy(image => image.X))
        {
            builder
                .Append("Página ")
                .Append(image.PageNumber)
                .Append(" | Tipo: ")
                .Append(NormalizeVisualKind(image.Kind))
                .Append(" | Archivo: ")
                .AppendLine(image.ImagePath);
        }

        return builder.ToString();
    }

    private static List<OcrPageResult> CreateBlankOcrPages(int pageCount)
    {
        return Enumerable.Range(0, Math.Max(1, pageCount))
            .Select(_ => new OcrPageResult { FullText = string.Empty, Lines = [], Words = [] })
            .ToList();
    }

    private static List<string> BuildVisualElementsManifestPages(IReadOnlyList<RescuedImageInfo> rescuedImages, int pageCount)
    {
        var pages = Enumerable.Range(0, Math.Max(1, pageCount))
            .Select(_ => new StringBuilder())
            .ToList();

        foreach (var image in rescuedImages
                     .OrderBy(image => image.PageNumber)
                     .ThenBy(image => image.Y)
                     .ThenBy(image => image.X))
        {
            var index = Math.Clamp(image.PageNumber - 1, 0, pages.Count - 1);
            pages[index]
                .Append("Tipo: ")
                .Append(NormalizeVisualKind(image.Kind))
                .Append(" | Archivo: ")
                .AppendLine(image.ImagePath);
        }

        return pages.Select(page => page.ToString().Trim()).ToList();
    }

    private static string NormalizeVisualKind(string kind)
    {
        return kind.ToLowerInvariant() switch
        {
            "table" => "tabla",
            "figure" => "figura/diagrama",
            "chart" => "gráfico",
            "formula" => "fórmula visual",
            _ => string.IsNullOrWhiteSpace(kind) ? "elemento visual" : kind
        };
    }

    private static double ScaleProgress(double rawProgress, double progressScale)
    {
        return Math.Clamp(rawProgress * progressScale, 0d, 100d);
    }

    private static void AddUniqueImages(List<RescuedImageInfo> target, IReadOnlyList<RescuedImageInfo> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (target.Any(existing => existing.PageNumber == candidate.PageNumber && OverlapRatio(existing, candidate) > 0.55))
            {
                continue;
            }

            target.Add(candidate);
        }
    }

    private static double OverlapRatio(RescuedImageInfo a, RescuedImageInfo b)
    {
        var x1 = Math.Max(a.X, b.X);
        var y1 = Math.Max(a.Y, b.Y);
        var x2 = Math.Min(a.X + a.Width, b.X + b.Width);
        var y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);

        if (x2 <= x1 || y2 <= y1)
        {
            return 0d;
        }

        var intersection = (x2 - x1) * (y2 - y1);
        var smaller = Math.Min(a.Width * a.Height, b.Width * b.Height);
        return intersection / Math.Max(1d, smaller);
    }

    private static bool ShouldUseDocumentAiText(string originalText, string documentAiText)
    {
        if (string.IsNullOrWhiteSpace(documentAiText))
        {
            return false;
        }

        var originalScore = CountContentCharacters(originalText);
        var documentAiScore = CountContentCharacters(documentAiText);
        if (documentAiScore < 20)
        {
            return false;
        }

        if (originalScore == 0)
        {
            return true;
        }

        if (documentAiScore < originalScore * 0.25)
        {
            return false;
        }

        if (documentAiScore > originalScore * 3.2)
        {
            return false;
        }

        return documentAiText.Contains('$') ||
            documentAiText.Contains('|') ||
            HasStructuredDocumentSignals(documentAiText) ||
            documentAiScore >= originalScore * 0.5;
    }

    private static bool ShouldSuppressLowQualityFallbackText(string originalText, string documentAiText)
    {
        if (!string.IsNullOrWhiteSpace(documentAiText) && CountContentCharacters(documentAiText) >= 20)
        {
            return false;
        }

        var normalized = originalText
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Trim();
        var contentScore = CountContentCharacters(normalized);
        if (contentScore is 0 or >= 35)
        {
            return false;
        }

        var words = normalized
            .Split([' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(word => word.Count(char.IsLetterOrDigit) > 0)
            .ToList();
        if (words.Count is < 2 or > 8)
        {
            return false;
        }

        if (normalized.Any(char.IsDigit) ||
            words.Any(word => StructuredTextService.LooksLikeHeading(word)) ||
            ContainsKnownShortContent(normalized))
        {
            return false;
        }

        var letters = normalized.Where(char.IsLetter).ToList();
        if (letters.Count == 0)
        {
            return true;
        }

        var lowercaseRatio = letters.Count(char.IsLower) / (double)letters.Count;
        var averageWordLength = words.Average(word => word.Count(char.IsLetterOrDigit));
        return lowercaseRatio > 0.72 && averageWordLength < 5.2;
    }

    private static int CountContentCharacters(string text)
    {
        return text.Count(char.IsLetterOrDigit);
    }

    private static bool HasStructuredDocumentSignals(string text)
    {
        var lines = text
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (lines.Any(line => StructuredTextService.TryReadMarkdownHeading(line, out _, out _) ||
                              StructuredTextService.LooksLikeHeading(line) ||
                              StructuredTextService.TryReadBullet(line, out _)))
        {
            return true;
        }

        return lines.Count(line => line.Length >= 80 && line.Contains(' ')) >= 2;
    }

    private static bool ContainsKnownShortContent(string text)
    {
        return text.Contains("ASHRAE", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("ISBN", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Figure", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Figura", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Table", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Tabla", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Chapter", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Capítulo", StringComparison.OrdinalIgnoreCase);
    }

    private static void HideDirectory(string path)
    {
        try
        {
            var directory = new DirectoryInfo(path);
            directory.Attributes |= FileAttributes.Hidden;
        }
        catch
        {
        }
    }

    private static void DeleteDirectoryQuietly(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static string CreateRunFolder(string inputPath, string outputRoot)
    {
        var safeName = Path.GetFileNameWithoutExtension(inputPath);
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            safeName = safeName.Replace(invalidChar, '_');
        }

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var folder = Path.Combine(outputRoot, $"{safeName}_{timestamp}");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string NormalizeLanguage(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length > 2 && normalized.Contains('-'))
        {
            normalized = normalized[..2];
        }

        return normalized;
    }
}
