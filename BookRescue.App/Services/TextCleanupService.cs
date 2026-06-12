using System.Text;
using System.Text.RegularExpressions;
using BookRescue.App.Models;

namespace BookRescue.App.Services;

public static partial class TextCleanupService
{
    public static IReadOnlyList<string> BuildOrderedLines(BookPageInfo page, OcrPageResult ocrPage)
    {
        return BuildOrderedLineBoxes(page, ocrPage)
            .Select(line => line.Text)
            .ToList();
    }

    public static IReadOnlyList<OcrLineBox> BuildOrderedLineBoxes(BookPageInfo page, OcrPageResult ocrPage)
    {
        var lines = ocrPage.Lines
            .Select(CleanLineBox)
            .Where(line => line is not null)
            .Cast<OcrLineBox>()
            .Where(KeepLine)
            .ToList();

        if (lines.Count == 0)
        {
            return [];
        }

        return OrderForReading(page, lines)
            .ToList();
    }

    public static string BuildOrderedPageText(BookPageInfo page, OcrPageResult ocrPage)
    {
        var blocks = DocumentLayoutService.BuildTextBlocks(page, ocrPage);
        if (blocks.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var block in blocks)
        {
            builder.AppendLine(block.Text);
        }

        return builder.ToString().Trim();
    }

    public static string CleanPlainText(string text)
    {
        var lines = text
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .Select(CleanText)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Where(KeepTextLine);

        return string.Join(Environment.NewLine, lines);
    }

    private static OcrLineBox? CleanLineBox(OcrLineBox line)
    {
        var cleaned = CleanText(line.Text);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return null;
        }

        return new OcrLineBox
        {
            Text = cleaned,
            X = line.X,
            Y = line.Y,
            Width = line.Width,
            Height = line.Height,
            Confidence = line.Confidence
        };
    }

    private static string CleanText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var cleaned = text
            .Replace('\u000c', ' ')
            .Replace('\u00a0', ' ')
            .Replace("ﬁ", "fi")
            .Replace("ﬂ", "fl")
            .Replace("“", "\"")
            .Replace("”", "\"")
            .Replace("’", "'")
            .Replace("‘", "'");

        cleaned = RemoveUnsafeSymbols(cleaned);
        cleaned = VisibleCharactersRegex().Replace(cleaned, " ");
        cleaned = RepeatedSymbolRegex().Replace(cleaned, " ");
        cleaned = WhitespaceRegex().Replace(cleaned, " ");
        return cleaned.Trim();
    }

    private static bool KeepLine(OcrLineBox line)
    {
        if (!KeepTextLine(line.Text))
        {
            return false;
        }

        if (line.Confidence >= 28)
        {
            return true;
        }

        return AlphaNumericRatio(line.Text) >= 0.62;
    }

    private static bool KeepTextLine(string line)
    {
        if (line.Length < 2)
        {
            return false;
        }

        if (LooksLikeFormulaLine(line))
        {
            return true;
        }

        var ratio = AlphaNumericRatio(line);
        if (line.Length >= 12 && ratio < 0.45)
        {
            return false;
        }

        if (line.Length < 12 && ratio < 0.62)
        {
            return false;
        }

        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tokenLengths = tokens
            .Select(AlphaNumericLength)
            .Where(length => length > 0)
            .ToList();

        if (tokenLengths.Count == 0)
        {
            return false;
        }

        if (tokenLengths.Count <= 3 && tokenLengths.Max() <= 2)
        {
            return false;
        }

        if (tokenLengths.Count == 1 && tokenLengths[0] <= 4)
        {
            return false;
        }

        if (tokenLengths.Count == 1 && IsSingleLowercaseNoise(tokens[0]))
        {
            return false;
        }

        if (tokenLengths.Count <= 3 && tokenLengths.Max() <= 3)
        {
            return false;
        }

        if (line.Length < 18 && CountMarks(line) >= 2 && tokenLengths.Max() <= 5)
        {
            return false;
        }

        if (tokenLengths.Count >= 5)
        {
            var shortTokenRatio = tokenLengths.Count(length => length <= 3) / (double)tokenLengths.Count;
            if (shortTokenRatio > 0.68 && tokenLengths.Max() <= 4)
            {
                return false;
            }
        }

        if (tokens.Length >= 8 && tokens.Count(token => token.Length <= 2) / (double)tokens.Length > 0.72)
        {
            return false;
        }

        return true;
    }

    private static bool LooksLikeFormulaLine(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length < 5)
        {
            return false;
        }

        var contentCount = trimmed.Count(char.IsLetterOrDigit);
        if (contentCount < 2)
        {
            return false;
        }

        return trimmed.Contains('=') ||
            trimmed.Contains('Δ') ||
            trimmed.Contains('∑') ||
            trimmed.Contains('√') ||
            trimmed.Contains('²') ||
            trimmed.Contains('³') ||
            trimmed.Contains('/') && trimmed.Any(char.IsDigit) && CountMarks(trimmed) >= 2;
    }

    private static IEnumerable<OcrLineBox> OrderForReading(BookPageInfo page, IReadOnlyList<OcrLineBox> lines)
    {
        var pageWidth = Math.Max(1, page.PixelWidth);
        var left = lines.Where(line => line.X + (line.Width / 2f) < pageWidth * 0.52f).ToList();
        var right = lines.Where(line => line.X + (line.Width / 2f) >= pageWidth * 0.52f).ToList();
        var twoColumns = left.Count >= 6 && right.Count >= 6 && MedianX(right) - MedianX(left) > pageWidth * 0.24f;

        if (!twoColumns)
        {
            return lines.OrderBy(line => line.Y).ThenBy(line => line.X);
        }

        return left
            .OrderBy(line => line.Y)
            .ThenBy(line => line.X)
            .Concat(right.OrderBy(line => line.Y).ThenBy(line => line.X));
    }

    private static float MedianX(IReadOnlyList<OcrLineBox> lines)
    {
        var ordered = lines.Select(line => line.X).Order().ToList();
        return ordered[ordered.Count / 2];
    }

    private static bool StartsNewParagraph(string previous, string current)
    {
        if (previous.EndsWith('.') || previous.EndsWith(':') || previous.EndsWith('?') || previous.EndsWith('!'))
        {
            return current.Length > 0 && char.IsUpper(current[0]);
        }

        return IsLikelyHeading(current);
    }

    public static bool IsLikelyHeading(string line)
    {
        if (line.Length is < 3 or > 90)
        {
            return false;
        }

        if (HeadingNumberRegex().IsMatch(line))
        {
            return true;
        }

        var letters = line.Where(char.IsLetter).ToList();
        return letters.Count >= 3 && letters.Count(char.IsUpper) / (double)letters.Count > 0.72;
    }

    private static double AlphaNumericRatio(string text)
    {
        var visible = text.Count(ch => !char.IsWhiteSpace(ch));
        if (visible == 0)
        {
            return 0;
        }

        var alphanumeric = text.Count(char.IsLetterOrDigit);
        return alphanumeric / (double)visible;
    }

    private static int AlphaNumericLength(string token)
    {
        return token.Count(char.IsLetterOrDigit);
    }

    private static bool IsSingleLowercaseNoise(string token)
    {
        if (!token.All(char.IsLetter))
        {
            return false;
        }

        var letters = token.Where(char.IsLetter).ToList();
        return letters.Count >= 5 && letters.All(char.IsLower);
    }

    private static int CountMarks(string text)
    {
        return text.Count(ch => char.IsPunctuation(ch) || IsAllowedTechnicalSymbol(ch));
    }

    private static string RemoveUnsafeSymbols(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch) || char.IsPunctuation(ch))
            {
                builder.Append(ch);
                continue;
            }

            if (IsAllowedTechnicalSymbol(ch))
            {
                builder.Append(ch);
                continue;
            }

            builder.Append(' ');
        }

        return builder.ToString();
    }

    private static bool IsAllowedTechnicalSymbol(char ch)
    {
        return ch is '&' or '%' or '$' or '@' or '#' or '+' or '=' or '*' or '/' or '\\'
            or '°' or '±' or '×' or '÷' or '≤' or '≥' or 'µ';
    }

    [GeneratedRegex(@"[^\p{L}\p{N}\p{P}\s&%$@#+=*/\\°±×÷≤≥µ]")]
    private static partial Regex VisibleCharactersRegex();

    [GeneratedRegex(@"([^\p{L}\p{N}\s])\1{2,}")]
    private static partial Regex RepeatedSymbolRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^(\d+(\.\d+)*|[A-Z]\.)\s+\S+")]
    private static partial Regex HeadingNumberRegex();
}
