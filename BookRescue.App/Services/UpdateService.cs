using System.Net.Http;
using System.Text.Json;

namespace BookRescue.App.Services;

public sealed class UpdateService : IDisposable
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/colombianitov2/Book-Rescue/releases/latest";
    private readonly HttpClient _httpClient = new();

    public UpdateService()
    {
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("BookRescue-Updater");
    }

    public async Task<string> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync(LatestReleaseUrl, cancellationToken);
            if ((int)response.StatusCode == 404)
            {
                return "Aun no hay actualizaciones publicadas.";
            }

            if (!response.IsSuccessStatusCode)
            {
                return "No se pudo revisar actualizaciones en este momento.";
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(json);
            var tag = document.RootElement.TryGetProperty("tag_name", out var tagElement)
                ? tagElement.GetString()
                : null;

            return string.IsNullOrWhiteSpace(tag)
                ? "Repositorio encontrado, sin version publicada."
                : $"Ultima version publicada: {tag}.";
        }
        catch
        {
            return "No se pudo revisar actualizaciones en este momento.";
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
