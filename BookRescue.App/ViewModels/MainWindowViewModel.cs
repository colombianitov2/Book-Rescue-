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
    private const string DefaultOutputFolder = @"D:\Pruebas con apps";

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

        OutputFolder = DefaultOutputFolder;
        StatusMessage = HardwareProfile.IsCapable
            ? "Listo. Recursos preparados para reconstrucción local."
            : HardwareProfile.MinimumHardwareMessage;
        if (!TryEnsureOutputFolderExists(OutputFolder, out var outputFolderError))
        {
            StatusMessage = $"{StatusMessage} No se pudo preparar el destino predeterminado: {outputFolderError}";
        }

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
        "Solo tablas y gráficos",
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

    [ObservableProperty]
    private bool isPaused;

    public string CreditsText => "Créditos: Ernesto Pernett- Ingeniero Mecánico · Codex";

    public string PauseResumeButtonText => IsPaused ? "Continuar" : "Pausar";

    public string PauseResumeToolTip => IsPaused
        ? "Continúa procesando los archivos pendientes."
        : "Pausa la cola de forma segura antes del siguiente archivo o etapa cooperativa.";

    public string ReconstructionModeHelp => SelectedReconstructionMode switch
    {
        "Reconstrucción perfecta pesada" => "Modo lento y detallado. Analiza regiones, tablas, fórmulas, figuras y orden de lectura para construir un documento blanco, limpio y coherente.",
        "Solo tablas y gráficos" => "Extrae solamente tablas, gráficos, diagramas, fórmulas como imagen y otros elementos no textuales. Omite el texto plano del documento final.",
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

        if (!TryEnsureOutputFolderExists(OutputFolder, out var outputFolderError))
        {
            StatusMessage = $"No se pudo preparar la carpeta destino: {outputFolderError}";
            return;
        }

        IsBusy = true;
        IsAiProcessing = false;
        IsPaused = false;
        RestorePausedItemsToPending();
        GlobalProgress = 0;
        _rescueCancellation?.Dispose();
        _rescueCancellation = new CancellationTokenSource();
        var cancellationToken = _rescueCancellation.Token;
        TogglePauseResumeCommand.NotifyCanExecuteChanged();

        try
        {
            var total = PendingBooks.Count;
            for (var index = 0; index < PendingBooks.Count; index++)
            {
                await WaitWhilePausedAsync(cancellationToken);

                if (cancellationToken.IsCancellationRequested)
                {
                    MarkRemainingPendingItemsCancelled(index);
                    StatusMessage = "Proceso cancelado. La cola restante quedó cancelada.";
                    break;
                }

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
                        cancellationToken));

                    item.Status = "Completado";
                    item.Details = result.TranslationApplied ? "Traducido y guardado." : "Guardado.";
                    item.MarkCompleted();
                    result.LibraryRecord.Status = item.Status;
                    await _libraryStore.SaveOrUpdateAsync(result.LibraryRecord, cancellationToken);
                    LibraryItems.Insert(0, new LibraryItemViewModel(result.LibraryRecord));
                }
                catch (OperationCanceledException)
                {
                    item.Status = "Cancelado";
                    item.Details = "Proceso cancelado. Algunas etapas nativas pueden terminar antes de detenerse.";
                    item.MarkStopped("Cancelado");
                    MarkRemainingPendingItemsCancelled(index + 1);
                    StatusMessage = "Proceso cancelado. La cola restante quedó cancelada.";
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
            IsPaused = false;
            IsBusy = false;
            _rescueCancellation?.Dispose();
            _rescueCancellation = null;
            StartRescueCommand.NotifyCanExecuteChanged();
            CancelRescueCommand.NotifyCanExecuteChanged();
            TogglePauseResumeCommand.NotifyCanExecuteChanged();
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

        StatusMessage = "Cancelando proceso... se detendrá al finalizar la etapa actual si el motor nativo no puede interrumpirse.";
        _rescueCancellation?.Cancel();
        CancelRescueCommand.NotifyCanExecuteChanged();
    }

    private bool CanCancelRescue()
    {
        return IsBusy && _rescueCancellation is not null && !_rescueCancellation.IsCancellationRequested;
    }

    [RelayCommand(CanExecute = nameof(CanTogglePauseResume))]
    private void TogglePauseResume()
    {
        if (!IsBusy)
        {
            return;
        }

        IsPaused = !IsPaused;
        if (IsPaused)
        {
            MarkPendingItemsPaused();
            StatusMessage = "Pausa solicitada. La conversión activa terminará su etapa nativa actual antes de pausar la cola.";
        }
        else
        {
            RestorePausedItemsToPending();
            StatusMessage = "Continuando cola de conversión...";
        }

        TogglePauseResumeCommand.NotifyCanExecuteChanged();
    }

    private bool CanTogglePauseResume()
    {
        return IsBusy;
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

    [RelayCommand(CanExecute = nameof(CanClearLibrary))]
    private async Task ClearLibraryAsync()
    {
        if (LibraryItems.Count == 0)
        {
            StatusMessage = "La biblioteca visible ya está vacía.";
            return;
        }

        var confirmation = MessageBox.Show(
            "Esto solo limpiará la lista de la biblioteca. No borrará los archivos generados. ¿Continuar?",
            "BookRescue",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        await _libraryStore.ClearAsync();
        LibraryItems.Clear();
        SelectedLibraryItem = null;
        StatusMessage = "Biblioteca limpia. No se eliminaron archivos generados.";
    }

    private bool CanClearLibrary()
    {
        return !IsBusy && !IsPaused;
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
        if (SelectedLibraryItem is { Status: "Completado" } selected &&
            !string.IsNullOrWhiteSpace(selected.OutputFolder))
        {
            if (TryOpenPath(selected.OutputFolder, "La carpeta de salida del libro seleccionado ya no existe."))
            {
                StatusMessage = $"Carpeta de salida abierta: {selected.OutputFolder}";
            }

            return;
        }

        OpenOutputFolderRoot();
    }

    [RelayCommand]
    private void OpenOutputFolderRoot()
    {
        if (!TryEnsureOutputFolderExists(OutputFolder, out var outputFolderError))
        {
            MessageBox.Show(
                $"No se pudo preparar la carpeta destino: {outputFolderError}",
                "BookRescue",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (TryOpenPath(OutputFolder, "La carpeta destino no existe. Selecciona o crea una carpeta destino válida."))
        {
            StatusMessage = $"Destino abierto: {OutputFolder}";
        }
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
                "Solo tablas y gráficos" => OutputReconstructionMode.VisualElementsOnly,
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
        TryOpenPath(path, "La ruta ya no existe.");
    }

    private static bool TryOpenPath(string path, string missingMessage)
    {
        if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
        {
            MessageBox.Show(missingMessage, "BookRescue", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            AppLogService.Log(ex, $"Abrir ruta: {path}");
            MessageBox.Show("No se pudo abrir la ruta seleccionada.", "BookRescue", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private static bool TryEnsureOutputFolderExists(string path, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "la ruta está vacía.";
            return false;
        }

        try
        {
            Directory.CreateDirectory(path);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            AppLogService.Log(ex, $"Preparar carpeta destino: {path}");
            return false;
        }
    }

    private void MarkRemainingPendingItemsCancelled(int startIndex)
    {
        for (var i = startIndex; i < PendingBooks.Count; i++)
        {
            var pending = PendingBooks[i];
            if (pending.Status is not ("Pendiente" or "Pausado"))
            {
                continue;
            }

            pending.Status = "Cancelado";
            pending.Details = "No iniciado por cancelación.";
            pending.MarkStopped("Cancelado");
        }
    }

    private async Task WaitWhilePausedAsync(CancellationToken cancellationToken)
    {
        if (!IsPaused)
        {
            return;
        }

        MarkPendingItemsPaused();
        StatusMessage = "Pausado. Presiona Continuar para reanudar la cola.";
        while (IsPaused)
        {
            await Task.Delay(300, cancellationToken);
        }

        RestorePausedItemsToPending();
        StatusMessage = "Continuando cola de conversión...";
    }

    private void MarkPendingItemsPaused()
    {
        foreach (var pending in PendingBooks.Where(x => x.Status == "Pendiente"))
        {
            pending.Status = "Pausado";
            pending.Details = "En espera. Presiona Continuar para procesar.";
        }
    }

    private void RestorePausedItemsToPending()
    {
        foreach (var paused in PendingBooks.Where(x => x.Status == "Pausado"))
        {
            paused.Status = "Pendiente";
            paused.Details = string.Empty;
        }
    }

    partial void OnIsBusyChanged(bool value)
    {
        StartRescueCommand.NotifyCanExecuteChanged();
        CancelRescueCommand.NotifyCanExecuteChanged();
        TogglePauseResumeCommand.NotifyCanExecuteChanged();
        ClearLibraryCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsPausedChanged(bool value)
    {
        OnPropertyChanged(nameof(PauseResumeButtonText));
        OnPropertyChanged(nameof(PauseResumeToolTip));
        TogglePauseResumeCommand.NotifyCanExecuteChanged();
        ClearLibraryCommand.NotifyCanExecuteChanged();
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
