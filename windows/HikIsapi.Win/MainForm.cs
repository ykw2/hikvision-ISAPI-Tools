namespace HikIsapi;

public sealed class BatchForm : Form
{
    private readonly TextBox _inventory = new();
    private readonly TextBox _profile = new();
    private readonly TextBox _workDir = new() { PlaceholderText = "空白則用清單所在目錄，報告寫到其下的 results" };
    private readonly TextBox _username = new() { PlaceholderText = "空白則用設定檔" };
    private readonly TextBox _password = new() { UseSystemPasswordChar = true, PlaceholderText = "不會存檔，也不會出現在命令列" };
    private readonly TextBox _passwordEnv = new() { Text = "HIK_PASSWORD" };
    private readonly TextBox _tags = new() { PlaceholderText = "gate;north，符合其中一個即可" };
    private readonly TextBox _only = new() { PlaceholderText = "cam-0001,cam-0002" };
    private readonly TextBox _offset = new() { PlaceholderText = "0" };
    private readonly TextBox _limit = new() { PlaceholderText = "空白＝全部" };
    private readonly TextBox _concurrency = new() { PlaceholderText = "沿用設定檔" };
    private readonly TextBox _timeout = new() { PlaceholderText = "秒" };
    private readonly TextBox _retries = new() { PlaceholderText = "次" };
    private readonly TextBox _ratio = new() { PlaceholderText = "0.2＝20%" };
    private readonly TextBox _samples = new() { PlaceholderText = "至少幾支" };
    private readonly CheckBox _disableSafety = new() { Text = "關閉失敗率保護", AutoSize = true };
    private readonly TextBox _getHost = new();
    private readonly TextBox _getPort = new() { PlaceholderText = "80/443" };
    private readonly TextBox _getPath = new() { Text = "/ISAPI/System/deviceInfo" };
    private readonly CheckBox _getHttps = new() { Text = "HTTPS", AutoSize = true };
    private readonly CheckBox _getVerifyTls = new() { Text = "驗證憑證", AutoSize = true };
    private readonly TextBox _getOutput = new() { PlaceholderText = "空白則顯示在下方" };
    private readonly TextBox _log = new();
    private readonly Label _status = new() { Text = "就緒", AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button _stop = new() { Text = "停止", Enabled = false, AutoSize = true, MinimumSize = new Size(88, 32) };
    private readonly List<Control> _lockables = new();
    private readonly string _settingsPath = UiSettingsStore.DefaultPath();
    private string _savedPython = "";
    private Panel? _settingsScroll;
    private CancellationTokenSource? _runCts;
    private bool _busy;

    public BatchForm()
    {
        Text = "蝕本成工具站 — 批次設定";
        Icon = AppIcon.Current;
        Font = UiFont();
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1024, 680);
        ClientSize = new Size(1100, 760);

        var shell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(12, 8, 12, 4),
        };
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 430));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _settingsScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var settings = BuildSettings();
        settings.Dock = DockStyle.Top;
        _settingsScroll.Controls.Add(settings);
        body.Controls.Add(_settingsScroll, 0, 0);
        body.Controls.Add(BuildLog(), 0, 1);
        shell.Controls.Add(body, 0, 0);
        shell.Controls.Add(_status, 0, 1);
        Controls.Add(shell);

        Load += (_, _) => LoadSettings();
        FormClosing += OnFormClosing;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_settingsScroll?.Parent is not TableLayoutPanel body)
            return;
        var height = Math.Clamp((int)(ClientSize.Height * 0.56), 280, 520);
        body.RowStyles[0] = new RowStyle(SizeType.Absolute, height);
    }

    private Control BuildSettings()
    {
        var stack = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Margin = new Padding(0),
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var files = CreateGrid();
        AddFileRow(files, "清單", _inventory, (_, _) =>
        {
            BrowseFile(_inventory, "CSV (*.csv)|*.csv|所有檔案 (*.*)|*.*");
            if (string.IsNullOrWhiteSpace(_workDir.Text) && File.Exists(_inventory.Text))
                _workDir.Text = Path.GetDirectoryName(_inventory.Text) ?? "";
        });
        AddFileRow(files, "設定檔", _profile, (_, _) => BrowseFile(_profile, "YAML (*.yml;*.yaml)|*.yml;*.yaml|所有檔案 (*.*)|*.*"));
        AddFileRow(files, "工作目錄", _workDir, (_, _) => BrowseFolder(_workDir));
        AddContentRow(files, "登入", FieldLine(
            MiniLabel("帳號"), _username,
            MiniLabel("密碼"), _password,
            MiniLabel("環境變數"), _passwordEnv));
        AddContentRow(files, "篩選", FieldLine(
            MiniLabel("標籤"), _tags,
            MiniLabel("只跑"), _only));
        AddContentRow(files, "範圍", FieldLine(
            MiniLabel("略過"), _offset,
            MiniLabel("最多幾支"), _limit,
            MiniLabel("並發"), _concurrency));
        AddContentRow(files, "連線", FieldLine(
            MiniLabel("逾時"), _timeout,
            MiniLabel("重試"), _retries,
            MiniLabel("失敗率"), _ratio,
            MiniLabel("樣本數"), _samples,
            _disableSafety));

        var hint = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(900, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 4, 0, 8),
            Text = "密碼只留在這次執行的記憶體，不會寫進設定檔，也不會出現在紀錄。清單某一列自己有密碼時，仍以那一列為準。正式套用前會再詢問一次。",
        };

        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 8) };
        buttons.Controls.Add(ActionButton("檢查清單", () => RunBatch(BatchCommand.Validate, dryRun: false, retryFailed: "")));
        buttons.Controls.Add(ActionButton("探測連線", () => RunBatch(BatchCommand.Probe, dryRun: false, retryFailed: "")));
        buttons.Controls.Add(ActionButton("演練", () => RunBatch(BatchCommand.Apply, dryRun: true, retryFailed: "")));
        buttons.Controls.Add(ActionButton("套用（會寫入）", () => RunBatch(BatchCommand.Apply, dryRun: false, retryFailed: "")));
        buttons.Controls.Add(ActionButton("重跑失敗", RetryFailedAsync));
        buttons.Controls.Add(_stop);
        buttons.Controls.Add(ActionButton("開啟報告資料夾", () =>
        {
            OpenResults();
            return Task.CompletedTask;
        }));
        _stop.Click += (_, _) => _runCts?.Cancel();

        var getBox = new GroupBox { Text = "讀取單一路徑", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, Padding = new Padding(8) };
        var getGrid = CreateGrid();
        AddContentRow(getGrid, "主機", FieldLine(
            _getHost, MiniLabel("埠"), _getPort, _getHttps, _getVerifyTls));
        AddContentRow(getGrid, "路徑", _getPath);
        AddFileRow(getGrid, "存到", _getOutput, (_, _) => BrowseSave(_getOutput, "XML (*.xml)|*.xml|所有檔案 (*.*)|*.*"));
        var read = ActionButton("讀取", RunGetAsync);
        AddContentRow(getGrid, "", read);
        getBox.Controls.Add(getGrid);

        stack.Controls.Add(files, 0, 0);
        stack.Controls.Add(hint, 0, 1);
        stack.Controls.Add(buttons, 0, 2);
        stack.Controls.Add(getBox, 0, 3);
        stack.RowCount = 4;
        for (var i = 0; i < stack.RowCount; i++)
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        return stack;
    }

    private Control BuildLog()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var caption = new Label { Text = "輸出", AutoSize = true, Margin = new Padding(0, 0, 0, 4) };
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Both;
        _log.WordWrap = false;
        _log.Font = LogFont();
        _log.BackColor = Color.FromArgb(28, 32, 36);
        _log.ForeColor = Color.FromArgb(230, 232, 234);
        _log.Dock = DockStyle.Fill;
        _log.BorderStyle = BorderStyle.FixedSingle;
        panel.Controls.Add(caption, 0, 0);
        panel.Controls.Add(_log, 0, 1);
        return panel;
    }

    private TableLayoutPanel CreateGrid()
    {
        var grid = new TableLayoutPanel
        {
            ColumnCount = 3,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Margin = new Padding(0),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        return grid;
    }

    private void AddFileRow(TableLayoutPanel grid, string label, TextBox box, EventHandler browse)
    {
        var button = new Button { Text = "瀏覽", Dock = DockStyle.Fill, Margin = new Padding(0, 3, 0, 3) };
        button.Click += browse;
        PrepareTextBox(box);
        AddRow(grid, label, box, button);
        _lockables.Add(button);
    }

    private void AddContentRow(TableLayoutPanel grid, string label, Control content)
    {
        content.Dock = DockStyle.Fill;
        content.Margin = new Padding(0, 3, 8, 3);
        RememberInputs(content);
        AddRow(grid, label, content, null);
    }

    private void AddRow(TableLayoutPanel grid, string label, Control content, Control? extra)
    {
        var row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var caption = new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 8, 8, 0),
        };
        grid.Controls.Add(caption, 0, row);
        if (extra == null)
        {
            grid.SetColumnSpan(content, 2);
            grid.Controls.Add(content, 1, row);
            return;
        }
        grid.Controls.Add(content, 1, row);
        grid.Controls.Add(extra, 2, row);
        if (content is TextBox)
            _lockables.Add(content);
    }

    private Control FieldLine(params Control[] controls)
    {
        var line = new TableLayoutPanel
        {
            ColumnCount = controls.Length,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            RowCount = 1,
        };
        line.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        for (var i = 0; i < controls.Length; i++)
        {
            var control = controls[i];
            var flexible = control is TextBox;
            line.ColumnStyles.Add(new ColumnStyle(flexible ? SizeType.Percent : SizeType.AutoSize, flexible ? 100 : 0));
            if (control is TextBox box)
                PrepareTextBox(box);
            else if (control is Label caption)
            {
                caption.AutoSize = true;
                caption.Anchor = AnchorStyles.Left;
                caption.Margin = new Padding(i == 0 ? 0 : 8, 8, 4, 0);
            }
            else
            {
                control.Anchor = AnchorStyles.Left;
                control.Margin = new Padding(8, 6, 0, 0);
            }
            line.Controls.Add(control, i, 0);
        }
        return line;
    }

    private void PrepareTextBox(TextBox box)
    {
        box.Dock = DockStyle.Fill;
        box.Margin = new Padding(0, 4, 8, 4);
    }

    private void RememberInputs(Control control)
    {
        if (control is TextBox or CheckBox or Button)
            _lockables.Add(control);
        foreach (Control child in control.Controls)
            RememberInputs(child);
    }

    private Button ActionButton(string text, Func<Task> action)
    {
        var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(96, 32), Margin = new Padding(0, 0, 8, 8) };
        button.Click += async (_, _) => await Guard(action);
        _lockables.Add(button);
        return button;
    }

    private static Label MiniLabel(string text) => new() { Text = text };

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
            _status.Text = "發生錯誤";
            AppendLog(ex.Message);
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task RunBatch(BatchCommand command, bool dryRun, string retryFailed)
    {
        var options = ReadBatchOptions();
        options.DryRun = dryRun;
        options.RetryFailed = retryFailed;
        var error = CliPlan.CheckBatch(command, options);
        if (error != null)
        {
            ShowError(error);
            return;
        }
        if (command == BatchCommand.Apply && !dryRun)
        {
            var message = string.IsNullOrWhiteSpace(retryFailed)
                ? CliPlan.ApplyConfirmMessage(options.Limit, options.ProfilePath)
                : CliPlan.RetryConfirmMessage(retryFailed);
            if (MessageBox.Show(this, message, "套用設定", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK)
            {
                _status.Text = "已取消";
                return;
            }
        }
        SaveSettings();
        await ExecuteLocalAsync(token => BatchJobs.RunAsync(command, options, _password.Text, AppendLog, token));
    }

    private async Task RetryFailedAsync()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "報告 (*.json)|*.json|所有檔案 (*.*)|*.*",
            Title = "選擇要重跑的報告",
        };
        var results = CurrentResultsDirectory(create: false);
        if (results != null && Directory.Exists(results))
            dialog.InitialDirectory = results;
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        await RunBatch(BatchCommand.Apply, dryRun: false, retryFailed: dialog.FileName);
    }

    private async Task RunGetAsync()
    {
        var options = ReadGetOptions();
        var error = CliPlan.CheckGet(options);
        if (error != null)
        {
            ShowError(error);
            return;
        }
        SaveSettings();
        await ExecuteLocalAsync(token => BatchJobs.GetAsync(options, _password.Text, AppendLog, token));
    }

    private async Task ExecuteLocalAsync(Func<CancellationToken, Task<int>> action)
    {
        _runCts = new CancellationTokenSource();
        SetBusy(true);
        try
        {
            var code = await action(_runCts.Token);
            var summary = code switch
            {
                0 => "完成",
                -1 => "已停止",
                2 => "設定錯誤",
                _ => "結束時有失敗",
            };
            _status.Text = $"{summary}（結束碼 {code}）";
            AppendLog($"--- {summary}，結束碼 {code} ---");
        }
        finally
        {
            _runCts.Dispose();
            _runCts = null;
            SetBusy(false);
        }
    }

    private void OpenResults()
    {
        var directory = CurrentResultsDirectory(create: true);
        if (directory == null)
        {
            ShowError("請先選擇清單或工作目錄");
            return;
        }
        Directory.CreateDirectory(directory);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = directory,
            UseShellExecute = true,
        });
    }

    private string? CurrentResultsDirectory(bool create)
    {
        var options = ReadBatchOptions();
        if (string.IsNullOrWhiteSpace(options.WorkDirectory) && string.IsNullOrWhiteSpace(options.InventoryPath))
            return null;
        if (!string.IsNullOrWhiteSpace(options.WorkDirectory) && !Directory.Exists(options.WorkDirectory) && !create)
            return options.WorkDirectory.Trim();
        var work = CliPlan.ResolveBatchDirectory(options);
        if (string.IsNullOrEmpty(work))
            return null;
        return CliPlan.ResultsDirectory(work);
    }

    private BatchOptions ReadBatchOptions() => new()
    {
        InventoryPath = _inventory.Text,
        ProfilePath = _profile.Text,
        WorkDirectory = _workDir.Text,
        Username = _username.Text,
        PasswordEnv = _passwordEnv.Text,
        Tags = _tags.Text,
        Only = _only.Text,
        Limit = _limit.Text,
        Offset = _offset.Text,
        Concurrency = _concurrency.Text,
        Timeout = _timeout.Text,
        Retries = _retries.Text,
        MaxFailureRatio = _ratio.Text,
        SafetySamples = _samples.Text,
        DisableSafety = _disableSafety.Checked,
    };

    private GetOptions ReadGetOptions() => new()
    {
        Host = _getHost.Text,
        Path = _getPath.Text,
        Port = _getPort.Text,
        Https = _getHttps.Checked,
        VerifyTls = _getVerifyTls.Checked,
        Username = _username.Text,
        PasswordEnv = _passwordEnv.Text,
        Timeout = _timeout.Text,
        Output = _getOutput.Text,
        WorkDirectory = _workDir.Text,
    };

    private void LoadSettings()
    {
        var settings = UiSettingsStore.LoadOrNew(_settingsPath, out var warning);
        _savedPython = settings.PythonPath;
        _inventory.Text = settings.InventoryPath;
        _profile.Text = settings.ProfilePath;
        _workDir.Text = settings.WorkDirectory;
        _username.Text = settings.Username;
        _passwordEnv.Text = string.IsNullOrWhiteSpace(settings.PasswordEnv) ? "HIK_PASSWORD" : settings.PasswordEnv;
        _tags.Text = settings.Tags;
        _only.Text = settings.Only;
        _limit.Text = settings.Limit;
        _offset.Text = settings.Offset;
        _concurrency.Text = settings.Concurrency;
        _timeout.Text = settings.Timeout;
        _retries.Text = settings.Retries;
        _ratio.Text = settings.MaxFailureRatio;
        _samples.Text = settings.SafetySamples;
        _disableSafety.Checked = settings.DisableSafety;
        _getHost.Text = settings.GetHost;
        _getPort.Text = settings.GetPort;
        _getPath.Text = string.IsNullOrWhiteSpace(settings.GetPath) ? "/ISAPI/System/deviceInfo" : settings.GetPath;
        _getHttps.Checked = settings.GetHttps;
        _getVerifyTls.Checked = settings.GetVerifyTls;
        _getOutput.Text = settings.GetOutput;
        if (settings.WindowWidth >= MinimumSize.Width && settings.WindowHeight >= MinimumSize.Height)
            Size = new Size(settings.WindowWidth, settings.WindowHeight);
        AppendLog("查詢、套用與批次設定都在這個程式裡完成，不必另外安裝 Python。");
        AppendLog("建議順序：檢查清單、探測連線、演練、再小批套用。數量上限空白代表全部符合條件的攝影機。");
        if (warning != null)
            AppendLog(warning);
    }

    private void SaveSettings()
    {
        try
        {
            var settings = UiSettingsStore.LoadOrNew(_settingsPath, out _);
            settings.PythonPath = _savedPython;
            settings.InventoryPath = _inventory.Text.Trim();
            settings.ProfilePath = _profile.Text.Trim();
            settings.WorkDirectory = _workDir.Text.Trim();
            settings.Username = _username.Text.Trim();
            settings.PasswordEnv = _passwordEnv.Text.Trim();
            settings.Tags = _tags.Text.Trim();
            settings.Only = _only.Text.Trim();
            settings.Limit = _limit.Text.Trim();
            settings.Offset = _offset.Text.Trim();
            settings.Concurrency = _concurrency.Text.Trim();
            settings.Timeout = _timeout.Text.Trim();
            settings.Retries = _retries.Text.Trim();
            settings.MaxFailureRatio = _ratio.Text.Trim();
            settings.SafetySamples = _samples.Text.Trim();
            settings.DisableSafety = _disableSafety.Checked;
            settings.GetHost = _getHost.Text.Trim();
            settings.GetPort = _getPort.Text.Trim();
            settings.GetPath = _getPath.Text.Trim();
            settings.GetHttps = _getHttps.Checked;
            settings.GetVerifyTls = _getVerifyTls.Checked;
            settings.GetOutput = _getOutput.Text.Trim();
            if (WindowState == FormWindowState.Normal)
            {
                settings.WindowWidth = Width;
                settings.WindowHeight = Height;
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
            var answer = MessageBox.Show(
                this,
                "仍在執行，關閉會停止目前的工作。確定要關閉？",
                Text,
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.OK)
            {
                e.Cancel = true;
                return;
            }
            _runCts?.Cancel();
        }
        SaveSettings();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        foreach (var control in _lockables)
            control.Enabled = !busy;
        _stop.Enabled = busy;
        if (busy)
            _status.Text = "執行中…";
    }

    private void ShowError(string message)
    {
        _status.Text = message;
        MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void AppendLog(string text)
    {
        if (IsDisposed)
            return;
        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(() => AppendLog(text));
            }
            catch (InvalidOperationException)
            {
            }
            return;
        }
        if (_log.TextLength > 400_000)
            _log.Text = _log.Text[^200_000..];
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}");
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    private void BrowseFile(TextBox target, string filter)
    {
        using var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true };
        SeedDialog(dialog, target.Text);
        if (dialog.ShowDialog(this) == DialogResult.OK)
            target.Text = dialog.FileName;
    }

    private void BrowseSave(TextBox target, string filter)
    {
        using var dialog = new SaveFileDialog { Filter = filter, OverwritePrompt = true };
        SeedDialog(dialog, target.Text);
        if (dialog.ShowDialog(this) == DialogResult.OK)
            target.Text = dialog.FileName;
    }

    private static void SeedDialog(FileDialog dialog, string current)
    {
        if (string.IsNullOrWhiteSpace(current))
            return;
        var directory = Path.GetDirectoryName(current);
        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
            dialog.InitialDirectory = directory;
        dialog.FileName = Path.GetFileName(current);
    }

    private void BrowseFolder(TextBox target)
    {
        using var dialog = new FolderBrowserDialog();
        if (Directory.Exists(target.Text))
            dialog.SelectedPath = target.Text;
        if (dialog.ShowDialog(this) == DialogResult.OK)
            target.Text = dialog.SelectedPath;
    }

    private static Font UiFont()
    {
        foreach (var name in new[] { "Microsoft JhengHei UI", "Microsoft JhengHei", "PMingLiU", "Segoe UI" })
        {
            try
            {
                return new Font(name, 9f);
            }
            catch (ArgumentException)
            {
            }
        }
        return SystemFonts.MessageBoxFont ?? new Font(FontFamily.GenericSansSerif, 9f);
    }

    private static Font LogFont()
    {
        foreach (var name in new[] { "Cascadia Mono", "Consolas", "Courier New" })
        {
            try
            {
                return new Font(name, 9f);
            }
            catch (ArgumentException)
            {
            }
        }
        return new Font(FontFamily.GenericMonospace, 9f);
    }
}
