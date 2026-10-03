namespace HikIsapi.Tests;

public sealed class LaunchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hik-isapi-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _inventory;
    private readonly string _profile;
    private readonly string _python;

    public LaunchTests()
    {
        Directory.CreateDirectory(_root);
        _inventory = Path.Combine(_root, "cam list.csv");
        _profile = Path.Combine(_root, "standard.yaml");
        _python = Path.Combine(_root, "python.exe");
        File.WriteAllText(_inventory, "id,host\ncam-1,10.0.0.1\n");
        File.WriteAllText(_profile, "name: standard\nsteps: []\n");
        File.WriteAllText(_python, "");
    }

    [Fact]
    public void ValidateOmitsRunFlagsAndPassword()
    {
        var options = Sample();
        options.Concurrency = "20";
        options.DryRun = true;
        var args = CliPlan.BuildBatchArgs(BatchCommand.Validate, options);
        Assert.Contains("validate", args);
        Assert.DoesNotContain("--concurrency", args);
        Assert.DoesNotContain("--dry-run", args);
        Assert.DoesNotContain("--password", args);
    }

    [Fact]
    public void ApplyBuildsFiltersRetriesAndSafety()
    {
        var options = Sample();
        options.Tags = " gate;;north | door ";
        options.Only = "cam-1,cam-2";
        options.Limit = "5";
        options.Offset = "10";
        options.Concurrency = "20";
        options.Timeout = "20";
        options.Retries = "2";
        options.MaxFailureRatio = "0,2";
        options.SafetySamples = "20";
        options.DryRun = true;
        var args = CliPlan.BuildBatchArgs(BatchCommand.Apply, options);
        Assert.Equal(
            new[]
            {
                "apply", "--inventory", _inventory, "--profile", _profile,
                "--username", "admin", "--password-env", "HIK_PASSWORD",
                "--tag", "gate", "--tag", "north", "--tag", "door",
                "--only", "cam-1,cam-2", "--limit", "5", "--offset", "10",
                "--concurrency", "20", "--timeout", "20", "--retries", "2",
                "--max-failure-ratio", "0.2", "--safety-samples", "20", "--dry-run",
            },
            args);
    }

    [Fact]
    public void DisableSafetySendsRatioOne()
    {
        var options = Sample();
        options.DisableSafety = true;
        options.MaxFailureRatio = "不是數字";
        Assert.Null(CliPlan.CheckBatch(BatchCommand.Apply, options));
        var args = CliPlan.BuildBatchArgs(BatchCommand.Apply, options);
        var index = args.IndexOf("--max-failure-ratio");
        Assert.Equal("1", args[index + 1]);
    }

    [Fact]
    public void RejectsOutOfRangeNumbersAndMissingFiles()
    {
        var options = Sample();
        options.Concurrency = "0";
        Assert.Contains("並發", CliPlan.CheckBatch(BatchCommand.Probe, options));
        options.Concurrency = "";
        options.Retries = "9";
        Assert.Contains("重試", CliPlan.CheckBatch(BatchCommand.Apply, options));
        options.Retries = "";
        options.MaxFailureRatio = "20";
        Assert.Contains("失敗率", CliPlan.CheckBatch(BatchCommand.Apply, options));
        options.InventoryPath = Path.Combine(_root, "missing.csv");
        Assert.Contains("找不到清單", CliPlan.CheckBatch(BatchCommand.Validate, options));
    }

    [Fact]
    public void LaunchPlanKeepsPasswordOutOfTheCommandLine()
    {
        const string password = "p@ss 密碼";
        var ok = LaunchPlanner.TryCreateBatch(
            BatchCommand.Apply,
            Sample(),
            _python,
            password,
            windows: true,
            _ => null,
            out var plan,
            out var error);
        Assert.True(ok, error);
        Assert.NotNull(plan);
        Assert.Equal(_python, plan!.FileName);
        Assert.Equal(new[] { "-X", "utf8", "-m", "hik_isapi" }, plan.Arguments.Take(4));
        Assert.DoesNotContain(password, plan.DisplayCommand);
        Assert.DoesNotContain(password, plan.Arguments);
        Assert.Equal(password, plan.Environment["HIK_PASSWORD"]);
        Assert.Equal("1", plan.Environment["PYTHONUTF8"]);
        Assert.Contains("\"", plan.DisplayCommand);
        Assert.Equal(_root, plan.WorkingDirectory);
    }

    [Fact]
    public void PyLauncherGetsTheWindowsSelector()
    {
        var launcher = Path.Combine(_root, "py.exe");
        File.WriteAllText(launcher, "");
        var ok = LaunchPlanner.TryCreateBatch(
            BatchCommand.Probe,
            Sample(),
            launcher,
            password: null,
            windows: true,
            _ => null,
            out var plan,
            out var error);
        Assert.True(ok, error);
        Assert.Equal(new[] { "-3", "-X", "utf8", "-m", "hik_isapi", "probe" }, plan!.Arguments.Take(6));
        Assert.False(plan.Environment.ContainsKey("HIK_PASSWORD"));
    }

    [Fact]
    public void AutoDetectsPythonFromPath()
    {
        var ok = LaunchPlanner.TryCreateBatch(
            BatchCommand.Validate,
            Sample(),
            pythonPath: "",
            password: null,
            windows: false,
            name => name == "python3" ? _python : null,
            out var plan,
            out var error);
        Assert.True(ok, error);
        Assert.Equal(_python, plan!.FileName);
    }

    [Fact]
    public void MissingPythonIsReported()
    {
        var ok = LaunchPlanner.TryCreateBatch(
            BatchCommand.Validate,
            Sample(),
            pythonPath: "",
            password: null,
            windows: true,
            _ => null,
            out _,
            out var error);
        Assert.False(ok);
        Assert.Contains("Python", error);
    }

    [Fact]
    public void GetRequiresARootedPathAndSkipsPassword()
    {
        var options = new GetOptions { Host = "10.1.0.11", Path = "ISAPI/System/time" };
        Assert.Contains("路徑必須以 / 開頭", CliPlan.CheckGet(options));
        options.Path = "/ISAPI/System/time";
        options.Https = true;
        options.Port = "443";
        var ok = LaunchPlanner.TryCreateGet(options, _python, "secret", windows: true, _ => null, out var plan, out var error);
        Assert.True(ok, error);
        Assert.Contains("--https", plan!.Arguments);
        Assert.DoesNotContain("--password", plan.Arguments);
        Assert.DoesNotContain("secret", plan.DisplayCommand);
        Assert.Equal("secret", plan.Environment["HIK_PASSWORD"]);
    }

    [Fact]
    public void ConfirmMessagesDescribeTheScope()
    {
        Assert.Contains("所有符合條件", CliPlan.ApplyConfirmMessage("  ", _profile));
        Assert.Contains("最多 5 支", CliPlan.ApplyConfirmMessage("5", _profile));
        Assert.Contains("report.json", CliPlan.RetryConfirmMessage("report.json"));
    }

    [Fact]
    public void FindOnPathHonorsWindowsExtensions()
    {
        var found = PythonCommand.FindOnPath("python", _root, windows: true);
        Assert.Equal(_python, found);
        Assert.Null(PythonCommand.FindOnPath("python", _root, windows: false));
    }

    private BatchOptions Sample() => new()
    {
        InventoryPath = _inventory,
        ProfilePath = _profile,
        Username = "admin",
        PasswordEnv = "HIK_PASSWORD",
    };

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
