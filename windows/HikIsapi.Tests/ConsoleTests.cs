namespace HikIsapi.Tests;

public sealed class IpRangeTests
{
    [Fact]
    public void ParsesSingleRangeAndList()
    {
        Assert.True(IpRangeParser.TryParse("192.168.38.201", out var single, out _));
        Assert.Equal(new[] { "192.168.38.201" }, single);

        Assert.True(IpRangeParser.TryParse("192.168.38.1-3; 192.168.38.3,192.168.38.8", out var many, out _));
        Assert.Equal(new[] { "192.168.38.1", "192.168.38.2", "192.168.38.3", "192.168.38.8" }, many);
        Assert.Equal(new[] { "cam-1" }, AssertParsed("cam-1"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("192.168.38.10-1")]
    [InlineData("192.168.38.1-300")]
    public void RejectsBadInput(string input)
    {
        Assert.False(IpRangeParser.TryParse(input, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    private static IReadOnlyList<string> AssertParsed(string input)
    {
        Assert.True(IpRangeParser.TryParse(input, out var addresses, out var error), error);
        return addresses;
    }
}

public sealed class ConsoleLaunchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hik-console-" + Guid.NewGuid().ToString("N"));
    private readonly string _python;

    public ConsoleLaunchTests()
    {
        Directory.CreateDirectory(_root);
        _python = Path.Combine(_root, "python.exe");
        File.WriteAllText(_python, "");
    }

    [Fact]
    public void SetTemperatureMergesAndHidesPassword()
    {
        const string password = "secret-密碼";
        var ok = ConsoleLaunch.TryCreate(
            ConsoleTask.SetTemp,
            new ConsoleInput
            {
                IpInput = "192.168.38.1-2",
                Username = "admin",
                Password = password,
                Alert = "220,0",
                Alarm = "230.5",
            },
            _python,
            windows: true,
            _ => null,
            Path.Combine(_root, "job"),
            out var plan,
            out var error);
        Assert.True(ok, error);
        Assert.Equal(new[] { "192.168.38.1", "192.168.38.2" }, plan!.Addresses);
        Assert.Equal(password, plan.Launch.Environment["HIK_PASSWORD"]);
        Assert.DoesNotContain(password, plan.Launch.DisplayCommand);
        Assert.Contains("--quiet", plan.Launch.Arguments);
        var yaml = File.ReadAllText(Path.Combine(_root, "job", "profile.yaml"));
        Assert.Contains("mode: merge_xml", yaml);
        Assert.Contains(ConsoleLaunch.ThermalPath, yaml);
        Assert.Contains("\"220.0\"", yaml);
        Assert.Contains("\"230.5\"", yaml);
        Assert.DoesNotContain(password, yaml);
    }

    [Fact]
    public void ManualRequestWritesBodyFile()
    {
        var ok = ConsoleLaunch.TryCreate(
            ConsoleTask.Manual,
            new ConsoleInput
            {
                IpInput = "10.0.0.8",
                Password = "pw",
                Method = "put",
                Path = "/ISAPI/System/reboot",
                Body = "<root/>",
            },
            _python,
            windows: true,
            _ => null,
            Path.Combine(_root, "manual"),
            out var plan,
            out var error);
        Assert.True(ok, error);
        Assert.Contains("apply", plan!.Launch.Arguments);
        Assert.Equal("<root/>", File.ReadAllText(Path.Combine(_root, "manual", "body.xml")));
        Assert.Contains("body_file: body.xml", File.ReadAllText(Path.Combine(_root, "manual", "profile.yaml")));
    }

    [Fact]
    public void RequiresPasswordAndRootedPath()
    {
        Assert.False(ConsoleLaunch.TryCreate(ConsoleTask.DeviceInfo, new ConsoleInput { IpInput = "10.0.0.1" }, _python, true, _ => null, _root, out _, out var missing));
        Assert.Contains("密碼", missing);
        Assert.False(ConsoleLaunch.TryCreate(
            ConsoleTask.Manual,
            new ConsoleInput { IpInput = "10.0.0.1", Password = "pw", Method = "GET", Path = "ISAPI/System/deviceInfo" },
            _python,
            true,
            _ => null,
            _root,
            out _,
            out var path));
        Assert.Contains("路徑必須以 / 開頭", path);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}

public sealed class ConsoleReportTests
{
    [Fact]
    public void FormatsDeviceInfoAndTemperatureChanges()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hik-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var info = Path.Combine(directory, "info.json");
        File.WriteAllText(info, """
        {
          "cameras": [
            {
              "host": "10.0.0.8",
              "ok": true,
              "skipped": false,
              "steps": [
                { "body": "<DeviceInfo xmlns=\"urn:x\"><model>DS-2</model><firmwareVersion>V1</firmwareVersion><serialNumber>ABC</serialNumber></DeviceInfo>" }
              ]
            }
          ]
        }
        """);
        var text = ConsoleReport.Format(ConsoleTask.DeviceInfo, info);
        Assert.Contains("10.0.0.8", text);
        Assert.Contains("DS-2", text);
        Assert.Contains("ABC", text);

        var temp = Path.Combine(directory, "temp.json");
        File.WriteAllText(temp, """
        {
          "cameras": [
            {
              "host": "10.0.0.8",
              "ok": true,
              "skipped": false,
              "steps": [
                {
                  "note": "已寫入",
                  "changes": [
                    {"path": "alert", "before": "200", "after": "220"},
                    {"path": "alarm", "before": "210", "after": "230"}
                  ]
                }
              ]
            }
          ]
        }
        """);
        var changed = ConsoleReport.Format(ConsoleTask.SetTemp, temp);
        Assert.Contains("200→220℃", changed);
        Assert.Contains("修改成功", changed);
        Directory.Delete(directory, recursive: true);
    }
}

public sealed class DigestTests
{
    [Fact]
    public void MatchesRfc2617Vector()
    {
        var challenge = new DigestChallenge
        {
            Realm = "testrealm@host.com",
            Nonce = "dcd98b7102dd2f0e8b11d0f600bfb0c093",
            Opaque = "5ccc069c403ebaf9f0171e9517f40e41",
            Qop = "auth",
        };
        var hash = DigestCalculator.ResponseHash(
            "GET",
            "/dir/index.html",
            "Mufasa",
            "Circle Of Life",
            challenge,
            "0a4f113b",
            1);
        Assert.Equal("6629fae49393a05397450978507c4ef1", hash);
        Assert.True(DigestCalculator.TryParseChallenge(
            "Digest realm=\"testrealm@host.com\", nonce=\"dcd98b7102dd2f0e8b11d0f600bfb0c093\", qop=\"auth\"",
            out var parsed));
        Assert.Equal("auth", parsed!.Qop);
    }

    [Fact]
    public void ParsesHikvisionChallengeWithNonceAfterQop()
    {
        Assert.True(DigestCalculator.TryParseChallenge(
            "Digest qop=\"auth\", realm=\"IP Camera(C2872)\", nonce=\"abc\", stale=\"FALSE\"",
            out var parsed));
        Assert.Equal("IP Camera(C2872)", parsed!.Realm);
        Assert.Equal("abc", parsed.Nonce);
        Assert.Equal("auth", parsed.Qop);
        Assert.Null(parsed.Opaque);
    }

    [Fact]
    public void JoinsDigestParametersSplitOnCommas()
    {
        var selected = DigestCalculator.SelectDigestChallenge(new[]
        {
            "Basic realm=\"IP Camera\"",
            "Digest realm=\"IP Camera(C2872)\"",
            "nonce=\"abc\"",
            "qop=\"auth\"",
            "stale=\"FALSE\"",
        });
        Assert.True(DigestCalculator.TryParseChallenge(selected, out var parsed));
        Assert.Equal("abc", parsed!.Nonce);
        Assert.Equal("auth", parsed.Qop);
    }

    [Fact]
    public void AuthorizationHeaderMatchesHttpxFieldOrder()
    {
        var header = DigestCalculator.AuthorizationHeader(
            "GET",
            "/ISAPI/Streaming/channels/101/picture",
            "admin",
            "secret",
            new DigestChallenge { Realm = "IP Camera(C2872)", Nonce = "4e44493d", Qop = "auth" },
            "3d990d12cad7554c",
            1);
        Assert.Equal(
            "Digest username=\"admin\", realm=\"IP Camera(C2872)\", nonce=\"4e44493d\", uri=\"/ISAPI/Streaming/channels/101/picture\", response=\"2a429d16461c8474bb9bcf8cba2c6725\", algorithm=MD5, qop=auth, nc=00000001, cnonce=\"3d990d12cad7554c\"",
            header);
    }

    [Fact]
    public async Task RetriesWithDigestAfterChallenge()
    {
        var handler = new ChallengeHandler();
        using var client = new DigestHttp(handler, disposeHandler: true, TimeSpan.FromSeconds(5));
        var response = await client.GetAsync(new Uri("http://10.0.0.8/ISAPI/Streaming/channels/101/picture"), "admin", "pw", CancellationToken.None);
        Assert.Equal(200, response.StatusCode);
        Assert.Equal(new byte[] { 1, 2, 3 }, response.Body);
        Assert.Equal(2, handler.Authorizations.Count);
        Assert.Null(handler.Authorizations[0]);
        Assert.Contains("Digest ", handler.Authorizations[1]);
        Assert.Contains("username=\"admin\"", handler.Authorizations[1]);
        var authorization = handler.Authorizations[1]!;
        Assert.True(authorization.IndexOf("response=", StringComparison.Ordinal) < authorization.IndexOf("algorithm=MD5", StringComparison.Ordinal));
        Assert.Equal("hik-isapi/0.1.0", handler.UserAgents[1]);
    }

    [Fact]
    public void ReadsNextNonceFromAuthenticationInfo()
    {
        Assert.Equal("n2", DigestCalculator.ReadNextNonce("nextnonce=\"n2\", qop=auth, nc=00000001"));
        Assert.Null(DigestCalculator.ReadNextNonce("qop=auth"));
    }

    [Fact]
    public async Task TakesAFreshNonceForEachSnapshot()
    {
        var handler = new OneTimeNonceHandler();
        using var client = new DigestHttp(handler, disposeHandler: true, TimeSpan.FromSeconds(5));
        var uri = new Uri("http://10.0.0.8/ISAPI/Streaming/channels/101/picture");
        var first = await client.GetAsync(uri, "admin", "pw", CancellationToken.None);
        var second = await client.GetAsync(uri, "admin", "pw", CancellationToken.None);
        Assert.Equal(200, first.StatusCode);
        Assert.Equal(200, second.StatusCode);
        Assert.Equal(4, handler.Authorizations.Count);
        Assert.Null(handler.Authorizations[0]);
        Assert.Contains("nonce=\"n1\"", handler.Authorizations[1]);
        Assert.Contains("nc=00000001", handler.Authorizations[1]);
        Assert.Null(handler.Authorizations[2]);
        Assert.Contains("nonce=\"n2\"", handler.Authorizations[3]);
        Assert.Contains("nc=00000001", handler.Authorizations[3]);
        Assert.All(handler.ConnectionClose, closed => Assert.True(closed));
    }

    [Fact]
    public async Task UsesNextNonceOnTheFollowingSnapshot()
    {
        var handler = new NextNonceHandler();
        using var client = new DigestHttp(handler, disposeHandler: true, TimeSpan.FromSeconds(5));
        var uri = new Uri("http://10.0.0.8/ISAPI/Streaming/channels/101/picture");
        Assert.Equal(200, (await client.GetAsync(uri, "admin", "pw", CancellationToken.None)).StatusCode);
        Assert.Equal(200, (await client.GetAsync(uri, "admin", "pw", CancellationToken.None)).StatusCode);
        Assert.Equal(3, handler.Authorizations.Count);
        Assert.Null(handler.Authorizations[0]);
        Assert.Contains("nonce=\"n1\"", handler.Authorizations[1]);
        Assert.Contains("nonce=\"n2\"", handler.Authorizations[2]);
        Assert.Contains("nc=00000001", handler.Authorizations[2]);
    }

    [Fact]
    public async Task KeepsNonceFromRawWwwAuthenticate()
    {
        var handler = new RawChallengeHandler();
        using var client = new DigestHttp(handler, disposeHandler: true, TimeSpan.FromSeconds(5));
        var response = await client.GetAsync(new Uri("http://10.0.0.8/ISAPI/Streaming/channels/101/picture"), "admin", "pw", CancellationToken.None);
        Assert.Equal(200, response.StatusCode);
        Assert.Contains("nonce=\"abc\"", handler.Authorizations[1]);
        Assert.Contains("realm=\"IP Camera(C2872)\"", handler.Authorizations[1]);
    }

    private sealed class ChallengeHandler : HttpMessageHandler
    {
        public List<string?> Authorizations { get; } = new();
        public List<string?> UserAgents { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorizations.Add(request.Headers.TryGetValues("Authorization", out var values) ? values.FirstOrDefault() : null);
            UserAgents.Add(request.Headers.TryGetValues("User-Agent", out var agents) ? agents.FirstOrDefault() : null);
            Assert.True(request.Headers.ConnectionClose);
            if (Authorizations.Count == 1)
            {
                var denied = new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized);
                denied.Headers.WwwAuthenticate.Add(new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Digest",
                    "realm=\"IP Camera\", nonce=\"abc\", qop=\"auth\", algorithm=MD5"));
                return Task.FromResult(denied);
            }
            var ok = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3 }),
            };
            return Task.FromResult(ok);
        }
    }

    private sealed class OneTimeNonceHandler : HttpMessageHandler
    {
        public List<string?> Authorizations { get; } = new();
        public List<bool?> ConnectionClose { get; } = new();
        private int _issued;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var authorization = request.Headers.TryGetValues("Authorization", out var values) ? values.FirstOrDefault() : null;
            Authorizations.Add(authorization);
            ConnectionClose.Add(request.Headers.ConnectionClose);
            if (string.IsNullOrEmpty(authorization))
            {
                _issued++;
                var denied = new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized);
                denied.Headers.TryAddWithoutValidation(
                    "WWW-Authenticate",
                    $"Digest qop=\"auth\", realm=\"IP Camera(C2872)\", nonce=\"n{_issued}\", stale=\"FALSE\"");
                return Task.FromResult(denied);
            }
            if (!authorization.Contains($"nonce=\"n{_issued}\"", StringComparison.Ordinal) || !authorization.Contains("nc=00000001", StringComparison.Ordinal))
            {
                var denied = new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized);
                denied.Headers.TryAddWithoutValidation(
                    "WWW-Authenticate",
                    $"Digest qop=\"auth\", realm=\"IP Camera(C2872)\", nonce=\"n{_issued}\", stale=\"TRUE\"");
                return Task.FromResult(denied);
            }
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 9 }),
            });
        }
    }

    private sealed class NextNonceHandler : HttpMessageHandler
    {
        public List<string?> Authorizations { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var authorization = request.Headers.TryGetValues("Authorization", out var values) ? values.FirstOrDefault() : null;
            Authorizations.Add(authorization);
            if (string.IsNullOrEmpty(authorization))
            {
                var denied = new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized);
                denied.Headers.TryAddWithoutValidation(
                    "WWW-Authenticate",
                    "Digest qop=\"auth\", realm=\"IP Camera(C2872)\", nonce=\"n1\", stale=\"FALSE\"");
                return Task.FromResult(denied);
            }
            if (authorization.Contains("nonce=\"n2\"", StringComparison.Ordinal) && authorization.Contains("nc=00000001", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(new byte[] { 9 }),
                });
            }
            var ok = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 9 }),
            };
            ok.Headers.TryAddWithoutValidation("Authentication-Info", "nextnonce=\"n2\", qop=auth");
            return Task.FromResult(ok);
        }
    }

    private sealed class RawChallengeHandler : HttpMessageHandler
    {
        public List<string?> Authorizations { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorizations.Add(request.Headers.TryGetValues("Authorization", out var values) ? values.FirstOrDefault() : null);
            if (Authorizations.Count == 1)
            {
                var denied = new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized);
                denied.Headers.TryAddWithoutValidation(
                    "WWW-Authenticate",
                    "Digest qop=\"auth\", realm=\"IP Camera(C2872)\", nonce=\"abc\", stale=\"FALSE\"");
                return Task.FromResult(denied);
            }
            var ok = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3 }),
            };
            return Task.FromResult(ok);
        }
    }
}
