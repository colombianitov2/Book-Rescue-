using BookRescue.App.Models;

namespace BookRescue.App.Services;

public enum EditorialBlockKind
{
    Title,
    Subtitle,
    SectionHeading,
    Body,
    Quote,
    Bullet,
    Numbered,
    Formula,
    Table,
    Caption
}

public sealed record EditorialBlock(
    EditorialBlockKind Kind,
    string Text,
    PageTextBlock? SourceBlock,
    IReadOnlyList<IReadOnlyList<string>>? TableRows = null,
    string Marker = "");

public static class PageBlockClassifier
{
    public static IReadOnlyList<EditorialBlock> Classify(
        BookPageInfo page,
        IReadOnlyList<PageTextBlock> blocks,
        IReadOnlyList<string> paragraphs,
        int pageIndex)
    {
        var applied = DocumentLayoutService.ApplyOutputText(blocks, paragraphs);
        return applied.Select((block, index) => ClassifyBlock(page, block, pageIndex, index)).ToList();
    }

    private static EditorialBlock ClassifyBlock(BookPageInfo page, PageTextBlock block, int pageIndex, int blockIndex)
    {
        var text = block.Text.Trim();
        if (StructuredTextService.TryParseMarkdownTable(text, out var rows))
        {
            return new EditorialBlock(EditorialBlockKind.Table, text, block, rows);
        }

        if (StructuredTextService.TryReadMarkdownHeading(text, out var headingText, out var level))
        {
            return new EditorialBlock(level <= 1 ? EditorialBlockKind.Title : EditorialBlockKind.SectionHeading, headingText, block);
        }

        if (StructuredTextService.IsFormula(text))
        {
            return new EditorialBlock(EditorialBlockKind.Formula, StructuredTextService.StripFormulaMarkers(text), block);
        }

        if (StructuredTextService.IsCaption(text))
        {
            return new EditorialBlock(EditorialBlockKind.Caption, text, block);
        }

        if (StructuredTextService.TryReadBullet(text, out var bulletText))
        {
            return new EditorialBlock(EditorialBlockKind.Bullet, bulletText, block);
        }

        if (StructuredTextService.TryReadNumberedItem(text, out var marker, out var itemText))
        {
            return new EditorialBlock(EditorialBlockKind.Numbered, itemText, block, Marker: marker);
        }

        if (LooksLikeTitle(page, block, text, pageIndex, blockIndex))
        {
            return new EditorialBlock(EditorialBlockKind.Title, NormalizeTitle(text), block);
        }

        if (LooksLikeSubtitle(page, block, text, blockIndex))
        {
            return new EditorialBlock(EditorialBlockKind.Subtitle, text, block);
        }

        if (StructuredTextService.ShouldRenderAsHeading(text, block.IsHeading))
        {
            return new EditorialBlock(EditorialBlockKind.SectionHeading, text, block);
        }

        if (LooksIndented(page, block) && text.Length > 40)
        {
            return new EditorialBlock(EditorialBlockKind.Quote, text, block);
        }

        return new EditorialBlock(EditorialBlockKind.Body, text, block);
    }

    private static bool LooksLikeTitle(BookPageInfo page, PageTextBlock block, string text, int pageIndex, int blockIndex)
    {
        if (pageIndex == 0 || blockIndex > 2 || text.Length is < 4 or > 95)
        {
            return false;
        }

        var topRatio = block.Y / Math.Max(1f, page.PixelHeight);
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return topRatio < 0.22f && words <= 12 && (block.IsHeading || UppercaseRatio(text) > 0.62d);
    }

    private static bool LooksLikeSubtitle(BookPageInfo page, PageTextBlock block, string text, int blockIndex)
    {
        if (blockIndex > 4 || text.Length is < 4 or > 120)
        {
            return false;
        }

        var topRatio = block.Y / Math.Max(1f, page.PixelHeight);
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return topRatio < 0.30f && words <= 16 && (block.IsHeading || UppercaseRatio(text) > 0.45d);
    }

    private static bool LooksIndented(BookPageInfo page, PageTextBlock block)
    {
        return block.X > page.PixelWidth * 0.15f && block.Width < page.PixelWidth * 0.72f;
    }

    private static double UppercaseRatio(string text)
    {
        var letters = text.Where(char.IsLetter).ToList();
        return letters.Count == 0 ? 0d : letters.Count(char.IsUpper) / (double)letters.Count;
    }

    private static string NormalizeTitle(string text)
    {
        var letters = text.Where(char.IsLetter).ToList();
        if (letters.Count > 0 && letters.Count(char.IsUpper) / (double)letters.Count > 0.80d)
        {
            return text;
        }

        return text;
    }
}
