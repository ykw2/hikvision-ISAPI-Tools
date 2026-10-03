using System.Text;
using System.Xml.Linq;

namespace HikIsapi;

public sealed class IsapiCall
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public int HttpStatus { get; init; }
    public string Body { get; init; } = "";
    public bool Ok { get; init; }
    public bool Retryable { get; init; }
    public string? Error { get; init; }
    public int? IsapiStatusCode { get; init; }
    public bool RebootRequired { get; init; }
}

public sealed class IsapiSession : IDisposable
{
    private static readonly HashSet<int> RetryableHttp = new() { 408, 500, 502, 503, 504 };
    private readonly DigestHttp _http;
    private readonly Uri _baseUri;
    private readonly string _username;
    private readonly string _password;

    public IsapiSession(DigestHttp http, Uri baseUri, string username, string password)
    {
        _http = http;
        _baseUri = baseUri;
        _username = username;
        _password = password;
    }

    public static IsapiSession Open(string host, int port, bool https, string username, string password, TimeSpan timeout, bool verifyTls)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.Zero,
            ConnectTimeout = TimeSpan.FromSeconds(3),
        };
        if (!verifyTls)
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        var http = new DigestHttp(handler, disposeHandler: true, timeout);
        var scheme = https ? "https" : "http";
        return new IsapiSession(http, new Uri($"{scheme}://{host}:{port}"), username, password);
    }

    public async Task<IsapiCall> RequestAsync(
        string method,
        string path,
        byte[]? content,
        string? contentType,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        var relative = path.StartsWith('/') ? path : "/" + path;
        var uri = new Uri(_baseUri, relative);
        var response = await _http.SendAsync(uri, method, _username, _password, content, contentType, headers, cancellationToken).ConfigureAwait(false);
        var body = Encoding.UTF8.GetString(response.Body);
        return Interpret(method.ToUpperInvariant(), relative, response.StatusCode, body, response.Error);
    }

    public static IsapiCall Interpret(string method, string path, int httpStatus, string body, string? transportError)
    {
        if (httpStatus == 0)
        {
            return new IsapiCall
            {
                Method = method,
                Path = path,
                HttpStatus = 0,
                Body = body,
                Ok = false,
                Retryable = true,
                Error = string.IsNullOrWhiteSpace(transportError) ? "連線失敗" : transportError,
            };
        }
        var status = ParseStatus(body);
        var reboot = status?.Code == 7;
        var retryable = RetryableHttp.Contains(httpStatus) || status?.Code == 2;
        string? error = null;
        var ok = false;
        if (httpStatus == 401)
            error = "認證失敗（HTTP 401）。請確認帳號密碼，以及攝影機的 Web 認證方式為 digest";
        else if (httpStatus == 403)
            error = "沒有權限（HTTP 403）";
        else if (status != null && status.Code is not (1 or 7))
            error = FormatStatus(status);
        else if (httpStatus is < 200 or > 299)
            error = "HTTP " + httpStatus;
        else
            ok = true;
        return new IsapiCall
        {
            Method = method,
            Path = path,
            HttpStatus = httpStatus,
            Body = body,
            Ok = ok,
            Retryable = retryable,
            Error = error,
            IsapiStatusCode = status?.Code,
            RebootRequired = reboot,
        };
    }

    public void Dispose() => _http.Dispose();

    private static ResponseStatus? ParseStatus(string body)
    {
        if (string.IsNullOrWhiteSpace(body) || !body.Contains('<'))
            return null;
        try
        {
            var root = IsapiXml.Parse(body);
            if (root.Name.LocalName != "ResponseStatus")
                return null;
            var codeText = IsapiXml.DirectText(root, "statusCode");
            int? code = int.TryParse(codeText, out var number) ? number : null;
            return new ResponseStatus(code, IsapiXml.DirectText(root, "statusString") ?? "", IsapiXml.DirectText(root, "subStatusCode") ?? "");
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static string FormatStatus(ResponseStatus status)
    {
        var parts = new List<string> { "ISAPI" };
        if (status.Code != null)
            parts.Add(status.Code.Value.ToString());
        if (!string.IsNullOrWhiteSpace(status.StatusString))
            parts.Add(status.StatusString);
        if (!string.IsNullOrWhiteSpace(status.SubStatus))
            parts.Add("(" + status.SubStatus + ")");
        return string.Join(" ", parts);
    }

    private sealed record ResponseStatus(int? Code, string StatusString, string SubStatus);
}
