using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml.Linq;

namespace HikIsapi;

public static class ConsoleReport
{
    private static readonly JsonSerializerOptions PrettyOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Pretty(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text ?? "";
        var trimmed = text.Trim();
        if (trimmed.Length < 2 || (trimmed[0] != '{' && trimmed[0] != '['))
            return text;
        try
        {
            using var document = JsonDocument.Parse(trimmed);
            return JsonSerializer.Serialize(document.RootElement, PrettyOptions);
        }
        catch (JsonException)
        {
            return text;
        }
    }

    public static string Format(ConsoleTask task, string jsonPath)
    {
        if (!File.Exists(jsonPath))
            return "沒有產生報告。";
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(jsonPath));
            if (!document.RootElement.TryGetProperty("cameras", out var cameras) || cameras.ValueKind != JsonValueKind.Array)
                return "報告裡沒有攝影機結果。";
            var items = cameras.EnumerateArray().ToList();
            var lines = new List<string> { Header(task) };
            foreach (var camera in items)
                lines.Add(FormatCamera(task, camera, includeBody: task == ConsoleTask.Manual && items.Count == 1));
            if (document.RootElement.TryGetProperty("safety_message", out var safety)
                && safety.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(safety.GetString()))
                lines.Add(safety.GetString()!);
            return string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return "無法讀取報告：" + ex.Message;
        }
    }

    public static string? XmlText(string? xml, string localName)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return null;
        try
        {
            var document = XDocument.Parse(xml);
            return document.Descendants().FirstOrDefault(element => element.Name.LocalName == localName)?.Value;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static string Header(ConsoleTask task) => task switch
    {
        ConsoleTask.DeviceInfo => string.Format("{0,-16} | {1,-18} | {2,-8} | {3}", "IP 位址", "型號", "韌體", "序號"),
        ConsoleTask.QueryTemp => string.Format("{0,-16} | {1,-12} | {2,-12}", "IP 位址", "預警 (Alert)", "警告 (Alarm)"),
        ConsoleTask.SetTemp => string.Format("{0,-16} | {1,-14} | {2,-14} | {3}", "IP 位址", "預警變更", "警告變更", "狀態"),
        ConsoleTask.Reboot => string.Format("{0,-16} | {1}", "IP 位址", "執行結果"),
        ConsoleTask.Manual => string.Format("{0,-16} | {1,-6} | {2}", "IP 位址", "HTTP", "結果"),
        _ => "結果",
    };

    private static string FormatCamera(ConsoleTask task, JsonElement camera, bool includeBody)
    {
        var host = Text(camera, "host") ?? "";
        var skipped = Flag(camera, "skipped");
        var ok = Flag(camera, "ok");
        var error = Text(camera, "error") ?? Text(camera, "note") ?? "失敗";
        if (skipped)
            return $"{host,-16} | 略過 {error}";
        if (!ok && task is not (ConsoleTask.DeviceInfo or ConsoleTask.QueryTemp or ConsoleTask.SetTemp or ConsoleTask.Manual))
            return $"{host,-16} | 失敗: {error}";

        var step = FirstStep(camera);
        return task switch
        {
            ConsoleTask.DeviceInfo => ok
                ? string.Format(
                    "{0,-16} | {1,-18} | {2,-8} | {3}",
                    host,
                    XmlText(Body(step), "model") ?? "未知",
                    XmlText(Body(step), "firmwareVersion") ?? "未知",
                    XmlText(Body(step), "serialNumber") ?? "未知")
                : $"{host,-16} | 失敗: {error}",
            ConsoleTask.QueryTemp => ok
                ? string.Format(
                    "{0,-16} | {1,-12} | {2,-12}",
                    host,
                    (XmlText(Body(step), "alert") ?? "--") + " ℃",
                    (XmlText(Body(step), "alarm") ?? "--") + " ℃")
                : $"{host,-16} | 失敗: {error}",
            ConsoleTask.SetTemp => string.Format(
                "{0,-16} | {1,-14} | {2,-14} | {3}",
                host,
                Change(step, "alert"),
                Change(step, "alarm"),
                ok ? StatusNote(step) : "失敗: " + error),
            ConsoleTask.Reboot => ok
                ? $"{host,-16} | 重啟指令已送出"
                : $"{host,-16} | 失敗: {error}",
            ConsoleTask.Manual => FormatManual(host, step, ok, error, includeBody),
            _ => $"{host,-16} | {(ok ? "完成" : error)}",
        };
    }

    private static string FormatManual(string host, JsonElement? step, bool ok, string error, bool includeBody)
    {
        var status = step != null && step.Value.TryGetProperty("http_status", out var code) && code.TryGetInt32(out var number)
            ? number.ToString()
            : "-";
        var line = string.Format("{0,-16} | {1,-6} | {2}", host, status, ok ? "完成" : error);
        if (!includeBody)
            return line;
        var body = Pretty(Body(step));
        return string.IsNullOrWhiteSpace(body) ? line : line + Environment.NewLine + body;
    }

    private static string StatusNote(JsonElement? step)
    {
        var note = step != null ? Text(step.Value, "note") : null;
        if (!string.IsNullOrWhiteSpace(note) && note.Contains("沒有差異", StringComparison.Ordinal))
            return "相同，未寫入";
        return "修改成功";
    }

    private static string Change(JsonElement? step, string name)
    {
        if (step == null || !step.Value.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
            return "--";
        foreach (var change in changes.EnumerateArray())
        {
            var path = Text(change, "path") ?? "";
            if (!path.Equals(name, StringComparison.OrdinalIgnoreCase)
                && !path.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase))
                continue;
            return $"{Text(change, "before")}→{Text(change, "after")}℃";
        }
        return "--";
    }

    private static JsonElement? FirstStep(JsonElement camera)
    {
        if (!camera.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var step in steps.EnumerateArray())
            return step;
        return null;
    }

    private static string? Body(JsonElement? step)
        => step != null ? Text(step.Value, "body") : null;

    private static string? Text(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            return null;
        return property.GetString();
    }

    private static bool Flag(JsonElement element, string name)
        => element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;
}
