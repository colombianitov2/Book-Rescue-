namespace BookRescue.App.Models;

public sealed class ConvertedBookRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string SourcePath { get; set; } = string.Empty;

    public string SourceFileName { get; set; } = string.Empty;

    public string OutputFolder { get; set; } = string.Empty;

    public string ReconstructionMode { get; set; } = string.Empty;

    public string RestoredImagesFolder { get; set; } = string.Empty;

    public string TextPath { get; set; } = string.Empty;

    public string ReconstructedPdfPath { get; set; } = string.Empty;

    public string ReconstructedDocxPath { get; set; } = string.Empty;

    public string ReconstructedEpubPath { get; set; } = string.Empty;

    public string CsvPath { get; set; } = string.Empty;

    public string ReportPath { get; set; } = string.Empty;

    public string ExtractedTextPath { get; set; } = string.Empty;

    public string OcrLanguages { get; set; } = string.Empty;

    public string Status { get; set; } = "Completado";

    public DateTimeOffset CreatedAtUtc { get; set; }
}
