using System.Globalization;
using System.Text;

namespace HikIsapi;

public enum ConsoleTask
{
    DeviceInfo,
    QueryTemp,
    SetTemp,
    Reboot,
    Manual,
}

public sealed class ConsoleInput
{
    public string IpInput { get; init; } = "";
    public string Username { get; init; } = "admin";
    public string Password { get; init; } = "";
    public string Alert { get; init; } = "";
    public string Alarm { get; init; } = "";
    public string Method { get; init; } = "GET";
    public string Path { get; init; } = ConsoleLaunch.DeviceInfoPath;
    public string Body { get; init; } = "";
}

public sealed class ConsolePlan
{
    public required LaunchPlan Launch { get; init; }
    public required string ReportPath { get; init; }
    public required ConsoleTask Task { get; init; }
    public required IReadOnlyList<string> Addresses { get; init; }
}

public static class ConsoleLaunch
{
    public const string DeviceInfoPath = "/ISAPI/System/deviceInfo";
    public const string ThermalPath = "/ISAPI/Thermal/channels/1/thermometry/basicParam";
    public const string RebootPath = "/ISAPI/System/reboot";

    public static bool TryCreate(
        ConsoleTask task,
        ConsoleInput input,
        string? pythonPath,
        bool windows,
        Func<string, string?> findOnPath,
        string jobDirectory,
        out ConsolePlan? plan,
        out string? error)
    {
        plan = null;
        if (!IpRangeParser.TryParse(input.IpInput, out var addresses, out error))
            return false;
        if (string.IsNullOrEmpty(input.Password))
        {
            error = "請輸入密碼";
            return false;
        }
        var username = string.IsNullOrWhiteSpace(input.Username) ? "admin" : input.Username.Trim();
        if (task == ConsoleTask.SetTemp && !TryTemperature(input.Alert, "預警", out error))
            return false;
        if (task == ConsoleTask.SetTemp && !TryTemperature(input.Alarm, "警告", out error))
            return false;
        if (task == ConsoleTask.Manual && !TryManual(input, out error))
            return false;

        Directory.CreateDirectory(jobDirectory);
        var inventory = Path.Combine(jobDirectory, "cameras.csv");
        var profile = Path.Combine(jobDirectory, "profile.yaml");
        var report = Path.Combine(jobDirectory, "report.json");
        WriteInventory(inventory, addresses);
        if (task == ConsoleTask.Manual && !string.IsNullOrWhiteSpace(input.Body))
            File.WriteAllText(Path.Combine(jobDirectory, "body.xml"), input.Body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(profile, BuildProfile(task, input, username), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var options = new BatchOptions
        {
            InventoryPath = inventory,
            ProfilePath = profile,
            WorkDirectory = jobDirectory,
            Username = username,
            PasswordEnv = "HIK_PASSWORD",
        };
        if (!LaunchPlanner.TryCreateBatch(
                BatchCommand.Apply,
                options,
                pythonPath,
                input.Password,
                windows,
                findOnPath,
                out var launch,
                out error)
            || launch == null)
            return false;

        var arguments = new List<string>(launch.Arguments) { "--quiet", "--output", report };
        plan = new ConsolePlan
        {
            Launch = new LaunchPlan
            {
                FileName = launch.FileName,
                Arguments = arguments,
                WorkingDirectory = launch.WorkingDirectory,
                Environment = launch.Environment,
                DisplayCommand = CliPlan.FormatCommand(launch.FileName, arguments),
            },
            ReportPath = report,
            Task = task,
            Addresses = addresses,
        };
        error = null;
        return true;
    }

    public static string BuildProfile(ConsoleTask task, ConsoleInput input, string username)
    {
        var builder = new StringBuilder();
        builder.AppendLine("name: " + ProfileName(task));
        builder.AppendLine("description: 操作台單次作業");
        builder.AppendLine("concurrency: 20");
        builder.AppendLine("timeout_seconds: 8");
        builder.AppendLine("retries: 1");
        builder.AppendLine("retry_backoff_seconds: 0.4");
        builder.AppendLine("username: " + YamlQuote(username));
        builder.AppendLine("password_env: HIK_PASSWORD");
        builder.AppendLine("use_https: false");
        builder.AppendLine("verify_tls: false");
        builder.AppendLine("max_failure_ratio: 0.2");
        builder.AppendLine("safety_min_samples: 20");
        builder.AppendLine("steps:");
        switch (task)
        {
            case ConsoleTask.DeviceInfo:
                AppendRequest(builder, "device_info", "GET", DeviceInfoPath, bodyFile: false, capture: true);
                break;
            case ConsoleTask.QueryTemp:
                AppendRequest(builder, "query_temp", "GET", ThermalPath, bodyFile: false, capture: true);
                break;
            case ConsoleTask.SetTemp:
                builder.AppendLine("  - id: set_temp");
                builder.AppendLine("    description: 合併寫入預警與警告");
                builder.AppendLine("    mode: merge_xml");
                builder.AppendLine("    method: PUT");
                builder.AppendLine("    path: " + YamlQuote(ThermalPath));
                builder.AppendLine("    on_error: continue");
                builder.AppendLine("    capture_body: true");
                builder.AppendLine("    set:");
                builder.AppendLine("      alert: " + YamlQuote(input.Alert.Trim().Replace(',', '.')));
                builder.AppendLine("      alarm: " + YamlQuote(input.Alarm.Trim().Replace(',', '.')));
                break;
            case ConsoleTask.Reboot:
                AppendRequest(builder, "reboot", "PUT", RebootPath, bodyFile: false, capture: false);
                break;
            case ConsoleTask.Manual:
                AppendRequest(
                    builder,
                    "manual",
                    input.Method.Trim().ToUpperInvariant(),
                    input.Path.Trim(),
                    bodyFile: !string.IsNullOrWhiteSpace(input.Body),
                    capture: true);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(task));
        }
        return builder.ToString();
    }

    private static void AppendRequest(StringBuilder builder, string id, string method, string path, bool bodyFile, bool capture)
    {
        builder.AppendLine("  - id: " + id);
        builder.AppendLine("    mode: request");
        builder.AppendLine("    method: " + method);
        builder.AppendLine("    path: " + YamlQuote(path));
        builder.AppendLine("    on_error: continue");
        builder.AppendLine("    capture_body: " + (capture ? "true" : "false"));
        if (bodyFile)
        {
            builder.AppendLine("    content_type: application/xml");
            builder.AppendLine("    body_file: body.xml");
        }
    }

    private static void WriteInventory(string path, IReadOnlyList<string> addresses)
    {
        var builder = new StringBuilder();
        builder.AppendLine("id,host");
        for (var index = 0; index < addresses.Count; index++)
            builder.AppendLine($"cam-{index + 1},{addresses[index]}");
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static bool TryTemperature(string text, string label, out string? error)
    {
        var normalized = text.Trim().Replace(',', '.');
        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            error = label + "必須是數字";
            return false;
        }
        error = null;
        return true;
    }

    private static bool TryManual(ConsoleInput input, out string? error)
    {
        var method = input.Method.Trim().ToUpperInvariant();
        if (method is not ("GET" or "PUT" or "POST" or "DELETE"))
        {
            error = "方法必須是 GET、PUT、POST 或 DELETE";
            return false;
        }
        if (string.IsNullOrWhiteSpace(input.Path) || !input.Path.Trim().StartsWith('/'))
        {
            error = "路徑必須以 / 開頭，例如 /ISAPI/System/deviceInfo";
            return false;
        }
        error = null;
        return true;
    }

    private static string ProfileName(ConsoleTask task) => task switch
    {
        ConsoleTask.DeviceInfo => "device-info",
        ConsoleTask.QueryTemp => "query-temp",
        ConsoleTask.SetTemp => "set-temp",
        ConsoleTask.Reboot => "reboot",
        ConsoleTask.Manual => "manual",
        _ => "console",
    };

    private static string YamlQuote(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n") + "\"";
}
