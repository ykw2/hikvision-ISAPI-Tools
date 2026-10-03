using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HikIsapi;

public sealed class BatchRunRequest
{
    public bool DryRun { get; init; }
    public string? Password { get; init; }
    public string? Username { get; init; }
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
    public Func<ResolvedCamera, IsapiProfile, IsapiSession>? OpenSession { get; init; }
    public Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }
    public Action<string>? OnProgress { get; init; }
    public Action<string>? OnSafety { get; init; }
    public CancellationToken CancellationToken { get; init; }
}

public sealed class StepOutcome
{
    public required string Id { get; init; }
    public bool Ok { get; init; }
    public bool Skipped { get; init; }
    public bool Sent { get; init; }
    public bool DryRun { get; init; }
    public string? Method { get; init; }
    public string? Path { get; init; }
    public int? HttpStatus { get; init; }
    public int? IsapiStatusCode { get; init; }
    public bool RebootRequired { get; init; }
    public int Attempts { get; init; }
    public double ElapsedMs { get; init; }
    public string? Error { get; init; }
    public string? Note { get; init; }
    public List<string> Warnings { get; init; } = new();
    public List<XmlChange> Changes { get; init; } = new();
    public string? Body { get; init; }
}

public sealed class CameraOutcome
{
    public required string Id { get; init; }
    public required string Host { get; init; }
    public bool Ok { get; init; }
    public bool Skipped { get; init; }
    public double ElapsedMs { get; init; }
    public string? Error { get; init; }
    public string? FailedStep { get; init; }
    public string? Note { get; init; }
    public List<StepOutcome> Steps { get; init; } = new();

    public bool RebootRequired => Steps.Any(step => step.RebootRequired);
}

public sealed class BatchReport
{
    public required string Profile { get; init; }
    public bool DryRun { get; init; }
    public required string StartedAt { get; init; }
    public required string FinishedAt { get; init; }
    public bool SafetyTripped { get; init; }
    public string? SafetyMessage { get; init; }
    public bool Cancelled { get; init; }
    public required IReadOnlyList<CameraOutcome> Cameras { get; init; }

    public int SuccessCount => Cameras.Count(camera => camera.Ok);
    public int FailedCount => Cameras.Count(camera => !camera.Ok && !camera.Skipped);
    public int SkippedCount => Cameras.Count(camera => camera.Skipped);
    public int ExitCode => FailedCount > 0 || SkippedCount > 0 || SafetyTripped ? 1 : 0;

    public string WriteJson(string path)
    {
        var file = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? path : Path.ChangeExtension(path, ".json");
        var directory = Path.GetDirectoryName(Path.GetFullPath(file));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(ToDocument(), JsonOptions);
        File.WriteAllText(file, json + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return file;
    }

    public string WriteCsv(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var builder = new StringBuilder();
        builder.AppendLine("id,host,ok,skipped,failed_step,error,elapsed_ms,reboot_required");
        foreach (var camera in Cameras)
        {
            builder.Append(Csv(camera.Id)).Append(',')
                .Append(Csv(camera.Host)).Append(',')
                .Append(camera.Ok ? "true" : "false").Append(',')
                .Append(camera.Skipped ? "true" : "false").Append(',')
                .Append(Csv(camera.FailedStep)).Append(',')
                .Append(Csv(camera.Error)).Append(',')
                .Append(Math.Round(camera.ElapsedMs, 1).ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(camera.RebootRequired ? "true" : "false")
                .AppendLine();
        }
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    public string WriteProbeCsv(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var builder = new StringBuilder();
        builder.AppendLine("id,host,ok,model,serial_number,firmware_version,firmware_released,device_name,mac_address,device_type,error");
        foreach (var camera in Cameras)
        {
            var info = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var step in camera.Steps)
            {
                if (!string.IsNullOrEmpty(step.Body))
                {
                    info = DeviceFields(step.Body);
                    break;
                }
            }
            string Field(string key) => info.TryGetValue(key, out var value) ? value : "";
            builder.Append(Csv(camera.Id)).Append(',')
                .Append(Csv(camera.Host)).Append(',')
                .Append(camera.Ok ? "true" : "false").Append(',')
                .Append(Csv(Field("model"))).Append(',')
                .Append(Csv(Field("serial_number"))).Append(',')
                .Append(Csv(Field("firmware_version"))).Append(',')
                .Append(Csv(Field("firmware_released"))).Append(',')
                .Append(Csv(Field("device_name"))).Append(',')
                .Append(Csv(Field("mac_address"))).Append(',')
                .Append(Csv(Field("device_type"))).Append(',')
                .Append(Csv(camera.Error))
                .AppendLine();
        }
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    public string Summary()
    {
        var lines = new List<string>();
        if (DryRun)
            lines.Add("演練模式，會修改裝置的請求沒有送出");
        lines.Add("成功 " + SuccessCount);
        lines.Add("失敗 " + FailedCount);
        lines.Add("略過 " + SkippedCount);
        if (!string.IsNullOrWhiteSpace(SafetyMessage))
            lines.Add(SafetyMessage);
        var failures = Cameras.Where(camera => !camera.Ok && !camera.Skipped).ToList();
        foreach (var camera in failures.Take(30))
            lines.Add($"{camera.Id}  {camera.Host}  {camera.FailedStep ?? "-"}  {camera.Error ?? ""}");
        if (failures.Count > 30)
            lines.Add($"另外 {failures.Count - 30} 支失敗，請看報告檔");
        return string.Join(Environment.NewLine, lines);
    }

    private object ToDocument() => new ReportDocument
    {
        profile = Profile,
        dry_run = DryRun,
        started_at = StartedAt,
        finished_at = FinishedAt,
        safety_tripped = SafetyTripped,
        safety_message = SafetyMessage,
        success_count = SuccessCount,
        failed_count = FailedCount,
        skipped_count = SkippedCount,
        cameras = Cameras.Select(camera => new CameraDocument
        {
            id = camera.Id,
            host = camera.Host,
            ok = camera.Ok,
            skipped = camera.Skipped,
            elapsed_ms = Math.Round(camera.ElapsedMs, 1),
            reboot_required = camera.RebootRequired,
            failed_step = camera.FailedStep,
            error = camera.Error,
            note = camera.Note,
            steps = camera.Steps.Select(step => new StepDocument
            {
                id = step.Id,
                ok = step.Ok,
                skipped = step.Skipped,
                sent = step.Sent,
                dry_run = step.DryRun,
                method = step.Method,
                path = step.Path,
                attempts = step.Attempts,
                elapsed_ms = Math.Round(step.ElapsedMs, 1),
                http_status = step.HttpStatus is > 0 ? step.HttpStatus : null,
                isapi_status_code = step.IsapiStatusCode,
                reboot_required = step.RebootRequired,
                error = step.Error,
                note = step.Note,
                warnings = step.Warnings.Count == 0 ? null : step.Warnings,
                changes = step.Changes.Count == 0 ? null : step.Changes.Select(change => new ChangeDocument
                {
                    action = change.Action,
                    path = change.Path,
                    before = change.Before,
                    after = change.After,
                }).ToList(),
                body = step.Body,
            }).ToList(),
        }).ToList(),
    };

    private static Dictionary<string, string> DeviceFields(string body)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var root = IsapiXml.Parse(body);
            var wanted = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["deviceName"] = "device_name",
                ["model"] = "model",
                ["serialNumber"] = "serial_number",
                ["firmwareVersion"] = "firmware_version",
                ["firmwareReleasedDate"] = "firmware_released",
                ["macAddress"] = "mac_address",
                ["deviceType"] = "device_type",
            };
            foreach (var child in root.Elements())
            {
                if (child.Elements().Any() || !wanted.TryGetValue(child.Name.LocalName, out var key))
                    continue;
                var text = (child.Value ?? "").Trim();
                if (text.Length > 0)
                    found[key] = text;
            }
        }
        catch (System.Xml.XmlException)
        {
            return found;
        }
        return found;
    }

    private static string Csv(string? value)
    {
        var text = value ?? "";
        if (text.Contains('"') || text.Contains(',') || text.Contains('\n') || text.Contains('\r'))
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        return text;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private sealed class ReportDocument
    {
        public string profile { get; set; } = "";
        public bool dry_run { get; set; }
        public string started_at { get; set; } = "";
        public string finished_at { get; set; } = "";
        public bool safety_tripped { get; set; }
        public string? safety_message { get; set; }
        public int success_count { get; set; }
        public int failed_count { get; set; }
        public int skipped_count { get; set; }
        public List<CameraDocument> cameras { get; set; } = new();
    }

    private sealed class CameraDocument
    {
        public string id { get; set; } = "";
        public string host { get; set; } = "";
        public bool ok { get; set; }
        public bool skipped { get; set; }
        public double elapsed_ms { get; set; }
        public bool reboot_required { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? failed_step { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? error { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? note { get; set; }
        public List<StepDocument> steps { get; set; } = new();
    }

    private sealed class StepDocument
    {
        public string id { get; set; } = "";
        public bool ok { get; set; }
        public bool skipped { get; set; }
        public bool sent { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool dry_run { get; set; }
        public string? method { get; set; }
        public string? path { get; set; }
        public int attempts { get; set; }
        public double elapsed_ms { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? http_status { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? isapi_status_code { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool reboot_required { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? error { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? note { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? warnings { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<ChangeDocument>? changes { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? body { get; set; }
    }

    private sealed class ChangeDocument
    {
        public string action { get; set; } = "";
        public string path { get; set; } = "";
        public string? before { get; set; }
        public string? after { get; set; }
    }
}

public static class IsapiBatch
{
    private static readonly HashSet<string> Mutating = new(StringComparer.Ordinal) { "PUT", "POST", "DELETE", "PATCH" };

    public static async Task<BatchReport> RunAsync(IsapiProfile profile, IReadOnlyList<CameraRow> cameras, BatchRunRequest request)
    {
        profile.Validate();
        var environ = request.Environment ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var resolved = InventoryFile.Resolve(cameras, profile, request.Username, request.Password, environ);
        if (resolved.Count == 0)
            throw new InvalidOperationException("沒有需要執行的攝影機");

        var startedAt = Timestamp();
        var total = resolved.Count;
        var results = new CameraOutcome[total];
        var gate = new SemaphoreSlim(Math.Max(1, profile.Concurrency));
        var sync = new object();
        var stop = false;
        var executed = 0;
        var failed = 0;
        var finished = 0;
        var safetyTripped = false;
        string? safetyMessage = null;
        var ratioEnabled = RatioEnabled(profile, total);
        var cancellation = request.CancellationToken;

        var tasks = new Task[total];
        for (var index = 0; index < total; index++)
        {
            var captured = index;
            var camera = resolved[captured];
            tasks[captured] = Task.Run(async () =>
            {
                var acquired = false;
                try
                {
                    await gate.WaitAsync(cancellation).ConfigureAwait(false);
                    acquired = true;
                    bool alreadyStopped;
                    lock (sync)
                        alreadyStopped = stop;
                    CameraOutcome outcome;
                    if (alreadyStopped || cancellation.IsCancellationRequested)
                    {
                        outcome = Skipped(camera, cancellation.IsCancellationRequested ? "已停止，未執行" : "已觸發失敗率保護，未執行");
                    }
                    else
                    {
                        try
                        {
                            outcome = await ApplyCamera(profile, camera, request, environ, cancellation).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            outcome = Skipped(camera, "已停止，未執行");
                            lock (sync)
                                stop = true;
                        }
                        catch (Exception ex) when (ex is not OutOfMemoryException)
                        {
                            outcome = Failed(camera, "內部錯誤：" + ex.Message, Array.Empty<StepOutcome>());
                        }
                    }

                    string? safety = null;
                    int done;
                    lock (sync)
                    {
                        results[captured] = outcome;
                        finished++;
                        done = finished;
                        if (!outcome.Skipped)
                        {
                            executed++;
                            if (!outcome.Ok)
                                failed++;
                            if (ratioEnabled && !stop && ShouldTrip(profile, failed, executed))
                            {
                                stop = true;
                                safetyTripped = true;
                                safetyMessage = SafetyMessage(profile, failed, executed);
                                safety = safetyMessage;
                            }
                        }
                    }
                    if (safety != null)
                        request.OnSafety?.Invoke(safety);
                    request.OnProgress?.Invoke(FormatProgress(outcome, done, total));
                }
                catch (OperationCanceledException)
                {
                    var outcome = Skipped(camera, "已停止，未執行");
                    int done;
                    lock (sync)
                    {
                        results[captured] = outcome;
                        finished++;
                        done = finished;
                        stop = true;
                    }
                    request.OnProgress?.Invoke(FormatProgress(outcome, done, total));
                }
                finally
                {
                    if (acquired)
                        gate.Release();
                }
            }, CancellationToken.None);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        for (var index = 0; index < total; index++)
        {
            if (results[index] == null)
                results[index] = Skipped(resolved[index], "已停止，未執行");
        }

        return new BatchReport
        {
            Profile = profile.Name,
            DryRun = request.DryRun,
            StartedAt = startedAt,
            FinishedAt = Timestamp(),
            SafetyTripped = safetyTripped,
            SafetyMessage = safetyMessage,
            Cancelled = cancellation.IsCancellationRequested,
            Cameras = results,
        };
    }

    public static string FormatProgress(CameraOutcome result, int done, int total)
    {
        var mark = result.Skipped ? "略過" : result.Ok ? "成功" : "失敗";
        var suffix = "";
        if (!result.Ok)
        {
            var step = string.IsNullOrEmpty(result.FailedStep) ? "" : " " + result.FailedStep;
            var detail = (step + " " + (result.Error ?? result.Note ?? "")).Trim();
            if (detail.Length > 0)
                suffix = " " + detail;
        }
        var seconds = (result.ElapsedMs / 1000).ToString("0.0", CultureInfo.InvariantCulture);
        return $"[{done}/{total}] {mark} {result.Id} {result.Host}{suffix} ({seconds}s)";
    }

    private static async Task<CameraOutcome> ApplyCamera(
        IsapiProfile profile,
        ResolvedCamera camera,
        BatchRunRequest request,
        IReadOnlyDictionary<string, string> environ,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var steps = new List<StepOutcome>();
        var abort = false;
        IsapiSession? session = null;
        try
        {
            var open = request.OpenSession ?? OpenDefault;
            session = open(camera, profile);
            foreach (var step in profile.Steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (abort)
                {
                    steps.Add(new StepOutcome
                    {
                        Id = step.Id,
                        Ok = false,
                        Skipped = true,
                        Path = step.Path,
                        Note = "前一步失敗，已略過",
                    });
                    continue;
                }
                var result = await RunStep(session, profile, camera, step, request, environ, cancellationToken).ConfigureAwait(false);
                steps.Add(result);
                if (!result.Ok && step.OnError == "abort_camera")
                    abort = true;
            }
        }
        finally
        {
            session?.Dispose();
        }

        var failed = steps.FirstOrDefault(step => !step.Ok && !step.Skipped);
        var ok = steps.Count > 0 && steps.All(step => step.Ok && !step.Skipped);
        return new CameraOutcome
        {
            Id = camera.Id,
            Host = camera.Host,
            Ok = ok,
            Skipped = false,
            ElapsedMs = watch.Elapsed.TotalMilliseconds,
            Error = failed?.Error,
            FailedStep = failed?.Id,
            Steps = steps,
        };
    }

    private static async Task<StepOutcome> RunStep(
        IsapiSession session,
        IsapiProfile profile,
        ResolvedCamera camera,
        IsapiStep step,
        BatchRunRequest request,
        IReadOnlyDictionary<string, string> environ,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        StepOutcome result;
        try
        {
            result = step.Mode switch
            {
                "request" => await RequestStep(session, profile, camera, step, request, environ, cancellationToken).ConfigureAwait(false),
                "merge_xml" => await MergeStep(session, profile, camera, step, request, environ, cancellationToken).ConfigureAwait(false),
                "assert_xml" => await AssertStep(session, profile, camera, step, request, environ, cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidOperationException("step " + step.Id + " 的 mode 不正確"),
            };
        }
        catch (InvalidOperationException ex)
        {
            result = new StepOutcome
            {
                Id = step.Id,
                Ok = false,
                Method = step.Method,
                Path = step.Path,
                Error = ex.Message,
            };
        }
        result = CopyElapsed(result, watch.Elapsed.TotalMilliseconds);
        return result;
    }

    private static async Task<StepOutcome> RequestStep(
        IsapiSession session,
        IsapiProfile profile,
        ResolvedCamera camera,
        IsapiStep step,
        BatchRunRequest request,
        IReadOnlyDictionary<string, string> environ,
        CancellationToken cancellationToken)
    {
        var path = RenderPath(step, camera, profile, environ);
        var headers = RenderHeaders(step, camera, profile, environ);
        var content = RenderBody(step, camera, profile, environ);
        if (request.DryRun && Mutating.Contains(step.Method))
        {
            return new StepOutcome
            {
                Id = step.Id,
                Ok = true,
                DryRun = true,
                Method = step.Method,
                Path = path,
                Note = "演練模式，未送出",
            };
        }
        var (response, attempts) = await Call(session, profile, step.Method, path, content, headers, request, cancellationToken, step.ContentType).ConfigureAwait(false);
        return FromHttp(step, step.Method, path, response, attempts, sent: true);
    }

    private static async Task<StepOutcome> MergeStep(
        IsapiSession session,
        IsapiProfile profile,
        ResolvedCamera camera,
        IsapiStep step,
        BatchRunRequest request,
        IReadOnlyDictionary<string, string> environ,
        CancellationToken cancellationToken)
    {
        var path = RenderPath(step, camera, profile, environ);
        var headers = RenderHeaders(step, camera, profile, environ);
        var fields = camera.TemplateFields();
        var assignments = step.SetValues
            .Select(item => (item.Path, TextTemplate.Render(item.Value, fields, profile.Vars, environ)))
            .ToList();
        var (response, attempts) = await Call(session, profile, "GET", path, null, headers, request, cancellationToken).ConfigureAwait(false);
        if (!response.Ok)
            return FromHttp(step, "GET", path, response, attempts, sent: true);
        System.Xml.Linq.XElement root;
        try
        {
            root = IsapiXml.Parse(response.Body);
        }
        catch (System.Xml.XmlException)
        {
            return new StepOutcome
            {
                Id = step.Id,
                Ok = false,
                Sent = true,
                Method = "GET",
                Path = path,
                HttpStatus = response.HttpStatus,
                Attempts = attempts,
                Error = "回應不是合法的 XML",
                Body = Truncate(response.Body, 4000),
            };
        }

        var edit = IsapiXml.Edit(root, assignments, step.RemovePaths, step.OnMissing);
        if (edit.Changes.Count == 0 && !step.Force)
        {
            return new StepOutcome
            {
                Id = step.Id,
                Ok = true,
                Method = "PUT",
                Path = path,
                HttpStatus = response.HttpStatus,
                Attempts = attempts,
                Note = "沒有差異，未寫入",
                Warnings = edit.Warnings,
            };
        }
        if (request.DryRun)
        {
            return new StepOutcome
            {
                Id = step.Id,
                Ok = true,
                DryRun = true,
                Method = "PUT",
                Path = path,
                HttpStatus = response.HttpStatus,
                Attempts = attempts,
                Note = "演練模式，未送出",
                Warnings = edit.Warnings,
                Changes = edit.Changes,
            };
        }

        var payload = Encoding.UTF8.GetBytes(IsapiXml.Serialize(root));
        var (put, putAttempts) = await Call(session, profile, "PUT", path, payload, headers, request, cancellationToken, step.ContentType).ConfigureAwait(false);
        var result = FromHttp(step, "PUT", path, put, attempts + putAttempts, sent: true);
        return CopyDetails(result, edit.Warnings, edit.Changes);
    }

    private static async Task<StepOutcome> AssertStep(
        IsapiSession session,
        IsapiProfile profile,
        ResolvedCamera camera,
        IsapiStep step,
        BatchRunRequest request,
        IReadOnlyDictionary<string, string> environ,
        CancellationToken cancellationToken)
    {
        var path = RenderPath(step, camera, profile, environ);
        var headers = RenderHeaders(step, camera, profile, environ);
        var (response, attempts) = await Call(session, profile, "GET", path, null, headers, request, cancellationToken).ConfigureAwait(false);
        if (!response.Ok)
            return FromHttp(step, "GET", path, response, attempts, sent: true);
        System.Xml.Linq.XElement root;
        try
        {
            root = IsapiXml.Parse(response.Body);
        }
        catch (System.Xml.XmlException)
        {
            return new StepOutcome
            {
                Id = step.Id,
                Ok = false,
                Sent = true,
                Method = "GET",
                Path = path,
                HttpStatus = response.HttpStatus,
                Attempts = attempts,
                Error = "回應不是合法的 XML",
                Body = Truncate(response.Body, 4000),
            };
        }

        var mismatches = new List<string>();
        var fields = camera.TemplateFields();
        foreach (var (itemPath, expectedRaw) in step.ExpectValues)
        {
            var expected = TextTemplate.Render(expectedRaw, fields, profile.Vars, environ).Trim();
            System.Xml.Linq.XElement node;
            try
            {
                node = IsapiXml.Find(root, itemPath, create: false);
            }
            catch (InvalidOperationException)
            {
                mismatches.Add(itemPath + " 不存在，預期 " + expected);
                continue;
            }
            if (node.Elements().Any())
            {
                mismatches.Add(itemPath + " 不是文字節點");
                continue;
            }
            var actual = (node.Value ?? "").Trim();
            if (actual != expected)
                mismatches.Add(itemPath + " 預期 " + expected + "，實際 " + actual);
        }
        if (mismatches.Count > 0)
        {
            return new StepOutcome
            {
                Id = step.Id,
                Ok = false,
                Sent = true,
                Method = "GET",
                Path = path,
                HttpStatus = response.HttpStatus,
                Attempts = attempts,
                Error = string.Join("；", mismatches),
            };
        }
        return new StepOutcome
        {
            Id = step.Id,
            Ok = true,
            Sent = true,
            Method = "GET",
            Path = path,
            HttpStatus = response.HttpStatus,
            Attempts = attempts,
            Note = "與預期一致",
        };
    }

    private static async Task<(IsapiCall Response, int Attempts)> Call(
        IsapiSession session,
        IsapiProfile profile,
        string method,
        string path,
        byte[]? content,
        IReadOnlyDictionary<string, string> headers,
        BatchRunRequest request,
        CancellationToken cancellationToken,
        string? contentType = null)
    {
        var attempts = 0;
        var delay = request.Delay ?? ((span, token) => Task.Delay(span, token));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempts++;
            string? type = contentType;
            var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in headers)
            {
                if (header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                    type = header.Value;
                else
                    extra[header.Key] = header.Value;
            }
            if (content != null)
                type ??= "application/xml";
            var response = await session.RequestAsync(method, path, content, type, extra.Count == 0 ? null : extra, cancellationToken).ConfigureAwait(false);
            if (response.Ok || !response.Retryable || attempts > profile.Retries)
                return (response, attempts);
            var seconds = Math.Min(10, profile.RetryBackoffSeconds * attempts);
            await delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
        }
    }

    private static string RenderPath(IsapiStep step, ResolvedCamera camera, IsapiProfile profile, IReadOnlyDictionary<string, string> environ)
    {
        var path = TextTemplate.Render(step.Path, camera.TemplateFields(), profile.Vars, environ);
        if (!path.StartsWith('/'))
            throw new InvalidOperationException("step " + step.Id + " 的 path 必須以 / 開頭");
        return path;
    }

    private static Dictionary<string, string> RenderHeaders(IsapiStep step, ResolvedCamera camera, IsapiProfile profile, IReadOnlyDictionary<string, string> environ)
    {
        var fields = camera.TemplateFields();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in step.Headers)
            headers[key] = TextTemplate.Render(value, fields, profile.Vars, environ);
        return headers;
    }

    private static byte[]? RenderBody(IsapiStep step, ResolvedCamera camera, IsapiProfile profile, IReadOnlyDictionary<string, string> environ)
    {
        if (step.Body == null)
            return null;
        var text = TextTemplate.Render(step.Body, camera.TemplateFields(), profile.Vars, environ);
        return Encoding.UTF8.GetBytes(text.Trim());
    }

    private static StepOutcome FromHttp(IsapiStep step, string method, string path, IsapiCall response, int attempts, bool sent)
    {
        var ok = response.Ok;
        string? note = null;
        if (response.RebootRequired && response.Ok)
            note = "裝置要求重新開機後設定才會完全生效";
        string? body = null;
        if (step.CaptureBody || !ok)
        {
            var limit = step.CaptureBody && ok ? 100_000 : 4_000;
            body = Truncate(response.Body, limit);
        }
        return new StepOutcome
        {
            Id = step.Id,
            Ok = ok,
            Sent = sent,
            Method = method,
            Path = path,
            HttpStatus = response.HttpStatus,
            IsapiStatusCode = response.IsapiStatusCode,
            RebootRequired = response.RebootRequired && response.Ok,
            Attempts = attempts,
            Error = ok ? null : (response.Error ?? "HTTP " + response.HttpStatus),
            Note = note,
            Body = body,
        };
    }

    private static StepOutcome CopyElapsed(StepOutcome result, double elapsedMs) => new()
    {
        Id = result.Id,
        Ok = result.Ok,
        Skipped = result.Skipped,
        Sent = result.Sent,
        DryRun = result.DryRun,
        Method = result.Method,
        Path = result.Path,
        HttpStatus = result.HttpStatus,
        IsapiStatusCode = result.IsapiStatusCode,
        RebootRequired = result.RebootRequired,
        Attempts = result.Attempts,
        ElapsedMs = elapsedMs,
        Error = result.Error,
        Note = result.Note,
        Warnings = result.Warnings,
        Changes = result.Changes,
        Body = result.Body,
    };

    private static StepOutcome CopyDetails(StepOutcome result, List<string> warnings, List<XmlChange> changes) => new()
    {
        Id = result.Id,
        Ok = result.Ok,
        Skipped = result.Skipped,
        Sent = result.Sent,
        DryRun = result.DryRun,
        Method = result.Method,
        Path = result.Path,
        HttpStatus = result.HttpStatus,
        IsapiStatusCode = result.IsapiStatusCode,
        RebootRequired = result.RebootRequired,
        Attempts = result.Attempts,
        ElapsedMs = result.ElapsedMs,
        Error = result.Error,
        Note = result.Note,
        Warnings = warnings,
        Changes = changes,
        Body = result.Body,
    };

    private static IsapiSession OpenDefault(ResolvedCamera camera, IsapiProfile profile)
        => IsapiSession.Open(
            camera.Host,
            camera.Port,
            camera.UseHttps,
            camera.Username,
            camera.Password,
            TimeSpan.FromSeconds(profile.TimeoutSeconds),
            profile.VerifyTls);

    private static CameraOutcome Skipped(ResolvedCamera camera, string note) => new()
    {
        Id = camera.Id,
        Host = camera.Host,
        Ok = false,
        Skipped = true,
        Error = note,
        Note = note,
    };

    private static CameraOutcome Failed(ResolvedCamera camera, string error, IReadOnlyList<StepOutcome> steps) => new()
    {
        Id = camera.Id,
        Host = camera.Host,
        Ok = false,
        Error = error,
        Steps = steps.ToList(),
    };

    private static bool RatioEnabled(IsapiProfile profile, int total)
    {
        var ratio = profile.MaxFailureRatio;
        return ratio != null && ratio < 1 && total >= profile.SafetyMinSamples;
    }

    private static bool ShouldTrip(IsapiProfile profile, int failed, int executed)
    {
        var ratio = profile.MaxFailureRatio;
        if (ratio == null || ratio >= 1 || executed < profile.SafetyMinSamples)
            return false;
        if (ratio <= 0)
            return failed > 0;
        return (double)failed / executed >= ratio;
    }

    public static string SafetyMessage(IsapiProfile profile, int failed, int executed)
    {
        var ratio = profile.MaxFailureRatio;
        var threshold = ratio == null || ratio <= 0
            ? "出現失敗即停止"
            : "門檻 " + Percent(ratio.Value);
        var actual = executed == 0 ? 0 : (double)failed / executed;
        return $"失敗率保護已啟動：完成 {executed} 支時有 {failed} 支失敗（{Percent(actual)}），{threshold}，已停止其餘攝影機";
    }

    private static string Percent(double value)
        => Math.Round(value * 100, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "%";

    private static string Timestamp()
        => DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);

    private static string Truncate(string text, int limit)
        => text.Length <= limit ? text : text[..limit] + "\n...（已截斷）";
}
