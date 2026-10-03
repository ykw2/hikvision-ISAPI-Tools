using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace HikIsapi;

public sealed class IsapiStep
{
    public required string Id { get; init; }
    public required string Path { get; init; }
    public string Mode { get; init; } = "request";
    public string Method { get; init; } = "GET";
    public string Description { get; init; } = "";
    public string OnError { get; init; } = "abort_camera";
    public string OnMissing { get; init; } = "error";
    public string ContentType { get; init; } = "application/xml";
    public string? Body { get; init; }
    public List<(string Path, string Value)> SetValues { get; init; } = new();
    public List<(string Path, string Value)> ExpectValues { get; init; } = new();
    public List<string> RemovePaths { get; init; } = new();
    public List<(string Key, string Value)> Headers { get; init; } = new();
    public bool Force { get; init; }
    public bool CaptureBody { get; init; }
}

public sealed class IsapiProfile
{
    private static readonly Regex StepId = new(@"^[A-Za-z_][\w\-]*$", RegexOptions.Compiled);

    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public required IReadOnlyList<IsapiStep> Steps { get; init; }
    public int Concurrency { get; init; } = 20;
    public double TimeoutSeconds { get; init; } = 20;
    public int Retries { get; init; } = 2;
    public double RetryBackoffSeconds { get; init; } = 0.5;
    public string Username { get; init; } = "admin";
    public string PasswordEnv { get; init; } = "HIK_PASSWORD";
    public bool UseHttps { get; init; }
    public bool VerifyTls { get; init; }
    public Dictionary<string, string> Vars { get; init; } = new();
    public double? MaxFailureRatio { get; init; } = 0.2;
    public int SafetyMinSamples { get; init; } = 20;

    public IsapiProfile WithOverrides(int? concurrency, double? timeout, int? retries, double? ratio, int? samples, bool disableSafety, string? passwordEnv = null)
    {
        var updated = new IsapiProfile
        {
            Name = Name,
            Description = Description,
            Steps = Steps,
            Concurrency = concurrency ?? Concurrency,
            TimeoutSeconds = timeout ?? TimeoutSeconds,
            Retries = retries ?? Retries,
            RetryBackoffSeconds = RetryBackoffSeconds,
            Username = Username,
            PasswordEnv = string.IsNullOrWhiteSpace(passwordEnv) ? PasswordEnv : passwordEnv.Trim(),
            UseHttps = UseHttps,
            VerifyTls = VerifyTls,
            Vars = Vars,
            MaxFailureRatio = disableSafety ? 1 : ratio ?? MaxFailureRatio,
            SafetyMinSamples = samples ?? SafetyMinSamples,
        };
        updated.Validate();
        return updated;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new InvalidOperationException("profile 缺少 name");
        if (Steps.Count == 0)
            throw new InvalidOperationException("profile 至少需要一個 step");
        if (Steps.Select(step => step.Id).Distinct(StringComparer.Ordinal).Count() != Steps.Count)
            throw new InvalidOperationException("step id 重複");
        if (Concurrency is < 1 or > 256)
            throw new InvalidOperationException("concurrency 必須介於 1 到 256");
        if (TimeoutSeconds is < 1 or > 180)
            throw new InvalidOperationException("timeout_seconds 必須介於 1 到 180");
        if (Retries is < 0 or > 5)
            throw new InvalidOperationException("retries 必須介於 0 到 5");
        if (RetryBackoffSeconds < 0)
            throw new InvalidOperationException("retry_backoff_seconds 不能是負數");
        if (MaxFailureRatio is < 0 or > 1)
            throw new InvalidOperationException("max_failure_ratio 必須介於 0 到 1");
        if (SafetyMinSamples < 1)
            throw new InvalidOperationException("safety_min_samples 必須大於 0");
        if (string.IsNullOrWhiteSpace(Username))
            throw new InvalidOperationException("username 不能是空的");
        if (string.IsNullOrWhiteSpace(PasswordEnv))
            throw new InvalidOperationException("password_env 不能是空的");
        foreach (var step in Steps)
            ValidateStep(step);
    }

    private static void ValidateStep(IsapiStep step)
    {
        if (!StepId.IsMatch(step.Id))
            throw new InvalidOperationException("step id 不合法：" + step.Id);
        if (step.Mode is not ("request" or "merge_xml" or "assert_xml"))
            throw new InvalidOperationException("step " + step.Id + " 的 mode 必須是 request、merge_xml 或 assert_xml");
        if (step.Method is not ("GET" or "PUT" or "POST" or "DELETE" or "PATCH" or "HEAD"))
            throw new InvalidOperationException("step " + step.Id + " 的 method 不正確");
        if (step.OnError is not ("abort_camera" or "continue"))
            throw new InvalidOperationException("step " + step.Id + " 的 on_error 必須是 abort_camera 或 continue");
        if (step.OnMissing is not ("error" or "skip" or "create"))
            throw new InvalidOperationException("step " + step.Id + " 的 on_missing 必須是 error、skip 或 create");
        if (!step.Path.StartsWith('/') && !step.Path.Contains("{{", StringComparison.Ordinal))
            throw new InvalidOperationException("step " + step.Id + " 的 path 必須以 / 開頭");
        if (step.Mode == "merge_xml" && step.Method != "PUT")
            throw new InvalidOperationException("step " + step.Id + " 是 merge_xml，method 必須是 PUT");
        if (step.Mode == "assert_xml" && step.Method != "GET")
            throw new InvalidOperationException("step " + step.Id + " 是 assert_xml，method 必須是 GET");
        if (step.Mode == "merge_xml" && step.SetValues.Count == 0 && step.RemovePaths.Count == 0)
            throw new InvalidOperationException("step " + step.Id + " 是 merge_xml，必須提供 set 或 remove");
        if (step.Mode == "assert_xml" && step.ExpectValues.Count == 0)
            throw new InvalidOperationException("step " + step.Id + " 是 assert_xml，必須提供 expect");
        if (step.Mode == "request" && (step.SetValues.Count > 0 || step.ExpectValues.Count > 0 || step.RemovePaths.Count > 0))
            throw new InvalidOperationException("step " + step.Id + " 是 request，欄位比對請改用 merge_xml 或 assert_xml");
        if (step.Mode != "request" && step.Body != null)
            throw new InvalidOperationException("step " + step.Id + " 的 body 只用於 request");
        if (step.RemovePaths.Count > 0 && step.Mode != "merge_xml")
            throw new InvalidOperationException("step " + step.Id + " 的 remove 只用於 merge_xml");
    }
}

public static class ProfileFile
{
    private static readonly HashSet<string> ProfileFields = new(StringComparer.Ordinal)
    {
        "name", "description", "concurrency", "timeout_seconds", "retries", "retry_backoff_seconds",
        "username", "password_env", "use_https", "verify_tls", "vars", "max_failure_ratio",
        "safety_min_samples", "user_agent", "steps",
    };
    private static readonly HashSet<string> StepFields = new(StringComparer.Ordinal)
    {
        "id", "description", "mode", "method", "path", "on_error", "on_missing", "content_type",
        "body", "body_file", "set", "expect", "remove", "headers", "force", "capture_body",
    };
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(NullNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static IsapiProfile Load(string path)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException("找不到設定檔：" + path);
        Dictionary<string, object> raw;
        try
        {
            raw = Yaml.Deserialize<Dictionary<string, object>>(File.ReadAllText(path))
                ?? throw new InvalidOperationException("設定檔是空的");
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("設定檔 YAML 格式錯誤：" + ex.Message);
        }
        if (!raw.TryGetValue("steps", out var stepsRaw) || stepsRaw is string || stepsRaw is not System.Collections.IEnumerable steps)
            throw new InvalidOperationException("steps 必須是非空清單");
        var stepItems = steps.Cast<object>().ToList();
        if (stepItems.Count == 0)
            throw new InvalidOperationException("steps 必須是非空清單");
        var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        var parsed = new List<IsapiStep>();
        var index = 1;
        foreach (var item in stepItems)
        {
            parsed.Add(ParseStep(item, directory, index));
            index++;
        }
        var ratio = raw.ContainsKey("max_failure_ratio")
            ? (AsText(raw["max_failure_ratio"]).Length == 0 ? (double?)null : AsDouble(raw["max_failure_ratio"], "max_failure_ratio"))
            : 0.2;
        RejectUnknown(raw.Keys, ProfileFields, "設定檔有未知欄位：");
        var profile = new IsapiProfile
        {
            Name = Text(raw, "name", Path.GetFileNameWithoutExtension(path)),
            Description = Text(raw, "description", ""),
            Steps = parsed,
            Concurrency = raw.ContainsKey("concurrency") ? AsInt(raw["concurrency"], "concurrency") : 20,
            TimeoutSeconds = raw.ContainsKey("timeout_seconds") ? AsDouble(raw["timeout_seconds"], "timeout_seconds") : 20,
            Retries = raw.ContainsKey("retries") ? AsInt(raw["retries"], "retries") : 2,
            RetryBackoffSeconds = raw.ContainsKey("retry_backoff_seconds") ? AsDouble(raw["retry_backoff_seconds"], "retry_backoff_seconds") : 0.5,
            Username = Text(raw, "username", "admin"),
            PasswordEnv = Text(raw, "password_env", "HIK_PASSWORD"),
            UseHttps = raw.ContainsKey("use_https") && AsBool(raw["use_https"]),
            VerifyTls = raw.ContainsKey("verify_tls") && AsBool(raw["verify_tls"]),
            Vars = ParseVars(raw),
            MaxFailureRatio = ratio,
            SafetyMinSamples = raw.ContainsKey("safety_min_samples") ? AsInt(raw["safety_min_samples"], "safety_min_samples") : 20,
        };
        profile.Validate();
        return profile;
    }

    private static IsapiStep ParseStep(object item, string directory, int index)
    {
        var fields = AsMap(item) ?? throw new InvalidOperationException("第 " + index + " 個 step 必須是鍵值對應");
        var id = Text(fields, "id", "").Trim();
        if (id.Length == 0)
            throw new InvalidOperationException("第 " + index + " 個 step 的 id 不合法");
        var mode = Text(fields, "mode", "request").Trim();
        var method = Text(fields, "method", mode == "merge_xml" ? "PUT" : "GET").Trim().ToUpperInvariant();
        var path = Text(fields, "path", "").Trim();
        if (!path.StartsWith('/') && !path.Contains("{{", StringComparison.Ordinal))
            throw new InvalidOperationException("step " + id + " 的 path 必須以 / 開頭");
        var body = LoadBody(fields, directory, id);
        RejectUnknown(fields.Keys, StepFields, "step " + id + " 有未知欄位：");
        return new IsapiStep
        {
            Id = id,
            Path = path,
            Mode = mode,
            Method = method,
            Description = Text(fields, "description", ""),
            OnError = Text(fields, "on_error", "abort_camera"),
            OnMissing = Text(fields, "on_missing", "error"),
            ContentType = Text(fields, "content_type", "application/xml"),
            Body = body,
            SetValues = Pairs(fields, "set"),
            ExpectValues = Pairs(fields, "expect"),
            RemovePaths = Strings(fields, "remove"),
            Headers = Pairs(fields, "headers"),
            Force = fields.ContainsKey("force") && AsBool(fields["force"]),
            CaptureBody = fields.ContainsKey("capture_body") && AsBool(fields["capture_body"]),
        };
    }

    private static string? LoadBody(Dictionary<string, object> fields, string directory, string id)
    {
        var hasBody = fields.ContainsKey("body") && fields["body"] != null;
        var hasFile = fields.ContainsKey("body_file") && fields["body_file"] != null;
        if (hasBody && hasFile)
            throw new InvalidOperationException("step " + id + " 不能同時寫 body 與 body_file");
        if (hasBody)
            return AsText(fields["body"]);
        if (!hasFile)
            return null;
        var relative = AsText(fields["body_file"]);
        var file = Path.IsPathRooted(relative) ? relative : Path.Combine(directory, relative);
        if (!File.Exists(file))
            throw new InvalidOperationException("step " + id + " 找不到 body_file：" + file);
        return File.ReadAllText(file);
    }

    private static Dictionary<string, string> ParseVars(Dictionary<string, object> raw)
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!raw.TryGetValue("vars", out var value) || value == null)
            return vars;
        var map = AsMap(value) ?? throw new InvalidOperationException("vars 必須是對應");
        foreach (var pair in map)
        {
            if (pair.Value == null)
                throw new InvalidOperationException("設定值不能是 null");
            vars[pair.Key] = AsText(pair.Value);
        }
        return vars;
    }

    private static List<(string, string)> Pairs(Dictionary<string, object> fields, string name)
    {
        var pairs = new List<(string, string)>();
        if (!fields.TryGetValue(name, out var value) || value == null)
            return pairs;
        var map = AsMap(value) ?? throw new InvalidOperationException("step 的 " + name + " 必須是對應");
        if (map.Count == 0 && name != "headers")
            throw new InvalidOperationException("step 的 " + name + " 必須是非空對應");
        foreach (var pair in map)
        {
            if (pair.Value == null)
                throw new InvalidOperationException("設定值不能是 null");
            if (pair.Key.Trim().Length == 0)
                throw new InvalidOperationException("step 的 " + name + " 有空白路徑");
            pairs.Add((pair.Key.Trim(), AsText(pair.Value)));
        }
        return pairs;
    }

    private static List<string> Strings(Dictionary<string, object> fields, string name)
    {
        if (!fields.TryGetValue(name, out var value) || value == null)
            return new List<string>();
        if (value is not IEnumerable<object> items)
            throw new InvalidOperationException("step 的 " + name + " 必須是清單");
        return items.Select(AsText).Where(text => text.Length > 0).ToList();
    }

    private static string Text(IReadOnlyDictionary<string, object> raw, string key, string fallback)
        => raw.TryGetValue(key, out var value) && value != null ? AsText(value) : fallback;

    private static Dictionary<string, object>? AsMap(object? value)
    {
        if (value == null)
            return null;
        if (value is IDictionary<string, object> typed)
            return new Dictionary<string, object>(typed, StringComparer.Ordinal);
        if (value is IDictionary<object, object> map)
        {
            var fields = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var pair in map)
                fields[pair.Key?.ToString() ?? ""] = pair.Value;
            return fields;
        }
        return null;
    }

    private static void RejectUnknown(IEnumerable<string> keys, HashSet<string> allowed, string prefix)
    {
        var unknown = keys.Where(key => !allowed.Contains(key)).OrderBy(key => key, StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
            throw new InvalidOperationException(prefix + string.Join("、", unknown));
    }

    private static string AsText(object? value)
    {
        switch (value)
        {
            case null:
                return "";
            case string text:
                return text;
            case bool flag:
                return flag ? "true" : "false";
            case double number when number == Math.Truncate(number) && !double.IsInfinity(number):
                return ((long)number).ToString(CultureInfo.InvariantCulture);
            case float number when number == Math.Truncate(number) && !float.IsInfinity(number):
                return ((long)number).ToString(CultureInfo.InvariantCulture);
            case decimal number when number == decimal.Truncate(number):
                return decimal.Truncate(number).ToString(CultureInfo.InvariantCulture);
            case IFormattable formattable:
                return formattable.ToString(null, CultureInfo.InvariantCulture) ?? "";
            default:
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }
    }

    private static int AsInt(object value, string name)
    {
        if (int.TryParse(AsText(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            return number;
        throw new InvalidOperationException(name + " 必須是整數");
    }

    private static double AsDouble(object value, string name)
    {
        if (double.TryParse(AsText(value), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return number;
        throw new InvalidOperationException(name + " 必須是數字");
    }

    private static bool AsBool(object value)
    {
        if (value is bool flag)
            return flag;
        var text = AsText(value).Trim().ToLowerInvariant();
        if (text is "1" or "true" or "yes" or "y" or "是")
            return true;
        if (text is "0" or "false" or "no" or "n" or "否")
            return false;
        throw new InvalidOperationException(AsText(value) + " 必須是 true 或 false");
    }
}
