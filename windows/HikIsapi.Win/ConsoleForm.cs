using System.Drawing.Drawing2D;

namespace HikIsapi;

public sealed class ConsoleForm : Form
{
    private readonly ComboBox _ip = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 315 };
    private readonly Label _network = new();
    private int _controlsHeight;
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
        var menu = new MenuStrip();
        var batch = new ToolStripMenuItem("批次設定檔");
        batch.Click += (_, _) => new BatchForm().Show(this);
        menu.Items.Add(batch);
        Controls.Add(_split);
        Controls.Add(menu);

        var workspace = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(12, 8, 12, 8),
        };
        var left = LeftColumnWidth();
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, left));
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        workspace.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _split.Panel1.Controls.Add(workspace);
        workspace.Controls.Add(BuildControls(), 0, 0);
        workspace.Controls.Add(BuildLive(), 1, 0);

        var logHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 4, 12, 8) };
        _log.Dock = DockStyle.Fill;
        _log.Multiline = true;
        _log.ScrollBars = ScrollBars.Both;
        _log.WordWrap = false;
        _log.ReadOnly = true;
        _log.BackColor = Color.White;
        _log.Font = new Font(FontFamily.GenericMonospace, 9.5f);
        logHost.Controls.Add(_log);
        _split.Panel2.Controls.Add(logHost);
        MinimumSize = new Size(Math.Max(1100, left + 480), 680);
        Size = new Size(Math.Max(1200, left + 700), Math.Min(980, _controlsHeight + 220));
    }

    private Control BuildControls()
    {
        var line = TextHeight();
        var field = line + 18;
        var chrome = line + 16;
        var ipHeight = chrome + field + line * 4 + 8;
        var authHeight = chrome + field * 2 + 8;
        var tempHeight = chrome + field * 2 + 8;
        var manualHeight = chrome + field * 2 + line + 8 + line * 5;
        var buttonHeight = field + 8;
        _controlsHeight = ipHeight + authHeight + tempHeight + manualHeight + buttonHeight + buttonHeight + 8;

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = new Padding(0, 0, 8, 0) };
        var stack = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 6,
            Dock = DockStyle.Top,
            Height = _controlsHeight,
            Margin = new Padding(0),
            Padding = new Padding(0, 0, 4, 0),
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, ipHeight));
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, authHeight));
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, tempHeight));
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, manualHeight));
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, buttonHeight));
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, buttonHeight));

        _ip.Items.AddRange(new object[] { "192.168.38.201", "192.168.38.1-10" });
        _network.Font = Font;
        _network.ForeColor = Color.DarkBlue;
        _network.BackColor = SystemColors.Control;
        _network.TextAlign = ContentAlignment.TopLeft;
        _network.Text = NetworkInfo();
        var ipLabel = TextWidth("相機 IP / 範圍");
        var ipTable = Grid(2, new ColumnStyle(SizeType.Absolute, ipLabel), new ColumnStyle(SizeType.Percent, 100));
        ipTable.RowCount = 2;
        ipTable.RowStyles.Add(new RowStyle(SizeType.Absolute, field));
        ipTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        ipTable.Controls.Add(FieldLabel("相機 IP / 範圍"), 0, 0);
        DockField(_ip, right: 0);
        ipTable.Controls.Add(_ip, 1, 0);
        _network.Dock = DockStyle.Fill;
        _network.Margin = new Padding(0, 2, 0, 0);
        ipTable.SetColumnSpan(_network, 2);
        ipTable.Controls.Add(_network, 0, 1);
        stack.Controls.Add(Section("目標相機 IP（單一、範圍或分號）", ipTable), 0, 0);

        _password.PlaceholderText = "不會存檔";
        var nameLabel = Math.Max(TextWidth("帳號"), TextWidth("密碼"));
        var toggleWidth = TextWidth("隱藏") + 22;
        var auth = Grid(3,
            new ColumnStyle(SizeType.Absolute, nameLabel),
            new ColumnStyle(SizeType.Percent, 100),
            new ColumnStyle(SizeType.Absolute, toggleWidth));
        auth.RowCount = 2;
        auth.RowStyles.Clear();
        auth.RowStyles.Add(new RowStyle(SizeType.Absolute, field));
        auth.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        auth.Controls.Add(FieldLabel("帳號"), 0, 0);
        DockField(_user, right: 0);
        auth.SetColumnSpan(_user, 2);
        auth.Controls.Add(_user, 1, 0);
        auth.Controls.Add(FieldLabel("密碼"), 0, 1);
        DockField(_password);
        auth.Controls.Add(_password, 1, 1);
        StyleButton(_showPassword, Color.FromArgb(230, 235, 240), Color.FromArgb(210, 218, 226), Color.FromArgb(190, 200, 210), Color.FromArgb(40, 40, 40), 6, 8.5f);
        _showPassword.Dock = DockStyle.Fill;
        _showPassword.Margin = new Padding(8, 4, 0, 4);
        _showPassword.Click += (_, _) => TogglePassword();
        auth.Controls.Add(_showPassword, 2, 1);
        stack.Controls.Add(Section("登入憑證", auth), 0, 1);

        var tempLabel = Math.Max(TextWidth("預警 (Alert)"), TextWidth("警告 (Alarm)"));
        var temp = Grid(2, new ColumnStyle(SizeType.Absolute, tempLabel), new ColumnStyle(SizeType.Percent, 100));
        temp.RowCount = 2;
        temp.RowStyles.Clear();
        temp.RowStyles.Add(new RowStyle(SizeType.Absolute, field));
        temp.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        temp.Controls.Add(FieldLabel("預警 (Alert)"), 0, 0);
        DockField(_alert, right: 0);
        temp.Controls.Add(_alert, 1, 0);
        temp.Controls.Add(FieldLabel("警告 (Alarm)"), 0, 1);
        DockField(_alarm, right: 0);
        temp.Controls.Add(_alarm, 1, 1);
        stack.Controls.Add(Section("欲修改的新數值", temp), 0, 2);

        _method.Items.AddRange(new object[] { "GET", "PUT", "POST", "DELETE" });
        _method.SelectedIndex = 0;
        _body.Multiline = true;
        _body.ScrollBars = ScrollBars.Vertical;
        _body.AcceptsReturn = true;
        _body.Font = new Font(FontFamily.GenericMonospace, 9f);
        _body.PlaceholderText = "XML，GET 可留空";
        var manualLabel = Math.Max(TextWidth("方法"), TextWidth("路徑"));
        var methodWidth = TextWidth("DELETE") + 36;
        var sendWidth = TextWidth("送出") + 28;
        var stopWidth = TextWidth("停止") + 28;
        var manual = Grid(5,
            new ColumnStyle(SizeType.Absolute, manualLabel),
            new ColumnStyle(SizeType.Absolute, methodWidth),
            new ColumnStyle(SizeType.Percent, 100),
            new ColumnStyle(SizeType.Absolute, sendWidth),
            new ColumnStyle(SizeType.Absolute, stopWidth));
        manual.RowCount = 4;
        manual.RowStyles.Clear();
        manual.RowStyles.Add(new RowStyle(SizeType.Absolute, field));
        manual.RowStyles.Add(new RowStyle(SizeType.Absolute, field));
        manual.RowStyles.Add(new RowStyle(SizeType.Absolute, line + 8));
        manual.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        manual.Controls.Add(FieldLabel("方法"), 0, 0);
        DockField(_method);
        manual.Controls.Add(_method, 1, 0);
        StyleButton(_send, Color.FromArgb(0, 120, 212), Color.FromArgb(16, 110, 190), Color.FromArgb(0, 90, 158), Color.White, 6, 8.5f);
        _send.Dock = DockStyle.Fill;
        _send.Margin = new Padding(0, 4, 6, 4);
        _send.Click += async (_, _) => await Guard(() => RunJobAsync(ConsoleTask.Manual));
        manual.Controls.Add(_send, 3, 0);
        StyleButton(_stop, Color.FromArgb(90, 98, 104), Color.FromArgb(70, 76, 82), Color.FromArgb(52, 58, 64), Color.White, 6, 8.5f);
        _stop.Dock = DockStyle.Fill;
        _stop.Margin = new Padding(0, 4, 0, 4);
        _stop.Click += (_, _) => _runCts?.Cancel();
        manual.Controls.Add(_stop, 4, 0);
        manual.Controls.Add(FieldLabel("路徑"), 0, 1);
        DockField(_path, right: 0);
        manual.SetColumnSpan(_path, 4);
        manual.Controls.Add(_path, 1, 1);
        var hint = new Label
        {
            Text = "送出＝原樣送出。套用溫度＝只改預警與警告。",
            Dock = DockStyle.Fill,
            ForeColor = SystemColors.GrayText,
            TextAlign = ContentAlignment.MiddleLeft,
            UseMnemonic = false,
        };
        manual.SetColumnSpan(hint, 5);
        manual.Controls.Add(hint, 0, 2);
        DockField(_body, 0, 0, 0);
        manual.SetColumnSpan(_body, 5);
        manual.Controls.Add(_body, 0, 3);
        stack.Controls.Add(Section("手動 ISAPI", manual), 0, 3);

        var info = new Button { Text = "查詢相機型號與序號", Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4) };
        StyleButton(info, Color.FromArgb(0, 120, 212), Color.FromArgb(16, 110, 190), Color.FromArgb(0, 90, 158), Color.White, 8, 9f);
        info.Click += async (_, _) => await Guard(() => RunJobAsync(ConsoleTask.DeviceInfo));
        stack.Controls.Add(info, 0, 4);

        var buttons = Grid(3, new ColumnStyle(SizeType.Percent, 34), new ColumnStyle(SizeType.Percent, 33), new ColumnStyle(SizeType.Percent, 33));
        buttons.Margin = new Padding(0);
        buttons.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var query = TaskButton("即時溫度查詢", new Padding(0, 2, 4, 2), Color.FromArgb(0, 130, 137), Color.FromArgb(0, 110, 116), Color.FromArgb(0, 90, 95), ConsoleTask.QueryTemp);
        var apply = TaskButton("套用新溫度", new Padding(4, 2, 4, 2), Color.FromArgb(16, 124, 65), Color.FromArgb(14, 109, 56), Color.FromArgb(11, 90, 46), ConsoleTask.SetTemp);
        var reboot = TaskButton("遠端重啟設備", new Padding(4, 2, 0, 2), Color.FromArgb(209, 52, 56), Color.FromArgb(177, 45, 48), Color.FromArgb(142, 36, 38), ConsoleTask.Reboot);
        buttons.Controls.Add(query, 0, 0);
        buttons.Controls.Add(apply, 1, 0);
        buttons.Controls.Add(reboot, 2, 0);
        stack.Controls.Add(buttons, 0, 5);

        Remember(_ip, _user, _password, _showPassword, _alert, _alarm, _method, _path, _body, _send, info, query, apply, reboot);
        scroll.Controls.Add(stack);
        return scroll;
    }

    private Control BuildLive()
    {
        var live = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        live.RowCount = 4;
        live.RowStyles.Clear();
        live.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        live.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        live.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        live.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        live.Controls.Add(new Label
        {
            Text = "Live View 即時影像",
            Dock = DockStyle.Fill,
            Font = new Font(Font, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 0);

        var channel = Grid(3,
            new ColumnStyle(SizeType.Absolute, TextWidth("鏡頭頻道")),
            new ColumnStyle(SizeType.Percent, 100),
            new ColumnStyle(SizeType.Absolute, TextWidth("停止串流") + 28));
        channel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        channel.Controls.Add(FieldLabel("鏡頭頻道"), 0, 0);
        _channel.Items.AddRange(new object[] { "101 (一般/可見光)", "201 (熱成像通道)" });
        _channel.SelectedIndex = 0;
        DockField(_channel);
        channel.Controls.Add(_channel, 1, 0);
        StyleButton(_liveToggle, Color.FromArgb(43, 87, 154), Color.FromArgb(32, 67, 120), Color.FromArgb(24, 50, 90), Color.White, 6, 9f);
        _liveToggle.Dock = DockStyle.Fill;
        _liveToggle.Margin = new Padding(8, 4, 0, 4);
        _liveToggle.Click += ToggleLive;
        channel.Controls.Add(_liveToggle, 2, 0);
        live.Controls.Add(channel, 0, 1);

        _liveStatus.Text = "狀態: 停止";
        _liveStatus.ForeColor = Color.DimGray;
        _liveStatus.Dock = DockStyle.Fill;
        _liveStatus.TextAlign = ContentAlignment.MiddleLeft;
        live.Controls.Add(_liveStatus, 0, 2);

        _picture.Dock = DockStyle.Fill;
        _picture.Margin = new Padding(0, 4, 0, 0);
        _picture.SizeMode = PictureBoxSizeMode.Zoom;
        _picture.BorderStyle = BorderStyle.FixedSingle;
        _picture.BackColor = Color.Black;
        live.Controls.Add(_picture, 0, 3);
        return live;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            var logMin = 120;
            var limit = _split.Height - logMin - _split.SplitterWidth;
            if (limit < 240)
                return;
            _split.Panel2MinSize = logMin;
            _split.Panel1MinSize = 240;
            _split.SplitterDistance = Math.Max(240, Math.Min(_controlsHeight + 16, limit));
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
        AppendLog("查詢、套用與手動 ISAPI 會呼叫已安裝的 hik-isapi。請先在專案目錄執行：");
        AppendLog("python -m pip install -e .");
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
        var button = new Button { Dock = DockStyle.Fill, Margin = margin, Text = text };
        StyleButton(button, background, hover, down, Color.White, 8, 9f);
        button.Click += async (_, _) => await Guard(() => RunJobAsync(task));
        return button;
    }

    private void Remember(params Control[] controls)
    {
        foreach (var control in controls)
            _lockables.Add(control);
    }

    private static GroupBox Section(string title, Control content)
    {
        var group = new GroupBox { Text = title, Dock = DockStyle.Fill, Padding = new Padding(10, 4, 10, 8) };
        content.Dock = DockStyle.Fill;
        group.Controls.Add(content);
        return group;
    }

    private static TableLayoutPanel Grid(int columns, params ColumnStyle[] styles)
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0) };
        grid.ColumnCount = columns;
        grid.RowCount = 1;
        grid.ColumnStyles.Clear();
        grid.RowStyles.Clear();
        foreach (var style in styles)
            grid.ColumnStyles.Add(style);
        return grid;
    }

    private int TextWidth(string text)
    {
        var size = TextRenderer.MeasureText(text, Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        return size.Width + 16;
    }

    private int TextHeight()
    {
        return TextRenderer.MeasureText("中文", Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Height + 2;
    }

    private int LeftColumnWidth()
    {
        using var bold = new Font(Font, FontStyle.Bold);
        var button = TextRenderer.MeasureText("即時溫度查詢", bold, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width + 36;
        var title = TextWidth("目標相機 IP（單一、範圍或分號）") + 28;
        var hint = TextWidth("送出＝原樣送出。套用溫度＝只改預警與警告。") + 28;
        return Math.Max(title, Math.Max(hint, button * 3 + 24));
    }

    private static Label FieldLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = false,
        UseMnemonic = false,
    };

    private static void DockField(Control control, int top = 5, int right = 8, int bottom = 5)
    {
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(0, top, right, bottom);
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
