using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using BookRescue.App.Models;

namespace BookRescue.App.Services;

public static class HardwareCapabilityService
{
    private const int MinimumLogicalProcessors = 4;
    private const double MinimumMemoryGb = 12;
    private const double MinimumFreeDiskGb = 5;
    private const double MinimumGpuMemoryGb = 3.5;

    private static readonly Lazy<HardwareProfile> Profile = new(AnalyzeHardware);

    public static HardwareProfile Current => Profile.Value;

    private static HardwareProfile AnalyzeHardware()
    {
        var logicalProcessors = Environment.ProcessorCount;
        var totalMemoryGb = GetTotalMemoryGb();
        var freeDiskGb = GetApplicationDiskFreeGb();
        var gpu = TryGetCudaGpu();

        var failures = new List<string>();
        if (!OperatingSystem.IsWindows())
        {
            failures.Add("Sistema operativo no compatible.");
        }

        if (logicalProcessors < MinimumLogicalProcessors)
        {
            failures.Add($"CPU insuficiente: {logicalProcessors} hilo(s).");
        }

        if (totalMemoryGb < MinimumMemoryGb)
        {
            failures.Add($"Memoria insuficiente: {totalMemoryGb:0.0} GB.");
        }

        if (freeDiskGb < MinimumFreeDiskGb)
        {
            failures.Add($"Espacio libre insuficiente: {freeDiskGb:0.0} GB.");
        }

        var shouldUseGpu = gpu.HasCudaGpu && gpu.TotalMemoryGb >= MinimumGpuMemoryGb;
        var summary = shouldUseGpu
            ? $"GPU dedicada: {gpu.Name} ({gpu.TotalMemoryGb:0.0} GB). CPU: {logicalProcessors} hilos. RAM: {totalMemoryGb:0.0} GB."
            : $"Modo CPU: {logicalProcessors} hilos. RAM: {totalMemoryGb:0.0} GB.";

        return new HardwareProfile
        {
            IsCapable = failures.Count == 0,
            ShouldUseGpu = shouldUseGpu,
            LogicalProcessors = logicalProcessors,
            TotalMemoryGb = totalMemoryGb,
            FreeDiskGb = freeDiskGb,
            HasCudaGpu = gpu.HasCudaGpu,
            GpuName = gpu.Name,
            GpuMemoryGb = gpu.TotalMemoryGb,
            Summary = summary,
            FailureReason = string.Join(Environment.NewLine, failures)
        };
    }

    private static double GetTotalMemoryGb()
    {
        var status = new MemoryStatusEx();
        if (!GlobalMemoryStatusEx(status))
        {
            return 0;
        }

        return status.TotalPhys / 1024d / 1024d / 1024d;
    }

    private static double GetApplicationDiskFreeGb()
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(AppContext.BaseDirectory)!);
            return drive.AvailableFreeSpace / 1024d / 1024d / 1024d;
        }
        catch
        {
            return 0;
        }
    }

    private static (bool HasCudaGpu, string Name, double TotalMemoryGb) TryGetCudaGpu()
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name,memory.total --format=csv,noheader,nounits",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            process.Start();
            if (!process.WaitForExit(5000) || process.ExitCode != 0)
            {
                TryKill(process);
                return (false, string.Empty, 0);
            }

            var line = process.StandardOutput.ReadToEnd()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(line))
            {
                return (false, string.Empty, 0);
            }

            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var memoryMiB))
            {
                return (false, parts[0], 0);
            }

            return (true, parts[0], memoryMiB / 1024d);
        }
        catch
        {
            return (false, string.Empty, 0);
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private sealed class MemoryStatusEx
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }
}
