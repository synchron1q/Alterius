using System.Diagnostics;
using Alterius.Core;
using Alterius.Models;
using Alterius.Upload;
using Microsoft.Win32;

namespace Alterius.UI;

/// <summary>
/// Headless WinForms <see cref="ApplicationContext"/> managing the system tray icon,
/// dynamic status updates, context menu actions, settings dialog, and graceful exit.
/// </summary>
public sealed class TrayApp : ApplicationContext
{
    private readonly ConfigManager _configManager;
    private readonly RecordingCoordinator _coordinator;
    private readonly SmbUploadWorker _uploadWorker;
    private readonly SmbConnectionManager _connectionManager;
    private readonly LocalQueue _localQueue;
    private readonly FileLogger _logger;

    private readonly Control _uiMarshaler;
    private readonly NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.Timer _uiRefreshTimer;

    private readonly Icon _grayIcon;
    private readonly Icon _redIcon;
    private readonly Icon _yellowIcon;
    private readonly Icon _yellowRecordingIcon;

    private readonly ToolStripMenuItem _toggleRecordingItem;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _settingsItem;
    private readonly ToolStripMenuItem _openBufferItem;
    private readonly ToolStripMenuItem _openLogsItem;
    private readonly ToolStripMenuItem _exitItem;

    private SettingsForm? _openSettingsForm;
    private bool _isExiting;
    private bool _disposed;

    public TrayApp(
        ConfigManager configManager,
        RecordingCoordinator coordinator,
        SmbUploadWorker uploadWorker,
        SmbConnectionManager connectionManager,
        LocalQueue localQueue,
        FileLogger logger)
    {
        _configManager = configManager;
        _coordinator = coordinator;
        _uploadWorker = uploadWorker;
        _connectionManager = connectionManager;
        _localQueue = localQueue;
        _logger = logger;

        _uiMarshaler = new Control();
        _ = _uiMarshaler.Handle;

        _grayIcon = TrayIconFactory.CreateCircleIcon(Color.FromArgb(130, 136, 145), Color.FromArgb(85, 90, 98));
        _redIcon = TrayIconFactory.CreateCircleIcon(Color.FromArgb(220, 48, 48), Color.FromArgb(145, 22, 22));
        _yellowIcon = TrayIconFactory.CreateCircleIcon(Color.FromArgb(242, 182, 34), Color.FromArgb(165, 114, 12));
        _yellowRecordingIcon = TrayIconFactory.CreateCircleIcon(
            Color.FromArgb(242, 182, 34),
            Color.FromArgb(165, 114, 12),
            Color.FromArgb(215, 36, 36));

        _toggleRecordingItem = new ToolStripMenuItem("Начать запись");
        _toggleRecordingItem.Click += async (_, _) => await ToggleRecordingAsync();

        _statusItem = new ToolStripMenuItem("Статус: [Ожидание | Очередь выгрузки: 0]")
        {
            Enabled = false
        };

        _settingsItem = new ToolStripMenuItem("Настройки...");
        _settingsItem.Click += (_, _) => ShowSettingsDialog();

        _openBufferItem = new ToolStripMenuItem("Открыть папку буфера");
        _openBufferItem.Click += (_, _) => OpenFolder(_configManager.Current.LocalBufferDir);

        _openLogsItem = new ToolStripMenuItem("Открыть логи");
        _openLogsItem.Click += (_, _) => OpenFolder(_logger.LogDirectory);

        _exitItem = new ToolStripMenuItem("Выход");
        _exitItem.Click += async (_, _) => await ExitGracefullyAsync();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_toggleRecordingItem);
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_settingsItem);
        menu.Items.Add(_openBufferItem);
        menu.Items.Add(_openLogsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = _grayIcon,
            Text = "Alterius: Ожидание",
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => ShowSettingsDialog();

        _coordinator.StateChanged += _ => MarshalToUi(RefreshTrayVisuals);
        _coordinator.NotificationRequested += (msg, isWarning) => MarshalToUi(() => ShowBalloon(msg, isWarning));
        _uploadWorker.StatusChanged += () => MarshalToUi(RefreshTrayVisuals);
        _uploadWorker.BufferLimitPauseRequested += reason =>
        {
            _ = Task.Run(() => _coordinator.PauseDueToBufferLimitAsync(reason));
        };

        SystemEvents.SessionEnding += OnWindowsSessionEnding;

        _uiRefreshTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _uiRefreshTimer.Tick += (_, _) => RefreshTrayVisuals();
        _uiRefreshTimer.Start();

        RefreshTrayVisuals();
        _ = InitializeStartupAsync();
    }

    private async Task InitializeStartupAsync()
    {
        try
        {
            AppConfig config = _configManager.Current;
            string ffmpegExe = AppPaths.ResolveFfmpegPath(config.FfmpegPath);
            (int tw, int th) = config.GetTargetResolution();

            int recovered = await _localQueue.RecoverOrphanedChunksAsync(
                config.LocalBufferDir,
                ffmpegExe,
                config.AudioBitrateKbps,
                $"{tw}x{th}",
                _coordinator.ActiveSessionId).ConfigureAwait(false);

            if (recovered > 0)
            {
                _logger.Info(nameof(TrayApp), $"Recovered {recovered} incomplete chunk(s) from previous session.");
            }

            _uploadWorker.Start();
            _uploadWorker.TriggerUploadNow();

            if (config.AutoStartRecording)
            {
                await _coordinator.StartRecordingAsync().ConfigureAwait(false);
                MarshalToUi(() => ShowBalloon("Автозапись запущена.", isWarning: false));
            }
        }
        catch (Exception ex)
        {
            _logger.Error(nameof(TrayApp), "Startup initialization failed.", ex);
            MarshalToUi(() => ShowBalloon($"Ошибка старта записи: {ex.Message}", isWarning: true));
        }
        finally
        {
            MarshalToUi(RefreshTrayVisuals);
        }
    }

    private async Task ToggleRecordingAsync()
    {
        if (_isExiting)
        {
            return;
        }

        _toggleRecordingItem.Enabled = false;
        try
        {
            if (_coordinator.State == RecordingState.Recording)
            {
                await _coordinator.StopRecordingAsync();
                ShowBalloon("Запись остановлена. Текущий чанк сохранён.", isWarning: false);
            }
            else
            {
                await _coordinator.StartRecordingAsync();
                if (_coordinator.State == RecordingState.Recording)
                {
                    ShowBalloon("Запись экрана, звука и телеметрии запущена.", isWarning: false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(nameof(TrayApp), "Failed toggling recording state.", ex);
            ShowBalloon($"Не удалось изменить состояние записи: {ex.Message}", isWarning: true);
        }
        finally
        {
            if (!_isExiting)
            {
                _toggleRecordingItem.Enabled = true;
            }

            RefreshTrayVisuals();
        }
    }

    private void ShowSettingsDialog()
    {
        if (_isExiting)
        {
            return;
        }

        if (_openSettingsForm is { IsDisposed: false })
        {
            _openSettingsForm.Activate();
            return;
        }

        using var form = new SettingsForm(_configManager, _connectionManager, _logger);
        _openSettingsForm = form;
        try
        {
            if (form.ShowDialog() == DialogResult.OK)
            {
                RefreshTrayVisuals();
                ShowBalloon("Настройки сохранены.", isWarning: false);
            }
        }
        finally
        {
            _openSettingsForm = null;
        }
    }

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.Warn(nameof(TrayApp), $"Unable to open folder '{path}': {ex.Message}");
            ShowBalloon($"Не удалось открыть папку: {ex.Message}", isWarning: true);
        }
    }

    private async Task ExitGracefullyAsync()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        _uiRefreshTimer.Stop();
        _toggleRecordingItem.Enabled = false;
        _settingsItem.Enabled = false;
        _exitItem.Enabled = false;
        _exitItem.Text = "Завершение и сохранение...";
        _statusItem.Text = "Статус: [Сохранение текущего чанка...]";

        try
        {
            await _coordinator.StopRecordingAsync();
            await _uploadWorker.StopAsync();
        }
        catch (Exception ex)
        {
            _logger.Error(nameof(TrayApp), "Error during graceful shutdown.", ex);
        }
        finally
        {
            _notifyIcon.Visible = false;
            ExitThread();
        }
    }

    private void OnWindowsSessionEnding(object? sender, SessionEndingEventArgs e)
    {
        try
        {
            _logger.Info(nameof(TrayApp), $"Windows session ending ({e.Reason}); finalizing active recording.");
            _coordinator.StopRecordingAsync().GetAwaiter().GetResult();
            _uploadWorker.StopAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.Error(nameof(TrayApp), "Error finalizing recording on session ending.", ex);
        }
    }

    private void RefreshTrayVisuals()
    {
        if (_disposed)
        {
            return;
        }

        RecordingState recState = _coordinator.State;
        int queueCount = _uploadWorker.PendingCount;
        bool hasUploadProblem = _uploadWorker.HasError || recState == RecordingState.PausedBufferFull;
        bool hasQueueBacklog = queueCount > 0 && (_uploadWorker.HasError || string.IsNullOrWhiteSpace(_configManager.Current.SmbSharePath));

        string stateLabel = recState switch
        {
            RecordingState.Recording => $"Идёт запись {_coordinator.SessionElapsed:hh\\:mm\\:ss}",
            RecordingState.Stopping => "Остановка и сохранение...",
            RecordingState.PausedBufferFull => "Пауза (буфер заполнен)",
            _ => "Ожидание"
        };

        _statusItem.Text = $"Статус: [{stateLabel} | Очередь выгрузки: {queueCount}]";

        if (!_isExiting)
        {
            _toggleRecordingItem.Text = recState == RecordingState.Recording ? "Остановить запись" : "Начать запись";
            _toggleRecordingItem.Enabled = recState != RecordingState.Stopping;
        }

        Icon targetIcon;
        if (hasUploadProblem || hasQueueBacklog)
        {
            targetIcon = recState == RecordingState.Recording ? _yellowRecordingIcon : _yellowIcon;
        }
        else if (recState == RecordingState.Recording)
        {
            targetIcon = _redIcon;
        }
        else
        {
            targetIcon = _grayIcon;
        }

        if (!ReferenceEquals(_notifyIcon.Icon, targetIcon))
        {
            _notifyIcon.Icon = targetIcon;
        }

        string tooltip = $"Alterius: {stateLabel} | Очередь: {queueCount}";
        _notifyIcon.Text = tooltip.Length <= 63 ? tooltip : tooltip[..63];
    }

    private void ShowBalloon(string message, bool isWarning)
    {
        if (_disposed || !_notifyIcon.Visible)
        {
            return;
        }

        _notifyIcon.ShowBalloonTip(
            4000,
            "Alterius",
            message,
            isWarning ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    private void MarshalToUi(Action action)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (_uiMarshaler.IsDisposed || !_uiMarshaler.IsHandleCreated)
            {
                return;
            }

            if (_uiMarshaler.InvokeRequired)
            {
                _uiMarshaler.BeginInvoke(action);
            }
            else
            {
                action();
            }
        }
        catch
        {
            // Ignore cross-thread races during teardown.
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _disposed = true;
            SystemEvents.SessionEnding -= OnWindowsSessionEnding;

            _uiRefreshTimer.Stop();
            _uiRefreshTimer.Dispose();

            _notifyIcon.Visible = false;
            _notifyIcon.ContextMenuStrip?.Dispose();
            _notifyIcon.Dispose();

            _grayIcon.Dispose();
            _redIcon.Dispose();
            _yellowIcon.Dispose();
            _yellowRecordingIcon.Dispose();

            _uiMarshaler.Dispose();
        }

        base.Dispose(disposing);
    }
}
