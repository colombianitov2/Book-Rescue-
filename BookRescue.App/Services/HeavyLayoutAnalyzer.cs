using BookRescue.App.Models;

namespace BookRescue.App.Services;

public sealed record HeavyPageLayout(
    int PageNumber,
    BookPageInfo Page,
    OcrPageResult Ocr,
    IReadOnlyList<DetectedVisualRegion> Regions,
    IReadOnlyList<RescuedImageInfo> PageImages,
    CleanPageBackground Background);

public sealed record HeavyPageLayoutInput(
    BookPageInfo Page,
    OcrPageResult Ocr,
    IReadOnlyList<RescuedImageInfo> PageImages);

public sealed class HeavyLayoutAnalyzer
{
    private readonly VisualRegionDetector detector = new();
    private readonly CleanBackgroundEstimator backgroundEstimator = new();
    private readonly RegionOcrMapper regionOcrMapper = new();

    public HeavyPageLayout Analyze(
        BookPageInfo page,
        OcrPageResult ocrPage,
        IReadOnlyList<RescuedImageInfo> rescuedImages,
        int pageNumber,
        string diagnosticsFolder = "")
    {
        var pageImages = rescuedImages
            .Where(image => image.PageNumber == pageNumber)
            .OrderBy(image => image.Y)
            .ThenBy(image => image.X)
            .ToList();
        var input = new HeavyPageLayoutInput(page, ocrPage, pageImages);

        var detectedRegions = detector.Detect(page, ocrPage, pageImages);
        var background = backgroundEstimator.Estimate(input, detectedRegions, diagnosticsFolder, pageNumber);

        var mappedRegions = detectedRegions
            .Select(region => AttachOcr(region, ocrPage))
            .ToList();

        return new HeavyPageLayout(
            pageNumber,
            page,
            ocrPage,
            mappedRegions,
            pageImages,
            background);
    }

    private DetectedVisualRegion AttachOcr(DetectedVisualRegion region, OcrPageResult ocrPage)
    {
        var lines = regionOcrMapper.SelectLines(region, ocrPage);
        var words = regionOcrMapper.SelectWords(region, ocrPage);
        if (lines.Count == 0 && words.Count == 0)
        {
            return region;
        }

        return region with
        {
            AssociatedText = string.Join(Environment.NewLine, lines.Select(line => line.Text.Trim()).Where(text => !string.IsNullOrWhiteSpace(text))),
            AssociatedLineCount = lines.Count,
            AssociatedWordCount = words.Count,
            AssociatedOcrConfidence = lines.Count > 0
                ? lines.Average(line => Math.Clamp(line.Confidence, 0f, 100f))
                : words.Average(word => Math.Clamp(word.Confidence, 0f, 100f))
        };
    }
}
