namespace Alterius.Core;

/// <summary>
/// Resolves application directories, configuration, log, and FFmpeg executable paths.
/// Uses <see cref="AppContext.BaseDirectory"/> for single-file publish compatibility.
/// </summary>
public static class AppPaths
{
    public static string BaseDirectory => AppContext.BaseDirectory;

    public static string ConfigFilePath => Path.Combine(BaseDirectory, "config.json");

    public static string AppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Alterius");

    public static string LogsDir => Path.Combine(AppDataDir, "logs");

    public static string LogFilePath => Path.Combine(LogsDir, "agent.log");

    public static string DefaultBufferDir => Path.Combine(AppDataDir, "buffer");

    /// <summary>
    /// Resolves the configured FFmpeg path relative to <see cref="BaseDirectory"/> when not rooted,
    /// falling back to <c>ffmpeg.exe</c> next to the executable, <c>bin\ffmpeg\ffmpeg.exe</c>,
    /// parent repository folders (for Debug/Release IDE runs), and system <c>PATH</c>.
    /// </summary>
    public static string ResolveFfmpegPath(string configuredPath)
    {
        string primary;
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            string expanded = Environment.ExpandEnvironmentVariables(configuredPath.Trim());
            primary = Path.IsPathRooted(expanded)
                ? Path.GetFullPath(expanded)
                : Path.GetFullPath(Path.Combine(BaseDirectory, expanded));
        }
        else
        {
            primary = Path.GetFullPath(Path.Combine(BaseDirectory, "bin", "ffmpeg", "ffmpeg.exe"));
        }

        if (File.Exists(primary))
        {
            return primary;
        }

        string nextToExe = Path.GetFullPath(Path.Combine(BaseDirectory, "ffmpeg.exe"));
        if (File.Exists(nextToExe))
        {
            return nextToExe;
        }

        string defaultSubdir = Path.GetFullPath(Path.Combine(BaseDirectory, "bin", "ffmpeg", "ffmpeg.exe"));
        if (File.Exists(defaultSubdir))
        {
            return defaultSubdir;
        }

        DirectoryInfo? current = new DirectoryInfo(BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        for (int depth = 0; depth < 5 && current?.Parent is not null; depth++)
        {
            current = current.Parent;
            string parentCandidate = Path.Combine(current.FullName, "bin", "ffmpeg", "ffmpeg.exe");
            if (File.Exists(parentCandidate))
            {
                return parentCandidate;
            }
        }

        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    string candidate = Path.Combine(dir, "ffmpeg.exe");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                    // Ignore malformed PATH entries.
                }
            }
        }

        return primary;
    }

    /// <summary>
    /// Ensures required local directories exist before capture or logging starts.
    /// </summary>
    public static void EnsureDirectoriesExist(string bufferDir)
    {
        Directory.CreateDirectory(AppDataDir);
        Directory.CreateDirectory(LogsDir);

        if (!string.IsNullOrWhiteSpace(bufferDir))
        {
            Directory.CreateDirectory(bufferDir);
        }
    }
}
