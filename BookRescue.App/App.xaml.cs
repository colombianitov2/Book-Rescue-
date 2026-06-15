using System.Windows;
using BookRescue.App.Models;
using BookRescue.App.Services;

namespace BookRescue.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        ConfigureCpuLimits();

        DispatcherUnhandledException += (_, args) =>
        {
            AppLogService.Log(args.Exception, "Excepción de interfaz");
            MessageBox.Show(
                "BookRescue encontró un problema y guardó un registro para revisarlo.",
                "BookRescue",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                AppLogService.Log(exception, "Excepción global");
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLogService.Log(args.Exception, "Excepción de tarea");
            args.SetObserved();
        };

        base.OnStartup(e);

        if (TryReadCliConversion(e.Args, out var inputPath, out var outputFolder, out var ocrLanguages, out var outputLanguage, out var useLocalAiAssistance, out var outputProfiles, out var enableTranslation))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                await RunCliConversionAsync(inputPath, outputFolder, ocrLanguages, outputLanguage, useLocalAiAssistance, outputProfiles, enableTranslation);
                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                Environment.ExitCode = 1;
                await WriteCliErrorAsync(outputFolder, ex);
            }

            Shutdown();
            return;
        }

        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    private static void ConfigureCpuLimits()
    {
        var profile = HardwareCapabilityService.Current;
        var threadLimit = profile.ShouldUseGpu ? "3" : Math.Min(6, Math.Max(4, Environment.ProcessorCount - 2)).ToString();
        Environment.SetEnvironmentVariable("OMP_THREAD_LIMIT", threadLimit);
        Environment.SetEnvironmentVariable("OMP_NUM_THREADS", threadLimit);
    }

    private static bool TryReadCliConversion(
        string[] args,
        out string inputPath,
        out string outputFolder,
        out string ocrLanguages,
        out string outputLanguage,
        out bool useLocalAiAssistance,
        out OutputProfileOptions outputProfiles,
        out bool enableTranslation)
    {
        inputPath = string.Empty;
        outputFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BookRescue");
        ocrLanguages = "eng";
        outputLanguage = "es";
        useLocalAiAssistance = false;
        outputProfiles = new OutputProfileOptions { Pdf = true, Word = true };
        enableTranslation = false;

        if (args.Length == 0 || !args.Contains("--convert", StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        inputPath = ReadArg(args, "--convert") ?? string.Empty;
        outputFolder = ReadArg(args, "--out") ?? outputFolder;
        ocrLanguages = ReadArg(args, "--ocr") ?? ocrLanguages;
        outputLanguage = ReadArg(args, "--lang") ?? outputLanguage;
        var reconstructionMode = ParseReconstructionMode(ReadArg(args, "--mode") ?? ReadArg(args, "--reconstruction-mode"));
        useLocalAiAssistance = (reconstructionMode == OutputReconstructionMode.PerfectHeavy || args.Contains("--ai", StringComparer.OrdinalIgnoreCase))
            && !args.Contains("--no-ai", StringComparer.OrdinalIgnoreCase);
        outputProfiles = ParseOutputProfiles(ReadArg(args, "--formats"), reconstructionMode);
        enableTranslation = args.Contains("--translate", StringComparer.OrdinalIgnoreCase)
            && !args.Contains("--no-translate", StringComparer.OrdinalIgnoreCase);

        return !string.IsNullOrWhiteSpace(inputPath);
    }

    private static string? ReadArg(string[] args, string name)
    {
        var index = Array.FindIndex(args, item => item.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length)
        {
            return null;
        }

        return args[index + 1];
    }

    private static async Task RunCliConversionAsync(string inputPath, string outputFolder, string ocrLanguages, string outputLanguage, bool useLocalAiAssistance, OutputProfileOptions outputProfiles, bool enableTranslation)
    {
        using var tessdata = new TessdataBootstrapper();
        using var languageDetection = new LanguageDetectionService();
        using var translation = new LibreTranslateService();
        using var ocr = new OcrExtractionService(tessdata);

        var pipeline = new BookConversionPipeline(
            tessdata,
            new ImageRestorationService(),
            ocr,
            languageDetection,
            translation,
            new PdfCloneWriter(),
            new DocxCloneWriter(),
            new EpubCloneWriter(),
            new CsvOutputWriter(),
            new ImageRegionRescueService(),
            new LocalAiDocumentAssistant(),
            new DocumentAiStructureService());

        await pipeline.ConvertAsync(
            inputPath,
            outputFolder,
            ocrLanguages,
            outputLanguage,
            enableTranslation: enableTranslation,
            useLocalAiAssistance: useLocalAiAssistance,
            outputProfiles: outputProfiles,
            translationEndpoint: "http://localhost:5000",
            translationApiKey: null);
    }

    private static OutputProfileOptions ParseOutputProfiles(string? value, OutputReconstructionMode reconstructionMode)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new OutputProfileOptions
            {
                ReconstructionMode = reconstructionMode,
                MaximumQuality = reconstructionMode == OutputReconstructionMode.PerfectHeavy,
                Pdf = true,
                Word = true
            };
        }

        var formats = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new OutputProfileOptions
        {
            ReconstructionMode = reconstructionMode,
            MaximumQuality = reconstructionMode == OutputReconstructionMode.PerfectHeavy,
            Pdf = formats.Contains("pdf"),
            Word = formats.Contains("word") || formats.Contains("docx"),
            Epub = formats.Contains("epub"),
            Csv = formats.Contains("csv") || formats.Contains("csj")
        };
    }

    private static OutputReconstructionMode ParseReconstructionMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return OutputReconstructionMode.TextAndPhotos;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "heavy" or "perfect" or "perfect-heavy" or "perfectheavy" or "reconstruccion-perfecta-pesada" => OutputReconstructionMode.PerfectHeavy,
            "text" or "text-only" or "texto" or "solo-texto" => OutputReconstructionMode.TextOnly,
            "photos" or "text-photos" or "text-and-photos" or "texto-y-fotos" => OutputReconstructionMode.TextAndPhotos,
            _ => OutputReconstructionMode.TextAndPhotos
        };
    }

    private static async Task WriteCliErrorAsync(string outputFolder, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(outputFolder);
            var logPath = Path.Combine(outputFolder, "bookrescue-cli-error.log");
            await File.WriteAllTextAsync(logPath, exception.ToString());
        }
        catch
        {
        }
    }
}
