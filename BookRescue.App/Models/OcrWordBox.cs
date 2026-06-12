namespace BookRescue.App.Models;

public sealed class OcrWordBox
{
    public required string Text { get; init; }

    public required float X { get; init; }

    public required float Y { get; init; }

    public required float Width { get; init; }

    public required float Height { get; init; }

    public required float Confidence { get; init; }
}
