namespace BookRescue.App.Models;

public sealed class DocumentAiAnalysisResult
{
    public required string OutputFolder { get; init; }

    public required string MarkdownPath { get; init; }

    public required string JsonPath { get; init; }

    public required IReadOnlyList<string> PageTexts { get; init; }

    public IReadOnlyList<RescuedImageInfo> RescuedImages { get; init; } = [];

    public bool HasUsableText => PageTexts.Any(text => !string.IsNullOrWhiteSpace(text));
}
