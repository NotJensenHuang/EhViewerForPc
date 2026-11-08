using System.Text;

namespace EhViewerReader.Services;

/// <summary>
/// Minimal append-only log. Logging must never take the application down, so all
/// failures are swallowed. Logs deliberately contain no token or page content.
/// </summary>
public sealed class LogService
{
    private readonly string _directory;
    private readonly object _gate = new();
    private readonly string _file;

    public LogService(string directory)
    {
        _directory = directory;
        _file = Path.Combine(directory, "app.log");
        try
        {
            Directory.CreateDirectory(directory);
            RotateIfNeeded();
        }
        catch
        {
            // Best effort only.
        }
    }

    public void Info(string message) => Write("INFO", message, null);

    public void Warn(string message, Exception? exception = null) => Write("WARN", message, exception);

    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        try
        {
            var builder = new StringBuilder(160);
            builder.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append(" [").Append(level).Append("] ")
                .Append(message);
            if (exception is not null)
            {
                builder.Append(" | ").Append(exception.GetType().Name).Append(": ").Append(exception.Message);
            }

            lock (_gate)
            {
                File.AppendAllText(_file, builder.AppendLine().ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Never surface logging failures.
        }
    }

    private void RotateIfNeeded()
    {
        var info = new FileInfo(_file);
        if (!info.Exists || info.Length < 2 * 1024 * 1024) return;
        string backup = Path.Combine(_directory, "app.log.1");
        try
        {
            File.Move(_file, backup, overwrite: true);
        }
        catch
        {
            // Keep appending to the existing file.
        }
    }
}
