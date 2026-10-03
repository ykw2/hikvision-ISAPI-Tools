using System.Globalization;
using System.Text.RegularExpressions;

namespace HikIsapi;

public static partial class IpRangeParser
{
    public const int MaxAddresses = 4096;

    [GeneratedRegex(@"^(?<prefix>(?:\d{1,3}\.){3})\s*(?<start>\d{1,3})\s*-\s*(?<end>\d{1,3})$")]
    private static partial Regex LastOctetRange();

    public static bool TryParse(string? input, out IReadOnlyList<string> addresses, out string? error)
    {
        addresses = Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(input))
        {
            error = "請輸入相機 IP 或範圍";
            return false;
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in input.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryExpand(token, out var expanded, out error))
                return false;
            foreach (var address in expanded)
            {
                if (seen.Add(address))
                    result.Add(address);
                if (result.Count > MaxAddresses)
                {
                    error = $"一次最多 {MaxAddresses} 支，請縮小範圍";
                    addresses = Array.Empty<string>();
                    return false;
                }
            }
        }

        if (result.Count == 0)
        {
            error = "無法解析你輸入的 IP 格式";
            return false;
        }

        addresses = result;
        error = null;
        return true;
    }

    private static bool TryExpand(string token, out IReadOnlyList<string> addresses, out string? error)
    {
        addresses = Array.Empty<string>();
        var match = LastOctetRange().Match(token);
        if (!match.Success)
        {
            if (token.Contains(' ') || token.Contains('/') || token.Contains('@'))
            {
                error = "IP 格式不正確：" + token;
                return false;
            }
            addresses = new[] { token };
            error = null;
            return true;
        }

        var prefix = match.Groups["prefix"].Value;
        if (!TryOctet(match.Groups["start"].Value, out var start) || !TryOctet(match.Groups["end"].Value, out var end))
        {
            error = "IP 範圍的數字必須介於 0 到 255：" + token;
            return false;
        }
        if (!IsIpv4Prefix(prefix))
        {
            error = "IP 格式不正確：" + token;
            return false;
        }
        if (start > end)
        {
            error = "IP 範圍的起點不能大於終點：" + token;
            return false;
        }

        var list = new List<string>(end - start + 1);
        for (var octet = start; octet <= end; octet++)
            list.Add(prefix + octet.ToString(CultureInfo.InvariantCulture));
        addresses = list;
        error = null;
        return true;
    }

    private static bool IsIpv4Prefix(string prefix)
    {
        var parts = prefix.Trim('.').Split('.');
        return parts.Length == 3 && parts.All(part => TryOctet(part, out _));
    }

    private static bool TryOctet(string text, out int value)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value is >= 0 and <= 255;
}
