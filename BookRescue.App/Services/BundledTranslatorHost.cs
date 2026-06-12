using System.Diagnostics;
using System.Net.Http;

namespace BookRescue.App.Services;

public sealed class BundledTranslatorHost : IDisposable
{
    private readonly SemaphoreSlim _sync = new(1, 1);
    private Process? _process;

    public async Task<bool> EnsureRunningAsync(Uri endpoint, CancellationToken cancellationToken = default)
    {
        if (!IsLocalEndpoint(endpoint))
        {
            return false;
        }

        await _sync.WaitAsync(cancellationToken);
        try
        {
            if (_process is { HasExited: false })
            {
                return true;
            }

            if (await CanReachAsync(endpoint, cancellationToken))
            {
                return true;
            }

            var runtimeRoot = LocateRuntimeRoot();
            if (runtimeRoot is null)
            {
                return false;
            }

            var frozenTranslatorExe = Path.Combine(runtimeRoot, "translator", "BookRescueTranslator", "BookRescueTranslator.exe");
            var pythonTranslatorExe = Path.Combine(runtimeRoot, "translator-venv", "Scripts", "libretranslate.exe");
            var argosData = Path.Combine(runtimeRoot, "argos-data");
            var argosCache = Path.Combine(runtimeRoot, "argos-cache");
            var argosConfig = Path.Combine(runtimeRoot, "argos-config");
            var argosPackages = Path.Combine(argosData, "argos-translate", "packages");

            var translatorExe = File.Exists(frozenTranslatorExe) ? frozenTranslatorExe : pythonTranslatorExe;
            if (!File.Exists(translatorExe) || !Directory.Exists(argosPackages))
            {
                return false;
            }

            Directory.CreateDirectory(argosData);
            Directory.CreateDirectory(argosCache);
            Directory.CreateDirectory(argosConfig);

            var startInfo = new ProcessStartInfo
            {
                FileName = translatorExe,
                Arguments = File.Exists(frozenTranslatorExe)
                    ? $"--port {endpoint.Port}"
                    : $"--host 127.0.0.1 --port {endpoint.Port} --load-only en,es --disable-web-ui",
                WorkingDirectory = runtimeRoot,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            startInfo.Environment["PYTHONUTF8"] = "1";
            startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
            startInfo.Environment["XDG_DATA_HOME"] = argosData;
            startInfo.Environment["XDG_CACHE_HOME"] = argosCache;
            startInfo.Environment["XDG_CONFIG_HOME"] = argosConfig;
            startInfo.Environment["ARGOS_PACKAGES_DIR"] = argosPackages;

            _process = Process.Start(startInfo);
            if (_process is null)
            {
                return false;
            }

            for (var i = 0; i < 60; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                if (await CanReachAsync(endpoint, cancellationToken))
                {
                    return true;
                }

                if (_process.HasExited)
                {
                    return false;
                }
            }

            return false;
        }
        finally
        {
            _sync.Release();
        }
    }

    private static async Task<bool> CanReachAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await client.GetAsync(new Uri(endpoint, "/languages"), cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLocalEndpoint(Uri endpoint)
    {
        return endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
               || endpoint.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase);
    }

    private static string? LocateRuntimeRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "runtime");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return null;
    }

    public void Dispose()
    {
        _sync.Dispose();

        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
        }
        catch
        {
        }
        finally
        {
            _process?.Dispose();
        }
    }
}
