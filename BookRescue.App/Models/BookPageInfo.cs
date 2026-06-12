namespace BookRescue.App.Models;

public sealed class BookPageInfo
{
    public required string OriginalImagePath { get; init; }

    public required string RestoredImagePath { get; init; }

    public required float WidthPoints { get; init; }

    public required float HeightPoints { get; init; }

    public required int PixelWidth { get; init; }

    public required int PixelHeight { get; init; }

    public required int PageIndex { get; init; }
}
