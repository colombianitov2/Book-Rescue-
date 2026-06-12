using BookRescue.App.Models;

namespace BookRescue.App.Services;

public sealed class RegionOcrMapper
{
    public IReadOnlyList<OcrLineBox> SelectLines(DetectedVisualRegion region, OcrPageResult ocrPage)
    {
        if (region.Kind is not ("text" or "heading" or "table"))
        {
            return [];
        }

        return ocrPage.Lines
            .Where(line => OverlapRatio(region, line) > 0.12d)
            .OrderBy(line => line.Y)
            .ThenBy(line => line.X)
            .ToList();
    }

    public IReadOnlyList<OcrWordBox> SelectWords(DetectedVisualRegion region, OcrPageResult ocrPage)
    {
        if (region.Kind is not ("text" or "heading" or "table"))
        {
            return [];
        }

        return ocrPage.Words
            .Where(word => OverlapRatio(region, word) > 0.10d)
            .OrderBy(word => word.Y)
            .ThenBy(word => word.X)
            .ToList();
    }

    private static double OverlapRatio(DetectedVisualRegion region, OcrLineBox line)
    {
        return OverlapRatio(region, line.X, line.Y, line.Width, line.Height);
    }

    private static double OverlapRatio(DetectedVisualRegion region, OcrWordBox word)
    {
        return OverlapRatio(region, word.X, word.Y, word.Width, word.Height);
    }

    private static double OverlapRatio(DetectedVisualRegion region, float x, float y, float width, float height)
    {
        var x1 = Math.Max(region.X, x);
        var y1 = Math.Max(region.Y, y);
        var x2 = Math.Min(region.X + region.Width, x + width);
        var y2 = Math.Min(region.Y + region.Height, y + height);
        if (x2 <= x1 || y2 <= y1)
        {
            return 0d;
        }

        var intersection = (x2 - x1) * (y2 - y1);
        var smaller = Math.Min(region.Width * region.Height, width * height);
        return intersection / Math.Max(1d, smaller);
    }
}
