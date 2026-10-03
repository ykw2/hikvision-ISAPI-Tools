using System.Globalization;
using System.Text;

namespace HikIsapi;

public static class BatchJobs
{
    public static async Task<int> RunAsync(
        BatchCommand command,
        BatchOptions options,
        string? password,
        Action<string>? log,
        CancellationToken cancellationToken,
        Func<ResolvedCamera, IsapiProfile, IsapiSession>? openSession = null)
    {
        try
        {
            var environ = ReadEnvironment();
            var loaded = ProfileFile.Load(options.ProfilePath.Trim());
            var profile = command == BatchCommand.Probe ? AsProbe(loaded) : loaded;
            if (command != BatchCommand.Validate)
                profile = ApplyRunOverrides(profile, options);
            else if (!string.IsNullOrWhiteSpace(options.PasswordEnv))
                profile = profile.WithOverrides(null, null, null, null, null, false, options.PasswordEnv);

            var cameras = InventoryFile.Load(options.InventoryPath.Trim());
            foreach (var warning in InventoryFile.Warnings(cameras))
                log?.Invoke("警告：" + warning);
            var disabled = cameras.Count(camera => !camera.Enabled);
            HashSet<string>? only = ParseOnly(options.Only);
            string? message = null;
            if (command == BatchCommand.Apply && !string.IsNullOrWhiteSpace(options.RetryFailed))
            {
                var failed = InventoryFile.FailedIds(options.RetryFailed.Trim());
                if (failed.Count == 0)
                    message = "這份報告沒有失敗的攝影機";
                else
                    only = only == null ? failed : new HashSet<string>(only.Where(failed.Contains), StringComparer.Ordinal);
            }

            var selected = new List<CameraRow>();
            if (message == null)
            {
                var offset = string.IsNullOrWhiteSpace(options.Offset) ? 0 : int.Parse(options.Offset.Trim(), CultureInfo.InvariantCulture);
                int? limit = string.IsNullOrWhiteSpace(options.Limit) ? null : int.Parse(options.Limit.Trim(), CultureInfo.InvariantCulture);
                selected = InventoryFile.Select(cameras, CliPlan.SplitTags(options.Tags), only, offset, limit);
                if (selected.Count == 0)
                    message = "沒有需要執行的攝影機";
            }

            log?.Invoke($"清單共 {cameras.Count} 支（停用 {disabled} 支），符合條件 {selected.Count} 支");
            log?.Invoke($"設定檔 {profile.Name}，{profile.Steps.Count} 個步驟：" + string.Join("、", profile.Steps.Select(step => step.Id)));
            log?.Invoke($"並發 {profile.Concurrency.ToString(CultureInfo.InvariantCulture)}，逾時 {profile.TimeoutSeconds.ToString("0.###", CultureInfo.InvariantCulture)} 秒，重試 {profile.Retries.ToString(CultureInfo.InvariantCulture)} 次。{Protection(profile)}");

            if (command == BatchCommand.Validate)
            {
                if (selected.Count > 0)
                    InventoryFile.Resolve(selected, profile, BlankToNull(options.Username), BlankToNull(password), environ);
                log?.Invoke("未連線，只檢查清單與設定檔");
                if (message != null)
                    log?.Invoke(message);
                return 0;
            }
            if (selected.Count == 0)
            {
                log?.Invoke(message ?? "沒有需要執行的攝影機");
                return 0;
            }

            var action = command == BatchCommand.Probe ? "探測" : options.DryRun ? "演練" : "套用";
            log?.Invoke($"開始{action} {profile.Name}：{selected.Count} 支，{profile.Steps.Count} 個步驟，並發 {profile.Concurrency}");
            var report = await IsapiBatch.RunAsync(profile, selected, new BatchRunRequest
            {
                DryRun = command == BatchCommand.Apply && options.DryRun,
                Password = BlankToNull(password),
                Username = BlankToNull(options.Username),
                Environment = environ,
                OpenSession = openSession,
                OnProgress = line => log?.Invoke(line),
                OnSafety = line => log?.Invoke(line),
                CancellationToken = cancellationToken,
            }).ConfigureAwait(false);

            var output = DefaultOutput(options, command);
            var jsonPath = report.WriteJson(output);
            var csvPath = command == BatchCommand.Probe
                ? report.WriteProbeCsv(Path.ChangeExtension(jsonPath, ".csv"))
                : report.WriteCsv(Path.ChangeExtension(jsonPath, ".csv"));
            log?.Invoke(report.Summary());
            log?.Invoke("報告 " + jsonPath);
            log?.Invoke("清單 " + csvPath);
            if (report.Cancelled || cancellationToken.IsCancellationRequested)
                return -1;
            return report.ExitCode;
        }
        catch (OperationCanceledException)
        {
            return -1;
        }
        catch (InvalidOperationException ex)
        {
            log?.Invoke(ex.Message);
            return 2;
        }
    }

    public static async Task<int> GetAsync(GetOptions options, string? password, Action<string>? log, CancellationToken cancellationToken)
    {
        try
        {
            var environ = ReadEnvironment();
            var envName = string.IsNullOrWhiteSpace(options.PasswordEnv) ? "HIK_PASSWORD" : options.PasswordEnv.Trim();
            var secret = string.IsNullOrEmpty(password) ? (environ.TryGetValue(envName, out var fromEnv) ? fromEnv : "") : password;
            if (string.IsNullOrEmpty(secret))
                throw new InvalidOperationException("請在視窗輸入密碼，或設定環境變數 " + envName);
            var https = options.Https;
            var port = string.IsNullOrWhiteSpace(options.Port) ? (https ? 443 : 80) : int.Parse(options.Port.Trim(), CultureInfo.InvariantCulture);
            var timeout = string.IsNullOrWhiteSpace(options.Timeout) ? 20 : double.Parse(options.Timeout.Trim().Replace(',', '.'), CultureInfo.InvariantCulture);
            var username = string.IsNullOrWhiteSpace(options.Username) ? "admin" : options.Username.Trim();
            using var session = IsapiSession.Open(options.Host.Trim(), port, https, username, secret, TimeSpan.FromSeconds(timeout), options.VerifyTls);
            var response = await session.RequestAsync("GET", options.Path.Trim(), null, null, null, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(options.Output))
            {
                var destination = options.Output.Trim();
                var directory = Path.GetDirectoryName(Path.GetFullPath(destination));
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                File.WriteAllText(destination, response.Body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                log?.Invoke("已寫入 " + destination);
            }
            else
            {
                log?.Invoke(ConsoleReport.Pretty(response.Body));
            }
            if (!response.Ok)
            {
                log?.Invoke(response.Error ?? "讀取失敗");
                return 1;
            }
            return 0;
        }
        catch (OperationCanceledException)
        {
            return -1;
        }
        catch (InvalidOperationException ex)
        {
            log?.Invoke(ex.Message);
            return 2;
        }
    }

    private static IsapiProfile ApplyRunOverrides(IsapiProfile profile, BatchOptions options)
    {
        int? concurrency = Blank(options.Concurrency) ? null : int.Parse(options.Concurrency.Trim(), CultureInfo.InvariantCulture);
        double? timeout = Blank(options.Timeout) ? null : double.Parse(options.Timeout.Trim().Replace(',', '.'), CultureInfo.InvariantCulture);
        int? retries = Blank(options.Retries) ? null : int.Parse(options.Retries.Trim(), CultureInfo.InvariantCulture);
        double? ratio = options.DisableSafety || Blank(options.MaxFailureRatio)
            ? null
            : double.Parse(options.MaxFailureRatio.Trim().Replace(',', '.'), CultureInfo.InvariantCulture);
        int? samples = Blank(options.SafetySamples) ? null : int.Parse(options.SafetySamples.Trim(), CultureInfo.InvariantCulture);
        return profile.WithOverrides(concurrency, timeout, retries, ratio, samples, options.DisableSafety, options.PasswordEnv);
    }

    private static IsapiProfile AsProbe(IsapiProfile profile) => new()
    {
        Name = "probe",
        Description = "連線探測",
        Steps = new[]
        {
            new IsapiStep
            {
                Id = "device_info",
                Description = "讀取裝置資訊",
                Path = "/ISAPI/System/deviceInfo",
                Mode = "request",
                Method = "GET",
                OnError = "abort_camera",
                CaptureBody = true,
            },
        },
        Concurrency = profile.Concurrency,
        TimeoutSeconds = profile.TimeoutSeconds,
        Retries = profile.Retries,
        RetryBackoffSeconds = profile.RetryBackoffSeconds,
        Username = profile.Username,
        PasswordEnv = profile.PasswordEnv,
        UseHttps = profile.UseHttps,
        VerifyTls = profile.VerifyTls,
        Vars = profile.Vars,
        MaxFailureRatio = profile.MaxFailureRatio,
        SafetyMinSamples = profile.SafetyMinSamples,
    };

    private static string Protection(IsapiProfile profile)
    {
        var ratio = profile.MaxFailureRatio;
        if (ratio == null || ratio >= 1)
            return "失敗率保護已關閉";
        if (ratio <= 0)
            return "失敗率保護：完成至少 " + profile.SafetyMinSamples + " 支後，只要有失敗就停止";
        return "失敗率保護：完成至少 " + profile.SafetyMinSamples + " 支後，失敗率達到 " + Math.Round(ratio.Value * 100, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "% 就停止";
    }

    private static string DefaultOutput(BatchOptions options, BatchCommand command)
    {
        var work = CliPlan.ResolveBatchDirectory(options) ?? Directory.GetCurrentDirectory();
        var results = CliPlan.ResultsDirectory(work);
        Directory.CreateDirectory(results);
        var kind = command == BatchCommand.Probe ? "probe" : options.DryRun ? "dry-run" : "apply";
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        return Path.Combine(results, kind + "-" + stamp + ".json");
    }

    private static HashSet<string>? ParseOnly(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var ids = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ids.Length == 0)
            throw new InvalidOperationException("只跑沒有有效的 id");
        return new HashSet<string>(ids, StringComparer.Ordinal);
    }

    private static Dictionary<string, string> ReadEnvironment()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
                map[key] = value;
        }
        return map;
    }

    private static string? BlankToNull(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static bool Blank(string? text) => string.IsNullOrWhiteSpace(text);
}
