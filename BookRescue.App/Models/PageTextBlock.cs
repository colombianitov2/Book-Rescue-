namespace BookRescue.App.Models;

public sealed class PageTextBlock
{
    public required string Text { get; init; }

    public required float X { get; init; }

    public required float Y { get; init; }

    public required float Width { get; init; }

    public required float Height { get; init; }

    public required bool IsHeading { get; init; }
}
