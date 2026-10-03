using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace HikIsapi;

public sealed class DigestChallenge
{
    public required string Realm { get; init; }
    public required string Nonce { get; init; }
    public string? Opaque { get; init; }
    public string? Qop { get; init; }
    public string Algorithm { get; init; } = "MD5";
}

public sealed class DigestResponse
{
    public int StatusCode { get; init; }
    public byte[] Body { get; init; } = Array.Empty<byte>();
    public string? Challenge { get; init; }
    public string? NextNonce { get; init; }
    public string? Error { get; init; }
    public bool Unauthorized => StatusCode == 401;
}

public static class DigestCalculator
{
    public static string ResponseHash(
        string method,
        string uri,
        string username,
        string password,
        DigestChallenge challenge,
        string cnonce,
        int nc)
    {
        var ha1 = Md5($"{username}:{challenge.Realm}:{password}");
        var ha2 = Md5($"{method}:{uri}");
        if (string.IsNullOrEmpty(challenge.Qop))
            return Md5($"{ha1}:{challenge.Nonce}:{ha2}");
        var count = nc.ToString("x8", CultureInfo.InvariantCulture);
        return Md5($"{ha1}:{challenge.Nonce}:{count}:{cnonce}:auth:{ha2}");
    }

    public static string AuthorizationHeader(
        string method,
        string uri,
        string username,
        string password,
        DigestChallenge challenge,
        string cnonce,
        int nc)
    {
        var hash = ResponseHash(method, uri, username, password, challenge, cnonce, nc);
        // 欄位順序跟 httpx DigestAuth 相同。這台相機接受那個客戶端，
        // 內嵌解析器有時會在意 response 與 algorithm 的先後。
        var parts = new List<string>
        {
            $"username=\"{Escape(username)}\"",
            $"realm=\"{Escape(challenge.Realm)}\"",
            $"nonce=\"{Escape(challenge.Nonce)}\"",
            $"uri=\"{Escape(uri)}\"",
            $"response=\"{hash}\"",
            "algorithm=MD5",
        };
        if (!string.IsNullOrEmpty(challenge.Opaque))
            parts.Add($"opaque=\"{Escape(challenge.Opaque)}\"");
        if (!string.IsNullOrEmpty(challenge.Qop))
        {
            parts.Add("qop=auth");
            parts.Add($"nc={nc.ToString("x8", CultureInfo.InvariantCulture)}");
            parts.Add($"cnonce=\"{Escape(cnonce)}\"");
        }
        return "Digest " + string.Join(", ", parts);
    }

    internal static string? SelectDigestChallenge(IEnumerable<string>? values)
    {
        if (values == null)
            return null;
        var parts = values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).ToList();
        for (var index = 0; index < parts.Count; index++)
        {
            if (!parts[index].StartsWith("Digest", StringComparison.OrdinalIgnoreCase))
                continue;
            var builder = new StringBuilder(parts[index]);
            for (var next = index + 1; next < parts.Count; next++)
            {
                if (IsAuthScheme(parts[next]))
                    break;
                builder.Append(", ").Append(parts[next]);
            }
            return builder.ToString();
        }
        return null;
    }

    private static bool IsAuthScheme(string value)
    {
        var space = value.IndexOf(' ');
        var word = space < 0 ? value : value[..space];
        return word.Equals("Basic", StringComparison.OrdinalIgnoreCase)
            || word.Equals("Digest", StringComparison.OrdinalIgnoreCase)
            || word.Equals("Bearer", StringComparison.OrdinalIgnoreCase)
            || word.Equals("Negotiate", StringComparison.OrdinalIgnoreCase)
            || word.Equals("NTLM", StringComparison.OrdinalIgnoreCase);
    }

    public static string? ReadNextNonce(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
            return null;
        var pairs = ParsePairs(header);
        return pairs.TryGetValue("nextnonce", out var nonce) && nonce.Length > 0 ? nonce : null;
    }

    public static bool TryParseChallenge(string? header, out DigestChallenge? challenge)
    {
        challenge = null;
        if (string.IsNullOrWhiteSpace(header))
            return false;
        var pairs = ParsePairs(header);
        if (!pairs.TryGetValue("realm", out var realm) || !pairs.TryGetValue("nonce", out var nonce))
            return false;
        var algorithm = pairs.TryGetValue("algorithm", out var parsed) && parsed.Length > 0 ? parsed : "MD5";
        if (!algorithm.Equals("MD5", StringComparison.OrdinalIgnoreCase))
            return false;
        string? qop = null;
        if (pairs.TryGetValue("qop", out var rawQop) && rawQop.Length > 0)
        {
            var modes = rawQop.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (!modes.Contains("auth", StringComparer.OrdinalIgnoreCase))
                return false;
            qop = "auth";
        }
        challenge = new DigestChallenge
        {
            Realm = realm,
            Nonce = nonce,
            Opaque = pairs.TryGetValue("opaque", out var opaque) ? opaque : null,
            Qop = qop,
            Algorithm = "MD5",
        };
        return true;
    }

    public static string Md5(string text)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static Dictionary<string, string> ParsePairs(string header)
    {
        var text = header.Trim();
        if (text.StartsWith("Digest", StringComparison.OrdinalIgnoreCase))
            text = text[6..].TrimStart();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        while (index < text.Length)
        {
            while (index < text.Length && (text[index] == ',' || char.IsWhiteSpace(text[index])))
                index++;
            if (index >= text.Length)
                break;
            var start = index;
            while (index < text.Length && text[index] != '=' && text[index] != ',')
                index++;
            if (index >= text.Length || text[index] != '=')
                break;
            var key = text[start..index].Trim();
            index++;
            string value;
            if (index < text.Length && text[index] == '"')
            {
                index++;
                var builder = new StringBuilder();
                while (index < text.Length && text[index] != '"')
                {
                    if (text[index] == '\\' && index + 1 < text.Length)
                    {
                        builder.Append(text[index + 1]);
                        index += 2;
                        continue;
                    }
                    builder.Append(text[index]);
                    index++;
                }
                if (index < text.Length && text[index] == '"')
                    index++;
                value = builder.ToString();
            }
            else
            {
                var valueStart = index;
                while (index < text.Length && text[index] != ',')
                    index++;
                value = text[valueStart..index].Trim();
            }
            if (key.Length > 0)
                result[key] = value;
        }
        return result;
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

public sealed class DigestHttp : IDisposable
{
    private readonly HttpClient _client;
    private DigestChallenge? _cached;
    private int _nc;

    public DigestHttp(TimeSpan? timeout = null)
        : this(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            // 海康抓圖的 nonce 跟這條連線綁在一起，留著連線再送下一張會直接 401。
            PooledConnectionLifetime = TimeSpan.Zero,
            ConnectTimeout = TimeSpan.FromSeconds(3),
        }, disposeHandler: true, timeout)
    {
    }

    public DigestHttp(HttpMessageHandler handler, bool disposeHandler = true, TimeSpan? timeout = null)
    {
        _client = new HttpClient(handler, disposeHandler)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(8),
        };
        _client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "hik-isapi/0.1.0");
        _client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/xml, application/json;q=0.9, */*;q=0.8");
    }

    public async Task<DigestResponse> GetAsync(Uri uri, string username, string password, CancellationToken cancellationToken)
    {
        var challenge = _cached;
        var first = await SendOnceAsync(uri, challenge, username, password, cancellationToken).ConfigureAwait(false);
        if (first.StatusCode != 401)
        {
            NoteSuccess(first, challenge);
            return first;
        }
        if (!DigestCalculator.TryParseChallenge(first.Challenge, out var next) || next == null)
        {
            _cached = null;
            return first;
        }
        _cached = next;
        _nc = 0;
        var second = await SendOnceAsync(uri, next, username, password, cancellationToken).ConfigureAwait(false);
        if (second.StatusCode == 401)
            _cached = null;
        else
            NoteSuccess(second, next);
        return second;
    }

    private void NoteSuccess(DigestResponse response, DigestChallenge? challenge)
    {
        if (response.StatusCode is < 200 or >= 300 || challenge == null || string.IsNullOrEmpty(response.NextNonce))
        {
            // 沒有 nextnonce 就不要把舊 nonce 留到下一張。這台相機用過一次就回 401。
            _cached = null;
            _nc = 0;
            return;
        }
        _cached = new DigestChallenge
        {
            Realm = challenge.Realm,
            Nonce = response.NextNonce,
            Opaque = challenge.Opaque,
            Qop = challenge.Qop,
            Algorithm = challenge.Algorithm,
        };
        _nc = 0;
    }

    private async Task<DigestResponse> SendOnceAsync(
        Uri uri,
        DigestChallenge? challenge,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.ConnectionClose = true;
        if (challenge != null)
        {
            _nc++;
            var cnonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            var header = DigestCalculator.AuthorizationHeader(
                "GET",
                uri.PathAndQuery,
                username,
                password,
                challenge,
                cnonce,
                _nc);
            request.Headers.TryAddWithoutValidation("Authorization", header);
        }
        try
        {
            using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return new DigestResponse
            {
                StatusCode = (int)response.StatusCode,
                Body = body,
                Challenge = ReadChallenge(response),
                NextNonce = ReadNextNonce(response),
            };
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DigestResponse { StatusCode = 0, Error = "逾時" };
        }
        catch (HttpRequestException ex)
        {
            return new DigestResponse { StatusCode = 0, Error = ex.Message };
        }
    }

    private static string? ReadNextNonce(HttpResponseMessage response)
    {
        if (response.Headers.NonValidated.TryGetValues("Authentication-Info", out var raw))
        {
            var nonce = DigestCalculator.ReadNextNonce(string.Join(", ", raw));
            if (nonce != null)
                return nonce;
        }
        return response.Headers.TryGetValues("Authentication-Info", out var values)
            ? DigestCalculator.ReadNextNonce(string.Join(", ", values))
            : null;
    }

    private static string? ReadChallenge(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return null;
        // WwwAuthenticate 會把 Digest 參數的逗號當成下一組認證，nonce 因此消失，重試被當成 401。
        if (response.Headers.NonValidated.TryGetValues("WWW-Authenticate", out var raw))
        {
            var selected = DigestCalculator.SelectDigestChallenge(raw);
            if (selected != null)
                return selected;
        }
        return DigestCalculator.SelectDigestChallenge(response.Headers.WwwAuthenticate.Select(header =>
            string.IsNullOrEmpty(header.Parameter) ? header.Scheme : header.Scheme + " " + header.Parameter));
    }

    public void Dispose() => _client.Dispose();
}
