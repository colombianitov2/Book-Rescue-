using BookRescue.App.Models;
using OpenCvSharp;

namespace BookRescue.App.Services;

public sealed record PerfectHeavyReconstructionResult(
    string PdfPath,
    string DocxPath,
    string ReportPath);

public sealed class PerfectHeavyReconstructionService
{
    private readonly HeavyLayoutAnalyzer analyzer = new();
    private readonly PdfEditorialWriter pdfEditorialWriter = new();
    private readonly DocxEditorialWriter docxEditorialWriter = new();
    private readonly ReconstructionReportWriter reportWriter = new();

    public PerfectHeavyReconstructionResult WriteOutputs(
        string pdfPath,
        string docxPath,
        string reportPath,
        bool writePdf,
        bool writeDocx,
        IReadOnlyList<BookPageInfo> pages,
        IReadOnlyList<OcrPageResult> ocrPages,
        IReadOnlyList<string> outputPageTexts,
        IReadOnlyList<RescuedImageInfo> rescuedImages,
        CancellationToken cancellationToken)
    {
        var outputFolder = Path.GetDirectoryName(reportPath)!;
        var fallbackFolder = Path.Combine(outputFolder, "fallback_visual_regional");
        var persistedRegionalFallbacks = PersistRegionalFallbacks(rescuedImages, fallbackFolder, cancellationToken);
        var allRegionalFallbacks = persistedRegionalFallbacks
            .Concat(CreateLowConfidenceTextFallbacks(pages, ocrPages, fallbackFolder, cancellationToken))
            .OrderBy(image => image.PageNumber)
            .ThenBy(image => image.Y)
            .ThenBy(image => image.X)
            .ToList();

        var layouts = new List<HeavyPageLayout>(pages.Count);
        for (var i = 0; i < pages.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            layouts.Add(analyzer.Analyze(
                pages[i],
                i < ocrPages.Count ? ocrPages[i] : new OcrPageResult { FullText = string.Empty, Words = [], Lines = [] },
                allRegionalFallbacks,
                i + 1,
                outputFolder));
        }

        var effectivePageTexts = pages
            .Select((page, index) => index < outputPageTexts.Count && !string.IsNullOrWhiteSpace(outputPageTexts[index])
                ? outputPageTexts[index]
                : TextCleanupService.BuildOrderedPageText(
                    page,
                    index < ocrPages.Count
                        ? ocrPages[index]
                        : new OcrPageResult { FullText = string.Empty, Words = [], Lines = [] }))
            .ToList();
        var editorialImages = allRegionalFallbacks
            .Where(image => !image.Kind.Equals("low-confidence-text", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (writePdf)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pdfEditorialWriter.WritePdf(pdfPath, pages, ocrPages, effectivePageTexts, editorialImages, includeImages: true);
        }

        if (writeDocx)
        {
            cancellationToken.ThrowIfCancellationRequested();
            docxEditorialWriter.WriteDocx(docxPath, pages, ocrPages, effectivePageTexts, editorialImages, includeImages: true);
        }

        cancellationToken.ThrowIfCancellationRequested();
        reportWriter.Write(reportPath, layouts);

        return new PerfectHeavyReconstructionResult(
            writePdf ? pdfPath : string.Empty,
            writeDocx ? docxPath : string.Empty,
            reportPath);
    }

    private static IReadOnlyList<RescuedImageInfo> PersistRegionalFallbacks(
        IReadOnlyList<RescuedImageInfo> rescuedImages,
        string fallbackFolder,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(fallbackFolder);
        var persisted = new List<RescuedImageInfo>();
        var countersByPage = new Dictionary<int, int>();

        foreach (var image in rescuedImages.OrderBy(image => image.PageNumber).ThenBy(image => image.Y).ThenBy(image => image.X))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(image.ImagePath) || IsLikelyDamageOnlyCrop(image.ImagePath))
            {
                continue;
            }

            countersByPage.TryGetValue(image.PageNumber, out var count);
            count++;
            countersByPage[image.PageNumber] = count;

            var outputPath = Path.Combine(fallbackFolder, $"pagina_{image.PageNumber:D4}_recorte_visual_{count:D2}.png");
            File.Copy(image.ImagePath, outputPath, overwrite: true);
            persisted.Add(new RescuedImageInfo
            {
                ImagePath = outputPath,
                PageNumber = image.PageNumber,
                X = image.X,
                Y = image.Y,
                Width = image.Width,
                Height = image.Height,
                PagePixelWidth = image.PagePixelWidth,
                PagePixelHeight = image.PagePixelHeight,
                Kind = image.Kind
            });
        }

        return persisted;
    }

    private static bool IsLikelyDamageOnlyCrop(string imagePath)
    {
        using var crop = Cv2.ImRead(imagePath, ImreadModes.Color);
        if (crop.Empty())
        {
            return false;
        }

        var ratio = crop.Width / (double)Math.Max(1, crop.Height);
        if (ratio is >= 0.38 and <= 2.65)
        {
            return false;
        }

        using var gray = new Mat();
        Cv2.CvtColor(crop, gray, ColorConversionCodes.BGR2GRAY);
        using var edges = new Mat();
        Cv2.Canny(gray, edges, 45, 150);
        var lines = Cv2.HoughLinesP(
            edges,
            1,
            Math.PI / 180,
            42,
            Math.Max(35, (int)(Math.Max(crop.Width, crop.Height) * 0.34)),
            22);

        if (lines.Length == 0)
        {
            return false;
        }

        var longest = lines.Max(line =>
        {
            var dx = line.P2.X - line.P1.X;
            var dy = line.P2.Y - line.P1.Y;
            return Math.Sqrt((dx * dx) + (dy * dy));
        });
        var longLineRatio = longest / Math.Max(1d, Math.Max(crop.Width, crop.Height));

        using var dark = new Mat();
        Cv2.Threshold(gray, dark, 90, 255, ThresholdTypes.BinaryInv);
        var darkRatio = Cv2.CountNonZero(dark) / (double)Math.Max(1, crop.Width * crop.Height);

        return longLineRatio > 0.42 && darkRatio < 0.11;
    }

    private static IReadOnlyList<RescuedImageInfo> CreateLowConfidenceTextFallbacks(
        IReadOnlyList<BookPageInfo> pages,
        IReadOnlyList<OcrPageResult> ocrPages,
        string fallbackFolder,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(fallbackFolder);
        var fallbacks = new List<RescuedImageInfo>();

        for (var pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = pages[pageIndex];
            var ocr = pageIndex < ocrPages.Count ? ocrPages[pageIndex] : new OcrPageResult { FullText = string.Empty, Words = [], Lines = [] };
            var imagePath = !string.IsNullOrWhiteSpace(page.RestoredImagePath) && File.Exists(page.RestoredImagePath)
                ? page.RestoredImagePath
                : page.OriginalImagePath;
            if (!File.Exists(imagePath))
            {
                continue;
            }

            using var source = Cv2.ImRead(imagePath, ImreadModes.Color);
            if (source.Empty())
            {
                continue;
            }

            var saved = 0;
            foreach (var line in ocr.Lines
                         .Where(line => line.Confidence is >= 10 and < 35)
                         .Where(line => !string.IsNullOrWhiteSpace(line.Text))
                         .Where(line => line.Width >= 32 && line.Height >= 8)
                         .OrderBy(line => line.Y)
                         .ThenBy(line => line.X)
                         .Take(60))
            {
                var rect = ExpandAndClamp(
                    new Rect(
                        (int)MathF.Round(line.X),
                        (int)MathF.Round(line.Y),
                        Math.Max(1, (int)MathF.Round(line.Width)),
                        Math.Max(1, (int)MathF.Round(line.Height))),
                    source.Width,
                    source.Height,
                    6);
                using var crop = new Mat(source, rect);
                var outputPath = Path.Combine(fallbackFolder, $"pagina_{pageIndex + 1:D4}_texto_baja_confianza_{saved + 1:D2}.png");
                Cv2.ImWrite(outputPath, crop);

                fallbacks.Add(new RescuedImageInfo
                {
                    ImagePath = outputPath,
                    PageNumber = pageIndex + 1,
                    X = rect.X,
                    Y = rect.Y,
                    Width = rect.Width,
                    Height = rect.Height,
                    PagePixelWidth = source.Width,
                    PagePixelHeight = source.Height,
                    Kind = "low-confidence-text"
                });
                saved++;
            }
        }

        return fallbacks;
    }

    private static Rect ExpandAndClamp(Rect rect, int maxWidth, int maxHeight, int padding)
    {
        var x = Math.Max(0, rect.X - padding);
        var y = Math.Max(0, rect.Y - padding);
        var right = Math.Min(maxWidth, rect.Right + padding);
        var bottom = Math.Min(maxHeight, rect.Bottom + padding);
        return new Rect(x, y, Math.Max(1, right - x), Math.Max(1, bottom - y));
    }
}
