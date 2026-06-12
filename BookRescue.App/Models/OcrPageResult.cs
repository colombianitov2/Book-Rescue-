namespace BookRescue.App.Models;

public sealed class OcrPageResult
{
    public required string FullText { get; init; }

    public required IReadOnlyList<OcrWordBox> Words { get; init; }

    public required IReadOnlyList<OcrLineBox> Lines { get; init; }
}
