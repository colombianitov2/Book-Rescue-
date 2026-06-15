using OpenCvSharp;
using BookRescue.App.Models;

namespace BookRescue.App.Services;

public sealed record VisualSimilarityResult(double Similarity, double Difference, string ComparedImagePath = "");

public sealed class VisualSimilarityService
{
    public VisualSimilarityResult CompareReconstructedCanvas(HeavyPageLayout layout, string outputFolder)
    {
        var expectedPath = !string.IsNullOrWhiteSpace(layout.Page.RestoredImagePath) && File.Exists(layout.Page.RestoredImagePath)
            ? layout.Page.RestoredImagePath
            : layout.Page.OriginalImagePath;
        if (!File.Exists(expectedPath))
        {
            return new VisualSimilarityResult(0d, 1d);
        }

        Directory.CreateDirectory(outputFolder);
        var canvasPath = Path.Combine(outputFolder, $"pagina_{layout.PageNumber:D4}_canvas_reconstruido.png");
        try
        {
            WriteReconstructedCanvas(layout, canvasPath);
            if (!string.IsNullOrWhiteSpace(layout.Background.ReconstructedCanvasPath))
            {
                File.Copy(canvasPath, layout.Background.ReconstructedCanvasPath, overwrite: true);
            }

            if (!string.IsNullOrWhiteSpace(layout.Background.SideBySideComparisonPath))
            {
                WriteSideBySide(expectedPath, canvasPath, layout.Background.SideBySideComparisonPath);
            }

            var result = Compare(expectedPath, canvasPath);
            return result with { ComparedImagePath = canvasPath };
        }
        catch (Exception ex)
        {
            AppLogService.Log(ex, "Comparación visual reconstruida");
            return Compare(layout.Page.OriginalImagePath, expectedPath);
        }
    }

    public VisualSimilarityResult Compare(string expectedImagePath, string actualImagePath)
    {
        if (!File.Exists(expectedImagePath) || !File.Exists(actualImagePath))
        {
            return new VisualSimilarityResult(0d, 1d);
        }

        using var expected = Cv2.ImRead(expectedImagePath, ImreadModes.Grayscale);
        using var actual = Cv2.ImRead(actualImagePath, ImreadModes.Grayscale);
        if (expected.Empty() || actual.Empty())
        {
            return new VisualSimilarityResult(0d, 1d);
        }

        using var resizedActual = new Mat();
        if (expected.Size() == actual.Size())
        {
            actual.CopyTo(resizedActual);
        }
        else
        {
            Cv2.Resize(actual, resizedActual, expected.Size());
        }

        using var diff = new Mat();
        Cv2.Absdiff(expected, resizedActual, diff);
        var mean = Cv2.Mean(diff).Val0;
        var difference = Math.Clamp(mean / 255d, 0d, 1d);
        return new VisualSimilarityResult(1d - difference, difference, actualImagePath);
    }

    private static void WriteReconstructedCanvas(HeavyPageLayout layout, string outputPath)
    {
        using var canvas = CreateBackgroundCanvas(layout);

        DrawRegionalImages(canvas, layout);
        DrawDecorations(canvas, layout);
        DrawVisibleText(canvas, layout);

        Cv2.ImWrite(outputPath, canvas);
    }

    private static Mat CreateBackgroundCanvas(HeavyPageLayout layout)
    {
        if (ShouldUseVisualBackground(layout) &&
            !string.IsNullOrWhiteSpace(layout.Background.CleanBackgroundImagePath) &&
            File.Exists(layout.Background.CleanBackgroundImagePath))
        {
            var background = Cv2.ImRead(layout.Background.CleanBackgroundImagePath, ImreadModes.Color);
            if (!background.Empty())
            {
                if (background.Width == layout.Page.PixelWidth && background.Height == layout.Page.PixelHeight)
                {
                    return background;
                }

                var resized = new Mat();
                Cv2.Resize(background, resized, new Size(layout.Page.PixelWidth, layout.Page.PixelHeight));
                background.Dispose();
                return resized;
            }

            background.Dispose();
        }

        return new Mat(
            Math.Max(1, layout.Page.PixelHeight),
            Math.Max(1, layout.Page.PixelWidth),
            MatType.CV_8UC3,
            new Scalar(layout.Background.Blue, layout.Background.Green, layout.Background.Red));
    }

    private static bool ShouldUseVisualBackground(HeavyPageLayout layout)
    {
        var background = layout.Background;
        var max = Math.Max(background.Red, Math.Max(background.Green, background.Blue));
        var min = Math.Min(background.Red, Math.Min(background.Green, background.Blue));
        var average = (background.Red + background.Green + background.Blue) / 3d;
        return !(average >= 216 && max - min <= 24);
    }

    private static void WriteSideBySide(string originalPath, string reconstructedPath, string outputPath)
    {
        if (!File.Exists(originalPath) || !File.Exists(reconstructedPath))
        {
            return;
        }

        using var original = Cv2.ImRead(originalPath, ImreadModes.Color);
        using var reconstructed = Cv2.ImRead(reconstructedPath, ImreadModes.Color);
        if (original.Empty() || reconstructed.Empty())
        {
            return;
        }

        using var resizedReconstructed = new Mat();
        if (original.Size() == reconstructed.Size())
        {
            reconstructed.CopyTo(resizedReconstructed);
        }
        else
        {
            Cv2.Resize(reconstructed, resizedReconstructed, original.Size());
        }

        using var sideBySide = new Mat();
        Cv2.HConcat([original, resizedReconstructed], sideBySide);
        Cv2.ImWrite(outputPath, sideBySide, [new ImageEncodingParam(ImwriteFlags.PngCompression, 2)]);
    }

    private static void DrawRegionalImages(Mat canvas, HeavyPageLayout layout)
    {
        foreach (var image in layout.PageImages.OrderBy(image => image.Y).ThenBy(image => image.X))
        {
            if (!File.Exists(image.ImagePath))
            {
                continue;
            }

            using var crop = Cv2.ImRead(image.ImagePath, ImreadModes.Color);
            if (crop.Empty())
            {
                continue;
            }

            var rect = ClampRect(image.X, image.Y, image.Width, image.Height, canvas.Width, canvas.Height);
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                continue;
            }

            using var resized = new Mat();
            Cv2.Resize(crop, resized, rect.Size);
            using var roi = new Mat(canvas, rect);
            resized.CopyTo(roi);
        }
    }

    private static void DrawDecorations(Mat canvas, HeavyPageLayout layout)
    {
        foreach (var region in layout.Regions.Where(region => region.Kind == "decoration"))
        {
            var rect = ClampRect(region.X, region.Y, region.Width, region.Height, canvas.Width, canvas.Height);
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                continue;
            }

            Cv2.Rectangle(canvas, rect, Scalar.Black, -1);
        }
    }

    private static void DrawVisibleText(Mat canvas, HeavyPageLayout layout)
    {
        foreach (var line in TextCleanupService.BuildOrderedLineBoxes(layout.Page, layout.Ocr)
                     .Where(line => line.Confidence >= 35 && !string.IsNullOrWhiteSpace(line.Text)))
        {
            var previewText = SanitizeForPreview(line.Text);
            if (string.IsNullOrWhiteSpace(previewText))
            {
                continue;
            }

            var fontScale = Math.Clamp(line.Height / 24d, 0.28d, 1.35d);
            var thickness = Math.Max(1, (int)Math.Round(fontScale * 1.3d));
            var origin = new Point(
                Math.Clamp((int)MathF.Round(line.X), 0, Math.Max(0, canvas.Width - 1)),
                Math.Clamp((int)MathF.Round(line.Y + line.Height), 0, Math.Max(0, canvas.Height - 1)));
            Cv2.PutText(canvas, previewText, origin, HersheyFonts.HersheySimplex, fontScale, Scalar.Black, thickness, LineTypes.AntiAlias);
        }
    }

    private static Rect ClampRect(float x, float y, float width, float height, int maxWidth, int maxHeight)
    {
        var left = Math.Clamp((int)MathF.Round(x), 0, Math.Max(0, maxWidth - 1));
        var top = Math.Clamp((int)MathF.Round(y), 0, Math.Max(0, maxHeight - 1));
        var right = Math.Clamp((int)MathF.Round(x + width), left + 1, maxWidth);
        var bottom = Math.Clamp((int)MathF.Round(y + height), top + 1, maxHeight);
        return new Rect(left, top, right - left, bottom - top);
    }

    private static string SanitizeForPreview(string text)
    {
        return new string(text.Where(ch => ch is >= ' ' and <= '~').ToArray()).Trim();
    }
}
