using System.Net;
using System.Net.Http;

namespace BookRescue.App.Services;

public sealed class TessdataBootstrapper : IDisposable
{
    private const string TessdataFastBaseUrl = "https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/main";
    private readonly HttpClient _httpClient;

    public TessdataBootstrapper()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(2)
        };

        var bundledTessdata = LocateBundledTessdata();
        if (!string.IsNullOrWhiteSpace(bundledTessdata))
        {
            TessdataDirectory = bundledTessdata;
        }
        else
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            TessdataDirectory = Path.Combine(appData, "BookRescue", "tessdata");
        }

        Directory.CreateDirectory(TessdataDirectory);
    }

    public string TessdataDirectory { get; }

    public IReadOnlyList<string> ParseLanguageExpression(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return new[] { "spa", "eng" };
        }

        return expression
            .Split(['+', ',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeLanguageCode)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task EnsureLanguagePacksAsync(string expression, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var languages = ParseLanguageExpression(expression).ToList();
        if (!languages.Contains("osd", StringComparer.OrdinalIgnoreCase))
        {
            languages.Add("osd");
        }

        foreach (var language in languages)
        {
            var targetFile = Path.Combine(TessdataDirectory, $"{language}.traineddata");
            if (File.Exists(targetFile))
            {
                continue;
            }

            progress?.Report($"Descargando paquete OCR '{language}'...");
            await DownloadLanguagePackAsync(language, cancellationToken);
        }
    }

    public async Task DownloadLanguagePackAsync(string languageCode, CancellationToken cancellationToken = default)
    {
        languageCode = NormalizeLanguageCode(languageCode);
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            throw new InvalidOperationException("Codigo de idioma invalido.");
        }

        var requestUrl = $"{TessdataFastBaseUrl}/{languageCode}.traineddata";
        using var response = await _httpClient.GetAsync(requestUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException($"No existe paquete OCR para el idioma '{languageCode}'.");
        }

        response.EnsureSuccessStatusCode();

        var tempPath = Path.Combine(TessdataDirectory, $"{languageCode}.traineddata.tmp");
        var finalPath = Path.Combine(TessdataDirectory, $"{languageCode}.traineddata");

        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = File.Create(tempPath))
        {
            await input.CopyToAsync(output, cancellationToken);
        }

        if (File.Exists(finalPath))
        {
            File.Delete(finalPath);
        }

        File.Move(tempPath, finalPath);
    }

    public IReadOnlyList<string> GetInstalledLanguages()
    {
        if (!Directory.Exists(TessdataDirectory))
        {
            return Array.Empty<string>();
        }

        return Directory
            .GetFiles(TessdataDirectory, "*.traineddata", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(name => name)
            .ToList()!;
    }

    private static string NormalizeLanguageCode(string code)
    {
        return code.Trim().ToLowerInvariant();
    }

    private static string? LocateBundledTessdata()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "runtime", "tessdata");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return null;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
