using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using BookRescue.App.Models;
using BookRescue.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Ookii.Dialogs.Wpf;

namespace BookRescue.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly TessdataBootstrapper _tessdata = new();
    private readonly OcrExtractionService _ocr;
    private readonly LanguageDetectionService _languageDetection = new();
    private readonly LibreTranslateService _translation = new();
    private readonly UpdateService _updateService = new();
    private readonly LocalLibraryStore _libraryStore = new();
    private readonly LocalAiDocumentAssistant _localAi = new();
    private readonly DocumentAiStructureService _documentAi = new();
    private readonly BookConversionPipeline _pipeline;
    private readonly DispatcherTimer _timingTimer;
    private CancellationTokenSource? _rescueCancellation;
    private bool hardwareWarningShown;

    public MainWindowViewModel()
    {
        _ocr = new OcrExtractionService(_tessdata);
        _pipeline = new BookConversionPipeline(
            _tessdata,
            new ImageRestorationService(),
            _ocr,
            _languageDetection,
            _translation,
            new PdfCloneWriter(),
            new DocxCloneWriter(),
            new EpubCloneWriter(),
            new CsvOutputWriter(),
            new ImageRegionRescueService(),
            _localAi,
            _documentAi);

        OutputFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BookRescue");
        StatusMessage = HardwareProfile.IsCapable
            ? "Listo. Recursos preparados para reconstrucción local."
            : HardwareProfile.MinimumHardwareMessage;

        _timingTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timingTimer.Tick += (_, _) => UpdateActiveBookTiming();
        _timingTimer.Start();
        _ = LoadPersistentLibraryAsync();
    }

    public ObservableCollection<PendingBookItem> PendingBooks { get; } = new();

    public ObservableCollection<LibraryItemViewModel> LibraryItems { get; } = new();

    public IReadOnlyList<string> OutputLanguageOptions { get; } = new[] { "es", "en" };

    public IReadOnlyList<string> ReconstructionModeOptions { get; } =
    [
        "Extraer solo texto",
        "Texto y fotos",
        "Reconstrucción perfecta pesada"
    ];

    public HardwareProfile HardwareProfile { get; } = HardwareCapabilityService.Current;

    [ObservableProperty]
    private PendingBookItem? selectedPendingBook;

    [ObservableProperty]
    private LibraryItemViewModel? selectedLibraryItem;

    [ObservableProperty]
    private string outputFolder;

    [ObservableProperty]
    private string ocrLanguages = "eng";

    [ObservableProperty]
    private string outputLanguage = "es";

    [ObservableProperty]
    private bool enableTranslation;

    [ObservableProperty]
    private bool outputPdf = true;

    [ObservableProperty]
    private bool outputWord = true;

    [ObservableProperty]
    private bool outputEpub;

    [ObservableProperty]
    private bool outputCsv;

    [ObservableProperty]
    private bool useLocalAiAssistance;

    [ObservableProperty]
    private string selectedReconstructionMode = "Texto y fotos";

    [ObservableProperty]
    private string translationEndpoint = "http://localhost:5000";

    [ObservableProperty]
    private string translationApiKey = string.Empty;

    [ObservableProperty]
    private string statusMessage = "Listo.";

    [ObservableProperty]
    private double globalProgress;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private bool isAiProcessing;

    public string CreditsText => "Créditos: Ernesto Pernett- Ingeniero Mecánico · Codex";

    public string ReconstructionModeHelp => SelectedReconstructionMode switch
    {
        "Reconstrucción perfecta pesada" => "Modo lento y detallado. Intenta conservar cada detalle visual del libro: fondos, imágenes, márgenes, decoración, tablas, texto vertical y composición original.",
        "Texto y fotos" => "Extrae texto y fotos, organiza el documento con fuentes predeterminadas, tablas, fórmulas sencillas y figuras rescatadas.",
        _ => "Extrae solamente texto limpio y organizado, sin insertar fotos ni imágenes en el documento final."
    };

    public string ReconstructionModeWarning => SelectedReconstructionMode == "Reconstrucción perfecta pesada"
        ? "Este modo puede tardar mucho más y usar más CPU/GPU."
        : string.Empty;

    public bool IsUsingLocalAi => IsAiProcessing && SelectedReconstructionMode == "Reconstrucción perfecta pesada" && (_localAi.IsAvailable || _documentAi.IsAvailable);

    public string AiIndicatorText
    {
        get
        {
            if (IsUsingLocalAi)
            {
                return "IA trabajando";
            }

            if (SelectedReconstructionMode != "Reconstrucción perfecta pesada")
            {
                return "Modo estándar";
            }

            return _localAi.IsAvailable || _documentAi.IsAvailable
                ? "Modo pesado listo"
                : "Modo pesado";
        }
    }

    public Brush AiIndicatorBrush => IsUsingLocalAi
        ? Brushes.LimeGreen
        : SelectedReconstructionMode != "Reconstrucción perfecta pesada"
            ? Brushes.LightGray
            : _localAi.IsAvailable || _documentAi.IsAvailable
            ? Brushes.Goldenrod
            : Brushes.LightGray;

    [RelayCommand]
    private void AddBooks()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Seleccionar libros, PDFs o imágenes",
            Multiselect = true,
            Filter = "Libros e imágenes|*.pdf;*.jpg;*.jpeg;*.png;*.tif;*.tiff;*.bmp;*.webp|PDF|*.pdf|Imágenes|*.jpg;*.jpeg;*.png;*.tif;*.tiff;*.bmp;*.webp|Todos|*.*"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        foreach (var fileName in dialog.FileNames)
        {
            if (PendingBooks.Any(x => x.SourcePath.Equals(fileName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            PendingBooks.Add(new PendingBookItem(fileName));
        }

        StatusMessage = $"{PendingBooks.Count} archivo(s) en cola.";
    }

    [RelayCommand]
    private void BrowseOutputFolder()
    {
        var dialog = new VistaFolderBrowserDialog
        {
            Description = "Selecciona dónde guardar los libros reconstruidos",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(OutputFolder) ? OutputFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };

        if (dialog.ShowDialog() == true)
        {
            OutputFolder = dialog.SelectedPath;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartRescue))]
    private async Task StartRescueAsync()
    {
        if (!HardwareProfile.IsCapable)
        {
            ShowHardwareWarningIfNeeded(force: true);
            return;
        }

        if (PendingBooks.Count == 0)
        {
            StatusMessage = "Agrega al menos un libro o imagen.";
            return;
        }

        IsBusy = true;
        IsAiProcessing = false;
        GlobalProgress = 0;
        _rescueCancellation?.Dispose();
        _rescueCancellation = new CancellationTokenSource();

        try
        {
            var total = PendingBooks.Count;
            for (var index = 0; index < PendingBooks.Count; index++)
            {
                var item = PendingBooks[index];
                item.Status = "Procesando";
                item.Progress = 0;
                item.Details = "Preparando...";
                item.MarkStarted();

                var progress = new Progress<ConversionProgressUpdate>(update =>
                {
                    IsAiProcessing =
                        update.Message.StartsWith("Organizando contenido", StringComparison.OrdinalIgnoreCase) ||
                        update.Message.StartsWith("Analizando estructura", StringComparison.OrdinalIgnoreCase) ||
                        update.Message.StartsWith("Estructura inteligente", StringComparison.OrdinalIgnoreCase);
                    item.Progress = update.Percent;
                    item.Details = update.Message;
                    item.UpdateTiming();
                    GlobalProgress = ((index * 100d) + update.Percent) / total;
                    StatusMessage = $"{item.FileName}: {update.Message}";
                });

                try
                {
                    var sourcePath = item.SourcePath;
                    var outputFolder = OutputFolder;
                    var ocrLanguages = OcrLanguages;
                    var outputLanguage = OutputLanguage;
                    var enableTranslation = EnableTranslation;
                    var outputProfiles = CreateOutputProfileOptions();
                    var useLocalAiAssistance = outputProfiles.ReconstructionMode == OutputReconstructionMode.PerfectHeavy;
                    var translationEndpoint = TranslationEndpoint;
                    var translationApiKey = TranslationApiKey;

                    var result = await Task.Run(() => _pipeline.ConvertAsync(
                        sourcePath,
                        outputFolder,
                        ocrLanguages,
                        outputLanguage,
                        enableTranslation,
                        useLocalAiAssistance,
                        outputProfiles,
                        translationEndpoint,
                        translationApiKey,
                        progress,
                        _rescueCancellation.Token));

                    item.Status = "Completado";
                    item.Details = result.TranslationApplied ? "Traducido y guardado." : "Guardado.";
                    item.MarkCompleted();
                    result.LibraryRecord.Status = item.Status;
                    await _libraryStore.SaveOrUpdateAsync(result.LibraryRecord, _rescueCancellation.Token);
                    LibraryItems.Insert(0, new LibraryItemViewModel(result.LibraryRecord));
                }
                catch (OperationCanceledException)
                {
                    item.Status = "Cancelado";
                    item.Details = "Proceso cancelado.";
                    item.MarkStopped("Cancelado");
                    StatusMessage = "Proceso cancelado.";
                    break;
                }
                catch (Exception ex)
                {
                    AppLogService.Log(ex, $"Error convirtiendo {item.SourcePath}");
                    item.Status = "Error";
                    item.Details = ex.Message;
                    item.MarkStopped("Error");
                }
            }

            if (_rescueCancellation?.IsCancellationRequested != true)
            {
                StatusMessage = "Proceso terminado.";
            }
        }
        finally
        {
            IsAiProcessing = false;
            IsBusy = false;
            _rescueCancellation?.Dispose();
            _rescueCancellation = null;
            StartRescueCommand.NotifyCanExecuteChanged();
            CancelRescueCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanStartRescue()
    {
        return !IsBusy && HardwareProfile.IsCapable && (OutputPdf || OutputWord || OutputEpub || OutputCsv);
    }

    [RelayCommand(CanExecute = nameof(CanCancelRescue))]
    private void CancelRescue()
    {
        if (!IsBusy)
        {
            return;
        }

        StatusMessage = "Cancelando proceso...";
        _rescueCancellation?.Cancel();
        CancelRescueCommand.NotifyCanExecuteChanged();
    }

    private bool CanCancelRescue()
    {
        return IsBusy && _rescueCancellation is not null && !_rescueCancellation.IsCancellationRequested;
    }

    public void ShowHardwareWarningIfNeeded(bool force = false)
    {
        if (HardwareProfile.IsCapable || (hardwareWarningShown && !force))
        {
            return;
        }

        hardwareWarningShown = true;
        var details = string.IsNullOrWhiteSpace(HardwareProfile.FailureReason)
            ? string.Empty
            : $"{Environment.NewLine}{Environment.NewLine}{HardwareProfile.FailureReason}";
        MessageBox.Show(
            $"{HardwareProfile.MinimumHardwareMessage}{details}",
            "BookRescue",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    [RelayCommand]
    private void RemoveSelectedBook()
    {
        if (SelectedPendingBook is not null)
        {
            PendingBooks.Remove(SelectedPendingBook);
        }
    }

    [RelayCommand]
    private void ClearCompleted()
    {
        foreach (var item in PendingBooks.Where(x => x.Status is "Completado" or "Error" or "Cancelado").ToList())
        {
            PendingBooks.Remove(item);
        }
    }

    [RelayCommand]
    private void ClearSessionLibrary()
    {
        LibraryItems.Clear();
        StatusMessage = "Vista de biblioteca limpia. El historial persistente se conserva.";
    }

    [RelayCommand]
    private void OpenSelectedPdf()
    {
        if (SelectedLibraryItem is null)
        {
            return;
        }

        OpenPath(SelectedLibraryItem.ReconstructedPdfPath);
    }

    [RelayCommand]
    private void OpenSelectedDocx()
    {
        if (SelectedLibraryItem is null)
        {
            return;
        }

        OpenPath(SelectedLibraryItem.ReconstructedDocxPath);
    }

    [RelayCommand]
    private void OpenSelectedOutputFolder()
    {
        if (SelectedLibraryItem is null)
        {
            return;
        }

        OpenPath(SelectedLibraryItem.OutputFolder);
    }

    [RelayCommand]
    private void OpenSelectedEpub()
    {
        if (SelectedLibraryItem is null)
        {
            return;
        }

        OpenPath(SelectedLibraryItem.ReconstructedEpubPath);
    }

    [RelayCommand]
    private void OpenSelectedCsv()
    {
        if (SelectedLibraryItem is null)
        {
            return;
        }

        OpenPath(SelectedLibraryItem.CsvPath);
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        IsBusy = true;
        try
        {
            StatusMessage = "Buscando actualizaciones...";
            StatusMessage = await _updateService.CheckForUpdatesAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void SetOutputLanguage(string language)
    {
        if (!string.IsNullOrWhiteSpace(language))
        {
            OutputLanguage = language;
            StatusMessage = $"Idioma de salida: {language}.";
        }
    }

    [RelayCommand]
    private void ShowCredits()
    {
        MessageBox.Show(
            "BookRescue\n\nErnesto Pernett- Ingeniero Mecánico\nCodex - asistente de desarrollo",
            "Créditos",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private OutputProfileOptions CreateOutputProfileOptions()
    {
        return new OutputProfileOptions
        {
            ReconstructionMode = SelectedReconstructionMode switch
            {
                "Reconstrucción perfecta pesada" => OutputReconstructionMode.PerfectHeavy,
                "Texto y fotos" => OutputReconstructionMode.TextAndPhotos,
                _ => OutputReconstructionMode.TextOnly
            },
            MaximumQuality = SelectedReconstructionMode == "Reconstrucción perfecta pesada",
            Pdf = OutputPdf,
            Word = OutputWord,
            Epub = OutputEpub,
            Csv = OutputCsv
        };
    }

    private async Task LoadPersistentLibraryAsync()
    {
        try
        {
            var records = await _libraryStore.LoadAsync();
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                LibraryItems.Clear();
                foreach (var record in records)
                {
                    LibraryItems.Add(new LibraryItemViewModel(record));
                }
            });
        }
        catch (Exception ex)
        {
            AppLogService.Log(ex, "Carga de biblioteca persistente");
        }
    }

    private static void OpenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
        {
            MessageBox.Show("La ruta ya no existe.", "BookRescue", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true
        });
    }

    partial void OnIsBusyChanged(bool value)
    {
        StartRescueCommand.NotifyCanExecuteChanged();
        CancelRescueCommand.NotifyCanExecuteChanged();
    }

    partial void OnUseLocalAiAssistanceChanged(bool value)
    {
        OnPropertyChanged(nameof(IsUsingLocalAi));
        OnPropertyChanged(nameof(AiIndicatorText));
        OnPropertyChanged(nameof(AiIndicatorBrush));
    }

    partial void OnSelectedReconstructionModeChanged(string value)
    {
        OnPropertyChanged(nameof(ReconstructionModeHelp));
        OnPropertyChanged(nameof(ReconstructionModeWarning));
        OnPropertyChanged(nameof(IsUsingLocalAi));
        OnPropertyChanged(nameof(AiIndicatorText));
        OnPropertyChanged(nameof(AiIndicatorBrush));
    }

    partial void OnIsAiProcessingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsUsingLocalAi));
        OnPropertyChanged(nameof(AiIndicatorText));
        OnPropertyChanged(nameof(AiIndicatorBrush));
    }

    partial void OnOutputPdfChanged(bool value)
    {
        StartRescueCommand.NotifyCanExecuteChanged();
    }

    partial void OnOutputWordChanged(bool value)
    {
        StartRescueCommand.NotifyCanExecuteChanged();
    }

    partial void OnOutputEpubChanged(bool value)
    {
        StartRescueCommand.NotifyCanExecuteChanged();
    }

    partial void OnOutputCsvChanged(bool value)
    {
        StartRescueCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        _timingTimer.Stop();
        _rescueCancellation?.Cancel();
        _rescueCancellation?.Dispose();
        _translation.Dispose();
        _languageDetection.Dispose();
        _ocr.Dispose();
        _tessdata.Dispose();
        _updateService.Dispose();
    }

    private void UpdateActiveBookTiming()
    {
        foreach (var item in PendingBooks.Where(x => x.Status == "Procesando"))
        {
            item.UpdateTiming();
        }
    }
}
