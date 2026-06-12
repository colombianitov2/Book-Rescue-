namespace BookRescue.App.Services;

public static class AppLogService
{
    private static readonly object Sync = new();

    public static void LogMessage(string message, string context)
    {
        WriteLog($"""
                  [{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}
                  {message}

                  """);
    }

    public static void Log(Exception exception, string context)
    {
        WriteLog($"""
                  [{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}
                  {exception}

                  """);
    }

    private static void WriteLog(string message)
    {
        try
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BookRescue",
                "logs");
            Directory.CreateDirectory(logDirectory);

            var logPath = Path.Combine(logDirectory, $"bookrescue-{DateTime.Now:yyyyMMdd}.log");

            lock (Sync)
            {
                File.AppendAllText(logPath, message);
            }
        }
        catch
        {
        }
    }
}
