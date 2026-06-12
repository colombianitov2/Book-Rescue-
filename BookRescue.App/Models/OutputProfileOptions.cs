namespace BookRescue.App.Models;

public sealed class OutputProfileOptions
{
    public OutputReconstructionMode ReconstructionMode { get; set; } = OutputReconstructionMode.TextAndPhotos;

    public bool MaximumQuality { get; set; }

    public bool Pdf { get; set; } = true;

    public bool Word { get; set; } = true;

    public bool Epub { get; set; }

    public bool Csv { get; set; }

    public bool HasAnySelected => Pdf || Word || Epub || Csv;
}
