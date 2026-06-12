using BookRescue.App.Models;
using iText.IO.Font.Constants;
using iText.IO.Image;
using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Layout;
using iText.Layout.Borders;
using iText.Layout.Element;
using iText.Layout.Properties;

namespace BookRescue.App.Services;

public sealed class PdfEditorialWriter
{
    public void WritePdf(
        string outputPdfPath,
        IReadOnlyList<BookPageInfo> pages,
        IReadOnlyList<OcrPageResult> ocrPages,
        IReadOnlyList<string> outputPageTexts,
        IReadOnlyList<RescuedImageInfo> rescuedImages,
        bool includeImages = true)
    {
        if (pages.Count == 0)
        {
            throw new InvalidOperationException("No hay páginas para reconstruir.");
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outputPdfPath)!);

        using var writer = new PdfWriter(outputPdfPath);
        using var pdfDocument = new PdfDocument(writer);
        using var document = new Document(pdfDocument, PageSize.LETTER);
        document.SetMargins(
            EditorialStyleProfile.MarginPoints,
            EditorialStyleProfile.MarginPoints,
            EditorialStyleProfile.MarginPoints,
            EditorialStyleProfile.MarginPoints);

        var startIndex = 0;
        if (includeImages)
        {
            AddCover(document, pages[0]);
            startIndex = 1;
        }

        for (var index = startIndex; index < pages.Count; index++)
        {
            if (index > startIndex || includeImages)
            {
                document.Add(new AreaBreak(AreaBreakType.NEXT_PAGE));
            }

            AddInteriorPage(
                document,
                pages[index],
                index < ocrPages.Count ? ocrPages[index] : new OcrPageResult { FullText = string.Empty, Lines = [], Words = [] },
                index < outputPageTexts.Count ? outputPageTexts[index] : string.Empty,
                includeImages ? rescuedImages.Where(image => image.PageNumber == index + 1).ToList() : [],
                index);
        }
    }

    private static void AddCover(Document document, BookPageInfo cover)
    {
        var coverPath = !string.IsNullOrWhiteSpace(cover.RestoredImagePath) && File.Exists(cover.RestoredImagePath)
            ? cover.RestoredImagePath
            : cover.OriginalImagePath;
        if (!File.Exists(coverPath))
        {
            return;
        }

        document.SetMargins(0, 0, 0, 0);
        var image = new Image(ImageDataFactory.Create(coverPath));
        image.ScaleToFit(PageSize.LETTER.GetWidth(), PageSize.LETTER.GetHeight());
        image.SetHorizontalAlignment(HorizontalAlignment.CENTER);
        document.Add(image);
        document.SetMargins(
            EditorialStyleProfile.MarginPoints,
            EditorialStyleProfile.MarginPoints,
            EditorialStyleProfile.MarginPoints,
            EditorialStyleProfile.MarginPoints);
    }

    private static void AddInteriorPage(
        Document document,
        BookPageInfo page,
        OcrPageResult ocrPage,
        string text,
        IReadOnlyList<RescuedImageInfo> pageImages,
        int pageIndex)
    {
        var sourceBlocks = DocumentLayoutService.BuildTextBlocks(page, ocrPage);
        var paragraphs = DocumentLayoutService.SplitParagraphs(text);
        var blocks = PageBlockClassifier.Classify(page, sourceBlocks, paragraphs, pageIndex);

        foreach (var block in blocks)
        {
            AddBlock(document, block);
        }

        foreach (var image in pageImages
                     .Where(image => !image.Kind.Equals("table", StringComparison.OrdinalIgnoreCase) ||
                                     !DocumentLayoutService.ContainsStructuredTable(paragraphs))
                     .OrderBy(image => image.Y)
                     .ThenBy(image => image.X))
        {
            AddImage(document, image);
        }
    }

    private static void AddBlock(Document document, EditorialBlock block)
    {
        switch (block.Kind)
        {
            case EditorialBlockKind.Title:
                document.Add(new Paragraph(block.Text)
                    .SetFont(PdfFontFactory.CreateFont(StandardFonts.TIMES_BOLD))
                    .SetFontSize(EditorialStyleProfile.TitleFontSize)
                    .SetTextAlignment(TextAlignment.CENTER)
                    .SetMarginTop(24)
                    .SetMarginBottom(18));
                break;
            case EditorialBlockKind.Subtitle:
                document.Add(new Paragraph(block.Text)
                    .SetFont(PdfFontFactory.CreateFont(StandardFonts.TIMES_BOLD))
                    .SetFontSize(EditorialStyleProfile.SubtitleFontSize)
                    .SetTextAlignment(TextAlignment.CENTER)
                    .SetMarginTop(18)
                    .SetMarginBottom(12));
                break;
            case EditorialBlockKind.SectionHeading:
                document.Add(new Paragraph(block.Text)
                    .SetFont(PdfFontFactory.CreateFont(StandardFonts.TIMES_BOLD))
                    .SetFontSize(EditorialStyleProfile.SectionFontSize)
                    .SetMarginTop(16)
                    .SetMarginBottom(8));
                break;
            case EditorialBlockKind.Quote:
                document.Add(CreateBodyParagraph(block.Text)
                    .SetFontSize(EditorialStyleProfile.QuoteFontSize)
                    .SetMarginLeft(24)
                    .SetMarginRight(24));
                break;
            case EditorialBlockKind.Bullet:
                document.Add(CreateBodyParagraph($"• {block.Text}")
                    .SetFirstLineIndent(0)
                    .SetMarginLeft(18));
                break;
            case EditorialBlockKind.Numbered:
                document.Add(CreateBodyParagraph($"{block.Marker} {block.Text}")
                    .SetFirstLineIndent(0)
                    .SetMarginLeft(18));
                break;
            case EditorialBlockKind.Formula:
                document.Add(new Paragraph(block.Text)
                    .SetFont(PdfFontFactory.CreateFont(StandardFonts.TIMES_ITALIC))
                    .SetFontSize(EditorialStyleProfile.FormulaFontSize)
                    .SetTextAlignment(TextAlignment.CENTER)
                    .SetMarginTop(8)
                    .SetMarginBottom(8));
                break;
            case EditorialBlockKind.Table:
                AddTable(document, block.TableRows ?? []);
                break;
            case EditorialBlockKind.Caption:
                document.Add(new Paragraph(block.Text)
                    .SetFont(PdfFontFactory.CreateFont(StandardFonts.TIMES_BOLD))
                    .SetFontSize(EditorialStyleProfile.CaptionFontSize)
                    .SetTextAlignment(TextAlignment.CENTER)
                    .SetMarginTop(4)
                    .SetMarginBottom(8));
                break;
            default:
                document.Add(CreateBodyParagraph(block.Text));
                break;
        }
    }

    private static Paragraph CreateBodyParagraph(string text)
    {
        return new Paragraph(text)
            .SetFont(PdfFontFactory.CreateFont(StandardFonts.TIMES_ROMAN))
            .SetFontSize(EditorialStyleProfile.BodyFontSize)
            .SetTextAlignment(TextAlignment.JUSTIFIED)
            .SetMultipliedLeading(EditorialStyleProfile.BodyLineMultiplier)
            .SetFirstLineIndent(EditorialStyleProfile.BodyFirstLineIndent)
            .SetMarginTop(0)
            .SetMarginBottom(EditorialStyleProfile.BodySpaceAfter);
    }

    private static void AddTable(Document document, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var columnCount = rows.Max(row => row.Count);
        var table = new Table(UnitValue.CreatePercentArray(columnCount))
            .UseAllAvailableWidth()
            .SetMarginTop(8)
            .SetMarginBottom(10);

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
            {
                var value = columnIndex < rows[rowIndex].Count ? rows[rowIndex][columnIndex] : string.Empty;
                var cell = new Cell()
                    .Add(new Paragraph(value).SetFontSize(9).SetMargin(0))
                    .SetBorder(new SolidBorder(ColorConstants.GRAY, 0.5f))
                    .SetPadding(3);
                if (rowIndex == 0)
                {
                    cell.SetBackgroundColor(new DeviceRgb(245, 245, 245));
                }

                table.AddCell(cell);
            }
        }

        document.Add(table);
    }

    private static void AddImage(Document document, RescuedImageInfo imageInfo)
    {
        if (!File.Exists(imageInfo.ImagePath))
        {
            return;
        }

        var image = new Image(ImageDataFactory.Create(imageInfo.ImagePath));
        image.SetMaxWidth(PageSize.LETTER.GetWidth() - (EditorialStyleProfile.MarginPoints * 2));
        image.SetAutoScaleHeight(true);
        image.SetHorizontalAlignment(HorizontalAlignment.CENTER);
        image.SetMarginTop(8);
        image.SetMarginBottom(EditorialStyleProfile.FigureSpaceAfter);
        document.Add(image);
    }
}
