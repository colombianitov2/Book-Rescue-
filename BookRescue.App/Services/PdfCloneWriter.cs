using iText.IO.Font.Constants;
using BookRescue.App.Models;
using iText.IO.Image;
using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Layout;
using iText.Layout.Element;
using iText.Layout.Properties;

namespace BookRescue.App.Services;

public sealed class PdfCloneWriter
{
    private readonly TypstScientificPdfWriter typstWriter = new();

    public void WriteClonePdf(
        string outputPdfPath,
        IReadOnlyList<BookPageInfo> pages,
        IReadOnlyList<OcrPageResult> ocrPages,
        IReadOnlyList<string> outputPageTexts,
        IReadOnlyList<RescuedImageInfo> rescuedImages)
    {
        if (pages.Count == 0)
        {
            throw new InvalidOperationException("No hay páginas para reconstruir.");
        }

        var outputDirectory = System.IO.Path.GetDirectoryName(outputPdfPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        if (typstWriter.TryWritePdf(outputPdfPath, pages, ocrPages, outputPageTexts, rescuedImages))
        {
            return;
        }

        var profile = DocumentCompositionProfile.Analyze(pages, ocrPages, outputPageTexts, rescuedImages);
        using var writer = new PdfWriter(outputPdfPath);
        using var pdfDocument = new PdfDocument(writer);
        using var document = new Document(
            pdfDocument,
            new PageSize((float)profile.PageWidthPoints, (float)profile.PageHeightPoints));
        document.SetMargins(
            (float)profile.MarginTopPoints,
            (float)profile.MarginRightPoints,
            (float)profile.MarginBottomPoints,
            (float)profile.MarginLeftPoints);

        for (var i = 0; i < pages.Count; i++)
        {
            if (i > 0)
            {
                document.Add(new AreaBreak(AreaBreakType.NEXT_PAGE));
            }

            AddPageContent(
                document,
                profile,
                pages[i],
                i < ocrPages.Count ? ocrPages[i] : new OcrPageResult { FullText = string.Empty, Words = [], Lines = [] },
                i < outputPageTexts.Count ? outputPageTexts[i] : string.Empty,
                rescuedImages.Where(image => image.PageNumber == i + 1).ToList());
        }
    }

    private static void AddPageContent(
        Document document,
        DocumentCompositionProfile profile,
        BookPageInfo page,
        OcrPageResult ocrPage,
        string text,
        IReadOnlyList<RescuedImageInfo> pageImages)
    {
        var sourceBlocks = DocumentLayoutService.BuildTextBlocks(page, ocrPage);
        var paragraphs = DocumentLayoutService.SplitParagraphs(text);
        var outputBlocks = DocumentLayoutService.ApplyOutputText(sourceBlocks, paragraphs);

        var blocks = new List<PageContentBlock>();
        blocks.AddRange(outputBlocks.Select(block => PageContentBlock.FromText(block)));
        var hasStructuredTable = DocumentLayoutService.ContainsStructuredTable(paragraphs);
        blocks.AddRange(pageImages
            .Where(image => !hasStructuredTable || !image.Kind.Equals("table", StringComparison.OrdinalIgnoreCase))
            .Select(image => PageContentBlock.FromImage(image)));

        if (blocks.Count == 0)
        {
            document.Add(new Paragraph("[Sin texto legible en esta página]"));
            return;
        }

        float? previousBottom = null;
        foreach (var block in blocks.OrderBy(block => block.Y).ThenBy(block => block.X))
        {
            var sourceSpacing = CalculateSourceSpacingPoints(profile, page, block, previousBottom);
            if (block.TextBlock is not null)
            {
                AddTextBlock(document, profile, page, block.TextBlock, sourceSpacing);
            }

            if (block.Image is not null)
            {
                AddImage(document, profile, block.Image, sourceSpacing);
            }

            previousBottom = previousBottom.HasValue
                ? Math.Max(previousBottom.Value, block.Bottom)
                : block.Bottom;
        }
    }

    private static void AddTextBlock(
        Document document,
        DocumentCompositionProfile profile,
        BookPageInfo page,
        PageTextBlock textBlock,
        float spacingBefore)
    {
        var text = textBlock.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (StructuredTextService.TryParseMarkdownTable(text, out var tableRows))
        {
            AddTable(document, tableRows, spacingBefore);
            return;
        }

        if (StructuredTextService.TryReadMarkdownHeading(text, out var headingText, out _))
        {
            document.Add(new Paragraph(headingText)
                .SetFontSize(12)
                .SetMarginTop(7 + spacingBefore)
                .SetMarginBottom(4));
            return;
        }

        if (StructuredTextService.IsFormula(text))
        {
            AddFormula(document, text, spacingBefore);
            return;
        }

        if (StructuredTextService.TryReadBullet(text, out var bulletText))
        {
            AddBullet(document, profile, page, textBlock, bulletText, spacingBefore);
            return;
        }

        if (StructuredTextService.TryReadNumberedItem(text, out var marker, out var itemText))
        {
            AddNumberedItem(document, profile, page, textBlock, marker, itemText, spacingBefore);
            return;
        }

        if (StructuredTextService.IsCaption(text))
        {
            AddCaption(document, text, spacingBefore);
            return;
        }

        if (StructuredTextService.ShouldRenderAsHeading(text, textBlock.IsHeading))
        {
            document.Add(new Paragraph(text)
                .SetFontSize(12)
                .SetMarginTop(7 + spacingBefore)
                .SetMarginBottom(4));
            return;
        }

        document.Add(new Paragraph(text)
            .SetFontSize((float)profile.BodyFontSizePoints)
            .SetTextAlignment(TextAlignment.JUSTIFIED)
            .SetMarginLeft(CalculateSourceIndentPoints(profile, page, textBlock))
            .SetMarginTop(spacingBefore)
            .SetMarginBottom(3));
    }

    private static void AddFormula(Document document, string text, float spacingBefore)
    {
        document.Add(new Paragraph(StructuredTextService.StripFormulaMarkers(text))
            .SetFontSize(10.5f)
            .SetTextAlignment(TextAlignment.CENTER)
            .SetMarginTop(4 + spacingBefore)
            .SetMarginBottom(6));
    }

    private static void AddBullet(
        Document document,
        DocumentCompositionProfile profile,
        BookPageInfo page,
        PageTextBlock textBlock,
        string text,
        float spacingBefore)
    {
        document.Add(new Paragraph($"• {text}")
            .SetFontSize(10.5f)
            .SetTextAlignment(TextAlignment.LEFT)
            .SetMarginLeft(18 + CalculateSourceIndentPoints(profile, page, textBlock))
            .SetMarginTop(spacingBefore)
            .SetMarginBottom(3));
    }

    private static void AddNumberedItem(
        Document document,
        DocumentCompositionProfile profile,
        BookPageInfo page,
        PageTextBlock textBlock,
        string marker,
        string text,
        float spacingBefore)
    {
        document.Add(new Paragraph()
            .Add(new Text($"{marker} ").SetFont(PdfFontFactory.CreateFont(StandardFonts.HELVETICA_BOLD)))
            .Add(new Text(text))
            .SetFontSize(10.5f)
            .SetTextAlignment(TextAlignment.LEFT)
            .SetMarginLeft(22 + CalculateSourceIndentPoints(profile, page, textBlock))
            .SetMarginTop(spacingBefore)
            .SetMarginBottom(3));
    }

    private static void AddTable(Document document, IReadOnlyList<IReadOnlyList<string>> rows, float spacingBefore)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var columnCount = rows.Max(row => row.Count);
        var table = new Table(UnitValue.CreatePercentArray(columnCount))
            .UseAllAvailableWidth()
            .SetMarginTop(5 + spacingBefore)
            .SetMarginBottom(7);

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
            {
                var value = columnIndex < rows[rowIndex].Count ? rows[rowIndex][columnIndex] : string.Empty;
                var cell = new Cell()
                    .Add(new Paragraph(value).SetFontSize(8.8f).SetMargin(0))
                    .SetPadding(3);

                if (rowIndex == 0)
                {
                    cell.SetBackgroundColor(ColorConstants.LIGHT_GRAY);
                }

                table.AddCell(cell);
            }
        }

        document.Add(table);
    }

    private static void AddCaption(Document document, string text, float spacingBefore)
    {
        document.Add(new Paragraph(text)
            .SetFont(PdfFontFactory.CreateFont(StandardFonts.HELVETICA_BOLD))
            .SetFontSize(9f)
            .SetTextAlignment(TextAlignment.CENTER)
            .SetMarginTop(spacingBefore)
            .SetMarginBottom(4));
    }

    private static void AddImage(
        Document document,
        DocumentCompositionProfile profile,
        RescuedImageInfo imageInfo,
        float spacingBefore)
    {
        var image = new Image(ImageDataFactory.Create(imageInfo.ImagePath));
        var widthPoints = profile.GetImageWidthPoints(imageInfo);

        image.SetWidth((float)widthPoints);
        image.SetAutoScaleHeight(true);
        image.SetHorizontalAlignment(HorizontalAlignment.CENTER);
        image.SetMarginTop(5 + spacingBefore);
        image.SetMarginBottom(7);
        document.Add(image);
    }

    private static float CalculateSourceSpacingPoints(
        DocumentCompositionProfile profile,
        BookPageInfo page,
        PageContentBlock block,
        float? previousBottom)
    {
        var pageScale = profile.PageHeightPoints / Math.Max(1d, page.PixelHeight);
        double spacingPoints;

        if (previousBottom.HasValue)
        {
            var gapPoints = Math.Max(0d, (block.Y - previousBottom.Value) * pageScale);
            spacingPoints = gapPoints <= 16d ? 0d : Math.Clamp((gapPoints - 12d) * 0.55d, 0d, 90d);
        }
        else
        {
            var topPoints = Math.Max(0d, block.Y * pageScale - profile.MarginTopPoints);
            spacingPoints = topPoints <= 24d ? 0d : Math.Clamp(topPoints * 0.28d, 0d, 70d);
        }

        return (float)spacingPoints;
    }

    private static float CalculateSourceIndentPoints(
        DocumentCompositionProfile profile,
        BookPageInfo page,
        PageTextBlock textBlock)
    {
        var scaleX = profile.PageWidthPoints / Math.Max(1d, page.PixelWidth);
        var sourceLeft = textBlock.X * scaleX;
        var extra = sourceLeft - profile.MarginLeftPoints;
        return (float)Math.Clamp(extra * 0.45d, 0d, 42d);
    }

    private sealed record PageContentBlock(float X, float Y, float Height, PageTextBlock? TextBlock, RescuedImageInfo? Image)
    {
        public float Bottom => Y + Math.Max(1f, Height);

        public static PageContentBlock FromText(PageTextBlock block)
        {
            return new PageContentBlock(block.X, block.Y, block.Height, block, null);
        }

        public static PageContentBlock FromImage(RescuedImageInfo image)
        {
            return new PageContentBlock(image.X, image.Y, image.Height, null, image);
        }
    }
}
