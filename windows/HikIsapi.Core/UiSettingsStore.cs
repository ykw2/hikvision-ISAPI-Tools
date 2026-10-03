using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HikIsapi;

public sealed class UiSettings
{
    public string PythonPath { get; set; } = "";
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
    public string GetHost { get; set; } = "";
    public string GetPort { get; set; } = "";
    public string GetPath { get; set; } = "/ISAPI/System/deviceInfo";
    public bool GetHttps { get; set; }
    public bool GetVerifyTls { get; set; }
    public string GetOutput { get; set; } = "";
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }
    public string ConsoleIp { get; set; } = "";
    public string ConsoleUsername { get; set; } = "admin";
    public string ConsoleAlert { get; set; } = "220.0";
    public string ConsoleAlarm { get; set; } = "220.0";
    public string ConsoleMethod { get; set; } = "GET";
    public string ConsolePath { get; set; } = "/ISAPI/System/deviceInfo";
    public string ConsoleChannel { get; set; } = "101 (一般/可見光)";
    public int ConsoleWidth { get; set; }
    public int ConsoleHeight { get; set; }
}

public static class UiSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string DefaultPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(root))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hik-isapi");
        return Path.Combine(root, "hik-isapi", "settings.json");
    }

    public static UiSettings LoadOrNew(string path, out string? warning)
    {
        warning = null;
        if (!File.Exists(path))
            return new UiSettings();
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<UiSettings>(json, JsonOptions) ?? new UiSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            warning = "無法讀取上次的視窗設定，已改用空白設定：" + ex.Message;
            return new UiSettings();
        }
    }

    public static void Save(string path, UiSettings settings)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(path, json);
    }
}
