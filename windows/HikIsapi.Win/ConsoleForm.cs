using System.Drawing.Drawing2D;

namespace HikIsapi;

public sealed class ConsoleForm : Form
{
    private readonly ComboBox _ip = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 315 };
    private readonly TextBox _network = new();
    private readonly TextBox _user = new() { Width = 80, Text = "admin" };
    private readonly TextBox _password = new() { Width = 105, PasswordChar = '*' };
    private readonly Button _showPassword = new() { Size = new Size(58, 26), Text = "顯示" };
    private readonly TextBox _alert = new() { Width = 75, Text = "220.0" };
    private readonly TextBox _alarm = new() { Width = 75, Text = "220.0" };
    private readonly ComboBox _method = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 78 };
    private readonly TextBox _path = new() { Width = 250, Text = ConsoleLaunch.DeviceInfoPath };
    private readonly TextBox _body = new();
    private readonly Button _send = new() { Size = new Size(72, 28), Text = "送出" };
    private readonly Button _stop = new() { Size = new Size(72, 28), Text = "停止", Enabled = false };
    private readonly TextBox _log = new();
    private readonly ComboBox _channel = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button _liveToggle = new() { Size = new Size(130, 30), Text = "啟動串流" };
    private readonly Label _liveStatus = new();
    private readonly PictureBox _picture = new();
    private readonly System.Windows.Forms.Timer _liveTimer = new() { Interval = 1000 };
    private readonly DigestHttp _snapshots = new(TimeSpan.FromMilliseconds(2000));
    private readonly ProcessRunner _runner = new();
    private readonly List<Control> _lockables = new();
    private readonly string _settingsPath = UiSettingsStore.DefaultPath();
    private readonly string _jobDirectory = Path.Combine(Path.GetTempPath(), "hik-isapi-console");
    private readonly SplitContainer _split = new()
    {
        Dock = DockStyle.Fill,
        Orientation = Orientation.Horizontal,
        SplitterWidth = 6,
    };
    private CancellationTokenSource? _runCts;
    private bool _busy;
    private bool _fetching;
    private int _consecutiveFailures;

    public ConsoleForm()
    {
        AutoScaleMode = AutoScaleMode.None;
        Font = UiFont();
        Text = "海康 ISAPI 操作台";
        Size = new Size(1160, 900);
        MinimumSize = new Size(1160, 760);
        StartPosition = FormStartPosition.CenterScreen;
        BuildLayout();
        _liveTimer.Tick += LiveTimer_Tick;
        Load += (_, _) => LoadSettings();
        FormClosing += OnFormClosing;
    }

    private void BuildLayout()
    {
        var menu = new MenuStrip();
        var batch = new ToolStripMenuItem("批次設定檔");
        batch.Click += (_, _) => new BatchForm().Show(this);
        menu.Items.Add(batch);

        Controls.Add(_split);
        Controls.Add(menu);

        var top = _split.Panel1;
        var ipGroup = Group("目標相機 IP 設定 (支援單一 / 範圍 / 分號隔開)", 15, 12, 455, 168);
        top.Controls.Add(ipGroup);
        var ipRow = Row(10, 22, 435, 32);
        ipGroup.Controls.Add(ipRow);
        ipRow.Controls.Add(Caption("相機 IP / 範圍:", 3));
        _ip.Font = Font;
        _ip.Margin = new Padding(0, 2, 0, 0);
        _ip.Items.AddRange(new object[] { "192.168.38.201", "192.168.38.1-10" });
        ipRow.Controls.Add(_ip);
        _network.SetBounds(12, 58, 430, 100);
        _network.Font = new Font("Microsoft JhengHei UI", 8.5f);
        _network.ForeColor = Color.DarkBlue;
        _network.BackColor = SystemColors.Control;
        _network.BorderStyle = BorderStyle.None;
        _network.Multiline = true;
        _network.ReadOnly = true;
        _network.ScrollBars = ScrollBars.Vertical;
        _network.Text = NetworkInfo();
        ipGroup.Controls.Add(_network);

        var auth = Group("登入憑證設定 (Credentials)", 15, 186, 455, 64);
        top.Controls.Add(auth);
        var authRow = Row(10, 22, 435, 34);
        auth.Controls.Add(authRow);
        authRow.Controls.Add(Caption("帳號:", 3));
        authRow.Controls.Add(_user);
        authRow.Controls.Add(Caption("密碼:", 8));
        _password.PlaceholderText = "不會存檔";
        authRow.Controls.Add(_password);
        StyleButton(_showPassword, Color.FromArgb(230, 235, 240), Color.FromArgb(210, 218, 226), Color.FromArgb(190, 200, 210), Color.FromArgb(40, 40, 40), 6, 8.5f);
        _showPassword.Margin = new Padding(6, 1, 0, 0);
        _showPassword.Click += (_, _) => TogglePassword();
        authRow.Controls.Add(_showPassword);

        var temp = Group("欲修改的新數值設定", 15, 256, 455, 64);
        top.Controls.Add(temp);
        var tempRow = Row(10, 22, 435, 34);
        temp.Controls.Add(tempRow);
        tempRow.Controls.Add(Caption("預警(Alert):", 3));
        tempRow.Controls.Add(_alert);
        tempRow.Controls.Add(Caption("警告(Alarm):", 10));
        tempRow.Controls.Add(_alarm);

        var manual = Group("手動 ISAPI（送出＝原樣；套用新溫度＝合併寫入）", 15, 326, 455, 150);
        top.Controls.Add(manual);
        var manualRow = Row(10, 22, 435, 34);
        manual.Controls.Add(manualRow);
        manualRow.Controls.Add(Caption("方法:", 3));
        _method.Items.AddRange(new object[] { "GET", "PUT", "POST", "DELETE" });
        _method.SelectedIndex = 0;
        _method.Margin = new Padding(0, 2, 6, 0);
        manualRow.Controls.Add(_method);
        manualRow.Controls.Add(_path);
        StyleButton(_send, Color.FromArgb(0, 120, 212), Color.FromArgb(16, 110, 190), Color.FromArgb(0, 90, 158), Color.White, 6, 8.5f);
        _send.Margin = new Padding(6, 0, 4, 0);
        _send.Click += async (_, _) => await Guard(() => RunJobAsync(ConsoleTask.Manual));
        manualRow.Controls.Add(_send);
        StyleButton(_stop, Color.FromArgb(90, 98, 104), Color.FromArgb(70, 76, 82), Color.FromArgb(52, 58, 64), Color.White, 6, 8.5f);
        _stop.Margin = new Padding(0, 0, 0, 0);
        _stop.Click += (_, _) => _runCts?.Cancel();
        manualRow.Controls.Add(_stop);
        _body.SetBounds(10, 58, 435, 82);
        _body.Multiline = true;
        _body.ScrollBars = ScrollBars.Vertical;
        _body.AcceptsReturn = true;
        _body.Font = new Font(FontFamily.GenericMonospace, 9f);
        _body.PlaceholderText = "GET 可留空。PUT 貼上要送的 XML。";
        manual.Controls.Add(_body);

        var info = ActionButton("查詢相機型號與序號 (DeviceInfo)", 15, 484, 455, 36,
            Color.FromArgb(0, 120, 212), Color.FromArgb(16, 110, 190), Color.FromArgb(0, 90, 158), 9f);
        info.Click += async (_, _) => await Guard(() => RunJobAsync(ConsoleTask.DeviceInfo));
        top.Controls.Add(info);

        var buttons = new TableLayoutPanel
        {
            Location = new Point(15, 526),
            Size = new Size(455, 40),
            ColumnCount = 3,
            RowCount = 1,
        };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));
        top.Controls.Add(buttons);
        buttons.Controls.Add(TaskButton("即時溫度查詢", 0, Color.FromArgb(0, 130, 137), Color.FromArgb(0, 110, 116), Color.FromArgb(0, 90, 95), ConsoleTask.QueryTemp), 0, 0);
        buttons.Controls.Add(TaskButton("套用新溫度", 2, Color.FromArgb(16, 124, 65), Color.FromArgb(14, 109, 56), Color.FromArgb(11, 90, 46), ConsoleTask.SetTemp), 1, 0);
        buttons.Controls.Add(TaskButton("遠端重啟設備", 2, Color.FromArgb(209, 52, 56), Color.FromArgb(177, 45, 48), Color.FromArgb(142, 36, 38), ConsoleTask.Reboot), 2, 0);

        var liveTitle = new Label
        {
            Location = new Point(485, 12),
            AutoSize = true,
            Text = "Live View 即時影像",
            Font = new Font(Font, FontStyle.Bold),
            Anchor = AnchorStyles.Top | AnchorStyles.Left,
        };
        top.Controls.Add(liveTitle);
        var liveRow = Row(485, 38, 640, 35);
        liveRow.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        top.Controls.Add(liveRow);
        liveRow.Controls.Add(Caption("鏡頭頻道:", 0));
        _channel.Items.AddRange(new object[] { "101 (一般/可見光)", "201 (熱成像通道)" });
        _channel.SelectedIndex = 0;
        _channel.Width = 180;
        _channel.Margin = new Padding(0, 3, 0, 0);
        liveRow.Controls.Add(_channel);
        StyleButton(_liveToggle, Color.FromArgb(43, 87, 154), Color.FromArgb(32, 67, 120), Color.FromArgb(24, 50, 90), Color.White, 6, 9f);
        _liveToggle.Margin = new Padding(10, 0, 0, 0);
        _liveToggle.Click += ToggleLive;
        liveRow.Controls.Add(_liveToggle);
        _liveStatus.SetBounds(485, 76, 640, 22);
        _liveStatus.Text = "狀態: 停止";
        _liveStatus.ForeColor = Color.DimGray;
        _liveStatus.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        top.Controls.Add(_liveStatus);
        _picture.SetBounds(485, 100, 640, 450);
        _picture.SizeMode = PictureBoxSizeMode.Zoom;
        _picture.BorderStyle = BorderStyle.FixedSingle;
        _picture.BackColor = Color.Black;
        _picture.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        top.Controls.Add(_picture);

        _log.SetBounds(15, 6, _split.Panel2.Width - 30, _split.Panel2.Height - 15);
        _log.Multiline = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.ReadOnly = true;
        _log.BackColor = Color.White;
        _log.Font = new Font(FontFamily.GenericMonospace, 9.5f);
        _log.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _split.Panel2.Controls.Add(_log);

        Remember(_ip, _user, _password, _showPassword, _alert, _alarm, _method, _path, _body, _send, info);
        foreach (Control control in buttons.Controls)
            _lockables.Add(control);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            var limit = _split.Height - 80 - _split.SplitterWidth;
            if (limit <= 560)
                return;
            _split.Panel2MinSize = 80;
            _split.Panel1MinSize = 560;
            _split.SplitterDistance = Math.Min(590, limit);
        }
        catch (InvalidOperationException)
        {
            // 視窗尚未完成配置時略過，使用者仍可自行拖拉分隔線。
        }
    }

    private async Task Guard(Func<Task> action)
    {
        if (_busy)
            return;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            AppendLog(ex.Message);
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task RunJobAsync(ConsoleTask task)
    {
        ShowPreset(task);
        var input = ReadInput();
        if (!ConsoleLaunch.TryCreate(
                task,
                input,
                "",
                OperatingSystem.IsWindows(),
                name => PythonCommand.FindOnPath(name),
                _jobDirectory,
                out var plan,
                out var error)
            || plan == null)
        {
            MessageBox.Show(this, error ?? "無法建立命令", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (task == ConsoleTask.Reboot
            && MessageBox.Show(this, $"確定要對 {plan.Addresses.Count} 支攝影機送出重啟嗎？", "重啟確認", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        var method = input.Method.Trim().ToUpperInvariant();
        if (task == ConsoleTask.Manual && method is "PUT" or "POST" or "DELETE"
            && MessageBox.Show(this, $"這會對 {plan.Addresses.Count} 支攝影機原樣送出 {method}。確定？", "送出確認", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        SaveSettings();
        _log.Clear();
        AppendLog($"[任務] {TaskTitle(task)} | 目標: {input.IpInput.Trim()} ({plan.Addresses.Count} 台) | 時間: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        if (task == ConsoleTask.SetTemp)
            AppendLog("套用新溫度會先讀取裝置上的 XML，只改預警與警告再寫回。");
        AppendLog("");
        _runCts = new CancellationTokenSource();
        SetBusy(true);
        try
        {
            var code = await _runner.RunAsync(new ProcessRequest
            {
                FileName = plan.Launch.FileName,
                Arguments = plan.Launch.Arguments,
                WorkingDirectory = plan.Launch.WorkingDirectory,
                Environment = plan.Launch.Environment,
            }, AppendLog, _runCts.Token);
            AppendLog("");
            AppendLog(ConsoleReport.Format(task, plan.ReportPath));
            AppendLog("");
            AppendLog(code switch
            {
                0 => "所有作業已完成。",
                -1 => "已停止。",
                2 => "設定錯誤。",
                _ => "作業結束，有攝影機失敗。",
            });
        }
        finally
        {
            _runCts.Dispose();
            _runCts = null;
            SetBusy(false);
        }
    }

    private void ShowPreset(ConsoleTask task)
    {
        switch (task)
        {
            case ConsoleTask.DeviceInfo:
                ShowRequest("GET", ConsoleLaunch.DeviceInfoPath, "");
                break;
            case ConsoleTask.QueryTemp:
                ShowRequest("GET", ConsoleLaunch.ThermalPath, "");
                break;
            case ConsoleTask.SetTemp:
                ShowRequest("PUT", ConsoleLaunch.ThermalPath, "");
                break;
            case ConsoleTask.Reboot:
                ShowRequest("PUT", ConsoleLaunch.RebootPath, "");
                break;
        }
    }

    private void ShowRequest(string method, string path, string body)
    {
        _method.SelectedItem = method;
        _path.Text = path;
        _body.Text = body;
    }

    private ConsoleInput ReadInput() => new()
    {
        IpInput = _ip.Text,
        Username = _user.Text,
        Password = _password.Text,
        Alert = _alert.Text,
        Alarm = _alarm.Text,
        Method = _method.Text,
        Path = _path.Text,
        Body = _body.Text,
    };

    private void ToggleLive(object? sender, EventArgs e)
    {
        if (_liveTimer.Enabled)
        {
            StopLive("狀態: 停止", Color.DimGray);
            return;
        }
        if (!IpRangeParser.TryParse(_ip.Text, out _, out var error))
        {
            MessageBox.Show(this, error ?? "請先輸入有效的相機 IP。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (string.IsNullOrEmpty(_password.Text))
        {
            MessageBox.Show(this, "請輸入密碼。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _consecutiveFailures = 0;
        _fetching = false;
        _liveTimer.Start();
        _liveToggle.Text = "停止串流";
        _liveToggle.Invalidate();
        _liveStatus.Text = "狀態: 連線中...";
        _liveStatus.ForeColor = Color.DarkOrange;
    }

    private async void LiveTimer_Tick(object? sender, EventArgs e)
    {
        if (_fetching || IsDisposed)
            return;
        _fetching = true;
        try
        {
            if (!IpRangeParser.TryParse(_ip.Text, out var addresses, out _))
                return;
            var channel = SelectedChannel();
            var uri = new Uri($"http://{addresses[0]}/ISAPI/Streaming/channels/{channel}/picture");
            var response = await _snapshots.GetAsync(uri, _user.Text.Trim(), _password.Text, CancellationToken.None);
            if (IsDisposed)
                return;
            if (response.Unauthorized)
            {
                StopLive("狀態: 401 未授權，已停止串流", Color.Red);
                MessageBox.Show(this, "Live View 帳號或密碼錯誤（HTTP 401）。", "認證失敗", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (response.StatusCode is >= 200 and < 300 && response.Body.Length > 0 && TryImage(response.Body, out var image))
            {
                var previous = _picture.Image;
                _picture.Image = image;
                previous?.Dispose();
                _consecutiveFailures = 0;
                _liveStatus.Text = $"狀態: 串流中 ({addresses[0]} - Channel {channel})";
                _liveStatus.ForeColor = Color.Green;
                return;
            }
            _consecutiveFailures++;
            var detail = response.Error ?? (response.StatusCode == 0 ? "連線失敗" : "HTTP " + response.StatusCode);
            if (_consecutiveFailures >= 3 || _picture.Image == null)
            {
                _picture.Image?.Dispose();
                _picture.Image = null;
                _liveStatus.Text = "狀態: 無法取得影像 (" + detail + ")";
                _liveStatus.ForeColor = Color.Red;
            }
            else
            {
                _liveStatus.Text = $"狀態: 重試中 ({addresses[0]}) [{_consecutiveFailures}/3]";
                _liveStatus.ForeColor = Color.DarkOrange;
            }
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
            {
                _liveStatus.Text = "狀態: 無法取得影像 (" + ex.Message + ")";
                _liveStatus.ForeColor = Color.Red;
            }
        }
        finally
        {
            _fetching = false;
        }
    }

    private static bool TryImage(byte[] bytes, out Image? image)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            using var source = Image.FromStream(stream);
            image = new Bitmap(source);
            return true;
        }
        catch (ArgumentException)
        {
            image = null;
            return false;
        }
    }

    private void StopLive(string status, Color color)
    {
        _liveTimer.Stop();
        _consecutiveFailures = 0;
        _fetching = false;
        _liveToggle.Text = "啟動串流";
        _liveToggle.Invalidate();
        _liveStatus.Text = status;
        _liveStatus.ForeColor = color;
        _picture.Image?.Dispose();
        _picture.Image = null;
    }

    private string SelectedChannel()
    {
        var text = _channel.Text;
        var digits = new string(text.TakeWhile(char.IsDigit).ToArray());
        return digits.Length > 0 ? digits : "101";
    }

    private void LoadSettings()
    {
        var settings = UiSettingsStore.LoadOrNew(_settingsPath, out var warning);
        if (!string.IsNullOrWhiteSpace(settings.ConsoleIp))
            _ip.Text = settings.ConsoleIp;
        if (!string.IsNullOrWhiteSpace(settings.ConsoleUsername))
            _user.Text = settings.ConsoleUsername;
        if (!string.IsNullOrWhiteSpace(settings.ConsoleAlert))
            _alert.Text = settings.ConsoleAlert;
        if (!string.IsNullOrWhiteSpace(settings.ConsoleAlarm))
            _alarm.Text = settings.ConsoleAlarm;
        if (_method.Items.Contains(settings.ConsoleMethod))
            _method.SelectedItem = settings.ConsoleMethod;
        if (!string.IsNullOrWhiteSpace(settings.ConsolePath))
            _path.Text = settings.ConsolePath;
        var channel = _channel.Items.Cast<object>().Select(item => item.ToString()).FirstOrDefault(item => item == settings.ConsoleChannel);
        if (channel != null)
            _channel.SelectedItem = channel;
        if (settings.ConsoleWidth >= MinimumSize.Width && settings.ConsoleHeight >= MinimumSize.Height)
            Size = new Size(settings.ConsoleWidth, settings.ConsoleHeight);
        AppendLog("查詢、套用與手動 ISAPI 會呼叫已安裝的 hik-isapi。請先在專案目錄執行：");
        AppendLog("py -3 -m pip install -e .");
        AppendLog("即時影像每秒抓一張 JPEG。101 是可見光，201 是熱成像。");
        if (warning != null)
            AppendLog(warning);
    }

    private void SaveSettings()
    {
        try
        {
            var settings = UiSettingsStore.LoadOrNew(_settingsPath, out _);
            settings.ConsoleIp = _ip.Text.Trim();
            settings.ConsoleUsername = _user.Text.Trim();
            settings.ConsoleAlert = _alert.Text.Trim();
            settings.ConsoleAlarm = _alarm.Text.Trim();
            settings.ConsoleMethod = _method.Text.Trim();
            settings.ConsolePath = _path.Text.Trim();
            settings.ConsoleChannel = _channel.Text.Trim();
            if (WindowState == FormWindowState.Normal)
            {
                settings.ConsoleWidth = Width;
                settings.ConsoleHeight = Height;
            }
            UiSettingsStore.Save(_settingsPath, settings);
        }
        catch (Exception ex)
        {
            AppendLog("無法儲存視窗設定：" + ex.Message);
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_busy)
        {
            var answer = MessageBox.Show(this, "仍在執行，關閉會停止目前的工作。確定要關閉？", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.OK)
            {
                e.Cancel = true;
                return;
            }
            _runCts?.Cancel();
        }
        _liveTimer.Stop();
        _picture.Image?.Dispose();
        _snapshots.Dispose();
        SaveSettings();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        foreach (var control in _lockables)
            control.Enabled = !busy;
        _stop.Enabled = busy;
    }

    private void AppendLog(string text)
    {
        if (IsDisposed)
            return;
        if (InvokeRequired)
        {
            try { BeginInvoke(() => AppendLog(text)); }
            catch (InvalidOperationException) { }
            return;
        }
        _log.AppendText(text + Environment.NewLine);
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    private void TogglePassword()
    {
        if (_password.PasswordChar == '*')
        {
            _password.PasswordChar = '\0';
            _showPassword.Text = "隱藏";
        }
        else
        {
            _password.PasswordChar = '*';
            _showPassword.Text = "顯示";
        }
        _showPassword.Invalidate();
    }

    private Button TaskButton(string text, int leftMargin, Color background, Color hover, Color down, ConsoleTask task)
    {
        var button = new Button
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(leftMargin, 0, leftMargin == 0 ? 2 : 0, 0),
            Text = text,
        };
        if (leftMargin == 2)
            button.Margin = new Padding(2, 0, 2, 0);
        StyleButton(button, background, hover, down, Color.White, 8, 8.5f);
        button.Click += async (_, _) => await Guard(() => RunJobAsync(task));
        return button;
    }

    private Button ActionButton(string text, int x, int y, int width, int height, Color background, Color hover, Color down, float fontSize)
    {
        var button = new Button { Bounds = new Rectangle(x, y, width, height), Text = text };
        StyleButton(button, background, hover, down, Color.White, 8, fontSize);
        return button;
    }

    private void Remember(params Control[] controls)
    {
        foreach (var control in controls)
            _lockables.Add(control);
    }

    private static GroupBox Group(string text, int x, int y, int width, int height) => new()
    {
        Bounds = new Rectangle(x, y, width, height),
        Text = text,
    };

    private static FlowLayoutPanel Row(int x, int y, int width, int height) => new()
    {
        Location = new Point(x, y),
        Size = new Size(width, height),
        FlowDirection = FlowDirection.LeftToRight,
        WrapContents = false,
    };

    private static Label Caption(string text, int left) => new()
    {
        AutoSize = true,
        Text = text,
        Margin = new Padding(left, 6, 4, 3),
    };

    private static string TaskTitle(ConsoleTask task) => task switch
    {
        ConsoleTask.DeviceInfo => "查詢型號與序號",
        ConsoleTask.QueryTemp => "即時溫度查詢",
        ConsoleTask.SetTemp => "套用新溫度",
        ConsoleTask.Reboot => "遠端重啟",
        ConsoleTask.Manual => "手動 ISAPI",
        _ => "作業",
    };

    private static string NetworkInfo()
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("本機網卡與 IP (已連線):");
        try
        {
            var count = 0;
            foreach (var adapter in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                    continue;
                if (adapter.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                    continue;
                foreach (var address in adapter.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
                        || System.Net.IPAddress.IsLoopback(address.Address))
                        continue;
                    builder.AppendLine($" • [{adapter.Name}] {address.Address}");
                    count++;
                }
            }
            if (count == 0)
                builder.AppendLine(" • [無作用中的網路連線]");
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            builder.AppendLine(" • [無法取得本機網卡資訊]");
        }
        return builder.ToString().TrimEnd();
    }

    private static void StyleButton(Button button, Color background, Color hover, Color down, Color foreground, int radius, float fontSize)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.Cursor = Cursors.Hand;
        button.Font = new Font("Microsoft JhengHei UI", fontSize, FontStyle.Bold);
        var current = background;
        button.MouseEnter += (_, _) => { current = hover; button.Invalidate(); };
        button.MouseLeave += (_, _) => { current = background; button.Invalidate(); };
        button.MouseDown += (_, _) => { current = down; button.Invalidate(); };
        button.MouseUp += (_, _) => { current = hover; button.Invalidate(); };
        button.Paint += (_, e) =>
        {
            var graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var parent = new SolidBrush(button.Parent?.BackColor ?? SystemColors.Control))
                graphics.FillRectangle(parent, button.ClientRectangle);
            var rect = new Rectangle(0, 0, button.Width - 1, button.Height - 1);
            if (rect.Width <= 0 || rect.Height <= 0)
                return;
            using var path = Rounded(rect, radius);
            using var brush = new SolidBrush(current);
            graphics.FillPath(brush, path);
            TextRenderer.DrawText(
                graphics,
                button.Text,
                button.Font,
                rect,
                foreground,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        };
    }

    private static GraphicsPath Rounded(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        if (diameter <= 0)
        {
            path.AddRectangle(rect);
            return path;
        }
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Font UiFont()
    {
        foreach (var name in new[] { "Microsoft JhengHei UI", "Microsoft JhengHei", "PMingLiU", "Segoe UI" })
        {
            try { return new Font(name, 9f); }
            catch (ArgumentException) { }
        }
        return SystemFonts.MessageBoxFont ?? new Font(FontFamily.GenericSansSerif, 9f);
    }
}
