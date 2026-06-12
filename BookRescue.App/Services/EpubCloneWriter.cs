using System.IO.Compression;
using System.Globalization;
using System.Text;
using BookRescue.App.Models;

namespace BookRescue.App.Services;

public sealed class EpubCloneWriter
{
    public async Task WriteAsync(
        string outputPath,
        string title,
        IReadOnlyList<BookPageInfo> pages,
        IReadOnlyList<string> outputPageTexts,
        IReadOnlyList<RescuedImageInfo> rescuedImages,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        using var archive = ZipFile.Open(outputPath, ZipArchiveMode.Create);
        var profile = DocumentCompositionProfile.Analyze(pages, [], outputPageTexts, rescuedImages);
        var rescuedImagesByPage = Enumerable
            .Range(1, pages.Count)
            .Select(pageNumber =>
            {
                var pageIndex = pageNumber - 1;
                var text = pageIndex < outputPageTexts.Count ? outputPageTexts[pageIndex] : string.Empty;
                IEnumerable<RescuedImageInfo> pageImages = rescuedImages
                    .Where(image => image.PageNumber == pageNumber)
                    .OrderBy(image => image.Y)
                    .ThenBy(image => image.X);

                if (DocumentLayoutService.ContainsStructuredTable(text))
                {
                    pageImages = pageImages.Where(image => !image.Kind.Equals("table", StringComparison.OrdinalIgnoreCase));
                }

                return pageImages.ToList();
            })
            .ToList();

        WriteStoredTextEntry(archive, "mimetype", "application/epub+zip");
        WriteTextEntry(archive, "META-INF/container.xml", CreateContainerXml());
        WriteTextEntry(archive, "OEBPS/content.opf", CreatePackageXml(title, rescuedImagesByPage));
        WriteTextEntry(archive, "OEBPS/nav.xhtml", CreateNavigation(title, pages.Count));

        for (var index = 0; index < pages.Count; index++)
        {
            var pageNumber = index + 1;
            var text = index < outputPageTexts.Count ? outputPageTexts[index] : string.Empty;
            var pageImages = rescuedImagesByPage[index];

            for (var imageIndex = 0; imageIndex < pageImages.Count; imageIndex++)
            {
                var imageEntryName = $"OEBPS/images/page_{pageNumber:D4}_image_{imageIndex + 1:D2}.png";
                await WriteFileEntryAsync(archive, imageEntryName, pageImages[imageIndex].ImagePath, cancellationToken);
            }

            WriteTextEntry(archive, $"OEBPS/page_{pageNumber:D4}.xhtml", CreatePageXhtml(title, pageNumber, text, pageImages, profile));
        }
    }

    private static void WriteStoredTextEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }

    private static void WriteTextEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }

    private static async Task WriteFileEntryAsync(ZipArchive archive, string entryName, string filePath, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var input = File.OpenRead(filePath);
        await using var output = entry.Open();
        await input.CopyToAsync(output, cancellationToken);
    }

    private static string CreateContainerXml()
    {
        return """
               <?xml version="1.0" encoding="utf-8"?>
               <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                 <rootfiles>
                   <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml" />
                 </rootfiles>
               </container>
               """;
    }

    private static string CreatePackageXml(string title, IReadOnlyList<IReadOnlyList<RescuedImageInfo>> rescuedImagesByPage)
    {
        var manifest = new StringBuilder();
        var spine = new StringBuilder();

        manifest.AppendLine("""    <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav" />""");
        var pageCount = rescuedImagesByPage.Count;
        for (var index = 1; index <= pageCount; index++)
        {
            manifest.AppendLine($"""    <item id="page{index}" href="page_{index:D4}.xhtml" media-type="application/xhtml+xml" />""");
            for (var imageIndex = 1; imageIndex <= rescuedImagesByPage[index - 1].Count; imageIndex++)
            {
                manifest.AppendLine($"""    <item id="image{index}_{imageIndex}" href="images/page_{index:D4}_image_{imageIndex:D2}.png" media-type="image/png" />""");
            }

            spine.AppendLine($"""    <itemref idref="page{index}" />""");
        }

        return $$"""
                <?xml version="1.0" encoding="utf-8"?>
                <package version="3.0" unique-identifier="book-id" xmlns="http://www.idpf.org/2007/opf">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                    <dc:identifier id="book-id">urn:uuid:{{Guid.NewGuid()}}</dc:identifier>
                    <dc:title>{{EscapeXml(title)}}</dc:title>
                    <dc:language>es</dc:language>
                    <meta property="dcterms:modified">{{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}}</meta>
                  </metadata>
                  <manifest>
                {{manifest}}  </manifest>
                  <spine>
                {{spine}}  </spine>
                </package>
                """;
    }

    private static string CreateNavigation(string title, int pageCount)
    {
        var navItems = new StringBuilder();
        for (var index = 1; index <= pageCount; index++)
        {
            navItems.AppendLine($"""      <li><a href="page_{index:D4}.xhtml">Página {index}</a></li>""");
        }

        return $$"""
                <?xml version="1.0" encoding="utf-8"?>
                <!DOCTYPE html>
                <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops" lang="es">
                <head><title>{{EscapeXml(title)}}</title></head>
                <body>
                  <nav epub:type="toc" id="toc">
                    <h1>{{EscapeXml(title)}}</h1>
                    <ol>
                {{navItems}}    </ol>
                  </nav>
                </body>
                </html>
                """;
    }

    private static string CreatePageXhtml(
        string title,
        int pageNumber,
        string text,
        IReadOnlyList<RescuedImageInfo> rescuedImages,
        DocumentCompositionProfile profile)
    {
        var images = new StringBuilder();
        for (var imageIndex = 1; imageIndex <= rescuedImages.Count; imageIndex++)
        {
            var image = rescuedImages[imageIndex - 1];
            var widthPercent = profile.GetImageWidthPercent(image);
            images.AppendLine($"""  <img src="images/page_{pageNumber:D4}_image_{imageIndex:D2}.png" alt="Imagen reconstruida {imageIndex}" style="width:{widthPercent.ToString("0.#", CultureInfo.InvariantCulture)}%;" />""");
        }

        var textHtml = CreateTextHtml(text);

        return $$"""
                <?xml version="1.0" encoding="utf-8"?>
                <!DOCTYPE html>
                <html xmlns="http://www.w3.org/1999/xhtml" lang="es">
                <head>
                  <title>{{EscapeXml(title)}} - Página {{pageNumber}}</title>
                  <style>
                    body { margin: 0; padding: 1rem; font-family: Georgia, "Times New Roman", serif; color: #202124; line-height: 1.45; }
                    h2 { font-size: 1.05rem; margin: .9rem 0 .35rem 0; font-weight: 700; }
                    .caption { text-align: center; font-size: .9rem; font-weight: 700; margin: .2rem 0 .75rem 0; }
                    p { margin: 0 0 .65rem 0; text-align: justify; }
                    .formula { text-align: center; font-family: "Cambria Math", Georgia, serif; font-style: italic; margin: .75rem 0; }
                    ul { margin: 0 0 .8rem 1.35rem; padding: 0; }
                    ol { margin: 0 0 .8rem 1.55rem; padding: 0; }
                    li { margin: 0 0 .35rem 0; }
                    .numbered { margin-left: 1.2rem; text-indent: -1.2rem; }
                    .numbered strong { font-weight: 700; }
                    table { border-collapse: collapse; width: 100%; margin: .75rem 0 1rem 0; font-size: .92rem; }
                    th, td { border: 1px solid #dadce0; padding: .35rem .45rem; vertical-align: top; }
                    th { background: #e8eaed; font-weight: 700; }
                    img { height: auto; display: block; margin: 0 auto 1rem auto; }
                  </style>
                </head>
                <body>
                {{textHtml}}
                {{images}}
                </body>
                </html>
                """;
    }

    private static string CreateTextHtml(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "  <p>[Sin texto legible en esta página]</p>";
        }

        var html = new StringBuilder();
        var blocks = text
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var block in blocks)
        {
            var normalized = NormalizeInlineText(block);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            if (StructuredTextService.TryParseMarkdownTable(block, out var tableRows))
            {
                html.AppendLine(CreateTableHtml(tableRows));
                continue;
            }

            if (StructuredTextService.TryReadMarkdownHeading(normalized, out var headingText, out var headingLevel))
            {
                var tag = headingLevel <= 2 ? "h2" : "h3";
                html.AppendLine($"""  <{tag}>{EscapeXml(headingText)}</{tag}>""");
                continue;
            }

            if (StructuredTextService.IsFormula(normalized))
            {
                html.AppendLine($"""  <div class="formula">{EscapeXml(StructuredTextService.StripFormulaMarkers(normalized))}</div>""");
                continue;
            }

            if (StructuredTextService.IsCaption(normalized))
            {
                html.AppendLine($"""  <div class="caption">{EscapeXml(normalized)}</div>""");
                continue;
            }

            var lines = block
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length > 0 && lines.All(line => StructuredTextService.TryReadBullet(line, out _)))
            {
                html.AppendLine("  <ul>");
                foreach (var line in lines)
                {
                    StructuredTextService.TryReadBullet(line, out var bulletText);
                    html.AppendLine($"""    <li>{EscapeXml(bulletText)}</li>""");
                }

                html.AppendLine("  </ul>");
                continue;
            }

            if (lines.Length > 0 && lines.All(line => StructuredTextService.TryReadNumberedItem(line, out _, out _)))
            {
                html.AppendLine("  <ol>");
                foreach (var line in lines)
                {
                    StructuredTextService.TryReadNumberedItem(line, out _, out var itemText);
                    html.AppendLine($"""    <li>{EscapeXml(itemText)}</li>""");
                }

                html.AppendLine("  </ol>");
                continue;
            }

            if (StructuredTextService.TryReadNumberedItem(normalized, out var marker, out var numberedText))
            {
                html.AppendLine($"""  <p class="numbered"><strong>{EscapeXml(marker)}</strong> {EscapeXml(numberedText)}</p>""");
                continue;
            }

            if (StructuredTextService.ShouldRenderAsHeading(normalized, sourceMarkedHeading: false))
            {
                html.AppendLine($"""  <h2>{EscapeXml(normalized)}</h2>""");
                continue;
            }

            html.AppendLine($"""  <p>{EscapeXml(normalized)}</p>""");
        }

        return html.Length == 0 ? "  <p>[Sin texto legible en esta página]</p>" : html.ToString();
    }

    private static string CreateTableHtml(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var html = new StringBuilder();
        html.AppendLine("  <table>");
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var tag = rowIndex == 0 ? "th" : "td";
            html.AppendLine("    <tr>");
            foreach (var cell in rows[rowIndex])
            {
                html.AppendLine($"""      <{tag}>{EscapeXml(cell)}</{tag}>""");
            }

            html.AppendLine("    </tr>");
        }

        html.AppendLine("  </table>");
        return html.ToString();
    }

    private static string NormalizeInlineText(string text)
    {
        return string.Join(' ', text
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string EscapeXml(string value)
    {
        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }
}
