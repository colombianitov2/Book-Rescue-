using BookRescue.App.Models;
using iText.IO.Font.Constants;
using iText.IO.Image;
using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;

namespace BookRescue.App.Services;

public sealed class PerfectPdfReconstructor
{
    public void WritePdf(
        string outputPdfPath,
        IReadOnlyList<HeavyPageLayout> layouts)
    {
        if (layouts.Count == 0)
        {
            throw new InvalidOperationException("No hay páginas para reconstruir.");
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outputPdfPath)!);

        using var writer = new PdfWriter(outputPdfPath);
        using var pdfDocument = new PdfDocument(writer);
        var bodyFont = PdfFontFactory.CreateFont(StandardFonts.TIMES_ROMAN);
        var headingFont = PdfFontFactory.CreateFont(StandardFonts.TIMES_BOLD);

        foreach (var layout in layouts)
        {
            var pageWidth = Math.Max(1f, layout.Page.WidthPoints);
            var pageHeight = Math.Max(1f, layout.Page.HeightPoints);
            var pdfPage = pdfDocument.AddNewPage(new PageSize(pageWidth, pageHeight));
            var canvas = new PdfCanvas(pdfPage);

            AddCleanBackground(canvas, layout, pageWidth, pageHeight);
            AddRegionalFallbackImages(canvas, layout, pageHeight);
            AddDecorationRegions(canvas, layout, pageHeight);
            AddVisibleOcrText(
                canvas,
                bodyFont,
                headingFont,
                layout.Page,
                layout.Ocr,
                pageHeight);
            AddEmergencyFullPageFallback(canvas, layout, pageWidth, pageHeight);
        }
    }

    private static void AddCleanBackground(PdfCanvas canvas, HeavyPageLayout layout, float pageWidth, float pageHeight)
    {
        var background = layout.Background;
        if (ShouldUseVisualBackground(layout) &&
            !string.IsNullOrWhiteSpace(background.CleanBackgroundImagePath) &&
            File.Exists(background.CleanBackgroundImagePath))
        {
            canvas.AddImageFittedIntoRectangle(
                ImageDataFactory.Create(background.CleanBackgroundImagePath),
                new Rectangle(0, 0, pageWidth, pageHeight),
                false);
            return;
        }

        canvas.SaveState();
        canvas.SetFillColor(new DeviceRgb(background.Red, background.Green, background.Blue));
        canvas.Rectangle(0, 0, pageWidth, pageHeight);
        canvas.Fill();
        canvas.RestoreState();
    }

    private static bool ShouldUseVisualBackground(HeavyPageLayout layout)
    {
        var background = layout.Background;
        var max = Math.Max(background.Red, Math.Max(background.Green, background.Blue));
        var min = Math.Min(background.Red, Math.Min(background.Green, background.Blue));
        var average = (background.Red + background.Green + background.Blue) / 3d;
        var looksLikePlainPaper = average >= 216 && max - min <= 24;
        return !looksLikePlainPaper;
    }

    private static void AddRegionalFallbackImages(PdfCanvas canvas, HeavyPageLayout layout, float pageHeight)
    {
        foreach (var image in layout.PageImages.OrderBy(image => image.Y).ThenBy(image => image.X))
        {
            if (!File.Exists(image.ImagePath))
            {
                continue;
            }

            var x = ScaleX(layout.Page, image.X);
            var y = pageHeight - ScaleY(layout.Page, image.Y + image.Height);
            var width = ScaleX(layout.Page, image.Width);
            var height = ScaleY(layout.Page, image.Height);
            canvas.AddImageFittedIntoRectangle(
                ImageDataFactory.Create(image.ImagePath),
                new Rectangle(x, y, Math.Max(1f, width), Math.Max(1f, height)),
                false);
        }
    }

    private static void AddDecorationRegions(PdfCanvas canvas, HeavyPageLayout layout, float pageHeight)
    {
        foreach (var region in layout.Regions.Where(region => region.Kind == "decoration"))
        {
            var x = ScaleX(layout.Page, region.X);
            var y = pageHeight - ScaleY(layout.Page, region.Y + region.Height);
            var width = ScaleX(layout.Page, region.Width);
            var height = ScaleY(layout.Page, region.Height);

            canvas.SaveState();
            canvas.SetFillColor(ColorConstants.BLACK);
            canvas.Rectangle(x, y, Math.Max(0.5f, width), Math.Max(0.5f, height));
            canvas.Fill();
            canvas.RestoreState();
        }
    }

    private static void AddVisibleOcrText(
        PdfCanvas canvas,
        PdfFont bodyFont,
        PdfFont headingFont,
        BookPageInfo page,
        OcrPageResult ocrPage,
        float pageHeight)
    {
        foreach (var line in TextCleanupService.BuildOrderedLineBoxes(page, ocrPage)
                     .Where(line => line.Confidence >= 35 && !string.IsNullOrWhiteSpace(line.Text)))
        {
            var text = SanitizePdfText(line.Text);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var isHeading = TextCleanupService.IsLikelyHeading(text);
            var x = ScaleX(page, line.X);
            var y = pageHeight - ScaleY(page, line.Y + line.Height);
            var fontSize = Math.Clamp(ScaleY(page, line.Height) * 0.86f, 4.8f, isHeading ? 24f : 16f);

            canvas.SaveState();
            canvas.BeginText();
            canvas.SetFontAndSize(isHeading ? headingFont : bodyFont, fontSize);
            canvas.SetFillColor(ColorConstants.BLACK);
            canvas.MoveText(x, y);
            canvas.ShowText(text);
            canvas.EndText();
            canvas.RestoreState();
        }
    }

    private static void AddEmergencyFullPageFallback(PdfCanvas canvas, HeavyPageLayout layout, float pageWidth, float pageHeight)
    {
        if (layout.Ocr.Lines.Count > 0 || layout.PageImages.Count > 0)
        {
            return;
        }

        AddFullPageImage(canvas, layout.Page, pageWidth, pageHeight);
    }

    private static void AddFullPageImage(PdfCanvas canvas, BookPageInfo page, float pageWidth, float pageHeight)
    {
        var imagePath = !string.IsNullOrWhiteSpace(page.RestoredImagePath) && File.Exists(page.RestoredImagePath)
            ? page.RestoredImagePath
            : page.OriginalImagePath;

        if (!File.Exists(imagePath))
        {
            return;
        }

        canvas.AddImageFittedIntoRectangle(
            ImageDataFactory.Create(imagePath),
            new Rectangle(0, 0, pageWidth, pageHeight),
            false);
    }

    private static float ScaleX(BookPageInfo page, float value)
    {
        return value / Math.Max(1f, page.PixelWidth) * Math.Max(1f, page.WidthPoints);
    }

    private static float ScaleY(BookPageInfo page, float value)
    {
        return value / Math.Max(1f, page.PixelHeight) * Math.Max(1f, page.HeightPoints);
    }

    private static string SanitizePdfText(string text)
    {
        return new string(text
            .Where(ch => ch is >= ' ' and <= '\u00FF')
            .ToArray())
            .Trim();
    }
}
