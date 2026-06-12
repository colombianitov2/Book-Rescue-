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

            AppendPageHeader(body, layouts[i]);
            AppendVisibleText(body, layouts[i]);
            AppendRegionalImages(mainPart, body, layouts[i]);
            AppendEmergencyFullPageFallback(mainPart, body, layouts[i]);
        }

        body.Append(new SectionProperties(
            new PageSize { Width = 12240U, Height = 15840U },
            new PageMargin { Top = 360, Bottom = 360, Left = 360U, Right = 360U, Header = 180U, Footer = 180U, Gutter = 0U }));
        mainPart.Document.Save();
    }

    private static void AppendPageHeader(Body body, HeavyPageLayout layout)
    {
        body.Append(new Paragraph(
            new ParagraphProperties(
                new Shading { Val = ShadingPatternValues.Clear, Fill = layout.Background.Hex.TrimStart('#') },
                new SpacingBetweenLines { Before = "80", After = "80" }),
            new Run(
                new RunProperties(new Bold(), new FontSize { Val = "20" }),
                new Text($"Página {layout.PageNumber}") { Space = SpaceProcessingModeValues.Preserve })));
    }

    private static void AppendVisibleText(Body body, HeavyPageLayout layout)
    {
        foreach (var line in TextCleanupService.BuildOrderedLineBoxes(layout.Page, layout.Ocr)
                     .Where(line => line.Confidence >= 35 && !string.IsNullOrWhiteSpace(line.Text)))
        {
            var isHeading = TextCleanupService.IsLikelyHeading(line.Text);
            var runProperties = new RunProperties(new FontSize { Val = isHeading ? "26" : "21" });
            if (isHeading)
            {
                runProperties.Append(new Bold());
            }

            body.Append(new Paragraph(
                new ParagraphProperties(
                    new Justification { Val = isHeading ? JustificationValues.Center : JustificationValues.Both },
                    new SpacingBetweenLines { Before = isHeading ? "150" : "20", After = isHeading ? "110" : "70" }),
                new Run(
                    runProperties,
                    new Text(line.Text.Trim()) { Space = SpaceProcessingModeValues.Preserve })));
        }
    }

    private static void AppendRegionalImages(MainDocumentPart mainPart, Body body, HeavyPageLayout layout)
    {
        foreach (var image in layout.PageImages.OrderBy(image => image.Y).ThenBy(image => image.X))
        {
            AppendImagePath(mainPart, body, image.ImagePath, maxWidthInches: 6.8d, maxHeightInches: 5.8d);
        }
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
        AppendImagePath(mainPart, body, imagePath, maxWidthInches: 7.55d, maxHeightInches: 10.25d);
    }

    private static void AppendImagePath(MainDocumentPart mainPart, Body body, string imagePath, double maxWidthInches, double maxHeightInches)
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
            new ParagraphProperties(
                new Justification { Val = JustificationValues.Center },
                new SpacingBetweenLines { Before = "0", After = "0" }),
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
}
