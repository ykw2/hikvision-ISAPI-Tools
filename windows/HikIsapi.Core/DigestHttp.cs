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
        var parts = new List<string>
        {
            $"username=\"{Escape(username)}\"",
            $"realm=\"{Escape(challenge.Realm)}\"",
            $"nonce=\"{Escape(challenge.Nonce)}\"",
            $"uri=\"{Escape(uri)}\"",
            "algorithm=MD5",
            $"response=\"{hash}\"",
        };
        if (!string.IsNullOrEmpty(challenge.Qop))
        {
            parts.Add("qop=auth");
            parts.Add($"nc={nc.ToString("x8", CultureInfo.InvariantCulture)}");
            parts.Add($"cnonce=\"{Escape(cnonce)}\"");
        }
        if (!string.IsNullOrEmpty(challenge.Opaque))
            parts.Add($"opaque=\"{Escape(challenge.Opaque)}\"");
        return "Digest " + string.Join(", ", parts);
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
        : this(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }, disposeHandler: true, timeout)
    {
    }

    public DigestHttp(HttpMessageHandler handler, bool disposeHandler = true, TimeSpan? timeout = null)
    {
        _client = new HttpClient(handler, disposeHandler)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(8),
        };
    }

    public async Task<DigestResponse> GetAsync(Uri uri, string username, string password, CancellationToken cancellationToken)
    {
        var challenge = _cached;
        var first = await SendOnceAsync(uri, challenge, username, password, cancellationToken).ConfigureAwait(false);
        if (first.StatusCode != 401)
        {
            if (first.StatusCode is >= 200 and < 300)
                _cached = challenge;
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
        return second;
    }

    private async Task<DigestResponse> SendOnceAsync(
        Uri uri,
        DigestChallenge? challenge,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
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

    private static string? ReadChallenge(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return null;
        foreach (var header in response.Headers.WwwAuthenticate)
        {
            if (!header.Scheme.Equals("Digest", StringComparison.OrdinalIgnoreCase))
                continue;
            return string.IsNullOrEmpty(header.Parameter) ? header.Scheme : header.Scheme + " " + header.Parameter;
        }
        if (response.Headers.TryGetValues("WWW-Authenticate", out var values))
            return values.FirstOrDefault();
        return null;
    }

    public void Dispose() => _client.Dispose();
}
