using System.Text.Json;
using System.Text.Json.Serialization;
using BookRescue.App.Models;

namespace BookRescue.App.Services;

public sealed record ReconstructionPageReport(
    int PageNumber,
    int RegionCount,
    string CleanBackgroundColor,
    string ReconstructedPreviewPath,
    int BackgroundRegions,
    int DecorationRegions,
    int TextRegions,
    int ImageRegions,
    int TableRegions,
    int CrackRegions,
    int StainRegions,
    int NoiseRegions,
    int ProtectedDamageRegions,
    int VisibleTextRegions,
    int RegionalVisualFallbacks,
    int DiscardedDamageRegions,
    int DecorativeRegions,
    int DiagramRegions,
    int GraphicalElementRegions,
    int ProtectedDiagramLineRegions,
    int GroupedNoiseRegions,
    IReadOnlyList<string> RegionalVisualCropPaths,
    IReadOnlyList<string> HeadingOcrCandidates,
    string DamageMaskPath,
    string ProtectedContentMaskPath,
    string CleanBackgroundPath,
    string ReconstructedCanvasPath,
    string SideBySideOriginalVsReconstructedPath,
    string RegionOverlayPath,
    double AverageOcrConfidence,
    double ReconstructedSimilarity,
    double ReconstructedDifference,
    IReadOnlyList<ReconstructionRegionReport> Regions,
    IReadOnlyList<string> Warnings);

public sealed record ReconstructionRegionReport(
    string Kind,
    float X,
    float Y,
    float Width,
    float Height,
    float Confidence,
    string PreservationStrategy,
    bool ReconstructedAsVisibleText,
    bool ReconstructedAsVisualCrop,
    bool IsBackground,
    bool IsTable,
    bool DiscardedAsDamage,
    string AssociatedText,
    int AssociatedLineCount,
    int AssociatedWordCount,
    double AssociatedOcrConfidence,
    string ArtifactPath,
    string Warning);

public sealed class ReconstructionReportWriter
{
    private readonly VisualSimilarityService similarityService = new();

    public void Write(
        string outputPath,
        IReadOnlyList<HeavyPageLayout> layouts)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var comparisonFolder = Path.Combine(Path.GetDirectoryName(outputPath)!, "comparacion_visual_reconstruida");
        var pages = layouts.Select(layout => CreatePageReport(layout, comparisonFolder)).ToList();
        var payload = new
        {
            createdAtUtc = DateTimeOffset.UtcNow,
            mode = "Reconstrucción perfecta pesada",
            note = "El PDF reconstruye por capas: fondo limpio, recortes regionales cuando hacen falta y texto OCR visible. DOCX es secundario y puede variar visualmente.",
            pages
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        File.WriteAllText(outputPath, JsonSerializer.Serialize(payload, options));
    }

    private ReconstructionPageReport CreatePageReport(HeavyPageLayout layout, string comparisonFolder)
    {
        var textRegions = layout.Regions.Count(region => region.Kind is "text" or "heading");
        var backgroundRegions = layout.Regions.Count(region => region.Kind == "background");
        var decorationRegions = layout.Regions.Count(region => region.Kind is "decoration" or "border" or "graphic");
        var decorativeRegions = layout.Regions.Count(region => region.Kind is "decoration" or "border" or "graphic" or "diagram" or "graphical-element");
        var diagramRegions = layout.Regions.Count(region => region.Kind == "diagram");
        var graphicalElementRegions = layout.Regions.Count(region => region.Kind == "graphical-element");
        var protectedDiagramLineRegions = layout.Regions.Count(region => region.Kind == "protected-diagram-line");
        var imageRegions = layout.Regions.Count(region => region.Kind == "image");
        var tableRegions = layout.Regions.Count(region => region.Kind == "table");
        var crackRegions = layout.Regions.Count(region => region.Kind == "crack");
        var stainRegions = layout.Regions.Count(region => region.Kind == "stain");
        var noiseRegions = layout.Regions.Count(region => region.Kind == "noise");
        var groupedNoiseRegions = layout.Regions.Count(region => region.Kind == "noise" && region.Warning.Contains("agrupado", StringComparison.OrdinalIgnoreCase));
        var protectedDamageRegions = layout.Regions.Count(region => region.Kind == "protected-damage" || region.PreservationStrategy == "protected-content-kept");
        var discardedDamageRegions = layout.Regions.Count(IsDiscardedDamage);
        var averageConfidence = layout.Ocr.Lines.Count == 0
            ? 0d
            : layout.Ocr.Lines.Average(line => Math.Clamp(line.Confidence, 0f, 100f));
        var similarity = similarityService.CompareReconstructedCanvas(layout, comparisonFolder);
        var visibleTextRegions = layout.Ocr.Lines.Count(line => line.Confidence >= 35 && !string.IsNullOrWhiteSpace(line.Text));
        var fallbackVisualRegions = layout.PageImages.Count;
        var regionalVisualCropPaths = layout.PageImages
            .Where(image => !string.IsNullOrWhiteSpace(image.ImagePath))
            .Select(image => image.ImagePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var headingOcrCandidates = layout.Ocr.Lines
            .Where(line => line.OcrCandidates.Count > 0)
            .Select(line => $"{line.Text}: {string.Join(" | ", line.OcrCandidates)}")
            .Take(20)
            .ToList();
        var regionReports = layout.Regions.Select(CreateRegionReport).ToList();

        var warnings = new List<string>();
        if (averageConfidence is > 0 and < 55)
        {
            warnings.Add("OCR de baja confianza: se prioriza conservar la imagen visual restaurada.");
        }

        if (layout.Ocr.Lines.Count == 0)
        {
            warnings.Add("No se detectó texto OCR confiable en esta página.");
        }

        if (discardedDamageRegions > 0)
        {
            warnings.Add("Se detectaron grietas/manchas/ruido fuera de texto e imágenes y se descartaron como daño del escaneo.");
        }

        if (layout.Regions.Any(region => region.Kind == "protected-damage"))
        {
            warnings.Add("Se detectó posible daño cruzando texto o imagen; se protegió el contenido útil y se marcó para revisión.");
        }

        if (protectedDiagramLineRegions > 0)
        {
            warnings.Add("Se protegieron líneas internas de diagramas/imágenes/tablas para evitar borrar contenido técnico real.");
        }

        foreach (var lineWarning in layout.Ocr.Lines.Select(line => line.Warning).Where(warning => !string.IsNullOrWhiteSpace(warning)).Distinct())
        {
            warnings.Add(lineWarning);
        }

        var lowConfidenceHeadings = layout.Regions
            .Where(region => region.Kind == "heading")
            .Where(region => region.Confidence < 75 ||
                region.AssociatedOcrConfidence is > 0 and < 75 ||
                region.AssociatedText.Contains('\\') ||
                region.AssociatedText.Contains('/') ||
                region.AssociatedText.EndsWith("HANDBOO", StringComparison.OrdinalIgnoreCase))
            .Select(region => string.IsNullOrWhiteSpace(region.AssociatedText) ? "(sin texto OCR)" : region.AssociatedText)
            .Take(3)
            .ToList();
        if (lowConfidenceHeadings.Count > 0)
        {
            warnings.Add($"Título/encabezado de baja confianza: {string.Join(" | ", lowConfidenceHeadings)}. No se inventó texto; revisar portada manualmente.");
        }

        if (fallbackVisualRegions > 0)
        {
            warnings.Add("Algunas regiones visuales se conservaron como recorte regional para no inventar contenido.");
        }

        if (similarity.Difference > 0.25)
        {
            warnings.Add("La reconstrucción por capas difiere bastante frente a la página restaurada.");
        }

        return new ReconstructionPageReport(
            layout.PageNumber,
            layout.Regions.Count,
            layout.Background.Hex,
            similarity.ComparedImagePath,
            backgroundRegions,
            decorationRegions,
            textRegions,
            imageRegions,
            tableRegions,
            crackRegions,
            stainRegions,
            noiseRegions,
            protectedDamageRegions,
            visibleTextRegions,
            fallbackVisualRegions,
            discardedDamageRegions,
            decorativeRegions,
            diagramRegions,
            graphicalElementRegions,
            protectedDiagramLineRegions,
            groupedNoiseRegions,
            regionalVisualCropPaths,
            headingOcrCandidates,
            layout.Background.DamageMaskPath,
            layout.Background.ProtectedContentMaskPath,
            layout.Background.CleanBackgroundImagePath,
            layout.Background.ReconstructedCanvasPath,
            layout.Background.SideBySideComparisonPath,
            layout.Background.RegionOverlayPath,
            Math.Round(averageConfidence, 2),
            Math.Round(similarity.Similarity, 4),
            Math.Round(similarity.Difference, 4),
            regionReports,
            warnings);
    }

    private static ReconstructionRegionReport CreateRegionReport(DetectedVisualRegion region)
    {
        return new ReconstructionRegionReport(
            region.Kind,
            region.X,
            region.Y,
            region.Width,
            region.Height,
            region.Confidence,
            region.PreservationStrategy,
            region.PreservationStrategy == "visible-digital-text-layer",
            region.PreservationStrategy == "regional-visual-fallback",
            region.Kind == "background",
            region.Kind == "table",
            IsDiscardedDamage(region),
            region.AssociatedText,
            region.AssociatedLineCount,
            region.AssociatedWordCount,
            Math.Round(region.AssociatedOcrConfidence, 2),
            region.ArtifactPath,
            region.Warning);
    }

    private static bool IsDiscardedDamage(DetectedVisualRegion region)
    {
        return region.PreservationStrategy == "discarded-scan-defect" ||
            (region.Kind is "scan-damage" or "crack" or "stain" or "noise" &&
                region.PreservationStrategy != "protected-content-kept");
    }
}
