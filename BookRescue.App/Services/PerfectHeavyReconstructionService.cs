using BookRescue.App.Models;
using OpenCvSharp;

namespace BookRescue.App.Services;

public sealed record PerfectHeavyReconstructionResult(
    string PdfPath,
    string DocxPath,
    string ReportPath);

public sealed class PerfectHeavyReconstructionService
{
    private const string PageVisualFallbackKind = "page-visual-fallback";

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
        var pageVisualFallbacks = CreateDenseVisualPageFallbacks(pages, ocrPages, persistedRegionalFallbacks, fallbackFolder, cancellationToken);
        var pageVisualFallbackPageNumbers = pageVisualFallbacks
            .Select(image => image.PageNumber)
            .ToHashSet();
        var allRegionalFallbacks = persistedRegionalFallbacks
            .Concat(pageVisualFallbacks)
            .Concat(CreateLowConfidenceTextFallbacks(pages, ocrPages, fallbackFolder, pageVisualFallbackPageNumbers, cancellationToken))
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
            .Select((page, index) =>
            {
                if (pageVisualFallbackPageNumbers.Contains(index + 1))
                {
                    return string.Empty;
                }

                return index < outputPageTexts.Count && !string.IsNullOrWhiteSpace(outputPageTexts[index])
                    ? outputPageTexts[index]
                    : TextCleanupService.BuildOrderedPageText(
                        page,
                        index < ocrPages.Count
                            ? ocrPages[index]
                            : new OcrPageResult { FullText = string.Empty, Words = [], Lines = [] });
            })
            .ToList();
        var editorialImages = allRegionalFallbacks
            .Where(image => !image.Kind.Equals("low-confidence-text", StringComparison.OrdinalIgnoreCase))
            .Where(image => !pageVisualFallbackPageNumbers.Contains(image.PageNumber) ||
                            image.Kind.Equals(PageVisualFallbackKind, StringComparison.OrdinalIgnoreCase))
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

    private static IReadOnlyList<RescuedImageInfo> CreateDenseVisualPageFallbacks(
        IReadOnlyList<BookPageInfo> pages,
        IReadOnlyList<OcrPageResult> ocrPages,
        IReadOnlyList<RescuedImageInfo> persistedRegionalFallbacks,
        string fallbackFolder,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(fallbackFolder);
        var fallbacks = new List<RescuedImageInfo>();

        for (var pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pageNumber = pageIndex + 1;
            var page = pages[pageIndex];
            var ocr = pageIndex < ocrPages.Count
                ? ocrPages[pageIndex]
                : new OcrPageResult { FullText = string.Empty, Words = [], Lines = [] };
            var pageImages = persistedRegionalFallbacks
                .Where(image => image.PageNumber == pageNumber)
                .ToList();

            if (pageImages.Count == 0)
            {
                continue;
            }

            var imagePath = !string.IsNullOrWhiteSpace(page.RestoredImagePath) && File.Exists(page.RestoredImagePath)
                ? page.RestoredImagePath
                : page.OriginalImagePath;
            if (!File.Exists(imagePath))
            {
                continue;
            }

            using var source = Cv2.ImRead(imagePath, ImreadModes.Color);
            if (source.Empty() || !ShouldPreservePageAsVisual(source, ocr, pageImages))
            {
                continue;
            }

            var extension = Path.GetExtension(imagePath);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".png";
            }

            var outputPath = Path.Combine(fallbackFolder, $"pagina_{pageNumber:D4}_preservacion_visual_pagina{extension}");
            File.Copy(imagePath, outputPath, overwrite: true);
            if (!File.Exists(outputPath))
            {
                continue;
            }

            fallbacks.Add(new RescuedImageInfo
            {
                ImagePath = outputPath,
                PageNumber = pageNumber,
                X = 0,
                Y = 0,
                Width = source.Width,
                Height = source.Height,
                PagePixelWidth = source.Width,
                PagePixelHeight = source.Height,
                Kind = PageVisualFallbackKind
            });
        }

        return fallbacks;
    }

    private static bool ShouldPreservePageAsVisual(
        Mat source,
        OcrPageResult ocrPage,
        IReadOnlyList<RescuedImageInfo> pageImages)
    {
        var pageArea = source.Width * source.Height;
        foreach (var image in pageImages)
        {
            var areaRatio = image.Width * image.Height / Math.Max(1d, pageArea);
            if (areaRatio < 0.24d)
            {
                continue;
            }

            using var crop = LoadVisualCrop(source, image);
            if (crop.Empty())
            {
                continue;
            }

            var complexity = MeasureVisualComplexity(crop);
            if (complexity.IsDenseChart || complexity.IsDenseTable || complexity.IsRotatedTable)
            {
                return true;
            }

            if (areaRatio >= 0.32d &&
                HasLowConfidenceTextInside(image, ocrPage) &&
                complexity.LongLineCount >= 8 &&
                complexity.EdgeRatio >= 0.012d)
            {
                return true;
            }
        }

        return false;
    }

    private static Mat LoadVisualCrop(Mat source, RescuedImageInfo image)
    {
        if (File.Exists(image.ImagePath))
        {
            return Cv2.ImRead(image.ImagePath, ImreadModes.Color);
        }

        var rect = ExpandAndClamp(
            new Rect(
                (int)MathF.Round(image.X),
                (int)MathF.Round(image.Y),
                Math.Max(1, (int)MathF.Round(image.Width)),
                Math.Max(1, (int)MathF.Round(image.Height))),
            source.Width,
            source.Height,
            0);
        return new Mat(source, rect).Clone();
    }

    private static VisualComplexity MeasureVisualComplexity(Mat crop)
    {
        using var gray = new Mat();
        Cv2.CvtColor(crop, gray, ColorConversionCodes.BGR2GRAY);

        using var edges = new Mat();
        Cv2.Canny(gray, edges, 45, 145);
        var area = Math.Max(1, crop.Width * crop.Height);
        var edgeRatio = Cv2.CountNonZero(edges) / (double)area;

        var lines = Cv2.HoughLinesP(
            edges,
            1,
            Math.PI / 180,
            34,
            Math.Max(32, Math.Min(crop.Width, crop.Height) / 7),
            16);

        var longLineCount = 0;
        var horizontalLineCount = 0;
        var verticalLineCount = 0;
        var diagonalLineCount = 0;
        foreach (var line in lines)
        {
            var dx = line.P2.X - line.P1.X;
            var dy = line.P2.Y - line.P1.Y;
            var length = Math.Sqrt((dx * dx) + (dy * dy));
            if (length < Math.Min(crop.Width, crop.Height) * 0.12d)
            {
                continue;
            }

            longLineCount++;
            var angle = Math.Abs(Math.Atan2(dy, dx) * 180d / Math.PI);
            if (angle > 90)
            {
                angle = 180 - angle;
            }

            if (angle <= 8)
            {
                horizontalLineCount++;
            }
            else if (angle >= 82)
            {
                verticalLineCount++;
            }
            else
            {
                diagonalLineCount++;
            }
        }

        using var binary = new Mat();
        Cv2.AdaptiveThreshold(gray, binary, 255, AdaptiveThresholdTypes.GaussianC, ThresholdTypes.BinaryInv, 35, 12);
        using var horizontalKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(Math.Max(16, crop.Width / 16), 1));
        using var verticalKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(1, Math.Max(12, crop.Height / 16)));
        using var horizontal = new Mat();
        using var vertical = new Mat();
        Cv2.MorphologyEx(binary, horizontal, MorphTypes.Open, horizontalKernel);
        Cv2.MorphologyEx(binary, vertical, MorphTypes.Open, verticalKernel);
        var gridInkRatio = (Cv2.CountNonZero(horizontal) + Cv2.CountNonZero(vertical)) / (double)area;

        var denseChart = edgeRatio >= 0.018d && longLineCount >= 16 && (diagonalLineCount >= 4 || gridInkRatio >= 0.006d);
        var denseTable = gridInkRatio >= 0.010d && horizontalLineCount >= 3 && verticalLineCount >= 3;
        var rotatedTable = edgeRatio >= 0.010d && verticalLineCount >= 6 && horizontalLineCount >= 2 && longLineCount >= 10;
        return new VisualComplexity(
            edgeRatio,
            gridInkRatio,
            longLineCount,
            horizontalLineCount,
            verticalLineCount,
            diagonalLineCount,
            denseChart,
            denseTable,
            rotatedTable);
    }

    private static bool HasLowConfidenceTextInside(RescuedImageInfo image, OcrPageResult ocrPage)
    {
        var overlappingLines = ocrPage.Lines
            .Where(line => RegionOverlapRatio(image.X, image.Y, image.Width, image.Height, line.X, line.Y, line.Width, line.Height) > 0.18d)
            .ToList();
        return overlappingLines.Count >= 3 &&
            overlappingLines.Average(line => Math.Clamp(line.Confidence, 0f, 100f)) < 55;
    }

    private static double RegionOverlapRatio(float ax, float ay, float aw, float ah, float bx, float by, float bw, float bh)
    {
        var x1 = Math.Max(ax, bx);
        var y1 = Math.Max(ay, by);
        var x2 = Math.Min(ax + aw, bx + bw);
        var y2 = Math.Min(ay + ah, by + bh);
        if (x2 <= x1 || y2 <= y1)
        {
            return 0d;
        }

        var intersection = (x2 - x1) * (y2 - y1);
        var smaller = Math.Min(aw * ah, bw * bh);
        return intersection / Math.Max(1d, smaller);
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
        IReadOnlySet<int> skipPageNumbers,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(fallbackFolder);
        var fallbacks = new List<RescuedImageInfo>();

        for (var pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (skipPageNumbers.Contains(pageIndex + 1))
            {
                continue;
            }

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

    private sealed record VisualComplexity(
        double EdgeRatio,
        double GridInkRatio,
        int LongLineCount,
        int HorizontalLineCount,
        int VerticalLineCount,
        int DiagonalLineCount,
        bool IsDenseChart,
        bool IsDenseTable,
        bool IsRotatedTable);
}
