using BookRescue.App.Models;

namespace BookRescue.App.ViewModels;

public sealed class LibraryItemViewModel
{
    public LibraryItemViewModel(ConvertedBookRecord record)
    {
        Record = record;
    }

    public ConvertedBookRecord Record { get; }

    public string SourceFileName => Record.SourceFileName;

    public string CreatedAtLocal => Record.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string ReconstructedPdfPath => Record.ReconstructedPdfPath;

    public string ReconstructedDocxPath => Record.ReconstructedDocxPath;

    public string ReconstructedEpubPath => Record.ReconstructedEpubPath;

    public string CsvPath => Record.CsvPath;

    public string OutputFolder => Record.OutputFolder;

    public string ExtractedTextPath => !string.IsNullOrWhiteSpace(Record.TextPath)
        ? Record.TextPath
        : Record.ExtractedTextPath;

    public string ReconstructionMode => Record.ReconstructionMode;

    public string ReportPath => Record.ReportPath;

    public string Status => Record.Status;

    public string AvailableOutputs
    {
        get
        {
            var outputs = new List<string>();
            AddIfPresent(outputs, Record.ReconstructedPdfPath, "PDF");
            AddIfPresent(outputs, Record.ReconstructedDocxPath, "Word");
            AddIfPresent(outputs, Record.ReconstructedEpubPath, "ePub");
            AddIfPresent(outputs, Record.CsvPath, "CSV");
            AddIfPresent(outputs, ExtractedTextPath, "Texto");
            AddIfPresent(outputs, Record.ReportPath, "Reporte");
            return outputs.Count == 0 ? "Texto" : string.Join(", ", outputs);
        }
    }

    private static void AddIfPresent(ICollection<string> outputs, string path, string label)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            outputs.Add(label);
        }
    }
}
