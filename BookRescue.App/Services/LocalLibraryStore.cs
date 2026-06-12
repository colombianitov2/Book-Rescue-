using System.Text.Json;
using BookRescue.App.Models;

namespace BookRescue.App.Services;

public sealed class LocalLibraryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly SemaphoreSlim _sync = new(1, 1);

    public LocalLibraryStore()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dataDirectory = Path.Combine(appData, "BookRescue");
        Directory.CreateDirectory(dataDirectory);
        StorageFilePath = Path.Combine(dataDirectory, "library.json");
    }

    public string StorageFilePath { get; }

    public async Task<IReadOnlyList<ConvertedBookRecord>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(StorageFilePath))
            {
                return Array.Empty<ConvertedBookRecord>();
            }

            await using var stream = File.OpenRead(StorageFilePath);
            var records = await JsonSerializer.DeserializeAsync<List<ConvertedBookRecord>>(stream, JsonOptions, cancellationToken)
                          ?? new List<ConvertedBookRecord>();

            return records
                .OrderByDescending(r => r.CreatedAtUtc)
                .ToList();
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task SaveOrUpdateAsync(ConvertedBookRecord record, CancellationToken cancellationToken = default)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            var records = new List<ConvertedBookRecord>();
            if (File.Exists(StorageFilePath))
            {
                await using var readStream = File.OpenRead(StorageFilePath);
                records = await JsonSerializer.DeserializeAsync<List<ConvertedBookRecord>>(readStream, JsonOptions, cancellationToken)
                          ?? new List<ConvertedBookRecord>();
            }

            records.RemoveAll(x =>
                x.Id == record.Id ||
                SameNonEmptyPath(x.OutputFolder, record.OutputFolder) ||
                SameNonEmptyPath(x.ReconstructedPdfPath, record.ReconstructedPdfPath) ||
                SameNonEmptyPath(x.ReconstructedDocxPath, record.ReconstructedDocxPath) ||
                SameNonEmptyPath(x.TextPath, record.TextPath) ||
                SameNonEmptyPath(x.ExtractedTextPath, record.ExtractedTextPath));
            records.Add(record);
            records = records.OrderByDescending(r => r.CreatedAtUtc).ToList();

            await using var writeStream = File.Create(StorageFilePath);
            await JsonSerializer.SerializeAsync(writeStream, records, JsonOptions, cancellationToken);
        }
        finally
        {
            _sync.Release();
        }
    }

    private static bool SameNonEmptyPath(string left, string right)
    {
        return !string.IsNullOrWhiteSpace(left) &&
            !string.IsNullOrWhiteSpace(right) &&
            left.Equals(right, StringComparison.OrdinalIgnoreCase);
    }
}
