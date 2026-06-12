using OpenCvSharp;

namespace BookRescue.App.Services;

public sealed class ImageRestorationService
{
    public (int pixelWidth, int pixelHeight) Restore(string inputPath, string outputPath)
    {
        using var source = Cv2.ImRead(inputPath, ImreadModes.Color);
        if (source.Empty())
        {
            throw new InvalidOperationException($"No se pudo leer la imagen: {inputPath}");
        }

        using var denoised = new Mat();
        Cv2.FastNlMeansDenoisingColored(source, denoised, 6, 6, 7, 21);

        using var lab = new Mat();
        Cv2.CvtColor(denoised, lab, ColorConversionCodes.BGR2Lab);

        var channels = Cv2.Split(lab);
        try
        {
            using var clahe = Cv2.CreateCLAHE(clipLimit: 2.4, tileGridSize: new Size(8, 8));
            clahe.Apply(channels[0], channels[0]);

            using var mergedLab = new Mat();
            Cv2.Merge(channels, mergedLab);

            using var contrastEnhanced = new Mat();
            Cv2.CvtColor(mergedLab, contrastEnhanced, ColorConversionCodes.Lab2BGR);

            using var blurred = new Mat();
            using var sharpened = new Mat();
            Cv2.GaussianBlur(contrastEnhanced, blurred, new Size(0, 0), 1.2);
            Cv2.AddWeighted(contrastEnhanced, 1.55, blurred, -0.55, 0, sharpened);

            using var grayscale = new Mat();
            Cv2.CvtColor(sharpened, grayscale, ColorConversionCodes.BGR2GRAY);

            using var cleaned = new Mat();
            Cv2.AdaptiveThreshold(grayscale, cleaned, 255, AdaptiveThresholdTypes.GaussianC, ThresholdTypes.Binary, 31, 6);

            using var blended = new Mat();
            Cv2.CvtColor(cleaned, blended, ColorConversionCodes.GRAY2BGR);
            Cv2.AddWeighted(sharpened, 0.82, blended, 0.18, 0, blended);

            Cv2.ImWrite(outputPath, blended, [new ImageEncodingParam(ImwriteFlags.PngCompression, 2)]);

            return (source.Width, source.Height);
        }
        finally
        {
            foreach (var channel in channels)
            {
                channel.Dispose();
            }
        }
    }
}
