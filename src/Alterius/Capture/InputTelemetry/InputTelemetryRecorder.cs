using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;
using Alterius.Core;
using Alterius.Models;

namespace Alterius.Capture.InputTelemetry;

/// <summary>
/// Captures mouse clicks/moves/wheel via <c>WH_MOUSE_LL</c>, active window focus transitions,
/// and optional anonymous typing burst counts (no keystrokes or character values recorded).
/// The Win32 hook callback strictly pushes lightweight structs into an asynchronous channel
/// so Windows mouse input never stalls.
/// </summary>
public sealed class InputTelemetryRecorder : IDisposable
{
    private const int MaxPendingMouseEvents = 8192;
    private const int ForegroundPollIntervalMs = 150;

    private static readonly JsonSerializerOptions JsonLineOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly FileLogger _logger;
    private readonly ActiveWindowTracker _windowTracker = new();

    private Channel<TelemetrySignal>? _channel;
    private Task? _consumerTask;
    private Thread? _hookThread;
    private NativeMethods.LowLevelProc? _mouseProc;
    private NativeMethods.LowLevelProc? _keyboardProc;
    private IntPtr _mouseHookHandle;
    private IntPtr _keyboardHookHandle;
    private uint _hookThreadId;

    private long _pendingMouseEvents;
    private long _typingBurstCount;
    private long _lastMoveTimestampTicks;
    private int _lastMoveX = int.MinValue;
    private int _lastMoveY = int.MinValue;
    private long _moveSampleIntervalTicks;
    private long _minMoveDistanceSq;
    private bool _enableTypingBurstCounter;
    private int _typingBurstWindowSeconds;
    private bool _isRunning;
    private bool _disposed;

    public InputTelemetryRecorder(FileLogger logger)
    {
        _logger = logger;
    }

    public async Task StartSessionAsync(
        string initialEventsPath,
        Rectangle captureBounds,
        DateTime chunkStartedUtc,
        AppConfig config)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_isRunning)
        {
            throw new InvalidOperationException("Telemetry recorder session is already running.");
        }

        _moveSampleIntervalTicks = (long)(config.MouseMoveSampleIntervalMs * (Stopwatch.Frequency / 1000.0));
        int minDist = Math.Max(1, config.MouseMoveMinDistancePx);
        _minMoveDistanceSq = (long)minDist * minDist;
        _enableTypingBurstCounter = config.EnableTypingBurstCounter;
        _typingBurstWindowSeconds = Math.Clamp(config.TypingBurstWindowSeconds, 1, 60);

        _pendingMouseEvents = 0;
        Interlocked.Exchange(ref _typingBurstCount, 0);
        _lastMoveTimestampTicks = 0;
        _lastMoveX = int.MinValue;
        _lastMoveY = int.MinValue;

        _channel = Channel.CreateUnbounded<TelemetrySignal>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        long startTicks = Stopwatch.GetTimestamp();
        _consumerTask = Task.Run(() => ConsumeLoopAsync(_channel, initialEventsPath, captureBounds, chunkStartedUtc, startTicks));

        var hookReadyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _hookThread = new Thread(() => RunHookThread(hookReadyTcs))
        {
            IsBackground = true,
            Name = "Alterius.InputHookThread"
        };
        _hookThread.SetApartmentState(ApartmentState.STA);
        _hookThread.Start();

        await hookReadyTcs.Task.ConfigureAwait(false);
        _isRunning = true;

        _logger.Info(nameof(InputTelemetryRecorder), $"Input telemetry hooks started -> {initialEventsPath}");
    }

    public async Task<List<TopAppEntry>> RotateChunkAsync(
        string nextEventsPath,
        Rectangle nextCaptureBounds,
        DateTime nextChunkStartedUtc,
        CancellationToken cancellationToken = default)
    {
        if (!_isRunning || _channel is null)
        {
            return new List<TopAppEntry>();
        }

        var tcs = new TaskCompletionSource<List<TopAppEntry>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var signal = TelemetrySignal.CreateRotate(nextEventsPath, nextCaptureBounds, nextChunkStartedUtc, Stopwatch.GetTimestamp(), tcs);
        await _channel.Writer.WriteAsync(signal, cancellationToken).ConfigureAwait(false);
        return await tcs.Task.ConfigureAwait(false);
    }

    public async Task<List<TopAppEntry>> StopSessionAsync()
    {
        if (!_isRunning)
        {
            return new List<TopAppEntry>();
        }

        _isRunning = false;
        StopHookThread();

        List<TopAppEntry> finalTopApps = new();
        if (_channel is not null)
        {
            var tcs = new TaskCompletionSource<List<TopAppEntry>>(TaskCreationOptions.RunContinuationsAsynchronously);
            await _channel.Writer.WriteAsync(TelemetrySignal.CreateStop(Stopwatch.GetTimestamp(), tcs)).ConfigureAwait(false);
            _channel.Writer.TryComplete();
            finalTopApps = await tcs.Task.ConfigureAwait(false);
        }

        if (_consumerTask is not null)
        {
            try
            {
                await _consumerTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Warn(nameof(InputTelemetryRecorder), $"Consumer task ended with warning: {ex.Message}");
            }
        }

        _consumerTask = null;
        _channel = null;
        _logger.Info(nameof(InputTelemetryRecorder), "Input telemetry hooks stopped cleanly.");
        return finalTopApps;
    }

    private void RunHookThread(TaskCompletionSource<bool> hookReadyTcs)
    {
        try
        {
            _hookThreadId = NativeMethods.GetCurrentThreadId();
            IntPtr hModule = NativeMethods.GetModuleHandle(null);

            _mouseProc = MouseHookCallback;
            _mouseHookHandle = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc, hModule, 0);
            if (_mouseHookHandle == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err, $"SetWindowsHookEx(WH_MOUSE_LL) failed with Win32 error {err}.");
            }

            if (_enableTypingBurstCounter)
            {
                _keyboardProc = KeyboardHookCallback;
                _keyboardHookHandle = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _keyboardProc, hModule, 0);
                if (_keyboardHookHandle == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    _logger.Warn(nameof(InputTelemetryRecorder), $"WH_KEYBOARD_LL installation failed (error {err}); typing burst counter disabled.");
                }
            }

            hookReadyTcs.TrySetResult(true);

            while (NativeMethods.GetMessage(out NativeMethods.MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(nameof(InputTelemetryRecorder), "Input hook thread failed.", ex);
            hookReadyTcs.TrySetException(ex);
        }
        finally
        {
            if (_keyboardHookHandle != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_keyboardHookHandle);
                _keyboardHookHandle = IntPtr.Zero;
            }

            if (_mouseHookHandle != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_mouseHookHandle);
                _mouseHookHandle = IntPtr.Zero;
            }

            _keyboardProc = null;
            _mouseProc = null;
            _hookThreadId = 0;
        }
    }

    private void StopHookThread()
    {
        uint threadId = _hookThreadId;
        if (threadId != 0)
        {
            NativeMethods.PostThreadMessage(threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        Thread? thread = _hookThread;
        _hookThread = null;
        if (thread is not null && thread.IsAlive)
        {
            thread.Join(TimeSpan.FromSeconds(2));
        }
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _channel is not null)
        {
            int message = wParam.ToInt32();
            var hookStruct = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
            long nowTicks = Stopwatch.GetTimestamp();

            switch (message)
            {
                case NativeMethods.WM_LBUTTONDOWN:
                    EnqueueMouseEvent(MouseInputKind.Click, hookStruct.pt.x, hookStruct.pt.y, "left", 0, nowTicks);
                    break;

                case NativeMethods.WM_RBUTTONDOWN:
                    EnqueueMouseEvent(MouseInputKind.Click, hookStruct.pt.x, hookStruct.pt.y, "right", 0, nowTicks);
                    break;

                case NativeMethods.WM_MBUTTONDOWN:
                    EnqueueMouseEvent(MouseInputKind.Click, hookStruct.pt.x, hookStruct.pt.y, "middle", 0, nowTicks);
                    break;

                case NativeMethods.WM_MOUSEWHEEL:
                {
                    short wheelDelta = unchecked((short)((hookStruct.mouseData >> 16) & 0xFFFF));
                    EnqueueMouseEvent(MouseInputKind.Wheel, hookStruct.pt.x, hookStruct.pt.y, null, wheelDelta, nowTicks);
                    break;
                }

                case NativeMethods.WM_MOUSEMOVE:
                {
                    if (nowTicks - _lastMoveTimestampTicks >= _moveSampleIntervalTicks)
                    {
                        long dx = (long)hookStruct.pt.x - _lastMoveX;
                        long dy = (long)hookStruct.pt.y - _lastMoveY;
                        long distSq = (_lastMoveX == int.MinValue) ? long.MaxValue : (dx * dx + dy * dy);

                        if (distSq > _minMoveDistanceSq)
                        {
                            _lastMoveTimestampTicks = nowTicks;
                            _lastMoveX = hookStruct.pt.x;
                            _lastMoveY = hookStruct.pt.y;
                            EnqueueMouseEvent(MouseInputKind.Move, hookStruct.pt.x, hookStruct.pt.y, null, 0, nowTicks);
                        }
                    }

                    break;
                }
            }
        }

        return NativeMethods.CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // Privacy rule: only count keydown occurrences; never inspect lParam, vkCode, or scanCode.
        if (nCode >= 0)
        {
            int message = wParam.ToInt32();
            if (message == NativeMethods.WM_KEYDOWN || message == NativeMethods.WM_SYSKEYDOWN)
            {
                Interlocked.Increment(ref _typingBurstCount);
            }
        }

        return NativeMethods.CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
    }

    private void EnqueueMouseEvent(MouseInputKind kind, int x, int y, string? button, int wheelDelta, long timestampTicks)
    {
        if (kind == MouseInputKind.Move && Interlocked.Read(ref _pendingMouseEvents) >= MaxPendingMouseEvents)
        {
            return;
        }

        var signal = TelemetrySignal.CreateMouse(kind, x, y, button, wheelDelta, timestampTicks, DateTime.UtcNow);
        if (_channel is not null && _channel.Writer.TryWrite(signal))
        {
            Interlocked.Increment(ref _pendingMouseEvents);
        }
    }

    private async Task ConsumeLoopAsync(
        Channel<TelemetrySignal> channel,
        string initialEventsPath,
        Rectangle initialCaptureBounds,
        DateTime initialChunkStartedUtc,
        long initialStartTicks)
    {
        ChunkTelemetryState state = OpenChunkState(initialEventsPath, initialCaptureBounds, initialChunkStartedUtc, initialStartTicks);
        long lastFocusPollTicks = 0;
        long pollIntervalTicks = (long)(ForegroundPollIntervalMs * (Stopwatch.Frequency / 1000.0));
        long burstIntervalTicks = _typingBurstWindowSeconds * Stopwatch.Frequency;
        long lastBurstFlushTicks = initialStartTicks;
        bool stopRequested = false;

        try
        {
            while (!stopRequested)
            {
                while (channel.Reader.TryRead(out TelemetrySignal? signal))
                {
                    if (signal.Kind == SignalKind.Mouse)
                    {
                        Interlocked.Decrement(ref _pendingMouseEvents);

                        long nowTicks = Stopwatch.GetTimestamp();
                        if (nowTicks - lastFocusPollTicks >= pollIntervalTicks)
                        {
                            UpdateForegroundWindowIfChanged(state, nowTicks, DateTime.UtcNow);
                            lastFocusPollTicks = nowTicks;
                        }

                        WriteMouseSignal(state, signal);
                    }
                    else if (signal.Kind == SignalKind.RotateChunk)
                    {
                        FlushTypingBurst(state, signal.TimestampTicks, DateTime.UtcNow);
                        lastBurstFlushTicks = signal.TimestampTicks;

                        List<TopAppEntry> completedApps = CloseChunkState(state, signal.TimestampTicks);
                        signal.Completion?.TrySetResult(completedApps);

                        state = OpenChunkState(
                            signal.NextEventsPath!,
                            signal.NextCaptureBounds,
                            signal.NextChunkStartedUtc,
                            signal.TimestampTicks);
                    }
                    else if (signal.Kind == SignalKind.StopSession)
                    {
                        FlushTypingBurst(state, signal.TimestampTicks, DateTime.UtcNow);
                        List<TopAppEntry> finalApps = CloseChunkState(state, signal.TimestampTicks);
                        signal.Completion?.TrySetResult(finalApps);
                        stopRequested = true;
                        break;
                    }
                }

                if (stopRequested)
                {
                    break;
                }

                long tickNow = Stopwatch.GetTimestamp();
                if (tickNow - lastFocusPollTicks >= pollIntervalTicks)
                {
                    UpdateForegroundWindowIfChanged(state, tickNow, DateTime.UtcNow);
                    lastFocusPollTicks = tickNow;
                }

                if (_enableTypingBurstCounter && tickNow - lastBurstFlushTicks >= burstIntervalTicks)
                {
                    FlushTypingBurst(state, tickNow, DateTime.UtcNow);
                    lastBurstFlushTicks = tickNow;
                }

                using var waitCts = new CancellationTokenSource(ForegroundPollIntervalMs);
                try
                {
                    await channel.Reader.WaitToReadAsync(waitCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected periodic wakeup to poll foreground window and typing burst window.
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(nameof(InputTelemetryRecorder), "Telemetry consumer loop failed unexpectedly.", ex);
            CloseChunkState(state, Stopwatch.GetTimestamp());
        }
    }

    private ChunkTelemetryState OpenChunkState(
        string eventsPath,
        Rectangle captureBounds,
        DateTime chunkStartedUtc,
        long chunkStartTicks)
    {
        string? dir = Path.GetDirectoryName(eventsPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var writer = new StreamWriter(
            new FileStream(eventsPath, FileMode.Create, FileAccess.Write, FileShare.Read, 65536),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = false
        };

        var state = new ChunkTelemetryState(writer, captureBounds, chunkStartedUtc, chunkStartTicks);
        ActiveWindowInfo initialWindow = _windowTracker.GetForegroundWindowInfo();
        state.CurrentWindow = initialWindow;
        state.LastFocusTicks = chunkStartTicks;
        state.RecordFocusStart(initialWindow);

        WriteEvent(state, new TelemetryEvent
        {
            Type = "window_focus",
            OffsetMs = 0,
            TimestampUtc = chunkStartedUtc,
            ProcessName = initialWindow.ProcessName,
            WindowTitle = initialWindow.WindowTitle,
            Hwnd = initialWindow.HwndHex
        });

        writer.Flush();
        return state;
    }

    private void UpdateForegroundWindowIfChanged(ChunkTelemetryState state, long nowTicks, DateTime utcNow)
    {
        ActiveWindowInfo current = _windowTracker.GetForegroundWindowInfo();
        if (current.Handle == state.CurrentWindow.Handle
            && string.Equals(current.WindowTitle, state.CurrentWindow.WindowTitle, StringComparison.Ordinal))
        {
            return;
        }

        state.AccumulateActiveTime(state.CurrentWindow.ProcessName, nowTicks);
        state.CurrentWindow = current;
        state.LastFocusTicks = nowTicks;
        state.RecordFocusStart(current);

        WriteEvent(state, new TelemetryEvent
        {
            Type = "window_focus",
            OffsetMs = ComputeOffsetMs(state.ChunkStartTicks, nowTicks),
            TimestampUtc = utcNow,
            ProcessName = current.ProcessName,
            WindowTitle = current.WindowTitle,
            Hwnd = current.HwndHex
        });
    }

    private void WriteMouseSignal(ChunkTelemetryState state, TelemetrySignal signal)
    {
        Rectangle bounds = state.CaptureBounds;
        double nx = Math.Round(Math.Clamp((signal.X - bounds.Left) / (double)Math.Max(1, bounds.Width), 0.0, 1.0), 4);
        double ny = Math.Round(Math.Clamp((signal.Y - bounds.Top) / (double)Math.Max(1, bounds.Height), 0.0, 1.0), 4);
        long offsetMs = ComputeOffsetMs(state.ChunkStartTicks, signal.TimestampTicks);

        TelemetryEvent evt = signal.MouseKind switch
        {
            MouseInputKind.Click => new TelemetryEvent
            {
                Type = "mouse_click",
                OffsetMs = offsetMs,
                TimestampUtc = signal.UtcTime,
                Button = signal.Button,
                X = signal.X,
                Y = signal.Y,
                Nx = nx,
                Ny = ny,
                WindowTitle = state.CurrentWindow.WindowTitle,
                ProcessName = state.CurrentWindow.ProcessName
            },
            MouseInputKind.Wheel => new TelemetryEvent
            {
                Type = "mouse_wheel",
                OffsetMs = offsetMs,
                TimestampUtc = signal.UtcTime,
                Delta = signal.WheelDelta,
                Direction = signal.WheelDelta >= 0 ? "up" : "down",
                X = signal.X,
                Y = signal.Y,
                Nx = nx,
                Ny = ny,
                WindowTitle = state.CurrentWindow.WindowTitle,
                ProcessName = state.CurrentWindow.ProcessName
            },
            _ => new TelemetryEvent
            {
                Type = "mouse_move",
                OffsetMs = offsetMs,
                TimestampUtc = signal.UtcTime,
                X = signal.X,
                Y = signal.Y,
                Nx = nx,
                Ny = ny
            }
        };

        WriteEvent(state, evt);
    }

    private void FlushTypingBurst(ChunkTelemetryState state, long nowTicks, DateTime utcNow)
    {
        long count = Interlocked.Exchange(ref _typingBurstCount, 0);
        if (!_enableTypingBurstCounter || count <= 0)
        {
            return;
        }

        WriteEvent(state, new TelemetryEvent
        {
            Type = "typing_burst",
            OffsetMs = ComputeOffsetMs(state.ChunkStartTicks, nowTicks),
            TimestampUtc = utcNow,
            KeyCount = (int)Math.Min(int.MaxValue, count),
            WindowSec = _typingBurstWindowSeconds,
            ProcessName = state.CurrentWindow.ProcessName,
            WindowTitle = state.CurrentWindow.WindowTitle
        });
    }

    private static void WriteEvent(ChunkTelemetryState state, TelemetryEvent evt)
    {
        string json = JsonSerializer.Serialize(evt, JsonLineOptions);
        state.Writer.WriteLine(json);
        state.EventsSinceFlush++;
        if (state.EventsSinceFlush >= 16 || evt.Type != "mouse_move")
        {
            state.Writer.Flush();
            state.EventsSinceFlush = 0;
        }
    }

    private static List<TopAppEntry> CloseChunkState(ChunkTelemetryState state, long closingTicks)
    {
        state.AccumulateActiveTime(state.CurrentWindow.ProcessName, closingTicks);

        try
        {
            state.Writer.Flush();
        }
        catch
        {
            // Ignore flush issues on shutdown.
        }

        state.Writer.Dispose();
        return state.BuildTopApps();
    }

    private static long ComputeOffsetMs(long chunkStartTicks, long timestampTicks)
    {
        long delta = timestampTicks - chunkStartTicks;
        if (delta <= 0)
        {
            return 0;
        }

        return (long)(delta * 1000.0 / Stopwatch.Frequency);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_isRunning)
        {
            StopSessionAsync().GetAwaiter().GetResult();
        }
    }

    private enum SignalKind
    {
        Mouse,
        RotateChunk,
        StopSession
    }

    private enum MouseInputKind
    {
        Move,
        Click,
        Wheel
    }

    private sealed class TelemetrySignal
    {
        public SignalKind Kind { get; init; }
        public MouseInputKind MouseKind { get; init; }
        public int X { get; init; }
        public int Y { get; init; }
        public string? Button { get; init; }
        public int WheelDelta { get; init; }
        public long TimestampTicks { get; init; }
        public DateTime UtcTime { get; init; }
        public string? NextEventsPath { get; init; }
        public Rectangle NextCaptureBounds { get; init; }
        public DateTime NextChunkStartedUtc { get; init; }
        public TaskCompletionSource<List<TopAppEntry>>? Completion { get; init; }

        public static TelemetrySignal CreateMouse(
            MouseInputKind mouseKind,
            int x,
            int y,
            string? button,
            int wheelDelta,
            long timestampTicks,
            DateTime utcTime) => new()
        {
            Kind = SignalKind.Mouse,
            MouseKind = mouseKind,
            X = x,
            Y = y,
            Button = button,
            WheelDelta = wheelDelta,
            TimestampTicks = timestampTicks,
            UtcTime = utcTime
        };

        public static TelemetrySignal CreateRotate(
            string nextEventsPath,
            Rectangle nextCaptureBounds,
            DateTime nextChunkStartedUtc,
            long timestampTicks,
            TaskCompletionSource<List<TopAppEntry>> completion) => new()
        {
            Kind = SignalKind.RotateChunk,
            NextEventsPath = nextEventsPath,
            NextCaptureBounds = nextCaptureBounds,
            NextChunkStartedUtc = nextChunkStartedUtc,
            TimestampTicks = timestampTicks,
            Completion = completion
        };

        public static TelemetrySignal CreateStop(long timestampTicks, TaskCompletionSource<List<TopAppEntry>> completion) => new()
        {
            Kind = SignalKind.StopSession,
            TimestampTicks = timestampTicks,
            Completion = completion
        };
    }

    private sealed class ChunkTelemetryState
    {
        private readonly Dictionary<string, AppAccumulator> _apps = new(StringComparer.OrdinalIgnoreCase);

        public ChunkTelemetryState(StreamWriter writer, Rectangle captureBounds, DateTime chunkStartedUtc, long chunkStartTicks)
        {
            Writer = writer;
            CaptureBounds = captureBounds;
            ChunkStartedUtc = chunkStartedUtc;
            ChunkStartTicks = chunkStartTicks;
            LastFocusTicks = chunkStartTicks;
        }

        public StreamWriter Writer { get; }
        public Rectangle CaptureBounds { get; }
        public DateTime ChunkStartedUtc { get; }
        public long ChunkStartTicks { get; }
        public ActiveWindowInfo CurrentWindow { get; set; }
        public long LastFocusTicks { get; set; }
        public int EventsSinceFlush { get; set; }

        public void RecordFocusStart(ActiveWindowInfo info)
        {
            string key = string.IsNullOrWhiteSpace(info.ProcessName) ? "unknown" : info.ProcessName;
            if (!_apps.TryGetValue(key, out AppAccumulator? acc))
            {
                acc = new AppAccumulator(key);
                _apps[key] = acc;
            }

            acc.FocusCount++;
            if (!string.IsNullOrWhiteSpace(info.WindowTitle) && acc.SampleTitles.Count < 5 && !acc.SampleTitles.Contains(info.WindowTitle))
            {
                acc.SampleTitles.Add(info.WindowTitle);
            }
        }

        public void AccumulateActiveTime(string processName, long nowTicks)
        {
            long elapsedTicks = Math.Max(0, nowTicks - LastFocusTicks);
            double seconds = elapsedTicks / (double)Stopwatch.Frequency;
            string key = string.IsNullOrWhiteSpace(processName) ? "unknown" : processName;

            if (!_apps.TryGetValue(key, out AppAccumulator? acc))
            {
                acc = new AppAccumulator(key);
                _apps[key] = acc;
            }

            acc.ActiveSeconds += seconds;
        }

        public List<TopAppEntry> BuildTopApps()
        {
            return _apps.Values
                .OrderByDescending(a => a.ActiveSeconds)
                .Take(15)
                .Select(a => new TopAppEntry
                {
                    ProcessName = a.ProcessName,
                    ActiveSeconds = Math.Round(a.ActiveSeconds, 2),
                    FocusCount = a.FocusCount,
                    SampleTitles = a.SampleTitles.ToList()
                })
                .ToList();
        }

        private sealed class AppAccumulator
        {
            public AppAccumulator(string processName) => ProcessName = processName;
            public string ProcessName { get; }
            public double ActiveSeconds { get; set; }
            public int FocusCount { get; set; }
            public List<string> SampleTitles { get; } = new();
        }
    }
}
