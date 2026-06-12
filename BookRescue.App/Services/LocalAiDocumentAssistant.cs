using System.Diagnostics;
using System.Text;

namespace BookRescue.App.Services;

public sealed partial class LocalAiDocumentAssistant
{
    private const int TimeoutMilliseconds = 10 * 60 * 1000;
    private const int DedicatedGpuFitTargetMiB = 0;
    private readonly Lazy<LocalAiRuntime?> runtime = new(FindRuntime);

    public bool IsAvailable => runtime.Value is not null;

    public bool IsGpuAvailable => runtime.Value is { } detectedRuntime &&
        File.Exists(Path.Combine(Path.GetDirectoryName(detectedRuntime.ExecutablePath)!, "ggml-cuda.dll")) &&
        HardwareCapabilityService.Current.ShouldUseGpu;

    public async Task<string> ImprovePageTextAsync(string pageText, CancellationToken cancellationToken)
    {
        var detectedRuntime = runtime.Value;
        if (string.IsNullOrWhiteSpace(pageText) || detectedRuntime is null)
        {
            return pageText;
        }

        var promptPath = Path.Combine(Path.GetTempPath(), $"bookrescue-ai-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(promptPath, BuildPrompt(pageText), Encoding.UTF8, cancellationToken);
            var output = await RunLlamaAsync(
                detectedRuntime.ExecutablePath,
                detectedRuntime.ModelPath,
                promptPath,
                ShouldUseGpu(detectedRuntime.ExecutablePath),
                cancellationToken);
            var cleaned = CleanModelOutput(output);
            var safe = LooksSafe(pageText, cleaned);
            AppLogService.LogMessage(
                $"Disponible={IsAvailable}; GPU={ShouldUseGpu(detectedRuntime.ExecutablePath)}; OriginalChars={pageText.Length}; CandidateChars={cleaned.Length}; Safe={safe}; Preview={cleaned[..Math.Min(120, cleaned.Length)].Replace(Environment.NewLine, " ")}",
                "Decision IA local");

            return safe ? cleaned : pageText;
        }
        catch (Exception ex)
        {
            AppLogService.Log(ex, "Asistencia IA local");
            return pageText;
        }
        finally
        {
            TryDelete(promptPath);
        }
    }

    private static async Task<string> RunLlamaAsync(
        string llamaCli,
        string modelPath,
        string promptPath,
        bool useGpu,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = llamaCli,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(llamaCli)!
        };

        process.StartInfo.ArgumentList.Add("-m");
        process.StartInfo.ArgumentList.Add(modelPath);
        process.StartInfo.ArgumentList.Add("-f");
        process.StartInfo.ArgumentList.Add(promptPath);
        process.StartInfo.ArgumentList.Add("-n");
        process.StartInfo.ArgumentList.Add("2500");
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add("8192");
        process.StartInfo.ArgumentList.Add("--temp");
        process.StartInfo.ArgumentList.Add("0");
        process.StartInfo.ArgumentList.Add("--top-p");
        process.StartInfo.ArgumentList.Add("0.85");
        process.StartInfo.ArgumentList.Add("--threads");
        process.StartInfo.ArgumentList.Add(GetThreadCount(useGpu).ToString());
        process.StartInfo.ArgumentList.Add("--threads-batch");
        process.StartInfo.ArgumentList.Add(GetThreadCount(useGpu).ToString());
        process.StartInfo.ArgumentList.Add("--poll");
        process.StartInfo.ArgumentList.Add("0");
        process.StartInfo.ArgumentList.Add("--poll-batch");
        process.StartInfo.ArgumentList.Add("0");
        if (useGpu)
        {
            process.StartInfo.ArgumentList.Add("--device");
            process.StartInfo.ArgumentList.Add("CUDA0");
            process.StartInfo.ArgumentList.Add("-ngl");
            process.StartInfo.ArgumentList.Add("auto");
            process.StartInfo.ArgumentList.Add("--fit");
            process.StartInfo.ArgumentList.Add("on");
            process.StartInfo.ArgumentList.Add("--fit-target");
            process.StartInfo.ArgumentList.Add(DedicatedGpuFitTargetMiB.ToString());
            process.StartInfo.ArgumentList.Add("-fa");
            process.StartInfo.ArgumentList.Add("auto");
        }

        process.StartInfo.ArgumentList.Add("--no-display-prompt");
        if (Path.GetFileName(llamaCli).Equals("llama-cli.exe", StringComparison.OrdinalIgnoreCase))
        {
            process.StartInfo.ArgumentList.Add("--single-turn");
        }

        process.StartInfo.ArgumentList.Add("--simple-io");
        process.StartInfo.ArgumentList.Add("--no-warmup");
        process.StartInfo.ArgumentList.Add("--no-perf");

        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                outputBuilder.AppendLine(args.Data);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                errorBuilder.AppendLine(args.Data);
            }
        };

        process.Start();
        TryBoostPriority(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(TimeoutMilliseconds);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"La IA local termino con codigo {process.ExitCode}: {errorBuilder}");
        }

        return outputBuilder.ToString();
    }

    private static string BuildPrompt(string pageText)
    {
        var clipped = pageText.Length > 9000 ? pageText[..9000] : pageText;
        return $$"""
                 <|im_start|>system
                 You are a conservative OCR layout editor for scanned technical book pages.
                 Follow these rules strictly:
                 - Do not invent facts, sentences, symbols, numbers, headings, formulas, or table values.
                 - Keep the original language.
                 - Do not summarize, paraphrase, complete, or rewrite.
                 - Keep the original wording and word order as close as possible.
                 - Prefer using only words already present in the OCR text.
                 - Fix OCR noise only when it is a clear spacing or punctuation problem.
                 - If the OCR ends mid-sentence, preserve that incomplete ending; never finish it yourself.
                 - Rebuild readable paragraphs from broken OCR lines without adding new content.
                 - Preserve titles, subtitles, formulas, numbered items, captions, and table-like rows.
                 - Remove isolated garbage fragments caused by scan artifacts.
                 - Return only the reconstructed page text. No explanations.
                 <|im_end|>
                 <|im_start|>user

                 OCR PAGE TEXT:
                 {{clipped}}
                 <|im_end|>
                 <|im_start|>assistant
                 RECONSTRUCTED PAGE TEXT:
                 """;
    }

    private static string CleanModelOutput(string output)
    {
        var cleaned = output
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Trim();

        cleaned = StripLlamaConsoleNoise(cleaned);

        var marker = "RECONSTRUCTED PAGE TEXT:";
        var markerIndex = cleaned.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0)
        {
            cleaned = cleaned[(markerIndex + marker.Length)..].Trim();
        }

        cleaned = cleaned
            .Replace("<|im_end|>", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("<|im_start|>assistant", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();

        cleaned = EndMarkerRegex().Replace(cleaned, string.Empty).Trim();

        var stopMarkers = new[] { "OCR PAGE TEXT:", "You are restoring", "Follow these rules" };
        foreach (var stopMarker in stopMarkers)
        {
            var index = cleaned.IndexOf(stopMarker, StringComparison.OrdinalIgnoreCase);
            if (index > 0)
            {
                cleaned = cleaned[..index].Trim();
            }
        }

        return cleaned;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\[?\s*(end of text|end of reconstructed text|fin del texto)\s*\]?", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex EndMarkerRegex();

    private static string StripLlamaConsoleNoise(string output)
    {
        var lines = output
            .Split('\n')
            .Select(line => line.TrimEnd())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Where(line => !line.StartsWith("Loading model", StringComparison.OrdinalIgnoreCase))
            .Where(line => !line.StartsWith("build", StringComparison.OrdinalIgnoreCase))
            .Where(line => !line.StartsWith("model", StringComparison.OrdinalIgnoreCase))
            .Where(line => !line.StartsWith("modalities", StringComparison.OrdinalIgnoreCase))
            .Where(line => !line.StartsWith("available commands", StringComparison.OrdinalIgnoreCase))
            .Where(line => !line.StartsWith("[ Prompt:", StringComparison.OrdinalIgnoreCase))
            .Where(line => !line.StartsWith("Exiting", StringComparison.OrdinalIgnoreCase))
            .Where(line => line is not ">" and not "▄▄ ▄▄" and not "██ ██")
            .ToList();

        return string.Join(Environment.NewLine, lines).Trim();
    }

    private static bool LooksSafe(string original, string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var originalAlphaNumeric = CountAlphaNumeric(original);
        var candidateAlphaNumeric = CountAlphaNumeric(candidate);
        if (originalAlphaNumeric < 120)
        {
            return candidateAlphaNumeric >= originalAlphaNumeric * 0.35;
        }

        if (candidateAlphaNumeric < originalAlphaNumeric * 0.45)
        {
            return false;
        }

        if (candidateAlphaNumeric > originalAlphaNumeric * 1.85)
        {
            return false;
        }

        if (EndsIncomplete(original) && !EndsIncomplete(candidate))
        {
            return false;
        }

        if (IntroducesTooManyNewWords(original, candidate))
        {
            return false;
        }

        return true;
    }

    private static int CountAlphaNumeric(string text)
    {
        return text.Count(char.IsLetterOrDigit);
    }

    private static bool EndsIncomplete(string text)
    {
        var trimmed = text.Trim();
        return trimmed.EndsWith(',') || trimmed.EndsWith('-') || trimmed.EndsWith(':');
    }

    private static bool IntroducesTooManyNewWords(string original, string candidate)
    {
        var originalWords = SignificantWords(original).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidateWords = SignificantWords(candidate).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (candidateWords.Count < 30)
        {
            return false;
        }

        var newWordCount = candidateWords.Count(word => !originalWords.Contains(word));
        return newWordCount / (double)candidateWords.Count > 0.075;
    }

    private static IEnumerable<string> SignificantWords(string text)
    {
        foreach (var rawToken in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = new string(rawToken.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            if (token.Length >= 4)
            {
                yield return token;
            }
        }
    }

    private static LocalAiRuntime? FindRuntime()
    {
        foreach (var runtimeFolder in GetRuntimeCandidates())
        {
            var llamaCli = File.Exists(Path.Combine(runtimeFolder, "llama-completion.exe"))
                ? Path.Combine(runtimeFolder, "llama-completion.exe")
                : Path.Combine(runtimeFolder, "llama-cli.exe");
            var modelPath = Directory.Exists(runtimeFolder)
                ? Directory.GetFiles(runtimeFolder, "*.gguf").OrderBy(path => path).FirstOrDefault() ?? string.Empty
                : string.Empty;

            if (File.Exists(llamaCli) && File.Exists(modelPath))
            {
                return new LocalAiRuntime(llamaCli, modelPath);
            }
        }

        return null;
    }

    private static bool IsGpuRuntime(string executablePath)
    {
        return File.Exists(Path.Combine(Path.GetDirectoryName(executablePath)!, "ggml-cuda.dll"));
    }

    private static bool ShouldUseGpu(string executablePath)
    {
        return IsGpuRuntime(executablePath) && HardwareCapabilityService.Current.ShouldUseGpu;
    }

    private static int GetThreadCount(bool useGpu)
    {
        var available = Math.Max(2, HardwareCapabilityService.Current.LogicalProcessors - 1);
        return useGpu
            ? Math.Clamp(available / 2, 2, 6)
            : Math.Clamp(available, 4, 12);
    }

    private static IEnumerable<string> GetRuntimeCandidates()
    {
        var baseDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var current = baseDirectory;
        for (var depth = 0; current is not null && depth < 8; depth++)
        {
            yield return Path.Combine(current.FullName, "runtime", "ai");
            current = current.Parent;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static void TryBoostPriority(Process process)
    {
        try
        {
            process.PriorityClass = ProcessPriorityClass.AboveNormal;
        }
        catch
        {
        }
    }

    private sealed record LocalAiRuntime(string ExecutablePath, string ModelPath);
}
