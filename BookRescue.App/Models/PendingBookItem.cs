using CommunityToolkit.Mvvm.ComponentModel;

namespace BookRescue.App.Models;

public partial class PendingBookItem : ObservableObject
{
    public PendingBookItem(string sourcePath)
    {
        SourcePath = sourcePath;
        FileName = Path.GetFileName(sourcePath);
    }

    public string SourcePath { get; }

    public string FileName { get; }

    [ObservableProperty]
    private string status = "Pendiente";

    [ObservableProperty]
    private string details = string.Empty;

    [ObservableProperty]
    private double progress;

    [ObservableProperty]
    private string elapsedText = "00:00:00";

    [ObservableProperty]
    private string estimatedText = "Por calcular";

    private DateTimeOffset? startedAtUtc;

    private DateTimeOffset? completedAtUtc;

    private string? fixedEstimatedText;

    public void MarkStarted()
    {
        startedAtUtc = DateTimeOffset.UtcNow;
        completedAtUtc = null;
        fixedEstimatedText = null;
        UpdateTiming();
    }

    public void MarkCompleted()
    {
        completedAtUtc = DateTimeOffset.UtcNow;
        fixedEstimatedText = "Completado";
        Progress = 100;
        UpdateTiming();
    }

    public void MarkStopped(string finalEstimate)
    {
        completedAtUtc = DateTimeOffset.UtcNow;
        fixedEstimatedText = finalEstimate;
        UpdateTiming();
    }

    public void UpdateTiming()
    {
        if (startedAtUtc is null)
        {
            ElapsedText = "00:00:00";
            EstimatedText = "Por calcular";
            return;
        }

        var now = completedAtUtc ?? DateTimeOffset.UtcNow;
        var elapsed = now - startedAtUtc.Value;
        ElapsedText = FormatDuration(elapsed);

        if (!string.IsNullOrWhiteSpace(fixedEstimatedText))
        {
            EstimatedText = fixedEstimatedText;
            return;
        }

        if (Progress <= 1 || elapsed < TimeSpan.FromSeconds(45))
        {
            EstimatedText = "Calculando";
            return;
        }

        if (Progress >= 100)
        {
            EstimatedText = "Completado";
            return;
        }

        var estimatedTotal = TimeSpan.FromTicks((long)(elapsed.Ticks / Math.Clamp(Progress / 100d, 0.01d, 1d)));
        var remaining = estimatedTotal - elapsed;
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        EstimatedText = $"{FormatDuration(remaining)} restantes";
    }

    private static string FormatDuration(TimeSpan value)
    {
        return $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}";
    }
}
