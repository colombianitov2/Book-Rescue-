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

public sealed class DocxCloneWriter
{
    private const long EmusPerInch = 914400L;

    public void WriteCloneDocx(
        string outputPath,
        IReadOnlyList<BookPageInfo> pages,
        IReadOnlyList<OcrPageResult> ocrPages,
        IReadOnlyList<string> outputPageTexts,
        IReadOnlyList<RescuedImageInfo> rescuedImages)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var profile = DocumentCompositionProfile.Analyze(pages, ocrPages, outputPageTexts, rescuedImages);

        using var document = WordprocessingDocument.Create(outputPath, WordprocessingDocumentType.Document);
        var mainPart = document.AddMainDocumentPart();
        mainPart.Document = new Document(new Body());
        AddStyles(mainPart);

        var body = mainPart.Document.Body!;

        for (var index = 0; index < pages.Count; index++)
        {
            if (index > 0)
            {
                body.Append(CreatePageBreak());
            }

            AppendPageContent(
                mainPart,
                body,
                profile,
                pages[index],
                index < ocrPages.Count ? ocrPages[index] : new OcrPageResult { FullText = string.Empty, Words = [], Lines = [] },
                index < outputPageTexts.Count ? outputPageTexts[index] : string.Empty,
                rescuedImages.Where(image => image.PageNumber == index + 1).ToList());
        }

        body.Append(CreateSectionProperties(profile));
        mainPart.Document.Save();
    }

    private static void AppendPageContent(
        MainDocumentPart mainPart,
        Body body,
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
            AppendParagraph(body, "[Sin texto legible en esta página]");
            return;
        }

        float? previousBottom = null;
        foreach (var block in blocks.OrderBy(block => block.Y).ThenBy(block => block.X))
        {
            var sourceSpacingBefore = CalculateSourceSpacingTwips(profile, page, block, previousBottom);
            if (block.TextBlock is not null)
            {
                AppendTextBlock(body, profile, page, block.TextBlock, sourceSpacingBefore);
            }

            if (block.Image is not null)
            {
                AppendImage(mainPart, body, profile, block.Image, sourceSpacingBefore);
            }

            previousBottom = previousBottom.HasValue
                ? Math.Max(previousBottom.Value, block.Bottom)
                : block.Bottom;
        }
    }

    private static void AppendImage(
        MainDocumentPart mainPart,
        Body body,
        DocumentCompositionProfile profile,
        RescuedImageInfo image,
        int spacingBeforeTwips)
    {
        var imagePart = mainPart.AddImagePart(ImagePartType.Png);
        using (var stream = File.OpenRead(image.ImagePath))
        {
            imagePart.FeedData(stream);
        }

        var relationshipId = mainPart.GetIdOfPart(imagePart);
        var (cx, cy) = CalculateImageSize(image, profile);
        var maxHeight = (long)(Math.Min(profile.ContentHeightPoints / 72d * 0.72d, 7.4d) * EmusPerInch);

        if (cy > maxHeight)
        {
            cy = maxHeight;
            var (width, height) = ReadImageSize(image.ImagePath);
            cx = (long)(maxHeight * (width / (double)Math.Max(1, height)));
        }

        body.Append(new Paragraph(
            new ParagraphProperties(
                new Justification { Val = JustificationValues.Center },
                new SpacingBetweenLines { Before = (80 + spacingBeforeTwips).ToString(), After = "110" }),
            new Run(CreateDrawing(relationshipId, Path.GetFileName(image.ImagePath), cx, cy))));
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
                    }))
            {
                DistanceFromTop = 0U,
                DistanceFromBottom = 0U,
                DistanceFromLeft = 0U,
                DistanceFromRight = 0U
            });
    }

    private static void AppendHeading(Body body, string text, int level, int spacingBeforeTwips)
    {
        var size = level <= 2 ? "24" : "21";
        var styleId = level switch
        {
            <= 1 => "BookRescueHeading1",
            2 => "BookRescueHeading2",
            _ => "BookRescueHeading3"
        };

        body.Append(new Paragraph(
            new ParagraphProperties(
                new ParagraphStyleId { Val = styleId },
                new SpacingBetweenLines { Before = (180 + spacingBeforeTwips).ToString(), After = "80" },
                new KeepNext()),
            new Run(
                new RunProperties(new Bold(), new FontSize { Val = size }),
                new Text(text) { Space = SpaceProcessingModeValues.Preserve })));
    }

    private static void AppendTextBlock(
        Body body,
        DocumentCompositionProfile profile,
        BookPageInfo page,
        PageTextBlock textBlock,
        int spacingBeforeTwips)
    {
        var text = textBlock.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (StructuredTextService.TryParseMarkdownTable(text, out var tableRows))
        {
            AppendTable(body, tableRows, spacingBeforeTwips);
            return;
        }

        if (StructuredTextService.TryReadMarkdownHeading(text, out var headingText, out var level))
        {
            AppendHeading(body, headingText, level, spacingBeforeTwips);
            return;
        }

        if (StructuredTextService.IsFormula(text))
        {
            AppendFormula(body, text, spacingBeforeTwips);
            return;
        }

        if (StructuredTextService.TryReadBullet(text, out var bulletText))
        {
            AppendBullet(body, bulletText, spacingBeforeTwips, CalculateSourceIndentTwips(profile, page, textBlock));
            return;
        }

        if (StructuredTextService.TryReadNumberedItem(text, out var marker, out var itemText))
        {
            AppendNumberedItem(body, marker, itemText, spacingBeforeTwips, CalculateSourceIndentTwips(profile, page, textBlock));
            return;
        }

        if (StructuredTextService.IsCaption(text))
        {
            AppendCaption(body, text, spacingBeforeTwips);
            return;
        }

        if (StructuredTextService.ShouldRenderAsHeading(text, textBlock.IsHeading))
        {
            AppendHeading(body, text, 3, spacingBeforeTwips);
            return;
        }

        AppendParagraph(body, text, spacingBeforeTwips, CalculateSourceIndentTwips(profile, page, textBlock));
    }

    private static void AppendFormula(Body body, string text, int spacingBeforeTwips)
    {
        var formula = StructuredTextService.StripFormulaMarkers(text);
        body.Append(new Paragraph(
            new ParagraphProperties(
                new ParagraphStyleId { Val = "BookRescueFormula" },
                new Justification { Val = JustificationValues.Center },
                new SpacingBetweenLines { Before = (80 + spacingBeforeTwips).ToString(), After = "100" }),
            CreateOfficeMath(formula)));
    }

    private static void AppendTable(Body body, IReadOnlyList<IReadOnlyList<string>> rows, int spacingBeforeTwips)
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
                    new TableCellWidth
                    {
                        Type = TableWidthUnitValues.Pct,
                        Width = Math.Max(1, 5000 / Math.Max(1, columnCount)).ToString()
                    });

                if (rowIndex == 0)
                {
                    cellProperties.Append(new Shading { Val = ShadingPatternValues.Clear, Fill = "E8EAED" });
                }

                var runProperties = rowIndex == 0
                    ? new RunProperties(new Bold(), new FontSize { Val = "19" })
                    : new RunProperties(new FontSize { Val = "19" });

                row.Append(new TableCell(
                    cellProperties,
                    new Paragraph(
                        new ParagraphProperties(new SpacingBetweenLines { Before = "0", After = "0" }),
                        new Run(runProperties, new Text(value) { Space = SpaceProcessingModeValues.Preserve }))));
            }

            table.Append(row);
        }

        body.Append(new Paragraph(new ParagraphProperties(new SpacingBetweenLines { Before = (80 + spacingBeforeTwips).ToString(), After = "40" })));
        body.Append(table);
        body.Append(new Paragraph(new ParagraphProperties(new SpacingBetweenLines { Before = "40", After = "80" })));
    }

    private static void AppendBullet(Body body, string text, int spacingBeforeTwips, int sourceIndentTwips)
    {
        body.Append(new Paragraph(
            new ParagraphProperties(
                new ParagraphStyleId { Val = "BookRescueBullet" },
                new SpacingBetweenLines { Before = spacingBeforeTwips.ToString(), After = "60" },
                new Indentation { Left = (360 + sourceIndentTwips).ToString(), Hanging = "240" }),
            new Run(
                new RunProperties(new FontSize { Val = "21" }),
                new Text("• ") { Space = SpaceProcessingModeValues.Preserve }),
            new Run(
                new RunProperties(new FontSize { Val = "21" }),
                new Text(text) { Space = SpaceProcessingModeValues.Preserve })));
    }

    private static void AppendNumberedItem(Body body, string marker, string text, int spacingBeforeTwips, int sourceIndentTwips)
    {
        body.Append(new Paragraph(
            new ParagraphProperties(
                new ParagraphStyleId { Val = "BookRescueNumbered" },
                new SpacingBetweenLines { Before = spacingBeforeTwips.ToString(), After = "60" },
                new Indentation { Left = (460 + sourceIndentTwips).ToString(), Hanging = "360" }),
            new Run(
                new RunProperties(new Bold(), new FontSize { Val = "21" }),
                new Text($"{marker} ") { Space = SpaceProcessingModeValues.Preserve }),
            new Run(
                new RunProperties(new FontSize { Val = "21" }),
                new Text(text) { Space = SpaceProcessingModeValues.Preserve })));
    }

    private static void AppendCaption(Body body, string text, int spacingBeforeTwips)
    {
        body.Append(new Paragraph(
            new ParagraphProperties(
                new ParagraphStyleId { Val = "BookRescueCaption" },
                new Justification { Val = JustificationValues.Center },
                new SpacingBetweenLines { Before = spacingBeforeTwips.ToString(), After = "70" }),
            new Run(
                new RunProperties(new Bold(), new FontSize { Val = "18" }),
                new Text(text) { Space = SpaceProcessingModeValues.Preserve })));
    }

    private static void AppendParagraph(Body body, string text, int spacingBeforeTwips = 0, int sourceIndentTwips = 0)
    {
        var paragraphProperties = new ParagraphProperties(
            new ParagraphStyleId { Val = "BookRescueBody" },
            new SpacingBetweenLines { Before = spacingBeforeTwips.ToString(), After = "80" },
            new Justification { Val = JustificationValues.Both });
        if (sourceIndentTwips > 0)
        {
            paragraphProperties.Append(new Indentation { Left = sourceIndentTwips.ToString() });
        }

        body.Append(new Paragraph(
            paragraphProperties,
            new Run(
                new RunProperties(new FontSize { Val = "21" }),
                new Text(text) { Space = SpaceProcessingModeValues.Preserve })));
    }

    private static M.OfficeMath CreateOfficeMath(string formula)
    {
        return new M.OfficeMath(
            new M.Run(
                new M.RunProperties(
                    new M.Style { Val = M.StyleValues.Italic }),
                new M.Text(formula) { Space = SpaceProcessingModeValues.Preserve }));
    }

    private static void AddStyles(MainDocumentPart mainPart)
    {
        var stylePart = mainPart.AddNewPart<StyleDefinitionsPart>();
        stylePart.Styles = new Styles(
            CreateParagraphStyle("BookRescueBody", "Texto de libro", basedOn: "Normal", fontSize: "21", isDefault: true),
            CreateParagraphStyle("BookRescueHeading1", "Título de libro", basedOn: "Heading1", fontSize: "30", bold: true),
            CreateParagraphStyle("BookRescueHeading2", "Subtítulo de libro", basedOn: "Heading2", fontSize: "25", bold: true),
            CreateParagraphStyle("BookRescueHeading3", "Encabezado técnico", basedOn: "Heading3", fontSize: "22", bold: true),
            CreateParagraphStyle("BookRescueFormula", "Fórmula técnica", basedOn: "Normal", fontSize: "21", italic: true, fontName: "Cambria Math"),
            CreateParagraphStyle("BookRescueBullet", "Viñeta técnica", basedOn: "Normal", fontSize: "21"),
            CreateParagraphStyle("BookRescueNumbered", "Lista numerada técnica", basedOn: "Normal", fontSize: "21"),
            CreateParagraphStyle("BookRescueCaption", "Leyenda de figura", basedOn: "Caption", fontSize: "18", bold: true));
        stylePart.Styles.Save();
    }

    private static Style CreateParagraphStyle(
        string styleId,
        string styleName,
        string basedOn,
        string fontSize,
        bool isDefault = false,
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
            StyleId = styleId,
            Default = isDefault
        };
    }

    private static bool IsFormula(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length > 2 && trimmed.StartsWith('$') && trimmed.EndsWith('$');
    }

    private static string StripFormulaMarkers(string text)
    {
        var trimmed = text.Trim();
        return IsFormula(trimmed) ? trimmed[1..^1].Trim() : trimmed;
    }

    private static bool TryReadBullet(string text, out string bulletText)
    {
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith("- ", StringComparison.Ordinal) ||
            trimmed.StartsWith("* ", StringComparison.Ordinal) ||
            trimmed.StartsWith("• ", StringComparison.Ordinal))
        {
            bulletText = trimmed[2..].Trim();
            return !string.IsNullOrWhiteSpace(bulletText);
        }

        bulletText = string.Empty;
        return false;
    }

    private static bool LooksLikeHeading(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length is < 4 or > 90)
        {
            return false;
        }

        var letters = trimmed.Where(char.IsLetter).ToList();
        return letters.Count >= 4 && letters.Count(letter => char.IsUpper(letter)) / (double)letters.Count > 0.78;
    }

    private static Paragraph CreatePageBreak()
    {
        return new Paragraph(new Run(new Break { Type = BreakValues.Page }));
    }

    private static SectionProperties CreateSectionProperties(DocumentCompositionProfile profile)
    {
        var pageSize = new PageSize
        {
            Width = ToTwipsUInt(profile.PageWidthPoints),
            Height = ToTwipsUInt(profile.PageHeightPoints)
        };
        if (profile.PageWidthPoints > profile.PageHeightPoints)
        {
            pageSize.Orient = PageOrientationValues.Landscape;
        }

        return new SectionProperties(
            pageSize,
            new PageMargin
            {
                Top = ToTwipsInt(profile.MarginTopPoints),
                Right = ToTwipsUInt(profile.MarginRightPoints),
                Bottom = ToTwipsInt(profile.MarginBottomPoints),
                Left = ToTwipsUInt(profile.MarginLeftPoints),
                Header = 300U,
                Footer = 300U,
                Gutter = 0U
            });
    }

    private static (int width, int height) ReadImageSize(string imagePath)
    {
        using var image = Cv2.ImRead(imagePath, ImreadModes.Unchanged);
        return image.Empty() ? (800, 600) : (image.Width, image.Height);
    }

    private static (long cx, long cy) CalculateImageSize(RescuedImageInfo image, DocumentCompositionProfile profile)
    {
        var (width, height) = ReadImageSize(image.ImagePath);
        var widthInches = Math.Clamp(profile.GetImageWidthPoints(image) / 72d, 0.9d, profile.ContentWidthPoints / 72d);

        var cx = (long)(widthInches * EmusPerInch);
        var cy = (long)(cx * (height / (double)Math.Max(1, width)));
        return (cx, cy);
    }

    private static int CalculateSourceSpacingTwips(
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

        return (int)Math.Clamp(Math.Round(spacingPoints * 20d), 0d, 1800d);
    }

    private static int CalculateSourceIndentTwips(
        DocumentCompositionProfile profile,
        BookPageInfo page,
        PageTextBlock textBlock)
    {
        var scaleX = profile.PageWidthPoints / Math.Max(1d, page.PixelWidth);
        var sourceLeft = textBlock.X * scaleX;
        var extraPoints = Math.Clamp((sourceLeft - profile.MarginLeftPoints) * 0.45d, 0d, 42d);
        return (int)Math.Clamp(Math.Round(extraPoints * 20d), 0d, 840d);
    }

    private static UInt32Value ToTwipsUInt(double points)
    {
        return (UInt32Value)(uint)Math.Clamp(Math.Round(points * 20d), 1d, 31680d);
    }

    private static Int32Value ToTwipsInt(double points)
    {
        return (Int32Value)(int)Math.Clamp(Math.Round(points * 20d), 1d, 31680d);
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
