using BookRescue.App.Models;
using Tesseract;

namespace BookRescue.App.Services;

public sealed class OcrExtractionService : IDisposable
{
    private readonly TessdataBootstrapper _tessdataBootstrapper;
    private readonly ThreadLocal<Dictionary<string, TesseractEngine>> _enginesByThread = new(
        () => new Dictionary<string, TesseractEngine>(StringComparer.OrdinalIgnoreCase),
        trackAllValues: true);

    public OcrExtractionService(TessdataBootstrapper tessdataBootstrapper)
    {
        _tessdataBootstrapper = tessdataBootstrapper;
    }

    public OcrPageResult Extract(string imagePath, string languageExpression)
    {
        var engine = GetEngine(languageExpression);

        using var pix = Pix.LoadFromFile(imagePath);
        using var page = engine.Process(pix, PageSegMode.Auto);
        var fullText = page.GetText()?.Trim() ?? string.Empty;

        var lines = ExtractLines(page);
        var words = new List<OcrWordBox>();
        using var iterator = page.GetIterator();
        iterator.Begin();

        do
        {
            if (!iterator.TryGetBoundingBox(PageIteratorLevel.Word, out var bounds))
            {
                continue;
            }

            var wordText = iterator.GetText(PageIteratorLevel.Word);
            if (string.IsNullOrWhiteSpace(wordText))
            {
                continue;
            }

            words.Add(new OcrWordBox
            {
                Text = wordText.Trim(),
                X = bounds.X1,
                Y = bounds.Y1,
                Width = bounds.Width,
                Height = bounds.Height,
                Confidence = iterator.GetConfidence(PageIteratorLevel.Word)
            });
        }
        while (iterator.Next(PageIteratorLevel.Word));

        return new OcrPageResult
        {
            FullText = fullText,
            Words = words,
            Lines = lines
        };
    }

    public OcrSingleLineResult ExtractSingleLine(string imagePath, string languageExpression)
    {
        var engine = GetEngine(languageExpression);

        using var pix = Pix.LoadFromFile(imagePath);
        using var page = engine.Process(pix, PageSegMode.SingleLine);
        var text = page.GetText()?.Trim() ?? string.Empty;
        var confidence = page.GetMeanConfidence();
        if (confidence <= 1.0f)
        {
            confidence *= 100f;
        }

        return new OcrSingleLineResult(text, Math.Clamp(confidence, 0f, 100f));
    }

    private static IReadOnlyList<OcrLineBox> ExtractLines(Page page)
    {
        var lines = new List<OcrLineBox>();
        using var iterator = page.GetIterator();
        iterator.Begin();

        do
        {
            if (!iterator.TryGetBoundingBox(PageIteratorLevel.TextLine, out var bounds))
            {
                continue;
            }

            var text = iterator.GetText(PageIteratorLevel.TextLine);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            lines.Add(new OcrLineBox
            {
                Text = text.Trim(),
                X = bounds.X1,
                Y = bounds.Y1,
                Width = bounds.Width,
                Height = bounds.Height,
                Confidence = iterator.GetConfidence(PageIteratorLevel.TextLine)
            });
        }
        while (iterator.Next(PageIteratorLevel.TextLine));

        return lines;
    }

    private TesseractEngine GetEngine(string languageExpression)
    {
        var engines = _enginesByThread.Value ?? throw new ObjectDisposedException(nameof(OcrExtractionService));
        if (engines.TryGetValue(languageExpression, out var engine))
        {
            return engine;
        }

        engine = new TesseractEngine(_tessdataBootstrapper.TessdataDirectory, languageExpression, EngineMode.LstmOnly)
        {
            DefaultPageSegMode = PageSegMode.Auto
        };
        engines[languageExpression] = engine;
        return engine;
    }

    public void Dispose()
    {
        foreach (var engines in _enginesByThread.Values)
        {
            foreach (var engine in engines.Values)
            {
                engine.Dispose();
            }

            engines.Clear();
        }

        _enginesByThread.Dispose();
    }
}

public sealed record OcrSingleLineResult(string Text, float Confidence);
