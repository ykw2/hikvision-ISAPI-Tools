using System.Globalization;
using System.Text.Json;

namespace HikIsapi;

public sealed class CameraRow
{
    public required string Id { get; init; }
    public required string Host { get; init; }
    public string Name { get; init; } = "";
    public int? Port { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
    public bool? UseHttps { get; init; }
    public bool Enabled { get; init; } = true;
    public List<string> Tags { get; init; } = new();
    public Dictionary<string, string> Attributes { get; init; } = new();
}

public sealed class ResolvedCamera
{
    public required string Id { get; init; }
    public required string Host { get; init; }
    public required string Name { get; init; }
    public required int Port { get; init; }
    public required string Username { get; init; }
    public required string Password { get; init; }
    public required bool UseHttps { get; init; }
    public List<string> Tags { get; init; } = new();
    public Dictionary<string, string> Attributes { get; init; } = new();

    public Dictionary<string, string> TemplateFields()
    {
        var fields = new Dictionary<string, string>(Attributes, StringComparer.Ordinal);
        fields["id"] = Id;
        fields["host"] = Host;
        fields["port"] = Port.ToString(CultureInfo.InvariantCulture);
        fields["name"] = Name;
        fields["username"] = Username;
        fields["https"] = UseHttps ? "true" : "false";
        fields["tags"] = string.Join(";", Tags);
        return fields;
    }
}

public static class InventoryFile
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "host", "port", "username", "password", "https", "name", "enabled", "tags",
    };

    public static List<CameraRow> Load(string path)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException("找不到清單：" + path);
        var text = File.ReadAllText(path);
        var rows = ParseCsv(text);
        if (rows.Count == 0)
            throw new InvalidOperationException("清單是空的：" + path);
        var header = rows[0].Select(cell => cell.Trim()).ToList();
        if (!header.Contains("id") || !header.Contains("host"))
            throw new InvalidOperationException("清單必須包含 id 與 host 欄");
        var cameras = new List<CameraRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var line = 1; line < rows.Count; line++)
        {
            var cells = rows[line];
            if (cells.All(string.IsNullOrWhiteSpace))
                continue;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var column = 0; column < header.Count; column++)
                map[header[column]] = column < cells.Count ? cells[column].Trim() : "";
            var camera = ParseRow(path, line + 1, map);
            if (!seen.Add(camera.Id))
                throw new InvalidOperationException(path + ":" + (line + 1) + " id 重複：" + camera.Id);
            cameras.Add(camera);
        }
        if (cameras.Count == 0)
            throw new InvalidOperationException("清單沒有任何攝影機：" + path);
        return cameras;
    }

    public static List<CameraRow> Select(IReadOnlyList<CameraRow> cameras, IReadOnlyList<string> tags, IReadOnlyCollection<string>? only, int offset, int? limit)
    {
        if (offset < 0)
            throw new InvalidOperationException("offset 不能是負數");
        if (limit is < 1)
            throw new InvalidOperationException("limit 必須大於 0");
        var byId = cameras.ToDictionary(camera => camera.Id, StringComparer.Ordinal);
        if (only != null)
        {
            var missing = only.Where(id => !byId.ContainsKey(id)).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException("清單沒有這些 id：" + string.Join("、", missing.Take(20)));
        }
        var wanted = new HashSet<string>(tags.Where(tag => tag.Length > 0), StringComparer.Ordinal);
        var chosen = new List<CameraRow>();
        foreach (var camera in cameras)
        {
            if (!camera.Enabled)
                continue;
            if (only != null && !only.Contains(camera.Id))
                continue;
            if (wanted.Count > 0 && !camera.Tags.Any(wanted.Contains))
                continue;
            chosen.Add(camera);
        }
        var sliced = chosen.Skip(offset).ToList();
        if (limit != null)
            sliced = sliced.Take(limit.Value).ToList();
        return sliced;
    }

    public static List<ResolvedCamera> Resolve(IReadOnlyList<CameraRow> cameras, IsapiProfile profile, string? username, string? password, IReadOnlyDictionary<string, string> environ)
    {
        var shared = !string.IsNullOrEmpty(password)
            ? password
            : (environ.TryGetValue(profile.PasswordEnv, out var fromEnv) ? fromEnv : "");
        var missing = new List<string>();
        var resolved = new List<ResolvedCamera>();
        foreach (var camera in cameras)
        {
            var https = camera.UseHttps ?? profile.UseHttps;
            var port = camera.Port ?? (https ? 443 : 80);
            var user = string.IsNullOrWhiteSpace(camera.Username) ? (string.IsNullOrWhiteSpace(username) ? profile.Username : username.Trim()) : camera.Username;
            var secret = string.IsNullOrEmpty(camera.Password) ? shared : camera.Password;
            if (string.IsNullOrEmpty(secret))
            {
                missing.Add(camera.Id);
                continue;
            }
            resolved.Add(new ResolvedCamera
            {
                Id = camera.Id,
                Host = camera.Host,
                Name = string.IsNullOrEmpty(camera.Name) ? camera.Host : camera.Name,
                Port = port,
                Username = user,
                Password = secret!,
                UseHttps = https,
                Tags = camera.Tags,
                Attributes = camera.Attributes,
            });
        }
        if (missing.Count > 0)
            throw new InvalidOperationException("這些攝影機沒有密碼（可在清單填 password，或在視窗輸入密碼）：" + string.Join("、", missing.Take(20)));
        return resolved;
    }

    public static List<string> Warnings(IReadOnlyList<CameraRow> cameras)
    {
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        foreach (var camera in cameras)
        {
            var key = camera.Host + "|" + (camera.Port?.ToString(CultureInfo.InvariantCulture) ?? "") + "|" + (camera.UseHttps?.ToString() ?? "");
            if (seen.TryGetValue(key, out var previous))
                warnings.Add(camera.Id + " 與 " + previous + " 的主機、埠與協定相同");
            else
                seen[key] = camera.Id;
        }
        return warnings;
    }

    public static HashSet<string> FailedIds(string reportPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(reportPath));
        if (!document.RootElement.TryGetProperty("cameras", out var cameras) || cameras.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("報告格式不正確，找不到 cameras");
        var failed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var camera in cameras.EnumerateArray())
        {
            var id = camera.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
            var ok = camera.TryGetProperty("ok", out var okValue) && okValue.ValueKind == JsonValueKind.True;
            if (!string.IsNullOrEmpty(id) && !ok)
                failed.Add(id);
        }
        return failed;
    }

    private static CameraRow ParseRow(string file, int line, Dictionary<string, string> row)
    {
        var id = Value(row, "id");
        var host = Value(row, "host");
        if (id.Length == 0 || host.Length == 0)
            throw new InvalidOperationException(file + ":" + line + " 需要 id 與 host");
        if (id.Any(char.IsWhiteSpace) || host.Any(char.IsWhiteSpace))
            throw new InvalidOperationException(file + ":" + line + " id 與 host 不能包含空白");
        int? port = null;
        var portText = Value(row, "port");
        if (portText.Length > 0)
        {
            if (!int.TryParse(portText, out var number) || number is < 1 or > 65535)
                throw new InvalidOperationException(file + ":" + line + " port 不正確");
            port = number;
        }
        var httpsText = Value(row, "https");
        bool? https = httpsText.Length == 0 ? null : IsTrue(httpsText);
        var enabledText = Value(row, "enabled");
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in row)
        {
            if (!Reserved.Contains(pair.Key))
                attributes[pair.Key] = pair.Value;
        }
        return new CameraRow
        {
            Id = id,
            Host = host,
            Name = Value(row, "name"),
            Port = port,
            Username = BlankToNull(Value(row, "username")),
            Password = BlankToNull(Value(row, "password")),
            UseHttps = https,
            Enabled = enabledText.Length == 0 || IsTrue(enabledText),
            Tags = Value(row, "tags").Split(new[] { ';', '|', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            Attributes = attributes,
        };
    }

    private static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new System.Text.StringBuilder();
        var quoted = false;
        for (var index = 0; index < text.Length; index++)
        {
            var ch = text[index];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        cell.Append('"');
                        index++;
                    }
                    else
                        quoted = false;
                }
                else
                    cell.Append(ch);
                continue;
            }
            if (ch == '"')
            {
                quoted = true;
                continue;
            }
            if (ch == ',')
            {
                row.Add(cell.ToString());
                cell.Clear();
                continue;
            }
            if (ch is '\r' or '\n')
            {
                if (ch == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                    index++;
                row.Add(cell.ToString());
                cell.Clear();
                rows.Add(row);
                row = new List<string>();
                continue;
            }
            cell.Append(ch);
        }
        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            rows.Add(row);
        }
        return rows;
    }

    private static string Value(Dictionary<string, string> row, string key)
        => row.TryGetValue(key, out var value) ? value : "";

    private static string? BlankToNull(string text) => text.Length == 0 ? null : text;

    private static bool IsTrue(string text)
        => text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1" || text.Equals("yes", StringComparison.OrdinalIgnoreCase);
}
