using BookRescue.App.Models;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using OpenCvSharp;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace BookRescue.App.Services;

public sealed class PerfectDocxReconstructor
{
    private const long EmusPerInch = 914400L;
    private const int DefaultMarginTwips = 360;

    public void WriteDocx(
        string outputDocxPath,
        IReadOnlyList<HeavyPageLayout> layouts)
    {
        if (layouts.Count == 0)
        {
            throw new InvalidOperationException("No hay páginas para reconstruir.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputDocxPath)!);

        using var document = WordprocessingDocument.Create(outputDocxPath, WordprocessingDocumentType.Document);
        var mainPart = document.AddMainDocumentPart();
        mainPart.Document = new Document(new Body());
        var body = mainPart.Document.Body!;

        for (var i = 0; i < layouts.Count; i++)
        {
            if (i > 0)
            {
                body.Append(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
            }

            AppendPageContent(mainPart, body, layouts[i]);
            AppendEmergencyFullPageFallback(mainPart, body, layouts[i]);
        }

        body.Append(CreateSectionProperties(layouts[0]));
        mainPart.Document.Save();
    }

    private static void AppendPageContent(MainDocumentPart mainPart, Body body, HeavyPageLayout layout)
    {
        var lines = TextCleanupService.BuildOrderedLineBoxes(layout.Page, layout.Ocr)
            .Where(line => line.Confidence >= 35 && !string.IsNullOrWhiteSpace(line.Text))
            .Select(line => PageElement.FromText(line))
            .ToList();

        var images = layout.PageImages
            .Where(image => File.Exists(image.ImagePath))
            .Select(PageElement.FromImage)
            .ToList();

        var elements = lines
            .Concat(images)
            .OrderBy(element => element.Y)
            .ThenBy(element => element.X)
            .ToList();

        var previousBottom = 0f;
        foreach (var element in elements)
        {
            var spacingBefore = EstimateSpacingBefore(layout.Page, previousBottom, element.Y);
            if (element.Line is not null)
            {
                AppendPositionedTextLine(body, layout, element.Line, spacingBefore);
            }
            else if (element.Image is not null)
            {
                AppendPositionedImage(mainPart, body, layout, element.Image, spacingBefore);
            }

            previousBottom = Math.Max(previousBottom, element.Bottom);
        }
    }

    private static void AppendPositionedTextLine(Body body, HeavyPageLayout layout, OcrLineBox line, int spacingBefore)
    {
        var text = line.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var isHeading = TextCleanupService.IsLikelyHeading(text);
        var runProperties = new RunProperties(new FontSize { Val = EstimateFontHalfPoints(layout.Page, line, isHeading).ToString() });
        if (isHeading)
        {
            runProperties.Append(new Bold());
        }

        var paragraphProperties = new ParagraphProperties(
            new Indentation { Left = EstimateLeftIndentTwips(layout.Page, line.X).ToString() },
            new Justification { Val = isHeading && IsCentered(layout.Page, line) ? JustificationValues.Center : JustificationValues.Left },
            new SpacingBetweenLines
            {
                Before = spacingBefore.ToString(),
                After = isHeading ? "80" : "20",
                LineRule = LineSpacingRuleValues.Auto
            });

        body.Append(new Paragraph(
            paragraphProperties,
            new Run(
                runProperties,
                new Text(text) { Space = SpaceProcessingModeValues.Preserve })));
    }

    private static void AppendPositionedImage(
        MainDocumentPart mainPart,
        Body body,
        HeavyPageLayout layout,
        RescuedImageInfo image,
        int spacingBefore)
    {
        var (widthInches, heightInches) = EstimateImageSizeInches(layout.Page, image);
        var paragraphProperties = new ParagraphProperties(
            new Indentation { Left = EstimateLeftIndentTwips(layout.Page, image.X).ToString() },
            new Justification { Val = JustificationValues.Left },
            new SpacingBetweenLines { Before = spacingBefore.ToString(), After = "80" });

        AppendImagePath(
            mainPart,
            body,
            image.ImagePath,
            widthInches,
            heightInches,
            paragraphProperties);
    }

    private static void AppendEmergencyFullPageFallback(MainDocumentPart mainPart, Body body, HeavyPageLayout layout)
    {
        if (layout.Ocr.Lines.Count > 0 || layout.PageImages.Count > 0)
        {
            return;
        }

        var imagePath = !string.IsNullOrWhiteSpace(layout.Page.RestoredImagePath) && File.Exists(layout.Page.RestoredImagePath)
            ? layout.Page.RestoredImagePath
            : layout.Page.OriginalImagePath;
        AppendImagePath(
            mainPart,
            body,
            imagePath,
            maxWidthInches: 7.55d,
            maxHeightInches: 10.25d,
            new ParagraphProperties(new Justification { Val = JustificationValues.Center }));
    }

    private static void AppendImagePath(
        MainDocumentPart mainPart,
        Body body,
        string imagePath,
        double maxWidthInches,
        double maxHeightInches,
        ParagraphProperties paragraphProperties)
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

        body.Append(new Paragraph(
            paragraphProperties,
            new Run(CreateDrawing(
                mainPart.GetIdOfPart(imagePart),
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

    private static (int width, int height) ReadImageSize(string imagePath)
    {
        using var image = Cv2.ImRead(imagePath, ImreadModes.Unchanged);
        return image.Empty() ? (900, 1200) : (image.Width, image.Height);
    }

    private static SectionProperties CreateSectionProperties(HeavyPageLayout layout)
    {
        var widthTwips = Math.Clamp((int)MathF.Round(layout.Page.WidthPoints * 20f), 720, 31680);
        var heightTwips = Math.Clamp((int)MathF.Round(layout.Page.HeightPoints * 20f), 720, 31680);
        return new SectionProperties(
            new PageSize { Width = (UInt32Value)(uint)widthTwips, Height = (UInt32Value)(uint)heightTwips },
            new PageMargin
            {
                Top = DefaultMarginTwips,
                Bottom = DefaultMarginTwips,
                Left = DefaultMarginTwips,
                Right = DefaultMarginTwips,
                Header = 180U,
                Footer = 180U,
                Gutter = 0U
            });
    }

    private static int EstimateLeftIndentTwips(BookPageInfo page, float x)
    {
        var contentWidth = ContentWidthTwips(page);
        var indent = (int)MathF.Round(x / Math.Max(1f, page.PixelWidth) * contentWidth);
        return Math.Clamp(indent, 0, Math.Max(0, contentWidth - 720));
    }

    private static int EstimateSpacingBefore(BookPageInfo page, float previousBottom, float currentY)
    {
        if (previousBottom <= 0 || currentY <= previousBottom)
        {
            return 0;
        }

        var gapPixels = currentY - previousBottom;
        var pageHeightTwips = Math.Max(720f, page.HeightPoints * 20f);
        var gapTwips = (int)MathF.Round(gapPixels / Math.Max(1f, page.PixelHeight) * pageHeightTwips * 0.82f);
        return Math.Clamp(gapTwips, 0, 900);
    }

    private static int EstimateFontHalfPoints(BookPageInfo page, OcrLineBox line, bool isHeading)
    {
        var lineHeightPoints = line.Height / Math.Max(1f, page.PixelHeight) * Math.Max(1f, page.HeightPoints);
        var halfPoints = (int)MathF.Round(lineHeightPoints * 2f * 0.92f);
        return Math.Clamp(halfPoints, isHeading ? 18 : 14, isHeading ? 42 : 26);
    }

    private static bool IsCentered(BookPageInfo page, OcrLineBox line)
    {
        var lineCenter = line.X + (line.Width / 2f);
        var pageCenter = page.PixelWidth / 2f;
        return Math.Abs(lineCenter - pageCenter) < page.PixelWidth * 0.16f;
    }

    private static (double widthInches, double heightInches) EstimateImageSizeInches(BookPageInfo page, RescuedImageInfo image)
    {
        var pageWidthInches = Math.Max(1d, page.WidthPoints / 72d);
        var pageHeightInches = Math.Max(1d, page.HeightPoints / 72d);
        var contentWidthInches = Math.Max(1d, (ContentWidthTwips(page) / 1440d));
        var contentHeightInches = Math.Max(1d, (ContentHeightTwips(page) / 1440d));

        var widthInches = image.Width / Math.Max(1d, page.PixelWidth) * pageWidthInches;
        var heightInches = image.Height / Math.Max(1d, page.PixelHeight) * pageHeightInches;

        widthInches = Math.Clamp(widthInches, 0.25d, contentWidthInches);
        heightInches = Math.Clamp(heightInches, 0.25d, contentHeightInches * 0.72d);

        var imageRatio = image.Width / Math.Max(1d, image.Height);
        var renderedRatio = widthInches / Math.Max(0.01d, heightInches);
        if (Math.Abs(renderedRatio - imageRatio) > 0.08d)
        {
            if (renderedRatio > imageRatio)
            {
                widthInches = heightInches * imageRatio;
            }
            else
            {
                heightInches = widthInches / imageRatio;
            }
        }

        return (Math.Max(0.25d, widthInches), Math.Max(0.25d, heightInches));
    }

    private static int ContentWidthTwips(BookPageInfo page)
    {
        var pageWidthTwips = Math.Clamp((int)MathF.Round(page.WidthPoints * 20f), 720, 31680);
        return Math.Max(720, pageWidthTwips - (DefaultMarginTwips * 2));
    }

    private static int ContentHeightTwips(BookPageInfo page)
    {
        var pageHeightTwips = Math.Clamp((int)MathF.Round(page.HeightPoints * 20f), 720, 31680);
        return Math.Max(720, pageHeightTwips - (DefaultMarginTwips * 2));
    }

    private sealed record PageElement(
        float X,
        float Y,
        float Bottom,
        OcrLineBox? Line,
        RescuedImageInfo? Image)
    {
        public static PageElement FromText(OcrLineBox line)
        {
            return new PageElement(line.X, line.Y, line.Y + line.Height, line, null);
        }

        public static PageElement FromImage(RescuedImageInfo image)
        {
            return new PageElement(image.X, image.Y, image.Y + image.Height, null, image);
        }
    }
}
