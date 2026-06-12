using BookRescue.App.Models;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using OpenCvSharp;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using M = DocumentFormat.OpenXml.Math;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace BookRescue.App.Services;

public sealed class DocxEditorialWriter
{
    private const long EmusPerInch = 914400L;
    private const int LetterWidthTwips = 12240;
    private const int LetterHeightTwips = 15840;
    private const int MarginTwips = 1440;
    private const int ContentWidthTwips = LetterWidthTwips - (MarginTwips * 2);

    public void WriteDocx(
        string outputPath,
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

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        using var document = WordprocessingDocument.Create(outputPath, WordprocessingDocumentType.Document);
        var mainPart = document.AddMainDocumentPart();
        mainPart.Document = new Document(new Body());
        AddStyles(mainPart);

        var body = mainPart.Document.Body!;
        var startIndex = 0;
        if (includeImages)
        {
            AppendCover(mainPart, body, pages[0]);
            startIndex = 1;
        }

        for (var index = startIndex; index < pages.Count; index++)
        {
            if (index > startIndex || includeImages)
            {
                body.Append(CreatePageBreak());
            }

            AppendInteriorPage(
                mainPart,
                body,
                pages[index],
                index < ocrPages.Count ? ocrPages[index] : new OcrPageResult { FullText = string.Empty, Words = [], Lines = [] },
                index < outputPageTexts.Count ? outputPageTexts[index] : string.Empty,
                includeImages ? rescuedImages.Where(image => image.PageNumber == index + 1).ToList() : [],
                index);
        }

        body.Append(CreateSectionProperties());
        mainPart.Document.Save();
    }

    private static void AppendCover(MainDocumentPart mainPart, Body body, BookPageInfo cover)
    {
        var coverPath = !string.IsNullOrWhiteSpace(cover.RestoredImagePath) && File.Exists(cover.RestoredImagePath)
            ? cover.RestoredImagePath
            : cover.OriginalImagePath;
        if (!File.Exists(coverPath))
        {
            return;
        }

        AppendImagePath(mainPart, body, coverPath, ContentWidthTwips / 1440d, 9.3d, spacingBeforeTwips: 0);
    }

    private static void AppendInteriorPage(
        MainDocumentPart mainPart,
        Body body,
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
            AppendBlock(body, block);
        }

        var hasStructuredTable = DocumentLayoutService.ContainsStructuredTable(paragraphs);
        foreach (var image in pageImages
                     .Where(image => !image.Kind.Equals("table", StringComparison.OrdinalIgnoreCase) || !hasStructuredTable)
                     .OrderBy(image => image.Y)
                     .ThenBy(image => image.X))
        {
            AppendImagePath(mainPart, body, image.ImagePath, maxWidthInches: 5.9d, maxHeightInches: 4.2d, spacingBeforeTwips: 120);
        }
    }

    private static void AppendBlock(Body body, EditorialBlock block)
    {
        switch (block.Kind)
        {
            case EditorialBlockKind.Title:
                AppendParagraph(body, block.Text, "BookRescueEditorialTitle", JustificationValues.Center, before: 320, after: 220);
                break;
            case EditorialBlockKind.Subtitle:
                AppendParagraph(body, block.Text, "BookRescueEditorialSubtitle", JustificationValues.Center, before: 220, after: 160);
                break;
            case EditorialBlockKind.SectionHeading:
                AppendParagraph(body, block.Text, "BookRescueEditorialHeading", JustificationValues.Left, before: 220, after: 100, keepNext: true);
                break;
            case EditorialBlockKind.Quote:
                AppendParagraph(body, block.Text, "BookRescueEditorialQuote", JustificationValues.Both, before: 80, after: 100, leftIndent: 360, rightIndent: 360);
                break;
            case EditorialBlockKind.Bullet:
                AppendParagraph(body, $"• {block.Text}", "BookRescueEditorialBody", JustificationValues.Left, before: 20, after: 70, leftIndent: 360, hanging: 240);
                break;
            case EditorialBlockKind.Numbered:
                AppendParagraph(body, $"{block.Marker} {block.Text}", "BookRescueEditorialBody", JustificationValues.Left, before: 20, after: 70, leftIndent: 420, hanging: 300);
                break;
            case EditorialBlockKind.Formula:
                AppendFormula(body, block.Text);
                break;
            case EditorialBlockKind.Table:
                AppendTable(body, block.TableRows ?? []);
                break;
            case EditorialBlockKind.Caption:
                AppendParagraph(body, block.Text, "BookRescueEditorialCaption", JustificationValues.Center, before: 60, after: 100);
                break;
            default:
                AppendParagraph(body, block.Text, "BookRescueEditorialBody", JustificationValues.Both, before: 0, after: 90, firstLine: 300);
                break;
        }
    }

    private static void AppendParagraph(
        Body body,
        string text,
        string styleId,
        JustificationValues justification,
        int before,
        int after,
        bool keepNext = false,
        int firstLine = 0,
        int leftIndent = 0,
        int rightIndent = 0,
        int hanging = 0)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var properties = new ParagraphProperties(
            new ParagraphStyleId { Val = styleId },
            new SpacingBetweenLines { Before = before.ToString(), After = after.ToString(), Line = "276", LineRule = LineSpacingRuleValues.Auto },
            new Justification { Val = justification });

        if (keepNext)
        {
            properties.Append(new KeepNext());
        }

        if (firstLine > 0 || leftIndent > 0 || rightIndent > 0 || hanging > 0)
        {
            properties.Append(new Indentation
            {
                FirstLine = firstLine > 0 ? firstLine.ToString() : null,
                Left = leftIndent > 0 ? leftIndent.ToString() : null,
                Right = rightIndent > 0 ? rightIndent.ToString() : null,
                Hanging = hanging > 0 ? hanging.ToString() : null
            });
        }

        body.Append(new Paragraph(
            properties,
            new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve })));
    }

    private static void AppendFormula(Body body, string text)
    {
        body.Append(new Paragraph(
            new ParagraphProperties(
                new ParagraphStyleId { Val = "BookRescueEditorialFormula" },
                new Justification { Val = JustificationValues.Center },
                new SpacingBetweenLines { Before = "130", After = "130" }),
            new M.OfficeMath(
                new M.Run(
                    new M.RunProperties(new M.Style { Val = M.StyleValues.Italic }),
                    new M.Text(text) { Space = SpaceProcessingModeValues.Preserve }))));
    }

    private static void AppendTable(Body body, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var columnCount = rows.Max(row => row.Count);
        var table = new Table(
            new TableProperties(
                new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct },
                new TableBorders(
                    new TopBorder { Val = BorderValues.Single, Size = 4 },
                    new BottomBorder { Val = BorderValues.Single, Size = 4 },
                    new LeftBorder { Val = BorderValues.Single, Size = 4 },
                    new RightBorder { Val = BorderValues.Single, Size = 4 },
                    new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
                    new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 })));

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = new TableRow();
            for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
            {
                var value = columnIndex < rows[rowIndex].Count ? rows[rowIndex][columnIndex] : string.Empty;
                var cellProperties = new TableCellProperties(
                    new TableCellWidth { Type = TableWidthUnitValues.Pct, Width = Math.Max(1, 5000 / columnCount).ToString() });
                if (rowIndex == 0)
                {
                    cellProperties.Append(new Shading { Val = ShadingPatternValues.Clear, Fill = "F1F3F4" });
                }

                row.Append(new TableCell(
                    cellProperties,
                    new Paragraph(
                        new ParagraphProperties(new SpacingBetweenLines { Before = "0", After = "0" }),
                        new Run(new Text(value) { Space = SpaceProcessingModeValues.Preserve }))));
            }

            table.Append(row);
        }

        body.Append(new Paragraph(new ParagraphProperties(new SpacingBetweenLines { Before = "120", After = "60" })));
        body.Append(table);
        body.Append(new Paragraph(new ParagraphProperties(new SpacingBetweenLines { Before = "60", After = "120" })));
    }

    private static void AppendImagePath(
        MainDocumentPart mainPart,
        Body body,
        string imagePath,
        double maxWidthInches,
        double maxHeightInches,
        int spacingBeforeTwips)
    {
        if (!File.Exists(imagePath))
        {
            return;
        }

        var imagePart = mainPart.AddImagePart(ImagePartType.Png);
        using (var stream = File.OpenRead(imagePath))
        {
            imagePart.FeedData(stream);
        }

        var (width, height) = ReadImageSize(imagePath);
        var ratio = width / (double)Math.Max(1, height);
        var widthInches = maxWidthInches;
        var heightInches = widthInches / ratio;
        if (heightInches > maxHeightInches)
        {
            heightInches = maxHeightInches;
            widthInches = heightInches * ratio;
        }

        var relationshipId = mainPart.GetIdOfPart(imagePart);
        body.Append(new Paragraph(
            new ParagraphProperties(
                new Justification { Val = JustificationValues.Center },
                new SpacingBetweenLines { Before = spacingBeforeTwips.ToString(), After = "120" }),
            new Run(CreateDrawing(
                relationshipId,
                Path.GetFileName(imagePath),
                (long)(widthInches * EmusPerInch),
                (long)(heightInches * EmusPerInch)))));
    }

    private static Drawing CreateDrawing(string relationshipId, string name, long cx, long cy)
    {
        return new Drawing(
            new DW.Inline(
                new DW.Extent { Cx = cx, Cy = cy },
                new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
                new DW.DocProperties { Id = (UInt32Value)(uint)Math.Abs(name.GetHashCode()), Name = name },
                new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
                new A.Graphic(
                    new A.GraphicData(
                        new PIC.Picture(
                            new PIC.NonVisualPictureProperties(
                                new PIC.NonVisualDrawingProperties { Id = 1U, Name = name },
                                new PIC.NonVisualPictureDrawingProperties()),
                            new PIC.BlipFill(
                                new A.Blip { Embed = relationshipId },
                                new A.Stretch(new A.FillRectangle())),
                            new PIC.ShapeProperties(
                                new A.Transform2D(
                                    new A.Offset { X = 0L, Y = 0L },
                                    new A.Extents { Cx = cx, Cy = cy }),
                                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
                    {
                        Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture"
                    })));
    }

    private static void AddStyles(MainDocumentPart mainPart)
    {
        var stylePart = mainPart.AddNewPart<StyleDefinitionsPart>();
        stylePart.Styles = new Styles(
            CreateParagraphStyle("BookRescueEditorialBody", "Texto editorial", "Normal", "22", fontName: "Cambria"),
            CreateParagraphStyle("BookRescueEditorialTitle", "Título editorial", "Title", "44", bold: true, fontName: "Cambria"),
            CreateParagraphStyle("BookRescueEditorialSubtitle", "Subtítulo editorial", "Subtitle", "32", bold: true, fontName: "Cambria"),
            CreateParagraphStyle("BookRescueEditorialHeading", "Encabezado editorial", "Heading1", "28", bold: true, fontName: "Cambria"),
            CreateParagraphStyle("BookRescueEditorialQuote", "Cita editorial", "Normal", "20", italic: true, fontName: "Cambria"),
            CreateParagraphStyle("BookRescueEditorialFormula", "Fórmula editorial", "Normal", "22", italic: true, fontName: "Cambria Math"),
            CreateParagraphStyle("BookRescueEditorialCaption", "Leyenda editorial", "Caption", "18", bold: true, fontName: "Cambria"));
        stylePart.Styles.Save();
    }

    private static Style CreateParagraphStyle(
        string styleId,
        string styleName,
        string basedOn,
        string fontSize,
        bool bold = false,
        bool italic = false,
        string fontName = "Cambria")
    {
        var runProperties = new StyleRunProperties(
            new RunFonts { Ascii = fontName, HighAnsi = fontName },
            new FontSize { Val = fontSize });

        if (bold)
        {
            runProperties.Append(new Bold());
        }

        if (italic)
        {
            runProperties.Append(new Italic());
        }

        return new Style(
            new StyleName { Val = styleName },
            new BasedOn { Val = basedOn },
            new UIPriority { Val = 1 },
            new PrimaryStyle(),
            runProperties)
        {
            Type = StyleValues.Paragraph,
            StyleId = styleId
        };
    }

    private static Paragraph CreatePageBreak()
    {
        return new Paragraph(new Run(new Break { Type = BreakValues.Page }));
    }

    private static SectionProperties CreateSectionProperties()
    {
        return new SectionProperties(
            new PageSize { Width = (UInt32Value)(uint)LetterWidthTwips, Height = (UInt32Value)(uint)LetterHeightTwips },
            new PageMargin
            {
                Top = MarginTwips,
                Bottom = MarginTwips,
                Left = (UInt32Value)(uint)MarginTwips,
                Right = (UInt32Value)(uint)MarginTwips,
                Header = 300U,
                Footer = 300U,
                Gutter = 0U
            });
    }

    private static (int width, int height) ReadImageSize(string imagePath)
    {
        using var image = Cv2.ImRead(imagePath, ImreadModes.Unchanged);
        return image.Empty() ? (900, 1200) : (image.Width, image.Height);
    }
}
