using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BookRescue.App.Models;
using OpenCvSharp;

namespace BookRescue.App.Services;

public sealed class DocumentAiStructureService
{
    private readonly Lazy<DocumentAiRuntime?> runtime = new(FindRuntime);

    public bool IsAvailable => runtime.Value is not null;

    public async Task<DocumentAiAnalysisResult?> AnalyzeAsync(
        string inputPath,
        string runFolder,
        IReadOnlyList<BookPageInfo> pages,
        string rescuedImagesFolder,
        CancellationToken cancellationToken)
    {
        var pageCount = pages.Count;
        var detectedRuntime = runtime.Value;
        if (!File.Exists(inputPath) || detectedRuntime is null)
        {
            return null;
        }

        var outputFolder = Path.Combine(runFolder, "estructura_inteligente");
        Directory.CreateDirectory(outputFolder);

        try
        {
            await RunDoclingAsync(
                detectedRuntime.DoclingExe,
                detectedRuntime.ModelsFolder,
                inputPath,
                outputFolder,
                pageCount,
                cancellationToken);

            var markdownPath = Directory.GetFiles(outputFolder, "*.md", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault() ?? string.Empty;
            var jsonPath = Directory.GetFiles(outputFolder, "*.json", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(jsonPath) || !File.Exists(jsonPath))
            {
                return null;
            }

            var pageTexts = await ParsePageTextsAsync(jsonPath, pageCount, cancellationToken);
            var rescuedImages = await ParseVisualImagesAsync(jsonPath, pages, rescuedImagesFolder, cancellationToken);
            return new DocumentAiAnalysisResult
            {
                OutputFolder = outputFolder,
                MarkdownPath = markdownPath,
                JsonPath = jsonPath,
                PageTexts = pageTexts,
                RescuedImages = rescuedImages
            };
        }
        catch (Exception ex)
        {
            AppLogService.Log(ex, "Analisis inteligente Docling");
            return null;
        }
    }

    private static async Task RunDoclingAsync(
        string doclingExe,
        string modelsFolder,
        string inputPath,
        string outputFolder,
        int pageCount,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = doclingExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(doclingExe)!
        };

        var device = SelectDocumentAiDevice(doclingExe);
        var threads = SelectThreadCount(device);
        AddArguments(process.StartInfo, inputPath, modelsFolder, outputFolder, device, threads);
        process.StartInfo.Environment["HF_HUB_OFFLINE"] = "1";
        process.StartInfo.Environment["TRANSFORMERS_OFFLINE"] = "1";
        process.StartInfo.Environment["OMP_NUM_THREADS"] = threads.ToString();
        process.StartInfo.Environment["OMP_THREAD_LIMIT"] = threads.ToString();
        if (device.Equals("cuda", StringComparison.OrdinalIgnoreCase))
        {
            process.StartInfo.Environment["CUDA_VISIBLE_DEVICES"] = "0";
            process.StartInfo.Environment["CUDA_MODULE_LOADING"] = "LAZY";
        }

        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                outputBuilder.AppendLine(args.Data);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                errorBuilder.AppendLine(args.Data);
            }
        };

        process.Start();
        TryBoostPriority(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var timeout = TimeSpan.FromMinutes(Math.Clamp(pageCount * 0.6d, 20d, 360d));
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Docling termino con codigo {process.ExitCode}: {errorBuilder}");
        }

        AppLogService.LogMessage(
            $"Páginas={pageCount}; Device={device}; Salida={outputFolder}; Log={outputBuilder.ToString()[..Math.Min(300, outputBuilder.Length)]}",
            "Docling");
    }

    private static void AddArguments(
        ProcessStartInfo startInfo,
        string inputPath,
        string modelsFolder,
        string outputFolder,
        string device,
        int threads)
    {
        startInfo.ArgumentList.Add(inputPath);
        startInfo.ArgumentList.Add("--to");
        startInfo.ArgumentList.Add("md");
        startInfo.ArgumentList.Add("--to");
        startInfo.ArgumentList.Add("json");
        startInfo.ArgumentList.Add("--image-export-mode");
        startInfo.ArgumentList.Add("referenced");
        startInfo.ArgumentList.Add("--force-ocr");
        startInfo.ArgumentList.Add("--ocr-engine");
        startInfo.ArgumentList.Add("rapidocr");
        startInfo.ArgumentList.Add("--tables");
        startInfo.ArgumentList.Add("--table-mode");
        startInfo.ArgumentList.Add("accurate");
        startInfo.ArgumentList.Add("--artifacts-path");
        startInfo.ArgumentList.Add(modelsFolder);
        startInfo.ArgumentList.Add("--device");
        startInfo.ArgumentList.Add(device);
        startInfo.ArgumentList.Add("--num-threads");
        startInfo.ArgumentList.Add(threads.ToString());
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add(outputFolder);
    }

    private static string SelectDocumentAiDevice(string doclingExe)
    {
        if (!HardwareCapabilityService.Current.ShouldUseGpu)
        {
            return "cpu";
        }

        try
        {
            var scriptsFolder = Path.GetDirectoryName(doclingExe);
            var pythonExe = scriptsFolder is null ? string.Empty : Path.Combine(scriptsFolder, "python.exe");
            if (!File.Exists(pythonExe))
            {
                return "cpu";
            }

            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = pythonExe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            process.StartInfo.ArgumentList.Add("-c");
            process.StartInfo.ArgumentList.Add("import torch; print('cuda' if torch.cuda.is_available() else 'cpu')");
            process.Start();
            if (!process.WaitForExit(8000))
            {
                TryKill(process);
                return "cpu";
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            return process.ExitCode == 0 && output.Contains("cuda", StringComparison.OrdinalIgnoreCase)
                ? "cuda"
                : "cpu";
        }
        catch
        {
            return "cpu";
        }
    }

    private static int SelectThreadCount(string device)
    {
        var hardware = HardwareCapabilityService.Current;
        var usableThreads = Math.Max(2, hardware.LogicalProcessors - 1);
        if (device.Equals("cuda", StringComparison.OrdinalIgnoreCase))
        {
            return Math.Clamp(usableThreads / 2, 2, 6);
        }

        return Math.Clamp(usableThreads, 2, 12);
    }

    private static async Task<IReadOnlyList<string>> ParsePageTextsAsync(string jsonPath, int pageCount, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(jsonPath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var pages = Enumerable.Range(0, Math.Max(1, pageCount)).Select(_ => new List<string>()).ToList();
        var blocks = ReadBodyTextBlocks(document.RootElement, pageCount);

        if (blocks.Count == 0)
        {
            blocks = ReadTextArrayBlocks(document.RootElement);
        }

        foreach (var block in blocks.OrderBy(block => block.Sequence))
        {
            var index = Math.Clamp(block.PageNumber - 1, 0, pages.Count - 1);
            pages[index].Add(block.Text);
        }

        return pages
            .Select(blocks => string.Join(Environment.NewLine + Environment.NewLine, blocks).Trim())
            .ToList();
    }

    private static List<DoclingTextBlock> ReadBodyTextBlocks(JsonElement root, int pageCount)
    {
        var blocks = new List<DoclingTextBlock>();
        if (!root.TryGetProperty("body", out var body) ||
            !body.TryGetProperty("children", out var children) ||
            children.ValueKind != JsonValueKind.Array)
        {
            return blocks;
        }

        var sequence = 0;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        AppendReferencedChildren(root, children, blocks, visited, ref sequence);
        return blocks
            .Where(block => block.PageNumber > 0 && block.PageNumber <= pageCount)
            .ToList();
    }

    private static List<DoclingTextBlock> ReadTextArrayBlocks(JsonElement root)
    {
        var blocks = new List<DoclingTextBlock>();
        if (!root.TryGetProperty("texts", out var texts) || texts.ValueKind != JsonValueKind.Array)
        {
            return blocks;
        }

        var sequence = 0;
        foreach (var item in texts.EnumerateArray())
        {
            AddTextItem(item, blocks, ref sequence);
        }

        return blocks;
    }

    private static void AppendReferencedChildren(
        JsonElement root,
        JsonElement children,
        List<DoclingTextBlock> blocks,
        HashSet<string> visited,
        ref int sequence)
    {
        foreach (var child in children.EnumerateArray())
        {
            var reference = ReadReference(child);
            if (string.IsNullOrWhiteSpace(reference) || !visited.Add(reference))
            {
                continue;
            }

            AppendReferencedItem(root, reference, blocks, visited, ref sequence);
        }
    }

    private static void AppendReferencedItem(
        JsonElement root,
        string reference,
        List<DoclingTextBlock> blocks,
        HashSet<string> visited,
        ref int sequence)
    {
        if (!TryReadReference(reference, out var section, out var index) ||
            !TryGetArrayItem(root, section, index, out var item))
        {
            return;
        }

        if (section.Equals("texts", StringComparison.OrdinalIgnoreCase))
        {
            AddTextItem(item, blocks, ref sequence);
            return;
        }

        if (section.Equals("tables", StringComparison.OrdinalIgnoreCase))
        {
            AddTableItem(item, blocks, ref sequence);
            return;
        }

        if (section.Equals("groups", StringComparison.OrdinalIgnoreCase) &&
            item.TryGetProperty("children", out var groupChildren) &&
            groupChildren.ValueKind == JsonValueKind.Array)
        {
            AppendReferencedChildren(root, groupChildren, blocks, visited, ref sequence);
        }
    }

    private static void AddTextItem(JsonElement item, List<DoclingTextBlock> blocks, ref int sequence)
    {
        var text = ReadBestText(item);
        if (string.IsNullOrWhiteSpace(text) || !TryReadPageNumber(item, out var pageNumber))
        {
            return;
        }

        var label = ReadString(item, "label");
        var formatted = FormatBlock(label, text);
        if (string.IsNullOrWhiteSpace(formatted))
        {
            return;
        }

        blocks.Add(new DoclingTextBlock(pageNumber, sequence++, formatted));
    }

    private static void AddTableItem(JsonElement item, List<DoclingTextBlock> blocks, ref int sequence)
    {
        if (!TryReadPageNumber(item, out var pageNumber))
        {
            return;
        }

        var tableText = ReadTableText(item);
        if (string.IsNullOrWhiteSpace(tableText))
        {
            return;
        }

        blocks.Add(new DoclingTextBlock(pageNumber, sequence++, tableText));
    }

    private static string ReadBestText(JsonElement item)
    {
        var text = ReadString(item, "text");
        if (!string.IsNullOrWhiteSpace(text))
        {
            return NormalizeText(text);
        }

        return NormalizeText(ReadString(item, "orig"));
    }

    private static async Task<IReadOnlyList<RescuedImageInfo>> ParseVisualImagesAsync(
        string jsonPath,
        IReadOnlyList<BookPageInfo> pages,
        string outputFolder,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputFolder);
        await using var stream = File.OpenRead(jsonPath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var rescued = new List<RescuedImageInfo>();
        await AddVisualImagesFromSectionAsync(document.RootElement, "pictures", pages, outputFolder, rescued, cancellationToken);
        await AddVisualImagesFromSectionAsync(document.RootElement, "tables", pages, outputFolder, rescued, cancellationToken);
        return rescued;
    }

    private static async Task AddVisualImagesFromSectionAsync(
        JsonElement root,
        string section,
        IReadOnlyList<BookPageInfo> pages,
        string outputFolder,
        List<RescuedImageInfo> rescued,
        CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty(section, out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var visualIndex = 0;
        foreach (var item in items.EnumerateArray())
        {
            if (!TryReadPageNumber(item, out var pageNumber) ||
                pageNumber < 1 ||
                pageNumber > pages.Count ||
                !TryReadBoundingBox(item, out var box) ||
                !TryReadImageUri(item, out var uri))
            {
                continue;
            }

            var page = pages[pageNumber - 1];
            if (!LooksLikeUsefulVisual(box, page))
            {
                continue;
            }

            var kind = section.Equals("tables", StringComparison.OrdinalIgnoreCase) ? "table" : "figure";
            var copiedImage = await CopyVisualImageAsync(uri, outputFolder, pageNumber, kind, ++visualIndex, cancellationToken);
            if (string.IsNullOrWhiteSpace(copiedImage.ImagePath))
            {
                continue;
            }

            rescued.Add(CreateImageInfo(copiedImage, page, pageNumber, box, kind));
        }
    }

    private static async Task<CopiedVisualImage> CopyVisualImageAsync(
        string uri,
        string outputFolder,
        int pageNumber,
        string kind,
        int visualIndex,
        CancellationToken cancellationToken)
    {
        var outputPath = Path.Combine(outputFolder, $"pagina_{pageNumber:D4}_ia_{kind}_{visualIndex:D2}.png");
        try
        {
            if (uri.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
            {
                var commaIndex = uri.IndexOf(',', StringComparison.Ordinal);
                if (commaIndex < 0)
                {
                    return CopiedVisualImage.Empty;
                }

                var bytes = Convert.FromBase64String(uri[(commaIndex + 1)..]);
                await File.WriteAllBytesAsync(outputPath, bytes, cancellationToken);
                return TightenVisualImage(outputPath);
            }

            var sourcePath = uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                ? new Uri(uri).LocalPath
                : uri;

            if (!File.Exists(sourcePath))
            {
                return CopiedVisualImage.Empty;
            }

            File.Copy(sourcePath, outputPath, overwrite: true);
            return TightenVisualImage(outputPath);
        }
        catch (Exception ex)
        {
            AppLogService.Log(ex, "Imagen inteligente Docling");
            return CopiedVisualImage.Empty;
        }
    }

    private static CopiedVisualImage TightenVisualImage(string imagePath)
    {
        try
        {
            using var source = Cv2.ImRead(imagePath, ImreadModes.Color);
            if (source.Empty())
            {
                return new CopiedVisualImage(imagePath, 0d, 0d, 1d, 1d);
            }

            using var gray = new Mat();
            Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);

            using var ink = new Mat();
            Cv2.Threshold(gray, ink, 248, 255, ThresholdTypes.BinaryInv);

            using var points = new Mat();
            Cv2.FindNonZero(ink, points);
            if (points.Empty())
            {
                return new CopiedVisualImage(imagePath, 0d, 0d, 1d, 1d);
            }

            var inkRect = Cv2.BoundingRect(points);
            var rect = ExpandAndClamp(inkRect, source.Width, source.Height, 12);
            if (rect.Width >= source.Width * 0.96d && rect.Height >= source.Height * 0.96d)
            {
                return new CopiedVisualImage(imagePath, 0d, 0d, 1d, 1d);
            }

            using var crop = new Mat(source, rect);
            Cv2.ImWrite(imagePath, crop);
            return new CopiedVisualImage(
                imagePath,
                rect.X / (double)Math.Max(1, source.Width),
                rect.Y / (double)Math.Max(1, source.Height),
                rect.Width / (double)Math.Max(1, source.Width),
                rect.Height / (double)Math.Max(1, source.Height));
        }
        catch (Exception ex)
        {
            AppLogService.Log(ex, "Recorte de imagen inteligente");
            return new CopiedVisualImage(imagePath, 0d, 0d, 1d, 1d);
        }
    }

    private static RescuedImageInfo CreateImageInfo(CopiedVisualImage image, BookPageInfo page, int pageNumber, DoclingBox box, string kind)
    {
        var x = (float)((box.Left + (box.Width * image.OffsetXRatio)) / Math.Max(1d, page.WidthPoints) * page.PixelWidth);
        var width = (float)(box.Width * image.WidthRatio / Math.Max(1d, page.WidthPoints) * page.PixelWidth);
        float y;

        if (box.CoordOrigin.Equals("TOPLEFT", StringComparison.OrdinalIgnoreCase))
        {
            y = (float)((box.Top + (box.Height * image.OffsetYRatio)) / Math.Max(1d, page.HeightPoints) * page.PixelHeight);
        }
        else
        {
            y = (float)((page.HeightPoints - box.Top + (box.Height * image.OffsetYRatio)) / Math.Max(1d, page.HeightPoints) * page.PixelHeight);
        }

        var height = (float)(box.Height * image.HeightRatio / Math.Max(1d, page.HeightPoints) * page.PixelHeight);
        return new RescuedImageInfo
        {
            ImagePath = image.ImagePath,
            PageNumber = pageNumber,
            X = Math.Max(0, x),
            Y = Math.Max(0, y),
            Width = Math.Max(1, width),
            Height = Math.Max(1, height),
            PagePixelWidth = page.PixelWidth,
            PagePixelHeight = page.PixelHeight,
            Kind = kind
        };
    }

    private static Rect ExpandAndClamp(Rect rect, int maxWidth, int maxHeight, int padding)
    {
        var x = Math.Max(0, rect.X - padding);
        var y = Math.Max(0, rect.Y - padding);
        var right = Math.Min(maxWidth, rect.Right + padding);
        var bottom = Math.Min(maxHeight, rect.Bottom + padding);
        return new Rect(x, y, Math.Max(1, right - x), Math.Max(1, bottom - y));
    }

    private static bool LooksLikeUsefulVisual(DoclingBox box, BookPageInfo page)
    {
        var pageArea = Math.Max(1d, page.WidthPoints * page.HeightPoints);
        var areaRatio = (box.Width * box.Height) / pageArea;
        var widthRatio = box.Width / Math.Max(1d, page.WidthPoints);
        var heightRatio = box.Height / Math.Max(1d, page.HeightPoints);

        if (areaRatio < 0.003 || box.Width < 20 || box.Height < 16)
        {
            return false;
        }

        if (areaRatio > 0.62 && widthRatio > 0.82 && heightRatio > 0.65)
        {
            return false;
        }

        return widthRatio <= 0.96 || heightRatio <= 0.88;
    }

    private static string ReadTableText(JsonElement item)
    {
        if (item.TryGetProperty("data", out var data))
        {
            var rows = ReadTableRows(data);
            if (rows.Count > 0)
            {
                return StructuredTextService.CreateMarkdownTable(rows);
            }
        }

        return ReadBestText(item);
    }

    private static IReadOnlyList<IReadOnlyList<string>> ReadTableRows(JsonElement data)
    {
        if (data.TryGetProperty("grid", out var grid) && grid.ValueKind == JsonValueKind.Array)
        {
            var gridRows = new List<IReadOnlyList<string>>();
            foreach (var row in grid.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var cells = row.EnumerateArray()
                    .Select(cell => cell.ValueKind == JsonValueKind.String ? cell.GetString() ?? string.Empty : ReadBestText(cell))
                    .ToList();
                if (cells.Count > 0)
                {
                    gridRows.Add(cells);
                }
            }

            return gridRows;
        }

        if (!data.TryGetProperty("table_cells", out var tableCells) || tableCells.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new Dictionary<(int Row, int Column), string>();
        var maxRow = 0;
        var maxColumn = 0;

        foreach (var cell in tableCells.EnumerateArray())
        {
            var row = ReadFirstInt(cell, "start_row_offset_idx", "row_offset_idx", "row", "row_idx");
            var column = ReadFirstInt(cell, "start_col_offset_idx", "col_offset_idx", "column", "col_idx");
            if (row < 0 || column < 0)
            {
                continue;
            }

            var text = ReadBestText(cell);
            values[(row, column)] = text;
            maxRow = Math.Max(maxRow, row);
            maxColumn = Math.Max(maxColumn, column);
        }

        if (values.Count == 0)
        {
            return [];
        }

        var rows = new List<IReadOnlyList<string>>();
        for (var row = 0; row <= maxRow; row++)
        {
            var cells = new List<string>();
            for (var column = 0; column <= maxColumn; column++)
            {
                values.TryGetValue((row, column), out var value);
                cells.Add(value ?? string.Empty);
            }

            rows.Add(cells);
        }

        return rows;
    }

    private static int ReadFirstInt(JsonElement item, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (item.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var result))
            {
                return result;
            }
        }

        return -1;
    }

    private static bool TryReadPageNumber(JsonElement item, out int pageNumber)
    {
        pageNumber = 0;
        if (!item.TryGetProperty("prov", out var prov) || prov.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var entry in prov.EnumerateArray())
        {
            if (entry.TryGetProperty("page_no", out var pageElement) && pageElement.TryGetInt32(out pageNumber))
            {
                return pageNumber > 0;
            }
        }

        return false;
    }

    private static string FormatBlock(string label, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        if (label.Contains("formula", StringComparison.OrdinalIgnoreCase))
        {
            return text.StartsWith('$') ? text : $"$ {text} $";
        }

        if (label.Contains("title", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("section", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("heading", StringComparison.OrdinalIgnoreCase))
        {
            return text.ToUpperInvariant();
        }

        return text;
    }

    private static string ReadString(JsonElement item, string propertyName)
    {
        return item.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static string ReadReference(JsonElement item)
    {
        return item.TryGetProperty("$ref", out var reference) && reference.ValueKind == JsonValueKind.String
            ? reference.GetString() ?? string.Empty
            : string.Empty;
    }

    private static bool TryReadReference(string reference, out string section, out int index)
    {
        section = string.Empty;
        index = -1;
        var parts = reference.Trim().Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3 || parts[0] != "#" || !int.TryParse(parts[2], out index))
        {
            return false;
        }

        section = parts[1];
        return !string.IsNullOrWhiteSpace(section) && index >= 0;
    }

    private static bool TryGetArrayItem(JsonElement root, string section, int index, out JsonElement item)
    {
        item = default;
        if (!root.TryGetProperty(section, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var currentIndex = 0;
        foreach (var candidate in array.EnumerateArray())
        {
            if (currentIndex == index)
            {
                item = candidate;
                return true;
            }

            currentIndex++;
        }

        return false;
    }

    private static bool TryReadImageUri(JsonElement item, out string uri)
    {
        uri = string.Empty;
        if (!item.TryGetProperty("image", out var image) || image.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        uri = ReadString(image, "uri");
        return !string.IsNullOrWhiteSpace(uri);
    }

    private static bool TryReadBoundingBox(JsonElement item, out DoclingBox box)
    {
        box = default;
        if (!item.TryGetProperty("prov", out var prov) || prov.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var entry in prov.EnumerateArray())
        {
            if (!entry.TryGetProperty("bbox", out var bbox) || bbox.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var left = ReadDouble(bbox, "l");
            var top = ReadDouble(bbox, "t");
            var right = ReadDouble(bbox, "r");
            var bottom = ReadDouble(bbox, "b");
            if (right <= left || Math.Abs(top - bottom) < 1)
            {
                continue;
            }

            box = new DoclingBox(
                left,
                Math.Max(top, bottom),
                right,
                Math.Min(top, bottom),
                ReadString(bbox, "coord_origin"));
            return true;
        }

        return false;
    }

    private static double ReadDouble(JsonElement item, string propertyName)
    {
        return item.TryGetProperty(propertyName, out var value) && value.TryGetDouble(out var result)
            ? result
            : 0d;
    }

    private static string NormalizeText(string value)
    {
        return value
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Trim();
    }

    private static DocumentAiRuntime? FindRuntime()
    {
        foreach (var root in GetRuntimeRoots())
        {
            var venvFolder = Path.Combine(root, "document-ai-venv");
            var pythonFolder = Path.Combine(root, "python312");
            var candidateExe = Path.Combine(venvFolder, "Scripts", "docling.exe");
            var candidateModels = Path.Combine(root, "document-ai-models");
            if (File.Exists(candidateExe) && Directory.Exists(candidateModels))
            {
                EnsurePortablePythonConfig(venvFolder, pythonFolder);
                return new DocumentAiRuntime(candidateExe, candidateModels);
            }
        }

        return null;
    }

    private static void EnsurePortablePythonConfig(string venvFolder, string pythonFolder)
    {
        var configPath = Path.Combine(venvFolder, "pyvenv.cfg");
        var pythonExe = Path.Combine(pythonFolder, "python.exe");
        if (!File.Exists(configPath) || !File.Exists(pythonExe))
        {
            return;
        }

        try
        {
            var desired = string.Join(Environment.NewLine,
                $"home = {pythonFolder}",
                "include-system-site-packages = false",
                "version = 3.12.10",
                $"executable = {pythonExe}",
                $"command = {pythonExe} -m venv {venvFolder}",
                string.Empty);

            var current = File.ReadAllText(configPath, Encoding.UTF8);
            if (!current.Equals(desired, StringComparison.Ordinal))
            {
                File.WriteAllText(configPath, desired, Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            AppLogService.Log(ex, "Configuracion portable de Python");
        }
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

    private static void TryBoostPriority(Process process)
    {
        try
        {
            process.PriorityClass = ProcessPriorityClass.AboveNormal;
        }
        catch
        {
        }
    }

    private sealed record DoclingTextBlock(int PageNumber, int Sequence, string Text);

    private sealed record DocumentAiRuntime(string DoclingExe, string ModelsFolder);

    private sealed record CopiedVisualImage(
        string ImagePath,
        double OffsetXRatio,
        double OffsetYRatio,
        double WidthRatio,
        double HeightRatio)
    {
        public static CopiedVisualImage Empty { get; } = new(string.Empty, 0d, 0d, 1d, 1d);
    }

    private readonly record struct DoclingBox(double Left, double Top, double Right, double Bottom, string CoordOrigin)
    {
        public double Width => Math.Max(1d, Right - Left);

        public double Height => Math.Max(1d, Top - Bottom);
    }
}
