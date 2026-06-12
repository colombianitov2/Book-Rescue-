using System.Text;
using System.Text.Json;
using System.Net.Http;

namespace BookRescue.App.Services;

public sealed class LibreTranslateService : IDisposable
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(45) };
    private readonly BundledTranslatorHost _bundledTranslatorHost = new();

    public async Task<string> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        string? endpoint,
        string? apiKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(targetLanguage))
        {
            return text;
        }

        if (string.Equals(sourceLanguage, targetLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        var normalizedEndpoint = NormalizeEndpoint(endpoint);
        if (normalizedEndpoint is null)
        {
            return text;
        }

        if (!await CanReachServerAsync(normalizedEndpoint, cancellationToken))
        {
            return text;
        }

        var chunks = ChunkText(text, 1800);
        var translated = new List<string>(chunks.Count);

        foreach (var chunk in chunks)
        {
            var result = await TranslateChunkAsync(chunk, sourceLanguage, targetLanguage, normalizedEndpoint, apiKey, cancellationToken);
            translated.Add(result);
        }

        return string.Join(string.Empty, translated);
    }

    public async Task<bool> CanReachServerAsync(string? endpoint, CancellationToken cancellationToken = default)
    {
        var normalizedEndpoint = NormalizeEndpoint(endpoint);
        if (normalizedEndpoint is null)
        {
            return false;
        }

        try
        {
            using var response = await _httpClient.GetAsync($"{normalizedEndpoint}/languages", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return true;
            }
        }
        catch
        {
        }

        if (!Uri.TryCreate(normalizedEndpoint, UriKind.Absolute, out var endpointUri))
        {
            return false;
        }

        try
        {
            return await _bundledTranslatorHost.EnsureRunningAsync(endpointUri, cancellationToken);
        }
        catch
        {
            return false;
        }
    }

    private async Task<string> TranslateChunkAsync(
        string chunk,
        string sourceLanguage,
        string targetLanguage,
        string endpoint,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, string>
        {
            ["q"] = chunk,
            ["source"] = NormalizeSourceLanguage(sourceLanguage),
            ["target"] = targetLanguage,
            ["format"] = "text"
        };

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            payload["api_key"] = apiKey;
        }

        using var response = await _httpClient.PostAsync($"{endpoint}/translate", new FormUrlEncodedContent(payload), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return chunk;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(json);

        return document.RootElement.TryGetProperty("translatedText", out var translated)
            ? translated.GetString() ?? chunk
            : chunk;
    }

    private static List<string> ChunkText(string text, int chunkSize)
    {
        var output = new List<string>();
        if (text.Length <= chunkSize)
        {
            output.Add(text);
            return output;
        }

        var lines = text.Split('\n');
        var current = new StringBuilder();

        foreach (var line in lines)
        {
            if (current.Length + line.Length + 1 > chunkSize && current.Length > 0)
            {
                output.Add(current.ToString());
                current.Clear();
            }

            if (line.Length > chunkSize)
            {
                var index = 0;
                while (index < line.Length)
                {
                    var length = Math.Min(chunkSize, line.Length - index);
                    output.Add(line.Substring(index, length));
                    index += length;
                }
            }
            else
            {
                current.AppendLine(line);
            }
        }

        if (current.Length > 0)
        {
            output.Add(current.ToString());
        }

        return output;
    }

    private static string? NormalizeEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return null;
        }

        return endpoint.Trim().TrimEnd('/');
    }

    private static string NormalizeSourceLanguage(string? sourceLanguage)
    {
        if (string.IsNullOrWhiteSpace(sourceLanguage))
        {
            return "auto";
        }

        var normalized = sourceLanguage.Trim().ToLowerInvariant();
        return normalized is "unknown" or "auto" ? "auto" : normalized;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _bundledTranslatorHost.Dispose();
    }
}
