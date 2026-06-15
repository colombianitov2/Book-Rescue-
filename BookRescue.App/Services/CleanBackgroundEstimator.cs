using OpenCvSharp;

namespace BookRescue.App.Services;

public sealed record CleanPageBackground(
    byte Red,
    byte Green,
    byte Blue,
    string CleanBackgroundImagePath = "",
    string DamageMaskPath = "",
    string ProtectedContentMaskPath = "",
    string RegionOverlayPath = "",
    string ReconstructedCanvasPath = "",
    string SideBySideComparisonPath = "")
{
    public string Hex => $"#{Red:X2}{Green:X2}{Blue:X2}";
}

public sealed class CleanBackgroundEstimator
{
    public CleanPageBackground Estimate(HeavyPageLayoutInput pageInput)
    {
        return Estimate(pageInput, [], string.Empty, pageNumber: 1);
    }

    public CleanPageBackground Estimate(
        HeavyPageLayoutInput pageInput,
        IReadOnlyList<DetectedVisualRegion> regions,
        string diagnosticsFolder,
        int pageNumber)
    {
        var imagePath = !string.IsNullOrWhiteSpace(pageInput.Page.RestoredImagePath) && File.Exists(pageInput.Page.RestoredImagePath)
            ? pageInput.Page.RestoredImagePath
            : pageInput.Page.OriginalImagePath;

        if (!File.Exists(imagePath))
        {
            return new CleanPageBackground(255, 255, 255);
        }

        using var source = Cv2.ImRead(imagePath, ImreadModes.Color);
        if (source.Empty())
        {
            return new CleanPageBackground(255, 255, 255);
        }

        var samples = new List<Vec3b>(capacity: 12000);
        var stepX = Math.Max(1, source.Width / 180);
        var stepY = Math.Max(1, source.Height / 180);
        for (var y = 0; y < source.Height; y += stepY)
        {
            for (var x = 0; x < source.Width; x += stepX)
            {
                if (OverlapsTextOrImage(x, y, pageInput))
                {
                    continue;
                }

                var pixel = source.At<Vec3b>(y, x);
                var brightness = (pixel.Item0 + pixel.Item1 + pixel.Item2) / 3d;
                if (brightness < 125)
                {
                    continue;
                }

                samples.Add(pixel);
            }
        }

        if (samples.Count < 80)
        {
            return new CleanPageBackground(255, 255, 255);
        }

        static byte Median(IEnumerable<byte> values)
        {
            var ordered = values.Order().ToList();
            return ordered[ordered.Count / 2];
        }

        var blue = Median(samples.Select(pixel => pixel.Item0));
        var green = Median(samples.Select(pixel => pixel.Item1));
        var red = Median(samples.Select(pixel => pixel.Item2));

        // Keep the original paper color, but nudge tiny scanning shadows toward a cleaner surface.
        red = CleanChannel(red);
        green = CleanChannel(green);
        blue = CleanChannel(blue);

        var paths = CreateDiagnosticPaths(diagnosticsFolder, pageNumber);
        if (!string.IsNullOrWhiteSpace(diagnosticsFolder))
        {
            Directory.CreateDirectory(diagnosticsFolder);
            WriteDiagnostics(source, pageInput, regions, paths);
        }

        return new CleanPageBackground(
            red,
            green,
            blue,
            paths.CleanBackgroundPath,
            paths.DamageMaskPath,
            paths.ProtectedContentMaskPath,
            paths.RegionOverlayPath,
            paths.ReconstructedCanvasPath,
            paths.SideBySidePath);
    }

    private static byte CleanChannel(byte value)
    {
        return value > 235 ? (byte)Math.Min(255, value + 8) : value;
    }

    private static bool OverlapsTextOrImage(int x, int y, HeavyPageLayoutInput pageInput)
    {
        if (pageInput.Ocr.Lines.Any(line => Contains(line.X, line.Y, line.Width, line.Height, x, y, padding: 10)))
        {
            return true;
        }

        return pageInput.PageImages.Any(image => Contains(image.X, image.Y, image.Width, image.Height, x, y, padding: 18));
    }

    private static bool Contains(float rx, float ry, float rw, float rh, int x, int y, int padding)
    {
        return x >= rx - padding &&
            x <= rx + rw + padding &&
            y >= ry - padding &&
            y <= ry + rh + padding;
    }

    private static DiagnosticPaths CreateDiagnosticPaths(string diagnosticsFolder, int pageNumber)
    {
        if (string.IsNullOrWhiteSpace(diagnosticsFolder))
        {
            return new DiagnosticPaths("", "", "", "", "", "");
        }

        var prefix = pageNumber <= 1 ? string.Empty : $"pagina_{pageNumber:D4}_";
        return new DiagnosticPaths(
            Path.Combine(diagnosticsFolder, $"{prefix}damage_mask.png"),
            Path.Combine(diagnosticsFolder, $"{prefix}protected_content_mask.png"),
            Path.Combine(diagnosticsFolder, $"{prefix}clean_background.png"),
            Path.Combine(diagnosticsFolder, $"{prefix}region_overlay.png"),
            Path.Combine(diagnosticsFolder, $"{prefix}reconstructed_canvas.png"),
            Path.Combine(diagnosticsFolder, $"{prefix}side_by_side_original_vs_reconstructed.png"));
    }

    private static void WriteDiagnostics(
        Mat source,
        HeavyPageLayoutInput pageInput,
        IReadOnlyList<DetectedVisualRegion> regions,
        DiagnosticPaths paths)
    {
        using var protectedMask = BuildProtectedContentMask(source, pageInput, regions);
        using var damageMask = BuildDamageMask(source, protectedMask, regions);
        using var textInkMask = BuildTextInkMask(source, pageInput);
        using var backgroundMask = BuildEditorialBackgroundMask(source, pageInput, damageMask, textInkMask);
        using var editorialBackground = new Mat();
        if (Cv2.CountNonZero(backgroundMask) > 0)
        {
            Cv2.Inpaint(source, backgroundMask, editorialBackground, 3.0, InpaintTypes.Telea);
            BlendTextInkWithLocalPaper(editorialBackground, textInkMask, pageInput);
        }
        else
        {
            source.CopyTo(editorialBackground);
        }

        Cv2.ImWrite(paths.DamageMaskPath, damageMask);
        Cv2.ImWrite(paths.ProtectedContentMaskPath, protectedMask);
        Cv2.ImWrite(paths.CleanBackgroundPath, editorialBackground, [new ImageEncodingParam(ImwriteFlags.PngCompression, 2)]);

        using var overlay = source.Clone();
        foreach (var region in regions)
        {
            var rect = ClampRect(region.X, region.Y, region.Width, region.Height, source.Width, source.Height);
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                continue;
            }

            var color = region.Kind switch
            {
                "crack" => new Scalar(0, 0, 255),
                "stain" => new Scalar(0, 128, 255),
                "noise" => new Scalar(0, 255, 255),
                "protected-damage" => new Scalar(255, 0, 255),
                "diagram" or "graphical-element" or "decoration" => new Scalar(0, 220, 0),
                "text" or "heading" => new Scalar(255, 180, 0),
                _ => Scalar.White
            };
            Cv2.Rectangle(overlay, rect, color, 2);
            Cv2.PutText(overlay, region.Kind, new Point(rect.X, Math.Max(12, rect.Y - 4)), HersheyFonts.HersheySimplex, 0.45, color, 1);
        }

        Cv2.ImWrite(paths.RegionOverlayPath, overlay, [new ImageEncodingParam(ImwriteFlags.PngCompression, 2)]);
    }

    private static Mat BuildProtectedContentMask(Mat source, HeavyPageLayoutInput pageInput, IReadOnlyList<DetectedVisualRegion> regions)
    {
        var protectedMask = new Mat(source.Rows, source.Cols, MatType.CV_8UC1, Scalar.Black);
        foreach (var line in pageInput.Ocr.Lines)
        {
            Cv2.Rectangle(protectedMask, ClampRect(line.X, line.Y, line.Width, line.Height, source.Width, source.Height, 8), Scalar.White, -1);
        }

        foreach (var image in pageInput.PageImages)
        {
            Cv2.Rectangle(protectedMask, ClampRect(image.X, image.Y, image.Width, image.Height, source.Width, source.Height, 12), Scalar.White, -1);
        }

        foreach (var region in regions.Where(region => region.Kind is "diagram" or "graphical-element" or "image" or "table"))
        {
            Cv2.Rectangle(protectedMask, ClampRect(region.X, region.Y, region.Width, region.Height, source.Width, source.Height, 14), Scalar.White, -1);
        }

        return protectedMask;
    }

    private static Mat BuildEditorialBackgroundMask(Mat source, HeavyPageLayoutInput pageInput, Mat damageMask, Mat textInkMask)
    {
        var mask = new Mat(source.Rows, source.Cols, MatType.CV_8UC1, Scalar.Black);
        if (!damageMask.Empty())
        {
            damageMask.CopyTo(mask);
        }

        if (!textInkMask.Empty())
        {
            Cv2.BitwiseOr(mask, textInkMask, mask);
        }

        foreach (var image in pageInput.PageImages.Where(image => File.Exists(image.ImagePath)))
        {
            Cv2.Rectangle(mask, ClampRect(image.X, image.Y, image.Width, image.Height, source.Width, source.Height, 4), Scalar.White, -1);
        }

        using var closeKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(3, 3));
        Cv2.MorphologyEx(mask, mask, MorphTypes.Close, closeKernel);
        return mask;
    }

    private static Mat BuildTextInkMask(Mat source, HeavyPageLayoutInput pageInput)
    {
        var mask = new Mat(source.Rows, source.Cols, MatType.CV_8UC1, Scalar.Black);
        using var gray = new Mat();
        Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);

        if (pageInput.Ocr.Words.Count > 0)
        {
            foreach (var word in pageInput.Ocr.Words.Where(word => word.Confidence >= 24 && !string.IsNullOrWhiteSpace(word.Text)))
            {
                var padding = Math.Clamp((int)MathF.Round(word.Height * 0.16f), 1, 5);
                AddInkMaskForRegion(gray, mask, ClampRect(word.X, word.Y, word.Width, word.Height, source.Width, source.Height, padding));
            }
        }
        else
        {
            foreach (var line in pageInput.Ocr.Lines.Where(line => line.Confidence >= 28 && !string.IsNullOrWhiteSpace(line.Text)))
            {
                var padding = Math.Clamp((int)MathF.Round(line.Height * 0.18f), 2, 8);
                AddInkMaskForRegion(gray, mask, ClampRect(line.X, line.Y, line.Width, line.Height, source.Width, source.Height, padding));
            }
        }

        return mask;
    }

    private static void AddInkMaskForRegion(Mat gray, Mat targetMask, Rect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        using var crop = new Mat(gray, rect);
        using var ink = new Mat();
        Cv2.Threshold(crop, ink, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);

        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(2, 2));
        Cv2.Dilate(ink, ink, kernel, iterations: 1);

        using var targetRoi = new Mat(targetMask, rect);
        Cv2.BitwiseOr(targetRoi, ink, targetRoi);
    }

    private static void BlendTextInkWithLocalPaper(Mat canvas, Mat textInkMask, HeavyPageLayoutInput pageInput)
    {
        if (textInkMask.Empty() || Cv2.CountNonZero(textInkMask) == 0)
        {
            return;
        }

        if (pageInput.Ocr.Words.Count > 0)
        {
            foreach (var word in pageInput.Ocr.Words.Where(word => word.Confidence >= 24 && !string.IsNullOrWhiteSpace(word.Text)))
            {
                var padding = Math.Clamp((int)MathF.Round(word.Height * 0.16f), 1, 5);
                BlendInkRegion(canvas, textInkMask, ClampRect(word.X, word.Y, word.Width, word.Height, canvas.Width, canvas.Height, padding));
            }
        }
        else
        {
            foreach (var line in pageInput.Ocr.Lines.Where(line => line.Confidence >= 28 && !string.IsNullOrWhiteSpace(line.Text)))
            {
                var padding = Math.Clamp((int)MathF.Round(line.Height * 0.18f), 2, 8);
                BlendInkRegion(canvas, textInkMask, ClampRect(line.X, line.Y, line.Width, line.Height, canvas.Width, canvas.Height, padding));
            }
        }
    }

    private static void BlendInkRegion(Mat canvas, Mat textInkMask, Rect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        using var maskRoi = new Mat(textInkMask, rect);
        if (Cv2.CountNonZero(maskRoi) == 0)
        {
            return;
        }

        var sampleRect = ExpandRect(rect, canvas.Width, canvas.Height, Math.Clamp(Math.Min(rect.Width, rect.Height), 8, 28));
        var paper = EstimateLocalPaperColor(canvas, textInkMask, sampleRect);
        using var targetRoi = new Mat(canvas, rect);
        using var patch = new Mat(rect.Height, rect.Width, canvas.Type(), new Scalar(paper.Item0, paper.Item1, paper.Item2));
        patch.CopyTo(targetRoi, maskRoi);
    }

    private static Vec3b EstimateLocalPaperColor(Mat canvas, Mat textInkMask, Rect sampleRect)
    {
        var blue = new List<byte>();
        var green = new List<byte>();
        var red = new List<byte>();
        var stepX = Math.Max(1, sampleRect.Width / 28);
        var stepY = Math.Max(1, sampleRect.Height / 28);

        for (var y = sampleRect.Y; y < sampleRect.Bottom; y += stepY)
        {
            for (var x = sampleRect.X; x < sampleRect.Right; x += stepX)
            {
                if (textInkMask.At<byte>(y, x) != 0)
                {
                    continue;
                }

                var pixel = canvas.At<Vec3b>(y, x);
                var brightness = (pixel.Item0 + pixel.Item1 + pixel.Item2) / 3d;
                if (brightness < 115)
                {
                    continue;
                }

                blue.Add(pixel.Item0);
                green.Add(pixel.Item1);
                red.Add(pixel.Item2);
            }
        }

        if (blue.Count < 4)
        {
            return new Vec3b(248, 248, 248);
        }

        static byte Median(List<byte> values)
        {
            values.Sort();
            return values[values.Count / 2];
        }

        return new Vec3b(Median(blue), Median(green), Median(red));
    }

    private static Rect ExpandRect(Rect rect, int maxWidth, int maxHeight, int padding)
    {
        var left = Math.Max(0, rect.X - padding);
        var top = Math.Max(0, rect.Y - padding);
        var right = Math.Min(maxWidth, rect.Right + padding);
        var bottom = Math.Min(maxHeight, rect.Bottom + padding);
        return new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private static Mat BuildDamageMask(Mat source, Mat protectedMask, IReadOnlyList<DetectedVisualRegion> regions)
    {
        using var gray = new Mat();
        Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);

        using var dark = new Mat();
        Cv2.Threshold(gray, dark, 92, 255, ThresholdTypes.BinaryInv);

        using var freeDamage = new Mat();
        dark.CopyTo(freeDamage);
        freeDamage.SetTo(Scalar.Black, protectedMask);

        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(2, 2));
        Cv2.MorphologyEx(freeDamage, freeDamage, MorphTypes.Close, kernel);

        var mask = new Mat(source.Rows, source.Cols, MatType.CV_8UC1, Scalar.Black);
        freeDamage.CopyTo(mask);

        foreach (var region in regions.Where(region => region.Kind is "crack" or "stain" or "noise" or "scan-damage"))
        {
            if (region.PreservationStrategy == "protected-content-kept")
            {
                continue;
            }

            var rect = ClampRect(region.X, region.Y, region.Width, region.Height, source.Width, source.Height, region.Kind == "crack" ? 2 : 1);
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                continue;
            }

            if (region.Kind == "crack")
            {
                using var crackRoi = new Mat(freeDamage, rect);
                using var destination = new Mat(mask, rect);
                Cv2.BitwiseOr(destination, crackRoi, destination);
            }
            else
            {
                Cv2.Rectangle(mask, rect, Scalar.White, -1);
            }
        }

        return mask;
    }

    private static Rect ClampRect(float x, float y, float width, float height, int maxWidth, int maxHeight, int padding = 0)
    {
        var left = Math.Clamp((int)MathF.Round(x) - padding, 0, Math.Max(0, maxWidth - 1));
        var top = Math.Clamp((int)MathF.Round(y) - padding, 0, Math.Max(0, maxHeight - 1));
        var right = Math.Clamp((int)MathF.Round(x + width) + padding, left + 1, maxWidth);
        var bottom = Math.Clamp((int)MathF.Round(y + height) + padding, top + 1, maxHeight);
        return new Rect(left, top, right - left, bottom - top);
    }

    private sealed record DiagnosticPaths(
        string DamageMaskPath,
        string ProtectedContentMaskPath,
        string CleanBackgroundPath,
        string RegionOverlayPath,
        string ReconstructedCanvasPath,
        string SideBySidePath);
}
