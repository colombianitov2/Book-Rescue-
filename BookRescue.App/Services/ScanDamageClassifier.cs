using BookRescue.App.Models;
using OpenCvSharp;

namespace BookRescue.App.Services;

public sealed class ScanDamageClassifier
{
    public IReadOnlyList<DetectedVisualRegion> DetectDamage(
        BookPageInfo page,
        OcrPageResult ocrPage,
        IReadOnlyList<RescuedImageInfo> pageImages,
        IReadOnlyList<DetectedVisualRegion>? protectedVisualRegions = null)
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

        using var dark = new Mat();
        Cv2.Threshold(gray, dark, 86, 255, ThresholdTypes.BinaryInv);

        using var protectedMask = new Mat(source.Rows, source.Cols, MatType.CV_8UC1, Scalar.Black);
        foreach (var line in ocrPage.Lines)
        {
            Cv2.Rectangle(protectedMask, Expand(ToRect(line.X, line.Y, line.Width, line.Height), source.Width, source.Height, 8), Scalar.White, -1);
        }

        foreach (var image in pageImages)
        {
            Cv2.Rectangle(protectedMask, Expand(ToRect(image.X, image.Y, image.Width, image.Height), source.Width, source.Height, 18), Scalar.White, -1);
        }

        if (protectedVisualRegions is not null)
        {
            foreach (var region in protectedVisualRegions.Where(region => region.Kind is "diagram" or "graphical-element" or "image" or "table"))
            {
                Cv2.Rectangle(protectedMask, Expand(ToRect(region.X, region.Y, region.Width, region.Height), source.Width, source.Height, 18), Scalar.White, -1);
            }
        }

        var damage = new List<DetectedVisualRegion>();
        AddLongLineCracks(gray, protectedMask, damage, source.Width, source.Height);
        AddSpecklesAndStains(gray, protectedMask, damage, source.Width, source.Height);

        using var protectedDark = new Mat();
        Cv2.BitwiseAnd(dark, protectedMask, protectedDark);
        AddProtectedDamageWarnings(protectedDark, damage, source.Width, source.Height);

        return damage
            .OrderBy(region => region.Y)
            .ThenBy(region => region.X)
            .Take(180)
            .ToList();
    }

    private static void AddLongLineCracks(Mat gray, Mat protectedMask, List<DetectedVisualRegion> damage, int pageWidth, int pageHeight)
    {
        using var blurred = new Mat();
        Cv2.GaussianBlur(gray, blurred, new Size(3, 3), 0);

        using var edges = new Mat();
        Cv2.Canny(blurred, edges, 40, 140);

        var minLineLength = Math.Max(70, Math.Min(pageWidth, pageHeight) / 7);
        var lines = Cv2.HoughLinesP(edges, 1, Math.PI / 180, 38, minLineLength, 26);
        foreach (var line in lines)
        {
            var dx = line.P2.X - line.P1.X;
            var dy = line.P2.Y - line.P1.Y;
            var length = Math.Sqrt((dx * dx) + (dy * dy));
            if (length < minLineLength)
            {
                continue;
            }

            var angle = Math.Abs(Math.Atan2(dy, dx) * 180d / Math.PI);
            angle = angle > 90 ? 180 - angle : angle;
            var likelyCrackAngle = angle is >= 18 and <= 82 || length > Math.Max(pageWidth, pageHeight) * 0.42;
            if (!likelyCrackAngle)
            {
                continue;
            }

            var rect = Expand(
                new Rect(
                    Math.Min(line.P1.X, line.P2.X),
                    Math.Min(line.P1.Y, line.P2.Y),
                    Math.Max(1, Math.Abs(dx)),
                    Math.Max(1, Math.Abs(dy))),
                pageWidth,
                pageHeight,
                10);

            if (rect.Width * rect.Height < 32)
            {
                continue;
            }

            var protectedRatio = MaskRatio(protectedMask, rect);
            if (protectedRatio > 0.82d)
            {
                AddIfNotDuplicate(damage, new DetectedVisualRegion(
                    "protected-diagram-line",
                    rect.X,
                    rect.Y,
                    rect.Width,
                    rect.Height,
                    82,
                    "protected-content-line-kept",
                    Warning: "Linea oscura dentro de diagrama, tabla, imagen o elemento grafico; se conserva como contenido real."));
                continue;
            }

            var strategy = protectedRatio > 0.06
                ? "protected-content-kept"
                : "discarded-scan-defect";

            AddIfNotDuplicate(damage, new DetectedVisualRegion(
                "crack",
                rect.X,
                rect.Y,
                rect.Width,
                rect.Height,
                92,
                strategy,
                Warning: protectedRatio > 0.06
                    ? "Grieta cruza texto, diagrama o recorte protegido; se evita borrar contenido útil."
                    : string.Empty));
        }
    }

    private static void AddSpecklesAndStains(Mat gray, Mat protectedMask, List<DetectedVisualRegion> damage, int pageWidth, int pageHeight)
    {
        using var dark = new Mat();
        Cv2.Threshold(gray, dark, 138, 255, ThresholdTypes.BinaryInv);
        dark.SetTo(Scalar.Black, protectedMask);

        using var grouped = new Mat();
        dark.CopyTo(grouped);
        using var closeKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(11, 11));
        Cv2.MorphologyEx(grouped, grouped, MorphTypes.Close, closeKernel);

        Cv2.FindContours(grouped, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        var pageArea = pageWidth * pageHeight;
        var savedNoise = 0;
        var savedStains = 0;

        foreach (var contour in contours.OrderBy(contour => Cv2.BoundingRect(contour).Y).ThenBy(contour => Cv2.BoundingRect(contour).X))
        {
            var rect = Cv2.BoundingRect(contour);
            var area = rect.Width * rect.Height;
            if (area < 2 || area > pageArea * 0.01)
            {
                continue;
            }

            var ratio = rect.Width / (double)Math.Max(1, rect.Height);
            string kind;
            float confidence;
            if (rect.Width <= 70 && rect.Height <= 70)
            {
                if (savedNoise >= 45)
                {
                    continue;
                }

                kind = "noise";
                confidence = 68;
                savedNoise++;
            }
            else if (rect.Width >= 8 && rect.Height >= 8 && ratio is >= 0.18 and <= 5.5)
            {
                if (savedStains >= 50)
                {
                    continue;
                }

                kind = "stain";
                confidence = 70;
                savedStains++;
            }
            else
            {
                var classified = ClassifyDamage(rect, area, pageArea);
                if (string.IsNullOrWhiteSpace(classified))
                {
                    continue;
                }

                kind = classified;
                confidence = 68;
            }

            AddIfNotDuplicate(damage, new DetectedVisualRegion(
                kind,
                rect.X,
                rect.Y,
                rect.Width,
                rect.Height,
                confidence,
                "discarded-scan-defect",
                Warning: kind == "noise" ? "Ruido agrupado fuera de contenido protegido." : string.Empty));
        }

        if (savedNoise == 0 && savedStains == 0)
        {
            AddResidualNoiseIfPresent(dark, damage, pageWidth, pageHeight);
        }
    }

    private static void AddResidualNoiseIfPresent(Mat darkMask, List<DetectedVisualRegion> damage, int pageWidth, int pageHeight)
    {
        if (Cv2.CountNonZero(darkMask) == 0)
        {
            return;
        }

        for (var y = 0; y < darkMask.Rows; y++)
        {
            for (var x = 0; x < darkMask.Cols; x++)
            {
                if (darkMask.At<byte>(y, x) == 0)
                {
                    continue;
                }

                var rect = Expand(new Rect(x, y, 1, 1), pageWidth, pageHeight, 4);
                AddIfNotDuplicate(damage, new DetectedVisualRegion(
                    "noise",
                    rect.X,
                    rect.Y,
                    rect.Width,
                    rect.Height,
                    58,
                    "discarded-scan-defect",
                    Warning: "Ruido residual oscuro detectado fuera de contenido protegido."));
                return;
            }
        }
    }

    private static string ClassifyDamage(Rect rect, int area, int pageArea)
    {
        if (area < 9 || area > pageArea * 0.035)
        {
            return string.Empty;
        }

        var ratio = rect.Width / (double)Math.Max(1, rect.Height);
        var slender = ratio > 5.5 || ratio < 0.18;
        var speckle = rect.Width <= 22 && rect.Height <= 22;
        var stain = area > pageArea * 0.00035 && rect.Width >= 18 && rect.Height >= 18 && ratio is >= 0.22 and <= 4.6;

        if (slender)
        {
            return "crack";
        }

        if (speckle)
        {
            return "noise";
        }

        if (stain)
        {
            return "stain";
        }

        return area > pageArea * 0.0008 ? "scan-damage" : string.Empty;
    }

    private static void AddProtectedDamageWarnings(Mat protectedDark, List<DetectedVisualRegion> damage, int pageWidth, int pageHeight)
    {
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 3));
        Cv2.MorphologyEx(protectedDark, protectedDark, MorphTypes.Close, kernel);
        Cv2.FindContours(protectedDark, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        var pageArea = pageWidth * pageHeight;

        foreach (var contour in contours)
        {
            var rect = Cv2.BoundingRect(contour);
            var area = rect.Width * rect.Height;
            var ratio = rect.Width / (double)Math.Max(1, rect.Height);
            var likelyCrossingCrack = area > pageArea * 0.0015 && (ratio > 8.0 || ratio < 0.12);
            if (!likelyCrossingCrack)
            {
                continue;
            }

            AddIfNotDuplicate(damage, new DetectedVisualRegion(
                "protected-damage",
                rect.X,
                rect.Y,
                rect.Width,
                rect.Height,
                80,
                "protected-content-kept",
                Warning: "Daño oscuro detectado sobre contenido protegido; conservar y revisar visualmente."));
        }
    }

    private static void AddIfNotDuplicate(List<DetectedVisualRegion> damage, DetectedVisualRegion candidate)
    {
        if (candidate.Kind is "noise" or "stain")
        {
            if (damage.Any(existing => existing.Kind == candidate.Kind && RegionOverlapRatio(existing, candidate) > 0.62))
            {
                return;
            }

            damage.Add(candidate);
            return;
        }

        if (damage.Any(existing => existing.Kind == candidate.Kind && RegionOverlapRatio(existing, candidate) > 0.62))
        {
            return;
        }

        damage.Add(candidate);
    }

    private static double MaskRatio(Mat mask, Rect rect)
    {
        var clipped = Expand(rect, mask.Width, mask.Height, 0);
        if (clipped.Width <= 0 || clipped.Height <= 0)
        {
            return 0;
        }

        using var roi = new Mat(mask, clipped);
        return Cv2.CountNonZero(roi) / (double)Math.Max(1, clipped.Width * clipped.Height);
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
