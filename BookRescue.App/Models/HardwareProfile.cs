namespace BookRescue.App.Models;

public sealed class HardwareProfile
{
    public required bool IsCapable { get; init; }

    public required bool ShouldUseGpu { get; init; }

    public required int LogicalProcessors { get; init; }

    public required double TotalMemoryGb { get; init; }

    public required double FreeDiskGb { get; init; }

    public required bool HasCudaGpu { get; init; }

    public required string GpuName { get; init; }

    public required double GpuMemoryGb { get; init; }

    public required string Summary { get; init; }

    public required string FailureReason { get; init; }

    public string MinimumHardwareMessage => "Lo sentimos, su pc no cuenta con hardware capacitado para ejecutar este programa";
}
