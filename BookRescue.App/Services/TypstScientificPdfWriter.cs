using System.Diagnostics;
using System.Globalization;
using System.Text;
using BookRescue.App.Models;

namespace BookRescue.App.Services;

public sealed class TypstScientificPdfWriter
{
    public bool IsAvailable => TryFindTypst(out _);

    public bool TryWritePdf(
        string outputPdfPath,
        IReadOnlyList<BookPageInfo> pages,
        IReadOnlyList<OcrPageResult> ocrPages,
        IReadOnlyList<string> outputPageTexts,
        IReadOnlyList<RescuedImageInfo> rescuedImages)
    {
        if (!TryFindTypst(out var typstExe))
        {
            return false;
        }

        var outputDirectory = Path.GetDirectoryName(outputPdfPath);
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            return false;
        }

        Directory.CreateDirectory(outputDirectory);
        var typstPath = Path.Combine(outputDirectory, $"{Path.GetFileNameWithoutExtension(outputPdfPath)}.typ");

        try
        {
            File.WriteAllText(
                typstPath,
                BuildTypstDocument(pages, ocrPages, outputPageTexts, rescuedImages),
                Encoding.UTF8);

            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = typstExe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = outputDirectory
            };
            process.StartInfo.ArgumentList.Add("compile");
            process.StartInfo.ArgumentList.Add(typstPath);
            process.StartInfo.ArgumentList.Add(outputPdfPath);

            var output = new StringBuilder();
            var error = new StringBuilder();
            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data is not null)
                {
                    output.AppendLine(args.Data);
                }
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data is not null)
                {
                    error.AppendLine(args.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit((int)TimeSpan.FromMinutes(8).TotalMilliseconds))
            {
                TryKill(process);
                return false;
            }

            if (process.ExitCode != 0 || !File.Exists(outputPdfPath))
            {
                AppLogService.LogMessage(
                    $"Typst no pudo componer el PDF. Salida={output}; Error={error}",
                    "Typst");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            AppLogService.Log(ex, "Composición Typst");
            return false;
        }
    }

    private static string BuildTypstDocument(
        IReadOnlyList<BookPageInfo> pages,
        IReadOnlyList<OcrPageResult> ocrPages,
        IReadOnlyList<string> outputPageTexts,
        IReadOnlyList<RescuedImageInfo> rescuedImages)
    {
        var profile = DocumentCompositionProfile.Analyze(pages, ocrPages, outputPageTexts, rescuedImages);
        var builder = new StringBuilder();
        builder.AppendLine(
            "#set page(" +
            $"width: {FormatPoints(profile.PageWidthPoints)}, " +
            $"height: {FormatPoints(profile.PageHeightPoints)}, " +
            "margin: (" +
            $"left: {FormatPoints(profile.MarginLeftPoints)}, " +
            $"right: {FormatPoints(profile.MarginRightPoints)}, " +
            $"top: {FormatPoints(profile.MarginTopPoints)}, " +
            $"bottom: {FormatPoints(profile.MarginBottomPoints)}" +
            "))");
        builder.AppendLine($"#set text(font: \"Libertinus Serif\", size: {FormatPoints(profile.BodyFontSizePoints)}, lang: \"es\")");
        builder.AppendLine(
            "#set par(" +
            "justify: true, " +
            $"leading: {profile.LeadingEm.ToString("0.##", CultureInfo.InvariantCulture)}em, " +
            $"first-line-indent: {FormatPoints(profile.FirstLineIndentPoints)})");
        builder.AppendLine("#show heading: set block(above: 0.82em, below: 0.42em)");
        builder.AppendLine("#show heading: set text(weight: \"bold\")");
        builder.AppendLine("#let formula(body) = block(above: 0.6em, below: 0.7em, align(center, text(font: \"Cambria Math\", style: \"italic\", body)))");
        builder.AppendLine("#let booktable(cols, body) = table(columns: cols, inset: 4pt, stroke: 0.45pt + luma(190), body)");
        builder.AppendLine();

        for (var i = 0; i < pages.Count; i++)
        {
            if (i > 0)
            {
                builder.AppendLine("#pagebreak()");
                builder.AppendLine();
            }

            var page = pages[i];
            var ocrPage = i < ocrPages.Count ? ocrPages[i] : new OcrPageResult { FullText = string.Empty, Words = [], Lines = [] };
            var pageText = i < outputPageTexts.Count ? outputPageTexts[i] : string.Empty;
            var pageImages = rescuedImages
                .Where(image => image.PageNumber == i + 1)
                .OrderBy(image => image.Y)
                .ThenBy(image => image.X)
                .ToList();

            AppendPage(builder, profile, page, ocrPage, pageText, pageImages);
        }

        return builder.ToString();
    }

    private static void AppendPage(
        StringBuilder builder,
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
        blocks.AddRange(outputBlocks.Select(PageContentBlock.FromText));
        var hasStructuredTable = DocumentLayoutService.ContainsStructuredTable(paragraphs);
        blocks.AddRange(pageImages
            .Where(image => !hasStructuredTable || !image.Kind.Equals("table", StringComparison.OrdinalIgnoreCase))
            .Select(PageContentBlock.FromImage));

        if (blocks.Count == 0)
        {
            AppendSimpleParagraph(builder, "[Sin texto legible en esta página]");
            return;
        }

        var useColumns = profile.PageLikelyUsesTwoColumns(page, ocrPage) &&
            pageImages.Count == 0 &&
            blocks.Count(block => block.TextBlock is not null) >= 8;
        if (useColumns)
        {
            builder.AppendLine("#columns(2, gutter: 18pt)[");
        }

        float? previousBottom = null;
        foreach (var block in blocks.OrderBy(block => block.Y).ThenBy(block => block.X))
        {
            if (!useColumns)
            {
                AppendSourceSpacing(builder, profile, page, block, previousBottom);
            }

            if (block.TextBlock is not null)
            {
                AppendTextBlock(builder, profile, page, block.TextBlock);
            }

            if (block.Image is not null)
            {
                AppendImage(builder, profile, block.Image);
            }

            previousBottom = previousBottom.HasValue
                ? Math.Max(previousBottom.Value, block.Bottom)
                : block.Bottom;
        }

        if (useColumns)
        {
            builder.AppendLine("]");
            builder.AppendLine();
        }
    }

    private static void AppendTextBlock(
        StringBuilder builder,
        DocumentCompositionProfile profile,
        BookPageInfo page,
        PageTextBlock textBlock)
    {
        var text = textBlock.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (StructuredTextService.TryParseMarkdownTable(text, out var rows))
        {
            AppendTable(builder, rows);
            return;
        }

        if (StructuredTextService.TryReadMarkdownHeading(text, out var headingText, out var level))
        {
            AppendHeading(builder, headingText, level);
            return;
        }

        if (StructuredTextService.IsFormula(text))
        {
            builder.AppendLine($"#formula[{EscapeContent(StructuredTextService.StripFormulaMarkers(text))}]");
            builder.AppendLine();
            return;
        }

        if (StructuredTextService.TryReadBullet(text, out var bulletText))
        {
            AppendBullet(builder, profile, page, textBlock, bulletText);
            return;
        }

        if (StructuredTextService.TryReadNumberedItem(text, out var marker, out var itemText))
        {
            AppendNumberedItem(builder, profile, page, textBlock, marker, itemText);
            return;
        }

        if (StructuredTextService.IsCaption(text))
        {
            AppendCaption(builder, text);
            return;
        }

        if (StructuredTextService.ShouldRenderAsHeading(text, textBlock.IsHeading))
        {
            AppendHeading(builder, text, 3);
            return;
        }

        AppendParagraph(builder, profile, page, textBlock, text);
    }

    private static void AppendHeading(StringBuilder builder, string text, int level)
    {
        var safeLevel = Math.Clamp(level, 1, 4);
        builder.AppendLine($"#heading(level: {safeLevel})[{EscapeContent(text)}]");
        builder.AppendLine();
    }

    private static void AppendParagraph(
        StringBuilder builder,
        DocumentCompositionProfile profile,
        BookPageInfo page,
        PageTextBlock textBlock,
        string text)
    {
        var content = $"#par(justify: true)[{EscapeContent(text)}]";
        AppendIndented(builder, profile, page, textBlock, content);
        builder.AppendLine();
    }

    private static void AppendSimpleParagraph(StringBuilder builder, string text)
    {
        builder.AppendLine($"#par(justify: true)[{EscapeContent(text)}]");
        builder.AppendLine();
    }

    private static void AppendBullet(
        StringBuilder builder,
        DocumentCompositionProfile profile,
        BookPageInfo page,
        PageTextBlock textBlock,
        string text)
    {
        AppendIndented(builder, profile, page, textBlock, $"#par(hanging-indent: 1.1em)[• {EscapeContent(text)}]");
        builder.AppendLine();
    }

    private static void AppendNumberedItem(
        StringBuilder builder,
        DocumentCompositionProfile profile,
        BookPageInfo page,
        PageTextBlock textBlock,
        string marker,
        string text)
    {
        AppendIndented(builder, profile, page, textBlock, $"#par(hanging-indent: 1.4em)[#strong[{EscapeContent(marker)}] {EscapeContent(text)}]");
        builder.AppendLine();
    }

    private static void AppendCaption(StringBuilder builder, string text)
    {
        builder.AppendLine($"#align(center)[#text(size: 9pt, weight: \"bold\")[{EscapeContent(text)}]]");
        builder.AppendLine();
    }

    private static void AppendIndented(
        StringBuilder builder,
        DocumentCompositionProfile profile,
        BookPageInfo page,
        PageTextBlock textBlock,
        string typstContent)
    {
        var indent = CalculateSourceIndentPoints(profile, page, textBlock);
        if (indent < 6d)
        {
            builder.AppendLine(typstContent);
            return;
        }

        builder.AppendLine($"#pad(left: {FormatPoints(indent)})[{typstContent}]");
    }

    private static void AppendTable(StringBuilder builder, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var columnCount = rows.Max(row => row.Count);
        builder.AppendLine($"#booktable({columnCount},");
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
            {
                var value = columnIndex < rows[rowIndex].Count ? rows[rowIndex][columnIndex] : string.Empty;
                var cell = EscapeContent(value);
                builder.Append(rowIndex == 0
                    ? $"  table.cell(fill: luma(235), text(weight: \"bold\", [{cell}])),"
                    : $"  [{cell}],");
                builder.AppendLine();
            }
        }

        builder.AppendLine(")");
        builder.AppendLine();
    }

    private static void AppendImage(StringBuilder builder, DocumentCompositionProfile profile, RescuedImageInfo image)
    {
        if (!File.Exists(image.ImagePath))
        {
            return;
        }

        var percent = profile.GetImageWidthPercent(image);
        var path = image.ImagePath.Replace('\\', '/').Replace("\"", "\\\"");
        builder.AppendLine($"#align(center)[#image(\"{path}\", width: {percent.ToString("0.#", CultureInfo.InvariantCulture)}%)]");
        builder.AppendLine();
    }

    private static void AppendSourceSpacing(
        StringBuilder builder,
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

        if (spacingPoints < 4d)
        {
            return;
        }

        builder.AppendLine($"#v({FormatPoints(spacingPoints)})");
        builder.AppendLine();
    }

    private static string FormatPoints(double value)
    {
        return $"{Math.Clamp(value, 1d, 2000d).ToString("0.##", CultureInfo.InvariantCulture)}pt";
    }

    private static double CalculateSourceIndentPoints(
        DocumentCompositionProfile profile,
        BookPageInfo page,
        PageTextBlock textBlock)
    {
        var scaleX = profile.PageWidthPoints / Math.Max(1d, page.PixelWidth);
        var sourceLeft = textBlock.X * scaleX;
        var extra = sourceLeft - profile.MarginLeftPoints;
        return Math.Clamp(extra * 0.45d, 0d, 42d);
    }

    private static string EscapeContent(string value)
    {
        var text = value
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Replace('\n', ' ')
            .Trim();

        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch is '\\' or '#' or '$' or '[' or ']' or '*' or '_' or '~')
            {
                builder.Append('\\');
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    private static bool TryFindTypst(out string typstExe)
    {
        foreach (var root in GetRuntimeRoots())
        {
            var candidate = Path.Combine(root, "typst", "typst.exe");
            if (File.Exists(candidate))
            {
                typstExe = candidate;
                return true;
            }
        }

        typstExe = string.Empty;
        return false;
    }

    private static IEnumerable<string> GetRuntimeRoots()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; current is not null && depth < 8; depth++)
        {
            yield return Path.Combine(current.FullName, "runtime");
            current = current.Parent;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
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
