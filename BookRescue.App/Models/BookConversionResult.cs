namespace BookRescue.App.Models;

public sealed class BookConversionResult
{
    public required ConvertedBookRecord LibraryRecord { get; init; }

    public required string DetectedLanguage { get; init; }

    public required bool TranslationApplied { get; init; }
}
