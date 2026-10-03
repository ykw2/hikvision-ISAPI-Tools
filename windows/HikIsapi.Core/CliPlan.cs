using System.Globalization;

namespace HikIsapi;

public enum BatchCommand
{
    Validate,
    Probe,
    Apply,
}

public sealed class BatchOptions
{
    public string InventoryPath { get; set; } = "";
    public string ProfilePath { get; set; } = "";
    public string WorkDirectory { get; set; } = "";
    public string Username { get; set; } = "";
    public string PasswordEnv { get; set; } = "HIK_PASSWORD";
    public string Tags { get; set; } = "";
    public string Only { get; set; } = "";
    public string Limit { get; set; } = "";
    public string Offset { get; set; } = "";
    public string Concurrency { get; set; } = "";
    public string Timeout { get; set; } = "";
    public string Retries { get; set; } = "";
    public string MaxFailureRatio { get; set; } = "";
    public string SafetySamples { get; set; } = "";
    public bool DisableSafety { get; set; }
    public bool DryRun { get; set; }
    public string RetryFailed { get; set; } = "";
}

public sealed class GetOptions
{
    public string Host { get; set; } = "";
    public string Path { get; set; } = "/ISAPI/System/deviceInfo";
    public string Port { get; set; } = "";
    public bool Https { get; set; }
    public bool VerifyTls { get; set; }
    public string Username { get; set; } = "";
    public string PasswordEnv { get; set; } = "HIK_PASSWORD";
    public string Timeout { get; set; } = "";
    public string Output { get; set; } = "";
    public string WorkDirectory { get; set; } = "";
}

public sealed class LaunchPlan
{
    public required string FileName { get; init; }
    public required IReadOnlyList<string> Arguments { get; init; }
    public required string WorkingDirectory { get; init; }
    public required IReadOnlyDictionary<string, string> Environment { get; init; }
    public required string DisplayCommand { get; init; }
}

public static class CliPlan
{
    public static string ApplyConfirmMessage(string? limit, string profilePath)
    {
        var scope = string.IsNullOrWhiteSpace(limit)
            ? "數量上限是空白，這會寫入所有符合條件的攝影機。"
            : $"這會寫入裝置，最多 {limit.Trim()} 支。";
        return scope + "\n\n設定檔：" + profilePath.Trim() + "\n\n確定要套用？";
    }

    public static string RetryConfirmMessage(string reportPath)
        => "將依這份報告重跑失敗或未執行的攝影機，而且會寫入裝置。\n\n"
            + reportPath.Trim()
            + "\n\n確定要套用？";

    public static IReadOnlyList<string> SplitTags(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<string>();
        return text.Split(
            new[] { ',', ';', '|', ' ', '\t', '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public static string? CheckBatch(BatchCommand command, BatchOptions options)
    {
        var inventoryError = CheckExistingFile(options.InventoryPath, "請選擇攝影機清單 CSV", "找不到清單：");
        if (inventoryError != null)
            return inventoryError;
        var profileError = CheckExistingFile(options.ProfilePath, "請選擇設定檔 YAML", "找不到設定檔：");
        if (profileError != null)
            return profileError;
        if (command == BatchCommand.Apply && !string.IsNullOrWhiteSpace(options.RetryFailed))
        {
            var report = options.RetryFailed.Trim();
            if (!File.Exists(report))
                return "找不到失敗報告：" + report;
        }

        string? error = CheckOptionalInt(options.Limit, 1, 1_000_000, "最多幾支")
            ?? CheckOptionalInt(options.Offset, 0, 1_000_000, "略過前幾支")
            ?? CheckOptionalInt(options.Concurrency, 1, 256, "並發")
            ?? CheckOptionalDecimal(options.Timeout, 1, 180, "逾時", "20")
            ?? CheckOptionalInt(options.Retries, 0, 5, "重試")
            ?? CheckOptionalInt(options.SafetySamples, 1, 1_000_000, "樣本數");
        if (error != null)
            return error;
        if (!options.DisableSafety)
        {
            error = CheckOptionalDecimal(options.MaxFailureRatio, 0, 1, "失敗率", "0.2");
            if (error != null)
                return error;
        }

        if (!string.IsNullOrWhiteSpace(options.WorkDirectory) && !Directory.Exists(options.WorkDirectory.Trim()))
            return "找不到工作目錄：" + options.WorkDirectory.Trim();
        return null;
    }

    public static string? CheckGet(GetOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Host))
            return "請填寫主機位址";
        if (options.Host.Any(char.IsWhiteSpace))
            return "主機位址不能含有空白";
        var path = options.Path.Trim();
        if (path.Length == 0 || !path.StartsWith('/'))
            return "路徑必須以 / 開頭，例如 /ISAPI/System/deviceInfo";
        var error = CheckOptionalInt(options.Port, 1, 65535, "埠")
            ?? CheckOptionalDecimal(options.Timeout, 1, 180, "逾時", "20");
        if (error != null)
            return error;
        if (!string.IsNullOrWhiteSpace(options.Output))
        {
            var directory = Path.GetDirectoryName(options.Output.Trim());
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                return "找不到存檔目錄：" + directory;
        }
        if (!string.IsNullOrWhiteSpace(options.WorkDirectory) && !Directory.Exists(options.WorkDirectory.Trim()))
            return "找不到工作目錄：" + options.WorkDirectory.Trim();
        return null;
    }

    public static List<string> BuildBatchArgs(BatchCommand command, BatchOptions options)
    {
        var args = new List<string>
        {
            CommandName(command),
            "--inventory", options.InventoryPath.Trim(),
            "--profile", options.ProfilePath.Trim(),
        };
        AddIfPresent(args, "--username", options.Username);
        AddPasswordEnv(args, options.PasswordEnv);
        foreach (var tag in SplitTags(options.Tags))
        {
            args.Add("--tag");
            args.Add(tag);
        }
        AddIfPresent(args, "--only", options.Only);
        AddIfPresent(args, "--limit", options.Limit);
        AddIfPresent(args, "--offset", options.Offset);
        if (command != BatchCommand.Validate)
        {
            AddIfPresent(args, "--concurrency", options.Concurrency);
            AddDecimal(args, "--timeout", options.Timeout);
            AddIfPresent(args, "--retries", options.Retries);
            if (options.DisableSafety)
            {
                args.Add("--max-failure-ratio");
                args.Add("1");
            }
            else
            {
                AddDecimal(args, "--max-failure-ratio", options.MaxFailureRatio);
            }
            AddIfPresent(args, "--safety-samples", options.SafetySamples);
        }
        if (command == BatchCommand.Apply)
        {
            if (options.DryRun)
                args.Add("--dry-run");
            AddIfPresent(args, "--retry-failed", options.RetryFailed);
        }
        return args;
    }

    public static List<string> BuildGetArgs(GetOptions options)
    {
        var args = new List<string>
        {
            "get",
            "--host", options.Host.Trim(),
            "--path", options.Path.Trim(),
        };
        AddIfPresent(args, "--port", options.Port);
        if (options.Https)
            args.Add("--https");
        if (options.VerifyTls)
            args.Add("--verify-tls");
        AddIfPresent(args, "--username", options.Username);
        AddPasswordEnv(args, options.PasswordEnv);
        AddDecimal(args, "--timeout", options.Timeout);
        AddIfPresent(args, "--output", options.Output);
        return args;
    }

    public static string? ResolveBatchDirectory(BatchOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.WorkDirectory))
            return Path.GetFullPath(options.WorkDirectory.Trim());
        var inventory = Path.GetFullPath(options.InventoryPath.Trim());
        return Path.GetDirectoryName(inventory);
    }

    public static string ResultsDirectory(string workingDirectory)
        => Path.Combine(workingDirectory, "results");

    public static string Quote(string value)
    {
        if (value.Length == 0)
            return "\"\"";
        if (value.Any(ch => char.IsWhiteSpace(ch) || ch == '"'))
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        return value;
    }

    public static string FormatCommand(string fileName, IReadOnlyList<string> arguments)
    {
        var parts = new List<string> { Quote(fileName) };
        parts.AddRange(arguments.Select(Quote));
        return string.Join(" ", parts);
    }

    private static string CommandName(BatchCommand command) => command switch
    {
        BatchCommand.Validate => "validate",
        BatchCommand.Probe => "probe",
        BatchCommand.Apply => "apply",
        _ => throw new ArgumentOutOfRangeException(nameof(command)),
    };

    private static string? CheckExistingFile(string path, string missingMessage, string notFoundPrefix)
    {
        if (string.IsNullOrWhiteSpace(path))
            return missingMessage;
        var trimmed = path.Trim();
        if (!File.Exists(trimmed))
            return notFoundPrefix + trimmed;
        return null;
    }

    private static void AddPasswordEnv(List<string> args, string? value)
    {
        args.Add("--password-env");
        args.Add(string.IsNullOrWhiteSpace(value) ? "HIK_PASSWORD" : value.Trim());
    }

    private static void AddIfPresent(List<string> args, string flag, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        args.Add(flag);
        args.Add(value.Trim());
    }

    private static void AddDecimal(List<string> args, string flag, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        args.Add(flag);
        args.Add(NormalizeDecimal(value));
    }

    private static string NormalizeDecimal(string text)
    {
        if (!TryParseDecimal(text, out var value))
            return text.Trim();
        return value.ToString("0.################", CultureInfo.InvariantCulture);
    }

    private static string? CheckOptionalInt(string text, int min, int max, string label)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || value < min
            || value > max)
            return $"{label}必須是 {min} 到 {max} 的整數";
        return null;
    }

    private static string? CheckOptionalDecimal(string text, double min, double max, string label, string example)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (!TryParseDecimal(text, out var value) || value < min || value > max)
        {
            return $"{label}必須介於 {min.ToString(CultureInfo.InvariantCulture)} 到 {max.ToString(CultureInfo.InvariantCulture)}，例如 {example}";
        }
        return null;
    }

    private static bool TryParseDecimal(string text, out double value)
    {
        var normalized = text.Trim().Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
