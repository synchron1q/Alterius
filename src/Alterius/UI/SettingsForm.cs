using Alterius.Capture.Audio;
using Alterius.Core;
using Alterius.Models;
using Alterius.Upload;

namespace Alterius.UI;

/// <summary>
/// Settings dialog allowing configuration of SMB share credentials (DPAPI-protected),
/// video/chunk parameters, microphone selection, local buffer limits, and autostart flag.
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly ConfigManager _configManager;
    private readonly SmbConnectionManager _connectionManager;
    private readonly FileLogger _logger;
    private readonly AppConfig _originalConfig;
    private readonly bool _hasSavedPassword;

    private TextBox _txtSmbPath = null!;
    private TextBox _txtSmbUser = null!;
    private TextBox _txtSmbPassword = null!;
    private CheckBox _chkClearPassword = null!;
    private Button _btnTestSmb = null!;
    private Label _lblSmbStatus = null!;

    private NumericUpDown _numChunkMinutes = null!;
    private NumericUpDown _numFps = null!;
    private ComboBox _cmbResolution = null!;
    private CheckBox _chkAllMonitors = null!;
    private TextBox _txtFfmpegPath = null!;

    private CheckBox _chkRecordMic = null!;
    private ComboBox _cmbMicrophone = null!;
    private CheckBox _chkRecordSystem = null!;

    private TextBox _txtBufferDir = null!;
    private NumericUpDown _numMaxBufferGb = null!;
    private ComboBox _cmbOverflowPolicy = null!;
    private CheckBox _chkAutoStart = null!;
    private CheckBox _chkTypingBurst = null!;

    private Button _btnSave = null!;
    private Button _btnCancel = null!;

    public SettingsForm(
        ConfigManager configManager,
        SmbConnectionManager connectionManager,
        FileLogger logger)
    {
        _configManager = configManager;
        _connectionManager = connectionManager;
        _logger = logger;
        _originalConfig = configManager.Current;
        _hasSavedPassword = !string.IsNullOrWhiteSpace(_originalConfig.SmbPasswordEncrypted);

        BuildLayout();
        LoadValues();
    }

    private void BuildLayout()
    {
        Text = "Настройки Alterius";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScroll = true;
        ClientSize = new Size(680, 750);
        Padding = new Padding(12);

        var rootLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5
        };
        rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        rootLayout.Controls.Add(BuildSmbGroup(), 0, 0);
        rootLayout.Controls.Add(BuildVideoGroup(), 0, 1);
        rootLayout.Controls.Add(BuildAudioGroup(), 0, 2);
        rootLayout.Controls.Add(BuildBufferGroup(), 0, 3);
        rootLayout.Controls.Add(BuildButtonsBar(), 0, 4);

        Controls.Add(rootLayout);
        AcceptButton = _btnSave;
        CancelButton = _btnCancel;
    }

    private GroupBox BuildSmbGroup()
    {
        var group = new GroupBox
        {
            Text = "Сетевая SMB-шара для выгрузки",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(10)
        };

        var grid = CreateTwoColumnGrid();

        _txtSmbPath = new TextBox { Dock = DockStyle.Fill, PlaceholderText = @"\\SERVER\AlteriusShare" };
        _txtSmbUser = new TextBox { Dock = DockStyle.Fill, PlaceholderText = @"DOMAIN\username (пусто = текущий пользователь)" };
        _txtSmbPassword = new TextBox
        {
            Dock = DockStyle.Fill,
            UseSystemPasswordChar = true,
            PlaceholderText = _hasSavedPassword ? "Пароль сохранён (оставьте пустым чтобы не менять)" : "Пароль SMB"
        };

        _chkClearPassword = new CheckBox
        {
            Text = "Очистить сохранённый пароль",
            AutoSize = true,
            Visible = _hasSavedPassword
        };
        _chkClearPassword.CheckedChanged += (_, _) => _txtSmbPassword.Enabled = !_chkClearPassword.Checked;

        _btnTestSmb = new Button
        {
            Text = "Проверить подключение",
            AutoSize = true
        };
        _btnTestSmb.Click += OnTestSmbClick;

        _lblSmbStatus = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(360, 40),
            Margin = new Padding(8, 6, 0, 0)
        };

        var testFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true
        };
        testFlow.Controls.Add(_btnTestSmb);
        testFlow.Controls.Add(_lblSmbStatus);

        AddGridRow(grid, 0, "Путь к SMB-шаре:", _txtSmbPath);
        AddGridRow(grid, 1, "Логин (опционально):", _txtSmbUser);
        AddGridRow(grid, 2, "Пароль (DPAPI):", _txtSmbPassword);

        if (_hasSavedPassword)
        {
            AddGridRow(grid, 3, string.Empty, _chkClearPassword);
            AddGridRow(grid, 4, string.Empty, testFlow);
        }
        else
        {
            AddGridRow(grid, 3, string.Empty, testFlow);
        }

        group.Controls.Add(grid);
        return group;
    }

    private GroupBox BuildVideoGroup()
    {
        var group = new GroupBox
        {
            Text = "Захват экрана и чанки",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(10)
        };

        var grid = CreateTwoColumnGrid();

        _numChunkMinutes = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 120,
            Width = 90
        };

        _numFps = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 5,
            Width = 90
        };

        _cmbResolution = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDown,
            Width = 150
        };
        _cmbResolution.Items.AddRange(new object[] { "1280x720", "1920x1080" });

        _chkAllMonitors = new CheckBox
        {
            Text = "Захватывать все мониторы (иначе только основной)",
            AutoSize = true
        };

        _txtFfmpegPath = new TextBox { Dock = DockStyle.Fill };
        var btnBrowseFfmpeg = new Button { Text = "...", Width = 34 };
        btnBrowseFfmpeg.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog
            {
                Title = "Выберите ffmpeg.exe",
                Filter = "FFmpeg executable (ffmpeg.exe)|ffmpeg.exe|All files (*.*)|*.*"
            };
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                _txtFfmpegPath.Text = dlg.FileName;
            }
        };

        Panel ffmpegPanel = CreatePathPickerPanel(_txtFfmpegPath, btnBrowseFfmpeg);

        AddGridRow(grid, 0, "Длина чанка (минут):", _numChunkMinutes);
        AddGridRow(grid, 1, "Частота кадров (1-5 FPS):", _numFps);
        AddGridRow(grid, 2, "Разрешение видео:", _cmbResolution);
        AddGridRow(grid, 3, "Мониторы:", _chkAllMonitors);
        AddGridRow(grid, 4, "Путь к ffmpeg.exe:", ffmpegPanel);

        group.Controls.Add(grid);
        return group;
    }

    private GroupBox BuildAudioGroup()
    {
        var group = new GroupBox
        {
            Text = "Аудио (Opus 32 kbps)",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(10)
        };

        var grid = CreateTwoColumnGrid();

        _chkRecordMic = new CheckBox
        {
            Text = "Записывать микрофон (audio_mic.ogg)",
            AutoSize = true
        };
        _chkRecordMic.CheckedChanged += (_, _) => _cmbMicrophone.Enabled = _chkRecordMic.Checked;

        _cmbMicrophone = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Fill
        };

        _chkRecordSystem = new CheckBox
        {
            Text = "Записывать системный звук / собеседников (audio_sys.ogg)",
            AutoSize = true
        };

        AddGridRow(grid, 0, "Микрофон:", _chkRecordMic);
        AddGridRow(grid, 1, "Устройство микрофона:", _cmbMicrophone);
        AddGridRow(grid, 2, "Системный звук:", _chkRecordSystem);

        group.Controls.Add(grid);
        return group;
    }

    private GroupBox BuildBufferGroup()
    {
        var group = new GroupBox
        {
            Text = "Буфер и поведение агента",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(10)
        };

        var grid = CreateTwoColumnGrid();

        _txtBufferDir = new TextBox { Dock = DockStyle.Fill };
        var btnBrowseBuffer = new Button { Text = "...", Width = 34 };
        btnBrowseBuffer.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "Выберите локальную папку буфера чанков",
                SelectedPath = _txtBufferDir.Text
            };
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                _txtBufferDir.Text = dlg.SelectedPath;
            }
        };

        Panel bufferPanel = CreatePathPickerPanel(_txtBufferDir, btnBrowseBuffer);

        _numMaxBufferGb = new NumericUpDown
        {
            DecimalPlaces = 1,
            Increment = 0.5m,
            Minimum = 0.5m,
            Maximum = 1000m,
            Width = 90
        };

        _cmbOverflowPolicy = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 240
        };
        _cmbOverflowPolicy.Items.AddRange(new object[]
        {
            "Удалять старые чанки",
            "Ставить запись на паузу"
        });

        _chkAutoStart = new CheckBox
        {
            Text = "Автоматически начинать запись при запуске агента (AutoStartRecording)",
            AutoSize = true
        };

        _chkTypingBurst = new CheckBox
        {
            Text = "Считать интенсивность печати за 10 сек (без записи клавиш)",
            AutoSize = true
        };

        AddGridRow(grid, 0, "Локальный буфер:", bufferPanel);
        AddGridRow(grid, 1, "Лимит буфера (ГБ):", _numMaxBufferGb);
        AddGridRow(grid, 2, "При переполнении:", _cmbOverflowPolicy);
        AddGridRow(grid, 3, "Автостарт:", _chkAutoStart);
        AddGridRow(grid, 4, "Телеметрия печати:", _chkTypingBurst);

        group.Controls.Add(grid);
        return group;
    }

    private FlowLayoutPanel BuildButtonsBar()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 0)
        };

        _btnCancel = new Button { Text = "Отмена", Width = 100, Height = 30, DialogResult = DialogResult.Cancel };
        _btnSave = new Button { Text = "Сохранить", Width = 110, Height = 30 };
        _btnSave.Click += OnSaveClick;

        panel.Controls.Add(_btnCancel);
        panel.Controls.Add(_btnSave);
        return panel;
    }

    private void LoadValues()
    {
        _txtSmbPath.Text = _originalConfig.SmbSharePath;
        _txtSmbUser.Text = _originalConfig.SmbUsername;

        _numChunkMinutes.Value = Math.Clamp(_originalConfig.ChunkDurationMinutes, 1, 120);
        _numFps.Value = Math.Clamp(_originalConfig.VideoFps, 1, 5);
        _cmbResolution.Text = _originalConfig.VideoResolution;
        _chkAllMonitors.Checked = _originalConfig.CaptureAllMonitors;
        _txtFfmpegPath.Text = _originalConfig.FfmpegPath;

        _chkRecordMic.Checked = _originalConfig.RecordMicrophone;
        _chkRecordSystem.Checked = _originalConfig.RecordSystemAudio;
        PopulateMicrophoneDropdown(_originalConfig.MicrophoneDeviceId);
        _cmbMicrophone.Enabled = _chkRecordMic.Checked;

        _txtBufferDir.Text = _originalConfig.LocalBufferDir;
        _numMaxBufferGb.Value = (decimal)Math.Clamp(_originalConfig.MaxLocalBufferGb, 0.5, 1000.0);
        _cmbOverflowPolicy.SelectedIndex = _originalConfig.BufferOverflowPolicy == BufferOverflowPolicy.PauseRecording ? 1 : 0;
        _chkAutoStart.Checked = _originalConfig.AutoStartRecording;
        _chkTypingBurst.Checked = _originalConfig.EnableTypingBurstCounter;
    }

    private void PopulateMicrophoneDropdown(string? selectedDeviceId)
    {
        _cmbMicrophone.Items.Clear();
        var defaultChoice = new MicrophoneChoice(null, "По умолчанию (системный микрофон)");
        _cmbMicrophone.Items.Add(defaultChoice);

        int selectedIndex = 0;
        foreach (AudioDeviceInfo dev in DualAudioRecorder.GetCaptureDevices(_logger))
        {
            var choice = new MicrophoneChoice(dev.Id, dev.FriendlyName);
            int idx = _cmbMicrophone.Items.Add(choice);
            if (!string.IsNullOrWhiteSpace(selectedDeviceId)
                && string.Equals(dev.Id, selectedDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                selectedIndex = idx;
            }
        }

        _cmbMicrophone.SelectedIndex = selectedIndex;
    }

    private async void OnTestSmbClick(object? sender, EventArgs e)
    {
        _btnTestSmb.Enabled = false;
        _lblSmbStatus.ForeColor = SystemColors.ControlText;
        _lblSmbStatus.Text = "Проверка подключения...";

        string sharePath = _txtSmbPath.Text.Trim();
        string username = _txtSmbUser.Text.Trim();
        string password = ResolveEffectivePlainPassword();

        try
        {
            (bool ok, string msg) = await Task.Run(() =>
            {
                bool success = _connectionManager.TestConnection(sharePath, username, password, out string message);
                return (success, message);
            });

            _lblSmbStatus.ForeColor = ok ? Color.DarkGreen : Color.Firebrick;
            _lblSmbStatus.Text = msg;
        }
        catch (Exception ex)
        {
            _lblSmbStatus.ForeColor = Color.Firebrick;
            _lblSmbStatus.Text = ex.Message;
        }
        finally
        {
            _btnTestSmb.Enabled = true;
        }
    }

    private void OnSaveClick(object? sender, EventArgs e)
    {
        AppConfig updated = _originalConfig.Clone();
        updated.SmbSharePath = _txtSmbPath.Text.Trim();
        updated.SmbUsername = _txtSmbUser.Text.Trim();

        if (_chkClearPassword.Checked)
        {
            updated.SmbPasswordEncrypted = string.Empty;
        }
        else if (!string.IsNullOrEmpty(_txtSmbPassword.Text))
        {
            updated.SmbPasswordEncrypted = DpapiHelper.Encrypt(_txtSmbPassword.Text);
        }

        updated.ChunkDurationMinutes = (int)_numChunkMinutes.Value;
        updated.VideoFps = (int)_numFps.Value;
        updated.VideoResolution = _cmbResolution.Text.Trim();
        updated.CaptureAllMonitors = _chkAllMonitors.Checked;
        updated.FfmpegPath = _txtFfmpegPath.Text.Trim();

        updated.RecordMicrophone = _chkRecordMic.Checked;
        updated.RecordSystemAudio = _chkRecordSystem.Checked;
        updated.MicrophoneDeviceId = (_cmbMicrophone.SelectedItem as MicrophoneChoice)?.DeviceId;

        updated.LocalBufferDir = _txtBufferDir.Text.Trim();
        updated.MaxLocalBufferGb = (double)_numMaxBufferGb.Value;
        updated.BufferOverflowPolicy = _cmbOverflowPolicy.SelectedIndex == 1
            ? BufferOverflowPolicy.PauseRecording
            : BufferOverflowPolicy.DeleteOldest;
        updated.AutoStartRecording = _chkAutoStart.Checked;
        updated.EnableTypingBurstCounter = _chkTypingBurst.Checked;

        updated.Normalize();
        _configManager.Save(updated);

        DialogResult = DialogResult.OK;
        Close();
    }

    private string ResolveEffectivePlainPassword()
    {
        if (_chkClearPassword.Checked)
        {
            return string.Empty;
        }

        if (!string.IsNullOrEmpty(_txtSmbPassword.Text))
        {
            return _txtSmbPassword.Text;
        }

        return _hasSavedPassword ? DpapiHelper.Decrypt(_originalConfig.SmbPasswordEncrypted) : string.Empty;
    }

    private static TableLayoutPanel CreateTwoColumnGrid()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 185));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return grid;
    }

    private static void AddGridRow(TableLayoutPanel grid, int row, string labelText, Control control)
    {
        if (grid.RowCount <= row)
        {
            grid.RowCount = row + 1;
        }

        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        if (!string.IsNullOrEmpty(labelText))
        {
            var label = new Label
            {
                Text = labelText,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 6, 8, 4)
            };
            grid.Controls.Add(label, 0, row);
        }

        control.Margin = new Padding(0, 3, 0, 4);
        grid.Controls.Add(control, 1, row);
    }

    private static Panel CreatePathPickerPanel(TextBox textBox, Button browseButton)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            Margin = new Padding(0)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        browseButton.Margin = new Padding(6, 0, 0, 0);
        panel.Controls.Add(textBox, 0, 0);
        panel.Controls.Add(browseButton, 1, 0);
        return panel;
    }

    private sealed record MicrophoneChoice(string? DeviceId, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }
}
