using System.Text;
using BookRescue.App.Models;

namespace BookRescue.App.Services;

public sealed class CsvOutputWriter
{
    public async Task WriteAsync(
        string outputPath,
        IReadOnlyList<string> originalPageTexts,
        IReadOnlyList<string> outputPageTexts,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var builder = new StringBuilder();
        builder.AppendLine("Página,Texto original,Texto de salida");

        for (var index = 0; index < originalPageTexts.Count; index++)
        {
            var originalText = originalPageTexts[index];
            var outputText = index < outputPageTexts.Count ? outputPageTexts[index] : string.Empty;
            builder
                .Append(index + 1)
                .Append(',')
                .Append(Escape(originalText))
                .Append(',')
                .Append(Escape(outputText))
                .AppendLine();
        }

        await File.WriteAllTextAsync(outputPath, builder.ToString(), Encoding.UTF8, cancellationToken);
    }

    private static string Escape(string value)
    {
        value = value.Replace("\r\n", "\n").Replace('\r', '\n');
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}
