using System.Drawing.Drawing2D;

namespace HikIsapi;

public sealed class ConsoleForm : Form
{
    private readonly ComboBox _ip = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 315 };
    private readonly Label _network = new();
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
    private readonly DigestHttp _snapshots = new(TimeSpan.FromSeconds(5));
    private readonly List<Control> _lockables = new();
    private readonly string _settingsPath = UiSettingsStore.DefaultPath();
    private readonly string _jobDirectory = Path.Combine(Path.GetTempPath(), "hik-isapi-console");
    private readonly SplitContainer _panes = new()
    {
        Dock = DockStyle.Fill,
        Orientation = Orientation.Vertical,
        SplitterWidth = 6,
    };
    private readonly SplitContainer _bands = new()
    {
        Dock = DockStyle.Fill,
        Orientation = Orientation.Horizontal,
        SplitterWidth = 6,
    };
    private readonly SplitContainer _lower = new()
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
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = UiFont();
        Text = "海康 ISAPI 操作台";
        StartPosition = FormStartPosition.CenterScreen;
        BuildLayout();
        _liveTimer.Tick += LiveTimer_Tick;
        Load += (_, _) => LoadSettings();
        FormClosing += OnFormClosing;
    }

    private void BuildLayout()
    {
        var line = TextHeight();
        var menu = new MenuStrip();
        var batch = new ToolStripMenuItem("批次設定檔");
        batch.Click += (_, _) => new BatchForm().Show(this);
        menu.Items.Add(batch);
        MainMenuStrip = menu;

        var logHost = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 4, 12, 8),
        };
        _log.Dock = DockStyle.Fill;
        _log.Multiline = true;
        _log.ScrollBars = ScrollBars.Both;
        _log.WordWrap = false;
        _log.ReadOnly = true;
        _log.BackColor = Color.White;
        _log.Font = new Font(FontFamily.GenericMonospace, 9.5f);
        logHost.Controls.Add(_log);

        _panes.Panel1.Controls.Add(BuildControls());
        _panes.Panel2.Controls.Add(BuildLive());
        _lower.Panel1.Controls.Add(BuildManualHost());
        _lower.Panel2.Controls.Add(logHost);
        _bands.Panel1.Controls.Add(_panes);
        _bands.Panel2.Controls.Add(_lower);
        Controls.Add(_bands);
        Controls.Add(menu);

        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        MinimumSize = new Size(Math.Min(960, Math.Max(720, area.Width - 80)), Math.Min(700, Math.Max(520, area.Height - 80)));
        Size = new Size(
            Math.Min(area.Width - 48, Math.Max(MinimumSize.Width, line * 62)),
            Math.Min(area.Height - 48, Math.Max(MinimumSize.Height, line * 44)));
    }

    private Control BuildControls()
    {
        var line = TextHeight();
        _ip.Items.AddRange(new object[] { "192.168.38.201", "192.168.38.1-10" });
        _network.Font = Font;
        _network.ForeColor = Color.DarkBlue;
        _network.BackColor = SystemColors.Control;
        _network.TextAlign = ContentAlignment.TopLeft;
        _network.AutoSize = false;
        _network.Height = line * 4;
        _network.Text = NetworkInfo();
        _password.PlaceholderText = "不會存檔";
        PrepareField(_ip, line);
        PrepareField(_user, line);
        PrepareField(_password, line);
        PrepareField(_alert, line);
        PrepareField(_alarm, line);
        PrepareField(_method, line);
        PrepareField(_path, line);
        StyleButton(_showPassword, Color.FromArgb(230, 235, 240), Color.FromArgb(210, 218, 226), Color.FromArgb(190, 200, 210), Color.FromArgb(40, 40, 40), 6, 8.5f);
        _showPassword.Click += (_, _) => TogglePassword();
        SizeToText(_showPassword);

        var ipGroup = Group("目標相機 IP（單一、範圍或分號）",
            FlexRow(LabelItem("相機 IP / 範圍"), FlexItem.Grow(_ip, line * 8)),
            FixedBlock(_network));

        var authGroup = Group("登入憑證",
            FlexRow(
                LabelItem("帳號"), FlexItem.Grow(_user, line * 5),
                LabelItem("密碼"), FlexItem.Grow(_password, line * 6),
                FlexItem.Fixed(_showPassword)));

        var tempGroup = Group("欲修改的新數值",
            FlexRow(
                LabelItem("預警 (Alert)"), FlexItem.Grow(_alert, line * 4),
                LabelItem("警告 (Alarm)"), FlexItem.Grow(_alarm, line * 4)));

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var content = Stack(ipGroup, authGroup, tempGroup, BuildActions());
        content.Padding = new Padding(4, 4, 8, 4);
        scroll.Controls.Add(content);
        Remember(_ip, _user, _password, _showPassword, _alert, _alarm);
        return scroll;
    }

    private Control BuildActions()
    {
        var info = TaskButton("查詢相機型號與序號", new Padding(0, 4, 8, 4), Color.FromArgb(0, 120, 212), Color.FromArgb(16, 110, 190), Color.FromArgb(0, 90, 158), ConsoleTask.DeviceInfo);
        var query = TaskButton("即時溫度查詢", new Padding(0, 4, 8, 4), Color.FromArgb(0, 130, 137), Color.FromArgb(0, 110, 116), Color.FromArgb(0, 90, 95), ConsoleTask.QueryTemp);
        var apply = TaskButton("套用新溫度", new Padding(0, 4, 8, 4), Color.FromArgb(16, 124, 65), Color.FromArgb(14, 109, 56), Color.FromArgb(11, 90, 46), ConsoleTask.SetTemp);
        var reboot = TaskButton("遠端重啟設備", new Padding(0, 4, 8, 4), Color.FromArgb(209, 52, 56), Color.FromArgb(177, 45, 48), Color.FromArgb(142, 36, 38), ConsoleTask.Reboot);
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoScroll = false,
            Padding = new Padding(0, 8, 0, 4),
            Margin = new Padding(0),
            Height = TextHeight() * 3,
        };
        bar.Controls.Add(info);
        bar.Controls.Add(query);
        bar.Controls.Add(apply);
        bar.Controls.Add(reboot);
        GrowToChildren(bar);
        Remember(info, query, apply, reboot);
        return bar;
    }

    private Control BuildManualHost()
    {
        var line = TextHeight();
        _method.Items.AddRange(new object[] { "GET", "PUT", "POST", "DELETE" });
        _method.SelectedIndex = 0;
        _body.Multiline = true;
        _body.ScrollBars = ScrollBars.Vertical;
        _body.AcceptsReturn = true;
        _body.Font = new Font(FontFamily.GenericMonospace, 9f);
        _body.PlaceholderText = "XML，GET 可留空";
        _body.Dock = DockStyle.Fill;
        _body.Margin = new Padding(0, 4, 0, 0);
        _body.MinimumSize = new Size(0, line * 2);
        StyleButton(_send, Color.FromArgb(0, 120, 212), Color.FromArgb(16, 110, 190), Color.FromArgb(0, 90, 158), Color.White, 6, 8.5f);
        _send.Click += async (_, _) => await Guard(() => RunJobAsync(ConsoleTask.Manual));
        SizeToText(_send);
        StyleButton(_stop, Color.FromArgb(90, 98, 104), Color.FromArgb(70, 76, 82), Color.FromArgb(52, 58, 64), Color.White, 6, 8.5f);
        _stop.Click += (_, _) => _runCts?.Cancel();
        SizeToText(_stop);
        var hint = new Label
        {
            Text = "送出＝原樣送出。套用溫度＝只改預警與警告。",
            AutoSize = true,
            Dock = DockStyle.Top,
            ForeColor = SystemColors.GrayText,
            UseMnemonic = false,
            MaximumSize = new Size(800, 0),
            Padding = new Padding(0, 2, 0, 2),
        };
        var manualGroup = new GroupBox
        {
            Text = "手動 ISAPI",
            Dock = DockStyle.Fill,
            Padding = new Padding(8, 4, 8, 6),
        };
        var row = FlexRow(
            LabelItem("方法"), FlexItem.Grow(_method, line * 5),
            LabelItem("路徑"), FlexItem.Grow(_path, line * 16),
            FlexItem.Fixed(_send),
            FlexItem.Fixed(_stop));
        var inner = new Panel { Dock = DockStyle.Fill };
        inner.Controls.Add(_body);
        inner.Controls.Add(hint);
        inner.Controls.Add(row);
        manualGroup.Controls.Add(inner);
        manualGroup.Resize += (_, _) =>
        {
            var width = Math.Max(80, manualGroup.ClientSize.Width - 24);
            if (hint.MaximumSize.Width != width)
                hint.MaximumSize = new Size(width, 0);
        };
        Remember(_method, _path, _body, _send);
        return manualGroup;
    }

    private static void GrowToChildren(FlowLayoutPanel panel)
    {
        var busy = false;
        void Fit(object? sender, EventArgs e)
        {
            if (busy)
                return;
            var width = panel.ClientSize.Width - panel.Padding.Horizontal;
            if (width < 40)
                return;
            busy = true;
            try
            {
                var pieces = new RowFlow.Piece[panel.Controls.Count];
                for (var index = 0; index < panel.Controls.Count; index++)
                {
                    var child = panel.Controls[index];
                    pieces[index] = new RowFlow.Piece(
                        NaturalWidth(child),
                        0,
                        child.Margin.Horizontal,
                        child.Margin.Vertical,
                        Math.Max(child.Height, Math.Max(child.MinimumSize.Height, child.Font.Height + 8)),
                        false);
                }

                var laid = RowFlow.Arrange(pieces, Math.Max(40, width - 8));
                panel.PerformLayout();
                var bottom = panel.Padding.Bottom;
                foreach (Control child in panel.Controls)
                    bottom = Math.Max(bottom, child.Bottom + child.Margin.Bottom + panel.Padding.Bottom);
                var height = Math.Max(laid.Height + panel.Padding.Vertical, bottom);
                panel.MinimumSize = new Size(0, height);
                if (panel.Height != height)
                    panel.Height = height;
            }
            finally
            {
                busy = false;
            }
        }
        panel.HandleCreated += Fit;
        panel.Resize += Fit;
        foreach (Control child in panel.Controls)
            child.SizeChanged += Fit;
    }

    private Control BuildLive()
    {
        var line = TextHeight();
        PrepareField(_channel, line);
        _channel.Items.AddRange(new object[] { "101 (一般/可見光)", "201 (熱成像通道)" });
        _channel.SelectedIndex = 0;
        StyleButton(_liveToggle, Color.FromArgb(43, 87, 154), Color.FromArgb(32, 67, 120), Color.FromArgb(24, 50, 90), Color.White, 6, 9f);
        _liveToggle.Margin = new Padding(8, 2, 0, 2);
        _liveToggle.Click += ToggleLive;
        SizeToText(_liveToggle);
        var title = new Label
        {
            Text = "Live View 即時影像",
            Dock = DockStyle.Top,
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Padding = new Padding(0, 4, 0, 4),
            UseMnemonic = false,
        };
        var channel = FlexRow(LabelItem("鏡頭頻道"), FlexItem.Grow(_channel, line * 12), FlexItem.Fixed(_liveToggle));
        _liveStatus.Text = "狀態: 停止";
        _liveStatus.ForeColor = Color.DimGray;
        _liveStatus.Dock = DockStyle.Top;
        _liveStatus.AutoSize = true;
        _liveStatus.Padding = new Padding(0, 2, 0, 4);
        _picture.Dock = DockStyle.Fill;
        _picture.Margin = new Padding(0, 4, 0, 0);
        _picture.SizeMode = PictureBoxSizeMode.Zoom;
        _picture.BorderStyle = BorderStyle.FixedSingle;
        _picture.BackColor = Color.Black;
        var live = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 4, 12, 8) };
        live.Controls.Add(_picture);
        live.Controls.Add(_liveStatus);
        live.Controls.Add(channel);
        live.Controls.Add(title);
        return live;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        PlaceSplitter(_panes, 0.40, 320, 280);
        PlaceSplitter(_bands, 0.56, 220, 180);
        PlaceSplitter(_lower, 0.58, 110, 80);
    }

    private static void PlaceSplitter(SplitContainer split, double portion, int panel1Min, int panel2Min)
    {
        try
        {
            var available = (split.Orientation == Orientation.Vertical ? split.Width : split.Height) - split.SplitterWidth;
            if (available < panel1Min + panel2Min)
                return;
            split.Panel1MinSize = panel1Min;
            split.Panel2MinSize = panel2Min;
            var distance = (int)((available + split.SplitterWidth) * portion);
            var max = available - panel2Min;
            split.SplitterDistance = Math.Max(panel1Min, Math.Min(distance, max));
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
        if (!ConsoleLaunch.TryPrepare(task, input, out var addresses, out var error))
        {
            MessageBox.Show(this, error ?? "無法開始作業", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (task == ConsoleTask.Reboot
            && MessageBox.Show(this, $"確定要對 {addresses.Count} 支攝影機送出重啟嗎？", "重啟確認", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        var method = input.Method.Trim().ToUpperInvariant();
        if (task == ConsoleTask.Manual && method is "PUT" or "POST" or "DELETE"
            && MessageBox.Show(this, $"這會對 {addresses.Count} 支攝影機原樣送出 {method}。確定？", "送出確認", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        SaveSettings();
        _log.Clear();
        AppendLog($"[任務] {TaskTitle(task)} | 目標: {input.IpInput.Trim()} ({addresses.Count} 台) | 時間: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        if (task == ConsoleTask.SetTemp)
            AppendLog("套用新溫度會先讀取裝置上的 XML，只改預警與警告再寫回。");
        AppendLog("");
        _runCts = new CancellationTokenSource();
        SetBusy(true);
        try
        {
            var code = await ConsoleJobs.RunAsync(task, input, _jobDirectory, _runCts.Token);
            AppendLog("");
            AppendLog(ConsoleReport.Format(task, Path.Combine(_jobDirectory, "report.json")));
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
                _consecutiveFailures++;
                if (_picture.Image != null && _consecutiveFailures < 3)
                {
                    _liveStatus.Text = $"狀態: 重試中 ({addresses[0]}) [{_consecutiveFailures}/3]";
                    _liveStatus.ForeColor = Color.DarkOrange;
                    return;
                }
                StopLive("狀態: 401 未授權，已停止串流", Color.Red);
                MessageBox.Show(this, "即時影像被相機拒絕（HTTP 401）。同一組密碼若可以重啟或查詢，代表帳號可用，請改試頻道 201，或確認這台相機允許 ISAPI 抓圖。", "認證失敗", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
        AppendLog("查詢、套用與手動 ISAPI 都在這個程式裡完成，不必另外安裝 Python。");
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

    private Button TaskButton(string text, Padding margin, Color background, Color hover, Color down, ConsoleTask task)
    {
        var button = new Button { Margin = margin, Text = text };
        StyleButton(button, background, hover, down, Color.White, 8, 9f);
        SizeToText(button);
        button.Click += async (_, _) => await Guard(() => RunJobAsync(task));
        return button;
    }

    private void Remember(params Control[] controls)
    {
        foreach (var control in controls)
            _lockables.Add(control);
    }

    private int TextHeight()
    {
        return TextRenderer.MeasureText("中文", Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Height + 2;
    }

    private void PrepareField(Control control, int line)
    {
        control.AutoSize = false;
        control.Height = line + 8;
        control.Margin = new Padding(0, 2, 8, 2);
    }

    private void SizeToText(Button button)
    {
        var em = Math.Max(button.Font.Height, TextHeight());
        var units = 0;
        foreach (var ch in button.Text)
            units += ch > 127 ? em : Math.Max(em / 2, 8);
        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.MinimumSize = new Size(units + em + em / 2, em + 12);
        button.Padding = new Padding(em / 3, 3, em / 3, 3);
    }

    private static FlexItem LabelItem(string text)
    {
        return FlexItem.Fixed(new Label
        {
            Text = text,
            AutoSize = true,
            AutoEllipsis = false,
            UseMnemonic = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 6, 8, 2),
        });
    }

    private Control FlexRow(params FlexItem[] items)
    {
        var row = new Panel
        {
            Dock = DockStyle.Top,
            Margin = new Padding(0),
            Padding = new Padding(0, 1, 0, 1),
        };
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoScroll = false,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        var busy = false;
        void Fit(object? sender, EventArgs e)
        {
            if (busy)
                return;
            var width = flow.ClientSize.Width;
            if (width < 40)
                width = row.ClientSize.Width - row.Padding.Horizontal;
            if (width < 40)
                return;
            busy = true;
            try
            {
                // Leave a few pixels so the flow panel does not wrap the last button
                // just because its client width is one pixel shorter than we measured.
                var usable = Math.Max(40, width - 8);
                var pieces = new RowFlow.Piece[items.Length];
                for (var index = 0; index < items.Length; index++)
                {
                    var control = items[index].Control;
                    pieces[index] = new RowFlow.Piece(
                        items[index].Flex ? 0 : NaturalWidth(control),
                        items[index].Flex ? Math.Max(items[index].MinWidth, 1) : 0,
                        control.Margin.Horizontal,
                        control.Margin.Vertical,
                        Math.Max(control.Height, Math.Max(control.MinimumSize.Height, control.Font.Height + 6)),
                        items[index].Flex);
                }

                var laid = RowFlow.Arrange(pieces, usable);
                for (var index = 0; index < items.Length; index++)
                {
                    if (!items[index].Flex || items[index].Control.Width == laid.Widths[index])
                        continue;
                    items[index].Control.Width = laid.Widths[index];
                }

                flow.PerformLayout();
                var bottom = 0;
                foreach (Control child in flow.Controls)
                    bottom = Math.Max(bottom, child.Bottom + child.Margin.Bottom);
                var height = Math.Max(laid.Height, bottom) + row.Padding.Vertical;
                if (row.Height != height)
                    row.Height = height;
            }
            finally
            {
                busy = false;
            }
        }
        foreach (var item in items)
        {
            flow.Controls.Add(item.Control);
            item.Control.SizeChanged += Fit;
            if (item.Control is Button button)
                button.TextChanged += Fit;
        }
        row.Controls.Add(flow);
        row.Resize += Fit;
        row.HandleCreated += Fit;
        return row;
    }

    private static int NaturalWidth(Control control)
    {
        if (control is Label)
            return TextSpan(control, extra: control.Font.Height);
        var chrome = control is ComboBox or TextBox
            ? control.Font.Height + 8
            : control.Padding.Horizontal + control.Font.Height / 2;
        var preferred = 0;
        try
        {
            preferred = control.GetPreferredSize(Size.Empty).Width;
        }
        catch (ArgumentException)
        {
            preferred = 0;
        }

        return Math.Max(
            control.MinimumSize.Width,
            Math.Max(preferred, Math.Max(control.Width, TextSpan(control, chrome))));
    }

    private static int TextSpan(Control control, int extra)
    {
        if (string.IsNullOrEmpty(control.Text))
            return Math.Max(extra, 0);
        var measured = TextRenderer.MeasureText(
            control.Text,
            control.Font,
            new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
        return measured + Math.Max(extra, 0);
    }

    private static Control FixedBlock(Control control)
    {
        control.Dock = DockStyle.Top;
        control.Margin = new Padding(0, 4, 0, 4);
        return control;
    }

    private Control Group(string title, params Control[] rows)
    {
        var group = new GroupBox
        {
            Text = title,
            Dock = DockStyle.Top,
            Margin = new Padding(0, 0, 0, 8),
        };
        var stack = Stack(rows);
        stack.Dock = DockStyle.None;
        stack.Location = new Point(8, TextHeight() + 6);
        group.Controls.Add(stack);
        void Sync(object? sender, EventArgs e)
        {
            var width = Math.Max(40, group.ClientSize.Width - 16);
            if (stack.Width != width)
                stack.Width = width;
            var height = stack.Height + TextHeight() + 18;
            if (group.Height != height)
                group.Height = height;
        }
        stack.SizeChanged += Sync;
        group.Resize += Sync;
        group.HandleCreated += Sync;
        return group;
    }

    private static Panel Stack(params Control[] rows)
    {
        var panel = new Panel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
        };
        for (var index = rows.Length - 1; index >= 0; index--)
        {
            rows[index].Dock = DockStyle.Top;
            panel.Controls.Add(rows[index]);
        }
        return panel;
    }

    private readonly record struct FlexItem(Control Control, int MinWidth, bool Flex)
    {
        public static FlexItem Grow(Control control, int minWidth) => new(control, minWidth, true);
        public static FlexItem Fixed(Control control) => new(control, 0, false);
    }

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
