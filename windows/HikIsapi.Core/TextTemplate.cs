using System.Text.RegularExpressions;

namespace HikIsapi;

public static class TextTemplate
{
    private static readonly Regex Token = new(@"\{\{\s*([A-Za-z_][\w]*)\.([A-Za-z_][\w]*)\s*\}\}", RegexOptions.Compiled);

    public static string Render(string text, IReadOnlyDictionary<string, string> camera, IReadOnlyDictionary<string, string> variables, IReadOnlyDictionary<string, string> environ)
    {
        return Token.Replace(text, match =>
        {
            var scope = match.Groups[1].Value;
            var key = match.Groups[2].Value;
            if (scope == "camera")
            {
                if (key == "password")
                    throw new InvalidOperationException("模板不能讀取密碼");
                if (!camera.TryGetValue(key, out var value))
                    throw new InvalidOperationException("攝影機沒有欄位 " + key);
                return value;
            }
            if (scope == "vars")
            {
                if (!variables.TryGetValue(key, out var value))
                    throw new InvalidOperationException("找不到變數 vars." + key);
                return value;
            }
            if (scope == "env")
            {
                if (!environ.TryGetValue(key, out var value) || value.Length == 0)
                    throw new InvalidOperationException("環境變數 " + key + " 未設定");
                return value;
            }
            throw new InvalidOperationException("不支援的模板範圍 " + scope + "，請使用 camera、vars 或 env");
        });
    }
}
