using System.Diagnostics;
using System.Text;

namespace BookRescue.App.Services;

public sealed class LibreOfficeDocumentService
{
    public bool IsAvailable => TryFindSoffice(out _);

    public async Task<bool> TryExportPdfAsync(
        string docxPath,
        string outputPdfPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(docxPath) || !TryFindSoffice(out var sofficeExe))
        {
            return false;
        }

        var outputDirectory = Path.GetDirectoryName(outputPdfPath);
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            return false;
        }

        Directory.CreateDirectory(outputDirectory);
        var tempDirectory = Path.Combine(outputDirectory, "_libreoffice_export");
        Directory.CreateDirectory(tempDirectory);
        var tempDocxPath = Path.Combine(tempDirectory, $"{Path.GetFileNameWithoutExtension(outputPdfPath)}.docx");
        var tempPdfPath = Path.Combine(tempDirectory, $"{Path.GetFileNameWithoutExtension(tempDocxPath)}.pdf");

        try
        {
            File.Copy(docxPath, tempDocxPath, overwrite: true);
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = sofficeExe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = tempDirectory
            };
            process.StartInfo.ArgumentList.Add("--headless");
            process.StartInfo.ArgumentList.Add("--nologo");
            process.StartInfo.ArgumentList.Add("--nofirststartwizard");
            process.StartInfo.ArgumentList.Add("--convert-to");
            process.StartInfo.ArgumentList.Add("pdf");
            process.StartInfo.ArgumentList.Add("--outdir");
            process.StartInfo.ArgumentList.Add(tempDirectory);
            process.StartInfo.ArgumentList.Add(tempDocxPath);

            var output = new StringBuilder();
            var error = new StringBuilder();
            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data is not null)
                {
                    output.AppendLine(args.Data);
                }
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data is not null)
                {
                    error.AppendLine(args.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0 || !File.Exists(tempPdfPath))
            {
                AppLogService.LogMessage(
                    $"LibreOffice no pudo exportar PDF. Salida={output}; Error={error}",
                    "LibreOffice");
                return false;
            }

            File.Copy(tempPdfPath, outputPdfPath, overwrite: true);
            return File.Exists(outputPdfPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLogService.Log(ex, "Exportación LibreOffice");
            return false;
        }
        finally
        {
            DeleteDirectoryQuietly(tempDirectory);
        }
    }

    private static bool TryFindSoffice(out string sofficeExe)
    {
        foreach (var root in GetRuntimeRoots())
        {
            var candidate = Path.Combine(root, "libreoffice", "program", "soffice.exe");
            if (File.Exists(candidate))
            {
                sofficeExe = candidate;
                return true;
            }
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var installed = Path.Combine(programFiles, "LibreOffice", "program", "soffice.exe");
        if (File.Exists(installed))
        {
            sofficeExe = installed;
            return true;
        }

        sofficeExe = string.Empty;
        return false;
    }

    private static IEnumerable<string> GetRuntimeRoots()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; current is not null && depth < 8; depth++)
        {
            yield return Path.Combine(current.FullName, "runtime");
            current = current.Parent;
        }
    }

    private static void DeleteDirectoryQuietly(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
