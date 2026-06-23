using System.Security.Cryptography;
using System.Text;

namespace BookRescue.App.Services;

internal static class SafeConversionPaths
{
    private const int MaxOutputBaseNameLength = 80;

    public static string CreateRunFolder(string outputRoot)
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var baseName = $"run_{timestamp}";

        for (var attempt = 0; attempt < 100; attempt++)
        {
            var folderName = attempt == 0
                ? baseName
                : $"{baseName}_{attempt + 1:D2}";
            var folder = Path.Combine(outputRoot, folderName);

            if (Directory.Exists(folder))
            {
                continue;
            }

            Directory.CreateDirectory(folder);
            return folder;
        }

        throw new IOException($"No se pudo crear una carpeta de ejecución única bajo: {outputRoot}");
    }

    public static string CreateOutputBaseName(string inputPath)
    {
        var originalBaseName = Path.GetFileNameWithoutExtension(inputPath);
        return SanitizeFileName(originalBaseName, "book", MaxOutputBaseNameLength);
    }

    public static string StageInputForNativeProcessing(string inputPath, string inputStagingFolder)
    {
        Directory.CreateDirectory(inputStagingFolder);

        var extension = Path.GetExtension(inputPath).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".bin";
        }

        var stagedInputPath = Path.Combine(inputStagingFolder, $"input_001{extension}");
        File.Copy(inputPath, stagedInputPath, overwrite: true);
        return stagedInputPath;
    }

    public static async Task WriteInputMetadataAsync(
        string runFolder,
        string originalInputPath,
        string stagedInputPath,
        string outputBaseName,
        CancellationToken cancellationToken)
    {
        var metadataPath = Path.Combine(runFolder, "conversion-input-metadata.txt");
        var lines = new[]
        {
            $"OriginalFileName={Path.GetFileName(originalInputPath)}",
            $"OriginalPath={originalInputPath}",
            $"StagedInputPath={stagedInputPath}",
            $"OutputBaseName={outputBaseName}"
        };

        await File.WriteAllLinesAsync(metadataPath, lines, Encoding.UTF8, cancellationToken);
    }

    private static string SanitizeFileName(string value, string fallback, int maxLength)
    {
        var sanitized = string.IsNullOrWhiteSpace(value)
            ? fallback
            : value.Trim();

        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            sanitized = sanitized.Replace(invalidChar, '_');
        }

        sanitized = sanitized.Replace(Path.DirectorySeparatorChar, '_')
            .Replace(Path.AltDirectorySeparatorChar, '_')
            .Trim(' ', '.');

        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = fallback;
        }

        if (sanitized.Length <= maxLength)
        {
            return sanitized;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sanitized)))[..8].ToLowerInvariant();
        var prefixLength = Math.Max(1, maxLength - hash.Length - 1);
        return $"{sanitized[..prefixLength]}_{hash}";
    }
}
