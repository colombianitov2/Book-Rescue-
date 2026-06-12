using System.Text;
using System.Text.Json;
using System.Net.Http;

namespace BookRescue.App.Services;

public sealed class LanguageDetectionService : IDisposable
{
    private static readonly IReadOnlyCollection<string> EnglishHints =
    [
        "the", "and", "that", "have", "for", "not", "with", "you", "this", "from", "book", "chapter", "shall", "which"
    ];

    private static readonly IReadOnlyCollection<string> SpanishHints =
    [
        "que", "para", "con", "una", "los", "las", "por", "del", "como", "este", "esta", "capitulo", "libro", "ser"
    ];

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(20) };

    public async Task<string> DetectAsync(string text, string? libreTranslateEndpoint, string? apiKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "unknown";
        }

        var endpoint = NormalizeEndpoint(libreTranslateEndpoint);
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            var detected = await TryDetectViaApiAsync(text, endpoint!, apiKey, cancellationToken);
            if (!string.IsNullOrWhiteSpace(detected))
            {
                return detected!;
            }
        }

        return DetectByHeuristic(text);
    }

    public string DetectByHeuristic(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "unknown";
        }

        var normalized = NormalizeText(text);
        var englishScore = ScoreHints(normalized, EnglishHints);
        var spanishScore = ScoreHints(normalized, SpanishHints);

        if (normalized.Contains('ñ') || normalized.Contains('á') || normalized.Contains('é') || normalized.Contains('í') || normalized.Contains('ó') || normalized.Contains('ú'))
        {
            spanishScore += 2;
        }

        if (englishScore == 0 && spanishScore == 0)
        {
            return "unknown";
        }

        return englishScore >= spanishScore ? "en" : "es";
    }

    private static string NormalizeText(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text.ToLowerInvariant())
        {
            builder.Append(char.IsLetter(ch) || char.IsWhiteSpace(ch) ? ch : ' ');
        }

        return builder.ToString();
    }

    private static int ScoreHints(string text, IEnumerable<string> hints)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return 0;
        }

        var set = new HashSet<string>(words);
        return hints.Count(set.Contains);
    }

    private async Task<string?> TryDetectViaApiAsync(string text, string endpoint, string? apiKey, CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, string>
        {
            ["q"] = text.Length > 2000 ? text[..2000] : text
        };

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            payload["api_key"] = apiKey;
        }

        try
        {
            using var response = await _httpClient.PostAsync($"{endpoint}/detect", new FormUrlEncodedContent(payload), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            {
                return null;
            }

            var language = doc.RootElement[0].GetProperty("language").GetString();
            return string.IsNullOrWhiteSpace(language) ? null : language;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizeEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return null;
        }

        return endpoint.Trim().TrimEnd('/');
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
