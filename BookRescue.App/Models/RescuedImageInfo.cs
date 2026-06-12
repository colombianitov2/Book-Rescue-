namespace BookRescue.App.Models;

public sealed class RescuedImageInfo
{
    public required string ImagePath { get; init; }

    public required int PageNumber { get; init; }

    public required float X { get; init; }

    public required float Y { get; init; }

    public required float Width { get; init; }

    public required float Height { get; init; }

    public required int PagePixelWidth { get; init; }

    public required int PagePixelHeight { get; init; }

    public string Kind { get; init; } = "figure";
}
