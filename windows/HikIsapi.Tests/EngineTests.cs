using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace HikIsapi.Tests;

public sealed class EngineTests
{
    [Fact]
    public void TemplateRejectsCameraPassword()
    {
        var camera = new Dictionary<string, string>(StringComparer.Ordinal) { ["name"] = "大門" };
        var error = Assert.Throws<InvalidOperationException>(() =>
            TextTemplate.Render("{{camera.password}}", camera, new Dictionary<string, string>(), new Dictionary<string, string>()));
        Assert.Contains("密碼", error.Message);
    }

    [Fact]
    public void LoadsStandardProfileAndSelectsInventory()
    {
        var root = FindRepoRoot();
        var profile = ProfileFile.Load(Path.Combine(root, "examples", "profiles", "standard.yaml"));
        Assert.Equal("standard", profile.Name);
        Assert.Equal("CST-8:00:00", profile.Vars["timezone"]);
        Assert.Contains(profile.Steps, step => step.Id == "ntp" && step.Mode == "merge_xml");
        Assert.Contains(profile.Steps.Single(step => step.Id == "ntp").SetValues, pair => pair.Path == "portNo" && pair.Value == "123");

        var replaced = ProfileFile.Load(Path.Combine(root, "examples", "profiles", "ntp-replace.yaml"));
        Assert.Contains("{{vars.ntp_server}}", replaced.Steps.Single(step => step.Id == "put_ntp").Body);

        var inventory = Path.Combine(root, "examples", "inventory.csv");
        var rows = InventoryFile.Load(inventory);
        var selected = InventoryFile.Select(rows, new[] { "gate" }, null, 0, null);
        Assert.Equal(new[] { "cam-0001" }, selected.Select(row => row.Id).ToArray());
        var enabled = InventoryFile.Select(rows, Array.Empty<string>(), null, 0, null);
        Assert.DoesNotContain(enabled, row => row.Id == "cam-0003");

        var directory = Path.Combine(Path.GetTempPath(), "hik-engine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var csv = Path.Combine(directory, "cameras.csv");
        File.WriteAllText(csv, "id,host,password\nrow-1,10.0.0.8,row-secret\nshared-1,10.0.0.9,\n");
        var loaded = InventoryFile.Load(csv);
        var resolved = InventoryFile.Resolve(loaded, profile, null, "window-secret", new Dictionary<string, string>());
        Assert.Equal("row-secret", resolved[0].Password);
        Assert.Equal("window-secret", resolved[1].Password);
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task DeviceInfoReportFormatsModel()
    {
        var handler = new ScriptHandler((method, path, _, authorized) =>
        {
            if (!authorized)
                return Challenge();
            Assert.Equal("GET", method);
            Assert.Equal("/ISAPI/System/deviceInfo", path);
            return Xml(200, "<DeviceInfo><model>DS-2</model><firmwareVersion>V1</firmwareVersion><serialNumber>ABC</serialNumber></DeviceInfo>");
        });
        var directory = TempDirectory();
        try
        {
            var code = await ConsoleJobs.RunAsync(
                ConsoleTask.DeviceInfo,
                new ConsoleInput { IpInput = "10.0.0.8", Username = "admin", Password = "not-in-report" },
                directory,
                CancellationToken.None,
                Open(handler));
            Assert.Equal(0, code);
            var report = Path.Combine(directory, "report.json");
            var shown = ConsoleReport.Format(ConsoleTask.DeviceInfo, report);
            Assert.Contains("DS-2", shown);
            Assert.Contains("ABC", shown);
            Assert.DoesNotContain("not-in-report", File.ReadAllText(report));
            Assert.DoesNotContain("not-in-report", File.ReadAllText(Path.Combine(directory, "profile.yaml")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MergeWritesChangedXmlAndDryRunDoesNot()
    {
        var puts = new List<string>();
        var handler = new ScriptHandler((method, path, body, authorized) =>
        {
            if (!authorized)
                return Challenge();
            if (method == "PUT")
            {
                puts.Add(body ?? "");
                return Xml(200, "<ResponseStatus><statusCode>1</statusCode><statusString>OK</statusString></ResponseStatus>");
            }
            return Xml(200, "<ThermometryBasicParam><alert>200</alert><alarm>210</alarm></ThermometryBasicParam>");
        });
        var profile = new IsapiProfile
        {
            Name = "set-temp",
            Steps = new[]
            {
                new IsapiStep
                {
                    Id = "set_temp",
                    Mode = "merge_xml",
                    Method = "PUT",
                    Path = "/ISAPI/Thermal/channels/1/thermometry/basicParam",
                    OnError = "continue",
                    CaptureBody = true,
                    SetValues = new List<(string, string)> { ("alert", "220.0"), ("alarm", "230.5") },
                },
            },
            Retries = 0,
            TimeoutSeconds = 5,
            SafetyMinSamples = 20,
        };
        var cameras = new[] { new CameraRow { Id = "cam-1", Host = "10.0.0.8" } };
        var directory = TempDirectory();
        try
        {
            var changed = await IsapiBatch.RunAsync(profile, cameras, new BatchRunRequest
            {
                Password = "secret",
                OpenSession = Open(handler),
                Delay = (_, _) => Task.CompletedTask,
            });
            Assert.Equal(0, changed.ExitCode);
            Assert.Contains("220.0", Assert.Single(puts));
            Assert.Contains("230.5", puts[0]);
            var json = changed.WriteJson(Path.Combine(directory, "temp.json"));
            var shown = ConsoleReport.Format(ConsoleTask.SetTemp, json);
            Assert.Contains("200→220.0℃", shown);
            Assert.Contains("修改成功", shown);

            puts.Clear();
            var dry = await IsapiBatch.RunAsync(profile, cameras, new BatchRunRequest
            {
                DryRun = true,
                Password = "secret",
                OpenSession = Open(handler),
                Delay = (_, _) => Task.CompletedTask,
            });
            Assert.Empty(puts);
            Assert.Equal("演練模式，未送出", dry.Cameras[0].Steps[0].Note);
            Assert.Equal(2, dry.Cameras[0].Steps[0].Changes.Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SafetyStopSkipsTheRest()
    {
        var handler = new ScriptHandler((_, _, _, authorized) => authorized
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
            : Challenge());
        var profile = new IsapiProfile
        {
            Name = "probe",
            Concurrency = 1,
            Retries = 0,
            TimeoutSeconds = 5,
            MaxFailureRatio = 0.5,
            SafetyMinSamples = 2,
            Steps = new[]
            {
                new IsapiStep
                {
                    Id = "device_info",
                    Mode = "request",
                    Method = "GET",
                    Path = "/ISAPI/System/deviceInfo",
                },
            },
        };
        var cameras = Enumerable.Range(1, 4).Select(index => new CameraRow { Id = "cam-" + index, Host = "10.0.0." + index }).ToList();
        var report = await IsapiBatch.RunAsync(profile, cameras, new BatchRunRequest
        {
            Password = "secret",
            OpenSession = Open(handler),
            Delay = (_, _) => Task.CompletedTask,
        });
        Assert.Equal(1, report.ExitCode);
        Assert.True(report.SafetyTripped);
        Assert.Equal(2, report.FailedCount);
        Assert.Equal(2, report.SkippedCount);
        Assert.Contains("失敗率保護已啟動", report.SafetyMessage);
        Assert.Contains("門檻 50%", report.SafetyMessage);
    }

    [Fact]
    public async Task RetriesBusyResponse()
    {
        var authorized = 0;
        var delays = 0;
        var handler = new ScriptHandler((_, _, _, hasAuth) =>
        {
            if (!hasAuth)
                return Challenge();
            authorized++;
            if (authorized == 1)
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            return Xml(200, "<DeviceInfo><model>DS-2</model></DeviceInfo>");
        });
        var profile = new IsapiProfile
        {
            Name = "probe",
            Retries = 1,
            RetryBackoffSeconds = 0.4,
            TimeoutSeconds = 5,
            SafetyMinSamples = 20,
            Steps = new[]
            {
                new IsapiStep
                {
                    Id = "device_info",
                    Method = "GET",
                    Path = "/ISAPI/System/deviceInfo",
                    CaptureBody = true,
                },
            },
        };
        var report = await IsapiBatch.RunAsync(
            profile,
            new[] { new CameraRow { Id = "cam-1", Host = "10.0.0.8" } },
            new BatchRunRequest
            {
                Password = "secret",
                OpenSession = Open(handler),
                Delay = (_, _) =>
                {
                    delays++;
                    return Task.CompletedTask;
                },
            });
        Assert.True(report.Cameras[0].Ok);
        Assert.Equal(2, report.Cameras[0].Steps[0].Attempts);
        Assert.Equal(1, delays);
        Assert.Contains("DS-2", report.Cameras[0].Steps[0].Body);
    }

    private static Func<ResolvedCamera, IsapiProfile, IsapiSession> Open(HttpMessageHandler handler)
        => (camera, profile) => new IsapiSession(
            new DigestHttp(handler, disposeHandler: false, TimeSpan.FromSeconds(profile.TimeoutSeconds)),
            new Uri("http://" + camera.Host + ":" + camera.Port),
            camera.Username,
            camera.Password);

    private static HttpResponseMessage Challenge()
    {
        var denied = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        denied.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue(
            "Digest",
            "realm=\"IP Camera\", nonce=\"abc\", qop=\"auth\", algorithm=MD5"));
        return denied;
    }

    private static HttpResponseMessage Xml(int status, string body)
        => new((HttpStatusCode)status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/xml"),
        };

    private static string TempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hik-engine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "examples", "profiles", "standard.yaml")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("找不到 examples/profiles/standard.yaml");
    }

    private sealed class ScriptHandler : HttpMessageHandler
    {
        private readonly Func<string, string, string?, bool, HttpResponseMessage> _respond;

        public ScriptHandler(Func<string, string, string?, bool, HttpResponseMessage> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var authorized = request.Headers.TryGetValues("Authorization", out var values) && values.Any(value => !string.IsNullOrEmpty(value));
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return _respond(request.Method.Method, request.RequestUri?.AbsolutePath ?? "", body, authorized);
        }
    }
}
