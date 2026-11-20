using System.Globalization;
using System.Text;

namespace Alterius.Core;

/// <summary>
/// Thread-safe file logger writing to %LOCALAPPDATA%\Alterius\logs\agent.log
/// with daily file rotation and retention pruning.
/// </summary>
public sealed class FileLogger
{
    private readonly object _sync = new();
    private readonly string _logDirectory;
    private readonly string _activeLogPath;
    private int _retentionDays;

    public FileLogger(int retentionDays = 14)
    {
        _logDirectory = AppPaths.LogsDir;
        _activeLogPath = AppPaths.LogFilePath;
        _retentionDays = Math.Clamp(retentionDays, 1, 365);

        Directory.CreateDirectory(_logDirectory);
        RotateIfNeededLocked(DateTime.Now.Date);
    }

    public string LogDirectory => _logDirectory;

    public string ActiveLogPath => _activeLogPath;

    public void SetRetentionDays(int retentionDays)
    {
        lock (_sync)
        {
            _retentionDays = Math.Clamp(retentionDays, 1, 365);
            PruneOldLogsLocked(DateTime.Now.Date);
        }
    }

    public void Debug(string component, string message) => Write("DEBUG", component, message);

    public void Info(string component, string message) => Write("INFO", component, message);

    public void Warn(string component, string message) => Write("WARN", component, message);

    public void Error(string component, string message, Exception? ex = null)
    {
        string fullMessage = ex is null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
        Write("ERROR", component, fullMessage);
    }

    private void Write(string level, string component, string message)
    {
        try
        {
            DateTime now = DateTime.Now;
            string line = string.Format(
                CultureInfo.InvariantCulture,
                "{0:yyyy-MM-dd HH:mm:ss.fff} [{1,-5}] [{2}] {3}{4}",
                now,
                level,
                component,
                message,
                Environment.NewLine);

            lock (_sync)
            {
                Directory.CreateDirectory(_logDirectory);
                RotateIfNeededLocked(now.Date);
                File.AppendAllText(_activeLogPath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never throw into callers or crash background hooks.
        }
    }

    private void RotateIfNeededLocked(DateTime today)
    {
        try
        {
            if (!File.Exists(_activeLogPath))
            {
                return;
            }

            DateTime lastWriteDate = File.GetLastWriteTime(_activeLogPath).Date;
            if (lastWriteDate >= today)
            {
                return;
            }

            string archiveName = $"agent_{lastWriteDate:yyyy-MM-dd}.log";
            string archivePath = Path.Combine(_logDirectory, archiveName);

            if (File.Exists(archivePath))
            {
                archiveName = $"agent_{lastWriteDate:yyyy-MM-dd}_{DateTime.Now:HHmmss}.log";
                archivePath = Path.Combine(_logDirectory, archiveName);
            }

            File.Move(_activeLogPath, archivePath);
            PruneOldLogsLocked(today);
        }
        catch
        {
            // Ignore rotation contention and continue logging.
        }
    }

    private void PruneOldLogsLocked(DateTime today)
    {
        try
        {
            DateTime cutoff = today.AddDays(-_retentionDays);
            foreach (string file in Directory.EnumerateFiles(_logDirectory, "agent_*.log"))
            {
                if (File.GetLastWriteTime(file).Date < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // Best-effort log pruning.
        }
    }
}
