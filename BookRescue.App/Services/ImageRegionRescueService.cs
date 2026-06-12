using BookRescue.App.Models;
using OpenCvSharp;

namespace BookRescue.App.Services;

public sealed class ImageRegionRescueService
{
    public IReadOnlyList<RescuedImageInfo> RescuePageImages(
        BookPageInfo page,
        OcrPageResult ocrPage,
        string outputFolder,
        int pageNumber)
    {
        Directory.CreateDirectory(outputFolder);

        using var source = Cv2.ImRead(page.RestoredImagePath, ImreadModes.Color);
        if (source.Empty())
        {
            return [];
        }

        using var gray = new Mat();
        Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);

        using var binary = new Mat();
        Cv2.AdaptiveThreshold(gray, binary, 255, AdaptiveThresholdTypes.GaussianC, ThresholdTypes.BinaryInv, 35, 12);

        using var edges = new Mat();
        Cv2.Canny(gray, edges, 60, 180);
        Cv2.BitwiseOr(binary, edges, binary);

        using var textMask = new Mat(source.Rows, source.Cols, MatType.CV_8UC1, Scalar.Black);
        foreach (var word in ocrPage.Words)
        {
            var rect = ExpandAndClamp(
                new Rect(
                    (int)MathF.Round(word.X),
                    (int)MathF.Round(word.Y),
                    (int)MathF.Round(word.Width),
                    (int)MathF.Round(word.Height)),
                source.Width,
                source.Height,
                8);
            Cv2.Rectangle(textMask, rect, Scalar.White, -1);
        }

        binary.SetTo(Scalar.Black, textMask);

        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(31, 21));
        Cv2.MorphologyEx(binary, binary, MorphTypes.Close, kernel);
        Cv2.Dilate(binary, binary, kernel, iterations: 1);

        Cv2.FindContours(binary, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        var pageArea = source.Width * source.Height;
        var accepted = new List<Rect>();

        foreach (var contour in contours.OrderByDescending(contour => Cv2.ContourArea(contour)))
        {
            var rect = Cv2.BoundingRect(contour);
            var area = rect.Width * rect.Height;
            if (!LooksLikeImageRegion(rect, area, pageArea))
            {
                continue;
            }

            rect = ExpandAndClamp(rect, source.Width, source.Height, 18);
            rect = TightenToInk(source, rect, 14);
            if (LooksLikeFullPageFalsePositive(rect, source.Width, source.Height))
            {
                continue;
            }

            if (accepted.Any(existing => OverlapRatio(existing, rect) > 0.55))
            {
                continue;
            }

            accepted.Add(rect);
        }

        var rescued = new List<RescuedImageInfo>();
        var saved = 0;
        foreach (var rect in accepted.OrderBy(rect => rect.Y).ThenBy(rect => rect.X))
        {
            using var crop = new Mat(source, rect);
            var outputPath = Path.Combine(outputFolder, $"pagina_{pageNumber:D4}_imagen_{saved + 1:D2}.png");
            Cv2.ImWrite(outputPath, crop);

            rescued.Add(new RescuedImageInfo
            {
                ImagePath = outputPath,
                PageNumber = pageNumber,
                X = rect.X,
                Y = rect.Y,
                Width = rect.Width,
                Height = rect.Height,
                PagePixelWidth = source.Width,
                PagePixelHeight = source.Height,
                Kind = LooksLikeTable(crop) ? "table" : "figure"
            });

            saved++;
        }

        return rescued;
    }

    private static bool LooksLikeImageRegion(Rect rect, int area, int pageArea)
    {
        if (rect.Width < 140 || rect.Height < 90)
        {
            return false;
        }

        if (area < pageArea * 0.012)
        {
            return false;
        }

        if (area > pageArea * 0.85)
        {
            return false;
        }

        var ratio = rect.Width / (double)Math.Max(1, rect.Height);
        return ratio is >= 0.18 and <= 6.0;
    }

    private static bool LooksLikeFullPageFalsePositive(Rect rect, int pageWidth, int pageHeight)
    {
        var areaRatio = rect.Width * rect.Height / (double)Math.Max(1, pageWidth * pageHeight);
        var widthRatio = rect.Width / (double)Math.Max(1, pageWidth);
        var heightRatio = rect.Height / (double)Math.Max(1, pageHeight);
        return areaRatio > 0.55 && widthRatio > 0.82 && heightRatio > 0.60;
    }

    private static Rect ExpandAndClamp(Rect rect, int maxWidth, int maxHeight, int padding)
    {
        var x = Math.Max(0, rect.X - padding);
        var y = Math.Max(0, rect.Y - padding);
        var right = Math.Min(maxWidth, rect.Right + padding);
        var bottom = Math.Min(maxHeight, rect.Bottom + padding);
        return new Rect(x, y, Math.Max(1, right - x), Math.Max(1, bottom - y));
    }

    private static Rect TightenToInk(Mat source, Rect rect, int padding)
    {
        using var crop = new Mat(source, rect);
        using var gray = new Mat();
        Cv2.CvtColor(crop, gray, ColorConversionCodes.BGR2GRAY);

        using var ink = new Mat();
        Cv2.Threshold(gray, ink, 245, 255, ThresholdTypes.BinaryInv);
        using var points = new Mat();
        Cv2.FindNonZero(ink, points);
        if (points.Empty())
        {
            return rect;
        }

        var inkRect = Cv2.BoundingRect(points);
        if (inkRect.Width * inkRect.Height < rect.Width * rect.Height * 0.08)
        {
            return rect;
        }

        var tightened = new Rect(
            rect.X + inkRect.X,
            rect.Y + inkRect.Y,
            inkRect.Width,
            inkRect.Height);

        return ExpandAndClamp(tightened, source.Width, source.Height, padding);
    }

    private static bool LooksLikeTable(Mat crop)
    {
        if (crop.Width < 180 || crop.Height < 80)
        {
            return false;
        }

        using var gray = new Mat();
        Cv2.CvtColor(crop, gray, ColorConversionCodes.BGR2GRAY);

        using var binary = new Mat();
        Cv2.AdaptiveThreshold(gray, binary, 255, AdaptiveThresholdTypes.GaussianC, ThresholdTypes.BinaryInv, 35, 12);

        using var horizontalKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(Math.Max(12, crop.Width / 18), 1));
        using var horizontal = new Mat();
        Cv2.MorphologyEx(binary, horizontal, MorphTypes.Open, horizontalKernel);

        using var verticalKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(1, Math.Max(8, crop.Height / 14)));
        using var vertical = new Mat();
        Cv2.MorphologyEx(binary, vertical, MorphTypes.Open, verticalKernel);

        var horizontalInk = Cv2.CountNonZero(horizontal);
        var verticalInk = Cv2.CountNonZero(vertical);
        var area = crop.Width * crop.Height;
        return horizontalInk > area * 0.015 && verticalInk > area * 0.006;
    }

    private static double OverlapRatio(Rect a, Rect b)
    {
        var x1 = Math.Max(a.X, b.X);
        var y1 = Math.Max(a.Y, b.Y);
        var x2 = Math.Min(a.Right, b.Right);
        var y2 = Math.Min(a.Bottom, b.Bottom);

        if (x2 <= x1 || y2 <= y1)
        {
            return 0;
        }

        var intersection = (x2 - x1) * (y2 - y1);
        var smaller = Math.Min(a.Width * a.Height, b.Width * b.Height);
        return intersection / (double)Math.Max(1, smaller);
    }
}
