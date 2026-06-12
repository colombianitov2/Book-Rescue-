namespace BookRescue.App.Services;

public static class StructuredTextService
{
    public static bool IsFormula(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length > 2 && trimmed.StartsWith('$') && trimmed.EndsWith('$');
    }

    public static string StripFormulaMarkers(string text)
    {
        var trimmed = text.Trim();
        return IsFormula(trimmed) ? trimmed[1..^1].Trim() : trimmed;
    }

    public static bool TryReadBullet(string text, out string bulletText)
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

    public static bool TryReadNumberedItem(string text, out string marker, out string itemText)
    {
        var trimmed = text.TrimStart();
        marker = string.Empty;
        itemText = string.Empty;

        var separatorIndex = -1;
        for (var i = 0; i < Math.Min(trimmed.Length, 10); i++)
        {
            if (trimmed[i] is '.' or ')')
            {
                separatorIndex = i;
                break;
            }
        }

        if (separatorIndex <= 0 || separatorIndex >= trimmed.Length - 1 || !char.IsWhiteSpace(trimmed[separatorIndex + 1]))
        {
            return false;
        }

        marker = trimmed[..(separatorIndex + 1)].Trim();
        itemText = trimmed[(separatorIndex + 1)..].Trim();
        if (itemText.Length < 3)
        {
            return false;
        }

        var markerCore = marker.TrimEnd('.', ')');
        var allDigitsOrDots = markerCore.All(ch => char.IsDigit(ch) || ch is '.' or '-');
        var singleLetter = markerCore.Length == 1 && char.IsLetter(markerCore[0]);
        return allDigitsOrDots || singleLetter;
    }

    public static bool IsCaption(string text)
    {
        var trimmed = text.Trim();
        return trimmed.StartsWith("Figure ", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Fig. ", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Figura ", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Table ", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Tabla ", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryReadMarkdownHeading(string text, out string headingText, out int level)
    {
        var trimmed = text.TrimStart();
        level = 0;
        while (level < trimmed.Length && level < 6 && trimmed[level] == '#')
        {
            level++;
        }

        if (level == 0 || level >= trimmed.Length || !char.IsWhiteSpace(trimmed[level]))
        {
            headingText = string.Empty;
            level = 0;
            return false;
        }

        headingText = trimmed[level..].Trim();
        return !string.IsNullOrWhiteSpace(headingText);
    }

    public static bool LooksLikeHeading(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length is < 4 or > 90)
        {
            return false;
        }

        var letters = trimmed.Where(char.IsLetter).ToList();
        return letters.Count >= 4 && letters.Count(letter => char.IsUpper(letter)) / (double)letters.Count > 0.78;
    }

    public static bool ShouldRenderAsHeading(string text, bool sourceMarkedHeading)
    {
        var trimmed = text.Trim();
        if (LooksLikeHeading(trimmed))
        {
            return true;
        }

        return sourceMarkedHeading &&
            trimmed.Length <= 95 &&
            !trimmed.EndsWith('.') &&
            !trimmed.EndsWith(';') &&
            !trimmed.Contains(" which ", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.Contains(" that ", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryParseMarkdownTable(string text, out IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var parsedRows = new List<IReadOnlyList<string>>();
        var lines = text
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Contains('|'))
            .ToList();

        if (lines.Count < 2)
        {
            rows = [];
            return false;
        }

        foreach (var line in lines)
        {
            if (IsMarkdownTableSeparator(line))
            {
                continue;
            }

            var cells = SplitMarkdownTableLine(line);
            if (cells.Count >= 2)
            {
                parsedRows.Add(cells);
            }
        }

        if (parsedRows.Count < 2)
        {
            rows = [];
            return false;
        }

        var columnCount = parsedRows.Max(row => row.Count);
        rows = parsedRows
            .Select(row => PadRow(row, columnCount))
            .ToList();
        return true;
    }

    public static string CreateMarkdownTable(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (rows.Count == 0)
        {
            return string.Empty;
        }

        var columnCount = rows.Max(row => row.Count);
        var padded = rows.Select(row => PadRow(row, columnCount)).ToList();
        var result = new List<string>
        {
            "| " + string.Join(" | ", padded[0].Select(EscapeMarkdownCell)) + " |",
            "| " + string.Join(" | ", Enumerable.Repeat("---", columnCount)) + " |"
        };

        result.AddRange(padded.Skip(1).Select(row => "| " + string.Join(" | ", row.Select(EscapeMarkdownCell)) + " |"));
        return string.Join(Environment.NewLine, result);
    }

    private static bool IsMarkdownTableSeparator(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length > 0 && trimmed.All(ch => ch is '|' or '-' or ':' or ' ');
    }

    private static IReadOnlyList<string> SplitMarkdownTableLine(string line)
    {
        var cells = line.Trim().Split('|').Select(cell => cell.Trim()).ToList();
        if (cells.Count > 0 && string.IsNullOrWhiteSpace(cells[0]))
        {
            cells.RemoveAt(0);
        }

        if (cells.Count > 0 && string.IsNullOrWhiteSpace(cells[^1]))
        {
            cells.RemoveAt(cells.Count - 1);
        }

        return cells.Select(cell => cell.Replace(@"\|", "|")).ToList();
    }

    private static IReadOnlyList<string> PadRow(IReadOnlyList<string> row, int columnCount)
    {
        if (row.Count >= columnCount)
        {
            return row;
        }

        return row.Concat(Enumerable.Repeat(string.Empty, columnCount - row.Count)).ToList();
    }

    private static string EscapeMarkdownCell(string value)
    {
        return value.Replace("|", @"\|").Replace("\r", " ").Replace("\n", " ").Trim();
    }
}
