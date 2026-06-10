using System.Text;

namespace POE2Radar.Overlay.Diagnostics;

internal static class CrashLog
{
    private static readonly object Gate = new();
    private static bool _installed;

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "logs", "poe2radar-crash.log");

    public static void InstallGlobalHandlers()
    {
        if (_installed) return;
        _installed = true;

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Write("Unhandled exception", ex);
            else Write("Unhandled exception", e.ExceptionObject?.ToString() ?? "<null>");
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }

    public static void Write(string title, Exception ex)
        => Write(title, ex.ToString());

    public static void Write(string title, string detail)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var sb = new StringBuilder();
                sb.AppendLine("============================================================");
                sb.AppendLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}  {title}");
                sb.AppendLine(detail);
                File.AppendAllText(FilePath, sb.ToString());
                Console.Error.WriteLine($"{title}: {detail}");
                Console.Error.WriteLine($"Crash log: {FilePath}");
            }
            catch
            {
                // Last-ditch diagnostics must never become the crash.
            }
        }
    }
}
