using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Alterius.Core;
using Alterius.Models;

namespace Alterius.Capture.Video;

/// <summary>
/// Captures desktop video to fragmented MP4 (<c>video.mp4</c>) using <c>ffmpeg.exe</c> and <c>gdigrab</c>.
/// Graceful shutdown sends <c>'q'</c> to FFmpeg's stdin and waits up to 5 seconds before force-killing.
/// </summary>
public sealed class FfmpegVideoRecorder : IDisposable
{
    private readonly FileLogger _logger;
    private readonly ConcurrentQueue<string> _stderrTail = new();
    private Process? _process;
    private bool _disposed;

    public FfmpegVideoRecorder(FileLogger logger)
    {
        _logger = logger;
    }

    public bool IsRunning => _process is { HasExited: false };

    public Rectangle CaptureBounds { get; private set; }

    public string OutputPath { get; private set; } = string.Empty;

    public static Rectangle GetCaptureBounds(bool captureAllMonitors)
    {
        if (captureAllMonitors)
        {
            Rectangle virtualBounds = SystemInformation.VirtualScreen;
            if (virtualBounds.Width > 0 && virtualBounds.Height > 0)
            {
                return virtualBounds;
            }
        }

        Rectangle primary = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        return primary.Width > 0 && primary.Height > 0 ? primary : new Rectangle(0, 0, 1920, 1080);
    }

    public void Start(string outputMp4Path, AppConfig config)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsRunning)
        {
            throw new InvalidOperationException("FFmpeg video recorder is already running.");
        }

        string ffmpegExe = AppPaths.ResolveFfmpegPath(config.FfmpegPath);
        if (!File.Exists(ffmpegExe))
        {
            throw new FileNotFoundException(
                $"FFmpeg executable not found at '{ffmpegExe}'. Place ffmpeg.exe in bin\\ffmpeg\\ffmpeg.exe or update config.json.",
                ffmpegExe);
        }

        string? parentDir = Path.GetDirectoryName(outputMp4Path);
        if (!string.IsNullOrEmpty(parentDir))
        {
            Directory.CreateDirectory(parentDir);
        }

        CaptureBounds = GetCaptureBounds(config.CaptureAllMonitors);
        OutputPath = outputMp4Path;

        (int targetW, int targetH) = config.GetTargetResolution();
        int fps = Math.Clamp(config.VideoFps, 1, 5);
        int gop = Math.Max(fps * 5, 2);

        // Even capture dimensions are required by libx264; scaling filter enforces even output dimensions too.
        int capW = Math.Max(2, (CaptureBounds.Width / 2) * 2);
        int capH = Math.Max(2, (CaptureBounds.Height / 2) * 2);

        string videoFilter = string.Format(
            CultureInfo.InvariantCulture,
            "scale='min({0},iw)':'min({1},ih)':force_original_aspect_ratio=decrease,scale=trunc(iw/2)*2:trunc(ih/2)*2",
            targetW,
            targetH);

        string arguments = string.Format(
            CultureInfo.InvariantCulture,
            "-y -hide_banner -loglevel warning " +
            "-f gdigrab -framerate {0} -offset_x {1} -offset_y {2} -video_size {3}x{4} -draw_mouse 1 -i desktop " +
            "-vf \"{5}\" -c:v libx264 -preset {6} -crf {7} -pix_fmt yuv420p -g {8} " +
            "-movflags +frag_keyframe+empty_moov \"{9}\"",
            fps,
            CaptureBounds.X,
            CaptureBounds.Y,
            capW,
            capH,
            videoFilter,
            config.VideoPreset,
            config.VideoCrf,
            gop,
            outputMp4Path);

        while (_stderrTail.TryDequeue(out _))
        {
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegExe,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (sender, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
            {
                return;
            }

            _stderrTail.Enqueue(e.Data);
            while (_stderrTail.Count > 20 && _stderrTail.TryDequeue(out _))
            {
            }
        };

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Failed to start ffmpeg.exe process.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;

        _logger.Info(
            nameof(FfmpegVideoRecorder),
            $"Started video capture ({capW}x{capH} -> {targetW}x{targetH} @ {fps} FPS) -> {outputMp4Path}");
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Process? process = _process;
        _process = null;

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                try
                {
                    // FFmpeg exits gracefully and flushes MOOF/MP4 atoms upon receiving 'q' on stdin.
                    await process.StandardInput.WriteAsync("q\n").ConfigureAwait(false);
                    await process.StandardInput.FlushAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.Warn(nameof(FfmpegVideoRecorder), $"Unable to write 'q' to FFmpeg stdin: {ex.Message}");
                }

                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                waitCts.CancelAfter(TimeSpan.FromSeconds(5));

                try
                {
                    await process.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (!process.HasExited)
                    {
                        _logger.Warn(nameof(FfmpegVideoRecorder), "FFmpeg did not exit within 5s after 'q'; terminating process tree.");
                        try
                        {
                            process.Kill(entireProcessTree: true);
                            process.WaitForExit(2000);
                        }
                        catch (Exception killEx)
                        {
                            _logger.Warn(nameof(FfmpegVideoRecorder), $"Error killing FFmpeg: {killEx.Message}");
                        }
                    }
                }
            }

            if (process.HasExited && process.ExitCode != 0)
            {
                string stderrSummary = string.Join(" | ", _stderrTail);
                if (!string.IsNullOrWhiteSpace(stderrSummary))
                {
                    _logger.Warn(nameof(FfmpegVideoRecorder), $"FFmpeg exited with code {process.ExitCode}: {stderrSummary}");
                }
            }
            else
            {
                _logger.Info(nameof(FfmpegVideoRecorder), $"Stopped video capture cleanly: {OutputPath}");
            }
        }
        finally
        {
            process.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Process? process = _process;
        _process = null;

        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    try
                    {
                        process.StandardInput.Write("q\n");
                        process.StandardInput.Flush();
                        process.WaitForExit(2000);
                    }
                    catch
                    {
                        // Fall through to kill.
                    }

                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
            }
            catch
            {
                // Best-effort cleanup on dispose.
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}
