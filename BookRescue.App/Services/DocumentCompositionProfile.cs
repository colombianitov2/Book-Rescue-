using BookRescue.App.Models;

namespace BookRescue.App.Services;

public enum DocumentCompositionKind
{
    Book,
    TechnicalManual,
    ScientificPaper
}

public sealed class DocumentCompositionProfile
{
    private DocumentCompositionProfile(
        double pageWidthPoints,
        double pageHeightPoints,
        double marginLeftPoints,
        double marginRightPoints,
        double marginTopPoints,
        double marginBottomPoints,
        bool usesTwoColumns,
        DocumentCompositionKind kind,
        double bodyFontSizePoints,
        double leadingEm,
        double firstLineIndentPoints)
    {
        PageWidthPoints = pageWidthPoints;
        PageHeightPoints = pageHeightPoints;
        MarginLeftPoints = marginLeftPoints;
        MarginRightPoints = marginRightPoints;
        MarginTopPoints = marginTopPoints;
        MarginBottomPoints = marginBottomPoints;
        UsesTwoColumns = usesTwoColumns;
        Kind = kind;
        BodyFontSizePoints = bodyFontSizePoints;
        LeadingEm = leadingEm;
        FirstLineIndentPoints = firstLineIndentPoints;
    }

    public double PageWidthPoints { get; }

    public double PageHeightPoints { get; }

    public double MarginLeftPoints { get; }

    public double MarginRightPoints { get; }

    public double MarginTopPoints { get; }

    public double MarginBottomPoints { get; }

    public bool UsesTwoColumns { get; }

    public DocumentCompositionKind Kind { get; }

    public double BodyFontSizePoints { get; }

    public double LeadingEm { get; }

    public double FirstLineIndentPoints { get; }

    public double ContentWidthPoints => Math.Max(180d, PageWidthPoints - MarginLeftPoints - MarginRightPoints);

    public double ContentHeightPoints => Math.Max(180d, PageHeightPoints - MarginTopPoints - MarginBottomPoints);

    public static DocumentCompositionProfile Analyze(
        IReadOnlyList<BookPageInfo> pages,
        IReadOnlyList<OcrPageResult> ocrPages,
        IReadOnlyList<string> pageTexts,
        IReadOnlyList<RescuedImageInfo> rescuedImages)
    {
        var pageWidth = MedianOrDefault(
            pages.Select(page => (double)page.WidthPoints).Where(value => value >= 180d),
            612d);
        var pageHeight = MedianOrDefault(
            pages.Select(page => (double)page.HeightPoints).Where(value => value >= 220d),
            792d);

        pageWidth = Math.Clamp(pageWidth, 288d, 1224d);
        pageHeight = Math.Clamp(pageHeight, 360d, 1584d);

        var measuredMargins = MeasureMargins(pages, ocrPages, pageWidth, pageHeight);
        var left = ClampMargin(measuredMargins.Left, pageWidth, 0.07d, horizontal: true);
        var right = ClampMargin(measuredMargins.Right, pageWidth, 0.07d, horizontal: true);
        var top = ClampMargin(measuredMargins.Top, pageHeight, 0.055d, horizontal: false);
        var bottom = ClampMargin(measuredMargins.Bottom, pageHeight, 0.055d, horizontal: false);

        if (pageWidth - left - right < 260d)
        {
            left = Math.Min(left, 42d);
            right = Math.Min(right, 42d);
        }

        var text = string.Join('\n', pageTexts);
        var formulas = pageTexts.Count(ContainsFormulaSignals);
        var markdownTables = pageTexts.Count(text => text.Contains('|'));
        var tableImages = rescuedImages.Count(image => image.Kind.Equals("table", StringComparison.OrdinalIgnoreCase));
        var usesTwoColumns = DetectTwoColumns(pages, ocrPages);
        var kind = DetectKind(text, formulas, markdownTables + tableImages, usesTwoColumns);

        var bodySize = kind switch
        {
            DocumentCompositionKind.ScientificPaper => 10.0d,
            DocumentCompositionKind.TechnicalManual => 10.4d,
            _ => 10.8d
        };

        var leading = kind == DocumentCompositionKind.Book ? 0.62d : 0.56d;
        var indent = kind == DocumentCompositionKind.ScientificPaper ? 8d : 12d;

        return new DocumentCompositionProfile(
            pageWidth,
            pageHeight,
            left,
            right,
            top,
            bottom,
            usesTwoColumns,
            kind,
            bodySize,
            leading,
            indent);
    }

    public double GetImageWidthPercent(RescuedImageInfo image)
    {
        var widthRatio = image.Width / Math.Max(1d, image.PagePixelWidth);
        var heightRatio = image.Height / Math.Max(1d, image.PagePixelHeight);
        var areaRatio = widthRatio * heightRatio;
        var desired = widthRatio * 100d;

        if (image.Kind.Equals("table", StringComparison.OrdinalIgnoreCase))
        {
            return Math.Clamp(desired, 18d, 92d);
        }

        var max = areaRatio < 0.05d ? 48d : 72d;
        return Math.Clamp(desired, 14d, max);
    }

    public double GetImageWidthPoints(RescuedImageInfo image)
    {
        return ContentWidthPoints * GetImageWidthPercent(image) / 100d;
    }

    public bool PageLikelyUsesTwoColumns(BookPageInfo page, OcrPageResult ocrPage)
    {
        return DetectTwoColumnsOnPage(page, ocrPage);
    }

    private static DocumentCompositionKind DetectKind(string text, int formulaPages, int tableCount, bool usesTwoColumns)
    {
        if (usesTwoColumns ||
            text.Contains("abstract", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("references", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("doi", StringComparison.OrdinalIgnoreCase))
        {
            return DocumentCompositionKind.ScientificPaper;
        }

        if (formulaPages > 0 ||
            tableCount > 0 ||
            text.Contains("ASHRAE", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("chapter", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("capitulo", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("capítulo", StringComparison.OrdinalIgnoreCase))
        {
            return DocumentCompositionKind.TechnicalManual;
        }

        return DocumentCompositionKind.Book;
    }

    private static bool ContainsFormulaSignals(string text)
    {
        return text.Contains('$') ||
            text.Contains('=') && (text.Contains('/') || text.Contains('Δ') || text.Contains('∑') || text.Contains('√'));
    }

    private static (double Left, double Right, double Top, double Bottom) MeasureMargins(
        IReadOnlyList<BookPageInfo> pages,
        IReadOnlyList<OcrPageResult> ocrPages,
        double pageWidthPoints,
        double pageHeightPoints)
    {
        var lefts = new List<double>();
        var rights = new List<double>();
        var tops = new List<double>();
        var bottoms = new List<double>();

        for (var i = 0; i < pages.Count && i < ocrPages.Count; i++)
        {
            var page = pages[i];
            var lines = TextCleanupService.BuildOrderedLineBoxes(page, ocrPages[i])
                .Where(line => line.Width > page.PixelWidth * 0.015f && line.Height > 2)
                .ToList();
            if (lines.Count < 4)
            {
                continue;
            }

            var scaleX = pageWidthPoints / Math.Max(1d, page.PixelWidth);
            var scaleY = pageHeightPoints / Math.Max(1d, page.PixelHeight);
            var left = lines.Min(line => line.X) * scaleX;
            var right = pageWidthPoints - lines.Max(line => line.X + line.Width) * scaleX;
            var top = lines.Min(line => line.Y) * scaleY;
            var bottom = pageHeightPoints - lines.Max(line => line.Y + line.Height) * scaleY;

            if (left >= 0 && right >= 0 && top >= 0 && bottom >= 0)
            {
                lefts.Add(left);
                rights.Add(right);
                tops.Add(top);
                bottoms.Add(bottom);
            }
        }

        return (
            MedianOrDefault(lefts, pageWidthPoints * 0.07d),
            MedianOrDefault(rights, pageWidthPoints * 0.07d),
            MedianOrDefault(tops, pageHeightPoints * 0.055d),
            MedianOrDefault(bottoms, pageHeightPoints * 0.055d));
    }

    private static double ClampMargin(double value, double pageSize, double fallbackRatio, bool horizontal)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
        {
            value = pageSize * fallbackRatio;
        }

        var minimum = horizontal ? 24d : 22d;
        var maximum = horizontal
            ? Math.Min(108d, Math.Max(30d, pageSize * 0.22d))
            : Math.Min(96d, Math.Max(28d, pageSize * 0.17d));

        return Math.Clamp(value, minimum, maximum);
    }

    private static bool DetectTwoColumns(IReadOnlyList<BookPageInfo> pages, IReadOnlyList<OcrPageResult> ocrPages)
    {
        if (pages.Count == 0 || ocrPages.Count == 0)
        {
            return false;
        }

        var inspected = 0;
        var matches = 0;
        for (var i = 0; i < pages.Count && i < ocrPages.Count; i++)
        {
            if (ocrPages[i].Lines.Count < 12)
            {
                continue;
            }

            inspected++;
            if (DetectTwoColumnsOnPage(pages[i], ocrPages[i]))
            {
                matches++;
            }
        }

        return inspected > 0 && matches / (double)inspected >= 0.22d;
    }

    private static bool DetectTwoColumnsOnPage(BookPageInfo page, OcrPageResult ocrPage)
    {
        var lines = TextCleanupService.BuildOrderedLineBoxes(page, ocrPage);
        if (lines.Count < 12)
        {
            return false;
        }

        var pageWidth = Math.Max(1d, page.PixelWidth);
        var left = lines.Where(line => line.X + (line.Width / 2f) < pageWidth * 0.50d).ToList();
        var right = lines.Where(line => line.X + (line.Width / 2f) >= pageWidth * 0.50d).ToList();
        if (left.Count < 6 || right.Count < 6)
        {
            return false;
        }

        var leftCenter = MedianOrDefault(left.Select(line => (double)(line.X + line.Width / 2f)), 0d);
        var rightCenter = MedianOrDefault(right.Select(line => (double)(line.X + line.Width / 2f)), 0d);
        var leftWidth = MedianOrDefault(left.Select(line => (double)line.Width), 0d);
        var rightWidth = MedianOrDefault(right.Select(line => (double)line.Width), 0d);

        return rightCenter - leftCenter > pageWidth * 0.25d &&
            leftWidth < pageWidth * 0.55d &&
            rightWidth < pageWidth * 0.55d;
    }

    private static double MedianOrDefault(IEnumerable<double> values, double fallback)
    {
        var ordered = values
            .Where(value => !double.IsNaN(value) && !double.IsInfinity(value) && value > 0)
            .Order()
            .ToList();
        if (ordered.Count == 0)
        {
            return fallback;
        }

        var middle = ordered.Count / 2;
        return ordered.Count % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2d
            : ordered[middle];
    }
}
