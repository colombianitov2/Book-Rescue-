using BookRescue.App.Models;
using OpenCvSharp;

namespace BookRescue.App.Services;

public sealed record DetectedVisualRegion(
    string Kind,
    float X,
    float Y,
    float Width,
    float Height,
    float Confidence,
    string PreservationStrategy,
    string AssociatedText = "",
    int AssociatedLineCount = 0,
    int AssociatedWordCount = 0,
    float AssociatedOcrConfidence = 0,
    string ArtifactPath = "",
    string Warning = "");

public sealed class VisualRegionDetector
{
    private readonly ScanDamageClassifier damageClassifier = new();

    public IReadOnlyList<DetectedVisualRegion> Detect(
        BookPageInfo page,
        OcrPageResult ocrPage,
        IReadOnlyList<RescuedImageInfo> pageImages)
    {
        var regions = new List<DetectedVisualRegion>
        {
            new(
                "background",
                0,
                0,
                page.PixelWidth,
                page.PixelHeight,
                100,
                "clean-dominant-color-background")
        };

        var textBlocks = DocumentLayoutService.BuildTextBlocks(page, ocrPage);
        regions.AddRange(textBlocks.Select(block =>
            new DetectedVisualRegion(
                block.IsHeading ? "heading" : "text",
                block.X,
                block.Y,
                block.Width,
                block.Height,
                EstimateBlockConfidence(block, ocrPage),
                "visible-digital-text-layer")));

        var rescuedVisualRegions = pageImages
            .Select(image =>
                new DetectedVisualRegion(
                    NormalizeVisualKind(image, ocrPage),
                    image.X,
                    image.Y,
                    image.Width,
                    image.Height,
                    100,
                    "regional-visual-fallback",
                    ArtifactPath: image.ImagePath))
            .ToList();
        regions.AddRange(rescuedVisualRegions);

        var inferredVisualRegions = DetectGraphicalContentRegions(page, ocrPage, rescuedVisualRegions);
        regions.AddRange(inferredVisualRegions);

        var protectedVisualRegions = rescuedVisualRegions
            .Concat(inferredVisualRegions)
            .Where(region => region.Kind is "diagram" or "graphical-element" or "image" or "table")
            .ToList();

        var damageRegions = damageClassifier.DetectDamage(page, ocrPage, pageImages, protectedVisualRegions);
        regions.AddRange(DetectEditorialDecorations(page, ocrPage, pageImages)
            .Where(decoration => !damageRegions.Any(damage => IsDiscardedDamage(damage) && RegionOverlapRatio(decoration, damage) > 0.10d)));
        regions.AddRange(damageRegions);

        return regions
            .OrderBy(region => region.Y)
            .ThenBy(region => region.X)
            .ToList();
    }

    private static IReadOnlyList<DetectedVisualRegion> DetectEditorialDecorations(
        BookPageInfo page,
        OcrPageResult ocrPage,
        IReadOnlyList<RescuedImageInfo> pageImages)
    {
        var imagePath = !string.IsNullOrWhiteSpace(page.RestoredImagePath) && File.Exists(page.RestoredImagePath)
            ? page.RestoredImagePath
            : page.OriginalImagePath;
        if (!File.Exists(imagePath))
        {
            return [];
        }

        using var source = Cv2.ImRead(imagePath, ImreadModes.Color);
        if (source.Empty())
        {
            return [];
        }

        using var gray = new Mat();
        Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);

        using var ink = new Mat();
        Cv2.Threshold(gray, ink, 90, 255, ThresholdTypes.BinaryInv);

        using var protectedMask = new Mat(source.Rows, source.Cols, MatType.CV_8UC1, Scalar.Black);
        foreach (var line in ocrPage.Lines)
        {
            Cv2.Rectangle(protectedMask, Expand(ToRect(line.X, line.Y, line.Width, line.Height), source.Width, source.Height, 8), Scalar.White, -1);
        }

        foreach (var image in pageImages)
        {
            Cv2.Rectangle(protectedMask, Expand(ToRect(image.X, image.Y, image.Width, image.Height), source.Width, source.Height, 14), Scalar.White, -1);
        }

        ink.SetTo(Scalar.Black, protectedMask);

        var decorations = new List<DetectedVisualRegion>();
        AddLineDecorations(ink, decorations, source.Width, source.Height, horizontal: true);
        AddLineDecorations(ink, decorations, source.Width, source.Height, horizontal: false);
        return decorations
            .OrderBy(region => region.Y)
            .ThenBy(region => region.X)
            .Take(60)
            .ToList();
    }

    private static IReadOnlyList<DetectedVisualRegion> DetectGraphicalContentRegions(
        BookPageInfo page,
        OcrPageResult ocrPage,
        IReadOnlyList<DetectedVisualRegion> knownVisualRegions)
    {
        var imagePath = !string.IsNullOrWhiteSpace(page.RestoredImagePath) && File.Exists(page.RestoredImagePath)
            ? page.RestoredImagePath
            : page.OriginalImagePath;
        if (!File.Exists(imagePath))
        {
            return [];
        }

        using var source = Cv2.ImRead(imagePath, ImreadModes.Color);
        if (source.Empty())
        {
            return [];
        }

        using var hsv = new Mat();
        Cv2.CvtColor(source, hsv, ColorConversionCodes.BGR2HSV);
        Cv2.Split(hsv, out var channels);
        using var saturation = channels[1];
        using var value = channels[2];
        channels[0].Dispose();

        using var saturated = new Mat();
        Cv2.Threshold(saturation, saturated, 18, 255, ThresholdTypes.Binary);
        using var bright = new Mat();
        Cv2.Threshold(value, bright, 80, 255, ThresholdTypes.Binary);
        using var mask = new Mat();
        Cv2.BitwiseAnd(saturated, bright, mask);

        using var closeKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(31, 31));
        Cv2.MorphologyEx(mask, mask, MorphTypes.Close, closeKernel);
        Cv2.Dilate(mask, mask, closeKernel, iterations: 1);

        Cv2.FindContours(mask, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        var pageArea = source.Width * source.Height;
        var regions = new List<DetectedVisualRegion>();

        void TryAddRegion(Rect rect, string sourceHint)
        {
            rect = Expand(rect, source.Width, source.Height, 16);
            var area = rect.Width * rect.Height;
            if (!LooksLikeLargeGraphicRect(rect, area, pageArea, source.Width, source.Height))
            {
                return;
            }

            if (knownVisualRegions.Any(region => RegionOverlapRatio(
                    region,
                    new DetectedVisualRegion("candidate", rect.X, rect.Y, rect.Width, rect.Height, 1, "")) > 0.55d))
            {
                return;
            }

            if (regions.Any(region => RegionOverlapRatio(
                    region,
                    new DetectedVisualRegion("candidate", rect.X, rect.Y, rect.Width, rect.Height, 1, "")) > 0.55d))
            {
                return;
            }

            using var crop = new Mat(source, rect);
            var hasCaption = HasNearbyFigureCaption(rect.X, rect.Y, rect.Width, rect.Height, ocrPage);
            var linework = LooksLikeGraphicalElement(crop) || LooksLikeTechnicalFigureCrop(crop);
            if (!hasCaption && !linework)
            {
                return;
            }

            regions.Add(new DetectedVisualRegion(
                hasCaption ? "diagram" : "graphical-element",
                rect.X,
                rect.Y,
                rect.Width,
                rect.Height,
                hasCaption ? 91 : 82,
                "protect-technical-graphic",
                Warning: hasCaption
                    ? "Figura protegida antes de detectar daño; sus líneas internas se conservan como contenido."
                    : $"Elemento gráfico protegido antes de detectar daño ({sourceHint})."));
        }

        foreach (var contour in contours.OrderByDescending(contour => Cv2.ContourArea(contour)))
        {
            TryAddRegion(Cv2.BoundingRect(contour), "color");
        }

        using var gray = new Mat();
        Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);
        using var edges = new Mat();
        Cv2.Canny(gray, edges, 50, 150);
        using var textMask = new Mat(source.Rows, source.Cols, MatType.CV_8UC1, Scalar.Black);
        foreach (var line in ocrPage.Lines)
        {
            Cv2.Rectangle(textMask, Expand(ToRect(line.X, line.Y, line.Width, line.Height), source.Width, source.Height, 10), Scalar.White, -1);
        }

        edges.SetTo(Scalar.Black, textMask);
        foreach (var rect in DetectLinePairGraphicRects(edges, source.Width, source.Height, ocrPage))
        {
            TryAddRegion(rect, "line-pair");
        }

        using var edgeKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(25, 19));
        Cv2.MorphologyEx(edges, edges, MorphTypes.Close, edgeKernel);
        Cv2.Dilate(edges, edges, edgeKernel, iterations: 1);
        Cv2.FindContours(edges, out var edgeContours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        foreach (var contour in edgeContours.OrderByDescending(contour => Cv2.ContourArea(contour)))
        {
            TryAddRegion(Cv2.BoundingRect(contour), "linework");
        }

        return regions
            .OrderBy(region => region.Y)
            .ThenBy(region => region.X)
            .Take(40)
            .ToList();
    }

    private static string NormalizeVisualKind(RescuedImageInfo image, OcrPageResult ocrPage)
    {
        if (!File.Exists(image.ImagePath))
        {
            return image.Kind.Equals("table", StringComparison.OrdinalIgnoreCase) ? "table" : "image";
        }

        using var crop = Cv2.ImRead(image.ImagePath, ImreadModes.Color);
        if (crop.Empty())
        {
            return image.Kind.Equals("table", StringComparison.OrdinalIgnoreCase) ? "table" : "image";
        }

        var hasFigureCaption = HasNearbyFigureCaption(image.X, image.Y, image.Width, image.Height, ocrPage);
        if (LooksLikeGraphicalElement(crop) || hasFigureCaption && LooksLikeTechnicalFigureCrop(crop))
        {
            return "diagram";
        }

        if (image.Kind.Equals("table", StringComparison.OrdinalIgnoreCase))
        {
            return "table";
        }

        return image.Kind.Equals("diagram", StringComparison.OrdinalIgnoreCase) ||
            image.Kind.Equals("graphical-element", StringComparison.OrdinalIgnoreCase)
                ? "diagram"
                : "image";
    }

    private static bool LooksLikeGraphicalElement(Mat crop)
    {
        if (crop.Width < 120 || crop.Height < 90)
        {
            return false;
        }

        using var gray = new Mat();
        Cv2.CvtColor(crop, gray, ColorConversionCodes.BGR2GRAY);

        using var edges = new Mat();
        Cv2.Canny(gray, edges, 50, 160);
        var edgeRatio = Cv2.CountNonZero(edges) / (double)Math.Max(1, crop.Width * crop.Height);

        using var horizontalKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(Math.Max(16, crop.Width / 12), 1));
        using var verticalKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(1, Math.Max(12, crop.Height / 12)));
        using var horizontal = new Mat();
        using var vertical = new Mat();
        Cv2.MorphologyEx(edges, horizontal, MorphTypes.Open, horizontalKernel);
        Cv2.MorphologyEx(edges, vertical, MorphTypes.Open, verticalKernel);

        var lineRatio = (Cv2.CountNonZero(horizontal) + Cv2.CountNonZero(vertical)) / (double)Math.Max(1, crop.Width * crop.Height);
        var colorfulness = EstimateColorfulness(crop);
        var hasBorderOrDiagramLines = edgeRatio > 0.010 && lineRatio > 0.0025;
        var hasMeaningfulColor = colorfulness > 8.0;

        return hasBorderOrDiagramLines && (hasMeaningfulColor || crop.Width * crop.Height > 35_000);
    }

    private static bool LooksLikeTechnicalFigureCrop(Mat crop)
    {
        if (crop.Width < 140 || crop.Height < 90)
        {
            return false;
        }

        using var gray = new Mat();
        Cv2.CvtColor(crop, gray, ColorConversionCodes.BGR2GRAY);
        using var edges = new Mat();
        Cv2.Canny(gray, edges, 45, 145);
        var edgeRatio = Cv2.CountNonZero(edges) / (double)Math.Max(1, crop.Width * crop.Height);

        var lines = Cv2.HoughLinesP(
            edges,
            1,
            Math.PI / 180,
            32,
            Math.Max(35, Math.Min(crop.Width, crop.Height) / 5),
            18);
        var longLineCount = lines.Count(line =>
        {
            var dx = line.P2.X - line.P1.X;
            var dy = line.P2.Y - line.P1.Y;
            return Math.Sqrt((dx * dx) + (dy * dy)) > Math.Min(crop.Width, crop.Height) * 0.18;
        });

        var colorfulness = EstimateColorfulness(crop);
        return edgeRatio > 0.006 && longLineCount >= 2 && (colorfulness > 5.5 || crop.Width * crop.Height > 60_000);
    }

    private static IReadOnlyList<Rect> DetectLinePairGraphicRects(Mat edgeMask, int pageWidth, int pageHeight, OcrPageResult ocrPage)
    {
        var lines = Cv2.HoughLinesP(
            edgeMask,
            1,
            Math.PI / 180,
            48,
            Math.Max(120, pageWidth / 4),
            24);
        if (lines.Length < 2)
        {
            return [];
        }

        var horizontal = lines
            .Where(line => IsHorizontal(line) && SegmentLength(line) >= pageWidth * 0.22)
            .OrderBy(line => Math.Min(line.P1.Y, line.P2.Y))
            .ToList();
        var candidates = new List<Rect>();
        for (var i = 0; i < horizontal.Count; i++)
        {
            for (var j = i + 1; j < horizontal.Count; j++)
            {
                var first = horizontal[i];
                var second = horizontal[j];
                var y1 = (first.P1.Y + first.P2.Y) / 2;
                var y2 = (second.P1.Y + second.P2.Y) / 2;
                var separation = Math.Abs(y2 - y1);
                if (separation < pageHeight * 0.06 || separation > pageHeight * 0.55)
                {
                    continue;
                }

                var x1 = Math.Min(Math.Min(first.P1.X, first.P2.X), Math.Min(second.P1.X, second.P2.X));
                var x2 = Math.Max(Math.Max(first.P1.X, first.P2.X), Math.Max(second.P1.X, second.P2.X));
                var width = x2 - x1;
                if (width < pageWidth * 0.28)
                {
                    continue;
                }

                var rect = new Rect(
                    Math.Max(0, x1 - 12),
                    Math.Max(0, Math.Min(y1, y2) - 18),
                    Math.Min(pageWidth - Math.Max(0, x1 - 12), width + 24),
                    Math.Min(pageHeight - Math.Max(0, Math.Min(y1, y2) - 18), separation + 36));
                if (!HasNearbyFigureCaption(rect.X, rect.Y, rect.Width, rect.Height, ocrPage))
                {
                    continue;
                }

                if (candidates.Any(existing => RectOverlapRatio(existing, rect) > 0.55d))
                {
                    continue;
                }

                candidates.Add(rect);
            }
        }

        return candidates;
    }

    private static bool LooksLikeLargeGraphicRect(Rect rect, int area, int pageArea, int pageWidth, int pageHeight)
    {
        if (rect.Width < pageWidth * 0.16 || rect.Height < pageHeight * 0.07)
        {
            return false;
        }

        if (area < pageArea * 0.012 || area > pageArea * 0.48)
        {
            return false;
        }

        var ratio = rect.Width / (double)Math.Max(1, rect.Height);
        return ratio is >= 0.28 and <= 7.5;
    }

    private static bool IsHorizontal(LineSegmentPoint line)
    {
        var dx = line.P2.X - line.P1.X;
        var dy = line.P2.Y - line.P1.Y;
        if (dx == 0)
        {
            return false;
        }

        var angle = Math.Abs(Math.Atan2(dy, dx) * 180d / Math.PI);
        return angle <= 7 || angle >= 173;
    }

    private static double SegmentLength(LineSegmentPoint line)
    {
        var dx = line.P2.X - line.P1.X;
        var dy = line.P2.Y - line.P1.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static bool HasNearbyFigureCaption(float x, float y, float width, float height, OcrPageResult ocrPage)
    {
        var bottom = y + height;
        var top = y;
        foreach (var line in ocrPage.Lines)
        {
            if (!line.Text.Contains("Figure", StringComparison.OrdinalIgnoreCase) &&
                !line.Text.Contains("Figura", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var lineCenterY = line.Y + (line.Height / 2f);
            var nearBelow = lineCenterY >= bottom - 20 && lineCenterY <= bottom + Math.Max(90, height * 0.20f);
            var nearAbove = lineCenterY >= top - Math.Max(70, height * 0.12f) && lineCenterY <= top + 20;
            if (!nearBelow && !nearAbove)
            {
                continue;
            }

            var overlap = HorizontalOverlapRatio(x, width, line.X, line.Width);
            if (overlap > 0.18d)
            {
                return true;
            }
        }

        return false;
    }

    private static double EstimateColorfulness(Mat image)
    {
        using var small = new Mat();
        Cv2.Resize(image, small, new Size(Math.Min(64, image.Width), Math.Min(64, image.Height)));

        var total = 0d;
        for (var y = 0; y < small.Rows; y++)
        {
            for (var x = 0; x < small.Cols; x++)
            {
                var pixel = small.At<Vec3b>(y, x);
                var max = Math.Max(pixel.Item0, Math.Max(pixel.Item1, pixel.Item2));
                var min = Math.Min(pixel.Item0, Math.Min(pixel.Item1, pixel.Item2));
                total += max - min;
            }
        }

        return total / Math.Max(1, small.Rows * small.Cols);
    }

    private static void AddLineDecorations(Mat ink, List<DetectedVisualRegion> regions, int pageWidth, int pageHeight, bool horizontal)
    {
        var kernelSize = horizontal
            ? new Size(Math.Max(24, pageWidth / 12), 1)
            : new Size(1, Math.Max(24, pageHeight / 12));
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, kernelSize);
        using var lines = new Mat();
        Cv2.MorphologyEx(ink, lines, MorphTypes.Open, kernel);
        Cv2.FindContours(lines, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        foreach (var contour in contours)
        {
            var rect = Cv2.BoundingRect(contour);
            var longEnough = horizontal
                ? rect.Width >= pageWidth * 0.18 && rect.Height <= Math.Max(10, pageHeight * 0.025)
                : rect.Height >= pageHeight * 0.12 && rect.Width <= Math.Max(10, pageWidth * 0.025);
            if (!longEnough)
            {
                continue;
            }

            regions.Add(new DetectedVisualRegion(
                "decoration",
                rect.X,
                rect.Y,
                rect.Width,
                rect.Height,
                72,
                "preserve-editorial-decoration"));
        }
    }

    private static float EstimateBlockConfidence(PageTextBlock block, OcrPageResult ocrPage)
    {
        var overlapping = ocrPage.Lines
            .Where(line => Overlaps(block.X, block.Y, block.Width, block.Height, line.X, line.Y, line.Width, line.Height))
            .Select(line => line.Confidence)
            .ToList();

        return overlapping.Count == 0 ? 0 : overlapping.Average();
    }

    private static bool Overlaps(float ax, float ay, float aw, float ah, float bx, float by, float bw, float bh)
    {
        var x1 = Math.Max(ax, bx);
        var y1 = Math.Max(ay, by);
        var x2 = Math.Min(ax + aw, bx + bw);
        var y2 = Math.Min(ay + ah, by + bh);
        return x2 > x1 && y2 > y1;
    }

    private static double RegionOverlapRatio(DetectedVisualRegion first, DetectedVisualRegion second)
    {
        var x1 = Math.Max(first.X, second.X);
        var y1 = Math.Max(first.Y, second.Y);
        var x2 = Math.Min(first.X + first.Width, second.X + second.Width);
        var y2 = Math.Min(first.Y + first.Height, second.Y + second.Height);
        if (x2 <= x1 || y2 <= y1)
        {
            return 0d;
        }

        var intersection = (x2 - x1) * (y2 - y1);
        var smaller = Math.Min(first.Width * first.Height, second.Width * second.Height);
        return intersection / Math.Max(1d, smaller);
    }

    private static double HorizontalOverlapRatio(float ax, float aw, float bx, float bw)
    {
        var x1 = Math.Max(ax, bx);
        var x2 = Math.Min(ax + aw, bx + bw);
        if (x2 <= x1)
        {
            return 0d;
        }

        return (x2 - x1) / Math.Max(1d, Math.Min(aw, bw));
    }

    private static double RectOverlapRatio(Rect first, Rect second)
    {
        var x1 = Math.Max(first.X, second.X);
        var y1 = Math.Max(first.Y, second.Y);
        var x2 = Math.Min(first.Right, second.Right);
        var y2 = Math.Min(first.Bottom, second.Bottom);
        if (x2 <= x1 || y2 <= y1)
        {
            return 0d;
        }

        var intersection = (x2 - x1) * (y2 - y1);
        var smaller = Math.Min(first.Width * first.Height, second.Width * second.Height);
        return intersection / Math.Max(1d, smaller);
    }

    private static bool IsDiscardedDamage(DetectedVisualRegion region)
    {
        return region.PreservationStrategy == "discarded-scan-defect" ||
            region.Kind is "scan-damage" or "crack" or "stain" or "noise";
    }

    private static Rect ToRect(float x, float y, float width, float height)
    {
        return new Rect(
            (int)MathF.Round(x),
            (int)MathF.Round(y),
            Math.Max(1, (int)MathF.Round(width)),
            Math.Max(1, (int)MathF.Round(height)));
    }

    private static Rect Expand(Rect rect, int maxWidth, int maxHeight, int padding)
    {
        var x = Math.Max(0, rect.X - padding);
        var y = Math.Max(0, rect.Y - padding);
        var right = Math.Min(maxWidth, rect.Right + padding);
        var bottom = Math.Min(maxHeight, rect.Bottom + padding);
        return new Rect(x, y, Math.Max(1, right - x), Math.Max(1, bottom - y));
    }
}
