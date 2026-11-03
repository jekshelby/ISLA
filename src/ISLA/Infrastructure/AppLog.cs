using System.IO;
using System.Text;

namespace ISLA.Infrastructure;

/// <summary>
/// Minimal file log kept next to the user's local application data.
/// Diagnostics must never take the app down, so write failures are swallowed.
/// </summary>
public static class AppLog
{
    private const long MaxFileBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();

    public static string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ISLA",
        "logs");

    public static string FilePath { get; } = Path.Combine(DirectoryPath, "isla.log");

    public static void Info(string message) => Write("INFO", message, exception: null);

    public static void Warn(string message, Exception? exception = null) => Write("WARN", message, exception);

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        var entry = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
            .Append(" [")
            .Append(level)
            .Append("] ")
            .Append(message);

        if (exception is not null)
        {
            entry.AppendLine().Append(exception);
        }

        entry.AppendLine();

        lock (Gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(DirectoryPath);
                RollIfNeeded();
                File.AppendAllText(FilePath, entry.ToString(), Encoding.UTF8);
            }
            catch (Exception)
            {
                // Logging is best effort only; it must never crash the application.
            }
        }
    }

    private static void RollIfNeeded()
    {
        var info = new FileInfo(FilePath);
        if (!info.Exists || info.Length < MaxFileBytes)
        {
            return;
        }

        try
        {
            var previous = FilePath + ".old";
            File.Delete(previous);
            File.Move(FilePath, previous);
        }
        catch (IOException)
        {
            // Another process is writing; keep appending until it can be rotated.
        }
        catch (UnauthorizedAccessException)
        {
            // Read-only log; drop the rotation attempt.
        }
    }
}
