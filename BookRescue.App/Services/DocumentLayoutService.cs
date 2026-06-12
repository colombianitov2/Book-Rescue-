using System.Text;
using BookRescue.App.Models;

namespace BookRescue.App.Services;

public static class DocumentLayoutService
{
    public static IReadOnlyList<PageTextBlock> BuildTextBlocks(BookPageInfo page, OcrPageResult ocrPage)
    {
        var lines = TextCleanupService.BuildOrderedLineBoxes(page, ocrPage);
        if (lines.Count == 0)
        {
            return [];
        }

        var blocks = new List<PageTextBlock>();
        var current = new List<OcrLineBox>();

        foreach (var line in lines)
        {
            if (current.Count > 0 && ShouldStartNewBlock(current[^1], line))
            {
                blocks.Add(CreateBlock(current));
                current.Clear();
            }

            current.Add(line);

            if (TextCleanupService.IsLikelyHeading(line.Text) || LooksStandalone(line.Text))
            {
                blocks.Add(CreateBlock(current));
                current.Clear();
            }
        }

        if (current.Count > 0)
        {
            blocks.Add(CreateBlock(current));
        }

        return blocks
            .Where(block => !string.IsNullOrWhiteSpace(block.Text))
            .ToList();
    }

    public static IReadOnlyList<string> SplitParagraphs(string text)
    {
        var lineNormalizedText = text
            .Replace("\r\n", "\n")
            .Replace('\r', '\n');

        if (lineNormalizedText.Contains("\n\n", StringComparison.Ordinal))
        {
            return lineNormalizedText
                .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(CleanStructuredBlock)
                .Where(block => !string.IsNullOrWhiteSpace(block))
                .ToList();
        }

        var normalized = TextCleanupService.CleanPlainText(lineNormalizedText);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return [];
        }

        return normalized
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();
    }

    private static string CleanStructuredBlock(string block)
    {
        var trimmed = block.Trim();
        if (StructuredTextService.TryParseMarkdownTable(trimmed, out _))
        {
            return trimmed;
        }

        if (StructuredTextService.IsFormula(trimmed) ||
            StructuredTextService.TryReadBullet(trimmed, out _) ||
            StructuredTextService.TryReadMarkdownHeading(trimmed, out _, out _))
        {
            return trimmed;
        }

        var cleaned = TextCleanupService.CleanPlainText(block);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return string.Empty;
        }

        return string.Join(' ', cleaned
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    public static IReadOnlyList<PageTextBlock> ApplyOutputText(
        IReadOnlyList<PageTextBlock> sourceBlocks,
        IReadOnlyList<string> outputParagraphs)
    {
        if (sourceBlocks.Count == 0)
        {
            return outputParagraphs
                .Select((paragraph, index) => new PageTextBlock
                {
                    Text = paragraph,
                    X = 0,
                    Y = index * 100,
                    Width = 1,
                    Height = 1,
                    IsHeading = TextCleanupService.IsLikelyHeading(paragraph)
                })
                .ToList();
        }

        if (outputParagraphs.Count == 0)
        {
            return sourceBlocks;
        }

        var result = new List<PageTextBlock>(Math.Max(sourceBlocks.Count, outputParagraphs.Count));
        var limit = Math.Min(sourceBlocks.Count, outputParagraphs.Count);
        for (var i = 0; i < limit; i++)
        {
            var source = sourceBlocks[i];
            var paragraph = outputParagraphs[i];
            result.Add(new PageTextBlock
            {
                Text = paragraph,
                X = source.X,
                Y = source.Y,
                Width = source.Width,
                Height = source.Height,
                IsHeading = source.IsHeading || TextCleanupService.IsLikelyHeading(paragraph)
            });
        }

        if (outputParagraphs.Count > sourceBlocks.Count)
        {
            var anchor = sourceBlocks[^1];
            for (var i = sourceBlocks.Count; i < outputParagraphs.Count; i++)
            {
                result.Add(new PageTextBlock
                {
                    Text = outputParagraphs[i],
                    X = anchor.X,
                    Y = anchor.Y + ((i - sourceBlocks.Count + 1) * anchor.Height),
                    Width = anchor.Width,
                    Height = anchor.Height,
                    IsHeading = TextCleanupService.IsLikelyHeading(outputParagraphs[i])
                });
            }
        }

        return result;
    }

    public static bool ContainsStructuredTable(string text)
    {
        return SplitParagraphs(text).Any(block => StructuredTextService.TryParseMarkdownTable(block, out _));
    }

    public static bool ContainsStructuredTable(IReadOnlyList<string> paragraphs)
    {
        return paragraphs.Any(block => StructuredTextService.TryParseMarkdownTable(block, out _));
    }

    private static bool ShouldStartNewBlock(OcrLineBox previous, OcrLineBox current)
    {
        if (TextCleanupService.IsLikelyHeading(current.Text))
        {
            return true;
        }

        var previousBottom = previous.Y + previous.Height;
        var verticalGap = current.Y - previousBottom;
        var lineHeight = Math.Max(previous.Height, current.Height);
        if (verticalGap > lineHeight * 0.85f)
        {
            return true;
        }

        var indentShift = current.X - previous.X;
        if (verticalGap > lineHeight * 0.35f && indentShift > lineHeight * 1.5f)
        {
            return true;
        }

        return LooksStandalone(previous.Text);
    }

    private static bool LooksStandalone(string text)
    {
        if (text.Length > 90)
        {
            return false;
        }

        if (text.StartsWith("Figure ", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Figura ", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Table ", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Tabla ", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var letters = text.Count(char.IsLetter);
        var digits = text.Count(char.IsDigit);
        return text.Length <= 45 && digits >= 2 && letters <= 12;
    }

    private static PageTextBlock CreateBlock(IReadOnlyList<OcrLineBox> lines)
    {
        var x = lines.Min(line => line.X);
        var y = lines.Min(line => line.Y);
        var right = lines.Max(line => line.X + line.Width);
        var bottom = lines.Max(line => line.Y + line.Height);

        return new PageTextBlock
        {
            Text = MergeLines(lines),
            X = x,
            Y = y,
            Width = Math.Max(1, right - x),
            Height = Math.Max(1, bottom - y),
            IsHeading = lines.Count <= 2 && lines.Any(line => TextCleanupService.IsLikelyHeading(line.Text))
        };
    }

    private static string MergeLines(IReadOnlyList<OcrLineBox> lines)
    {
        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            var text = line.Text.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (builder.Length == 0)
            {
                builder.Append(text);
                continue;
            }

            if (builder[^1] == '-')
            {
                builder.Length--;
                builder.Append(text);
            }
            else
            {
                builder.Append(' ');
                builder.Append(text);
            }
        }

        return builder.ToString().Trim();
    }
}
