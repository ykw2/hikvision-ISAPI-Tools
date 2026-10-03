namespace HikIsapi.Tests;

public sealed class SettingsTests
{
    [Fact]
    public void RoundTripKeepsChinesePathsAndDropsPasswords()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hik-isapi-settings-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        var settings = new UiSettings
        {
            InventoryPath = @"D:\鏡頭\inventory.csv",
            ProfilePath = @"D:\鏡頭\standard.yaml",
            Limit = "5",
            PasswordEnv = "HIK_PASSWORD",
        };
        UiSettingsStore.Save(path, settings);
        var json = File.ReadAllText(path);
        Assert.Contains("鏡頭", json);
        Assert.DoesNotContain("\"password\"", json, StringComparison.OrdinalIgnoreCase);

        var loaded = UiSettingsStore.LoadOrNew(path, out var warning);
        Assert.Null(warning);
        Assert.Equal(settings.InventoryPath, loaded.InventoryPath);
        Assert.Equal("5", loaded.Limit);

        File.WriteAllText(path, "{");
        var fresh = UiSettingsStore.LoadOrNew(path, out warning);
        Assert.NotNull(warning);
        Assert.Equal("", fresh.InventoryPath);
        Directory.Delete(directory, recursive: true);
    }
}

public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task CapturesUtf8OutputAndEnvironment()
    {
        var python = PythonCommand.FindOnPath("python3") ?? "/usr/bin/python3";
        Assert.True(File.Exists(python), "這個測試需要 python3");
        var lines = new List<string>();
        var code = await new ProcessRunner().RunAsync(new ProcessRequest
        {
            FileName = python,
            Arguments = new[] { "-X", "utf8", "-c", "import os; print('你好'); print(os.environ['HIK_MARKER'])" },
            Environment = new Dictionary<string, string> { ["HIK_MARKER"] = "標記", ["PYTHONIOENCODING"] = "utf-8" },
        }, lines.Add, CancellationToken.None);
        Assert.Equal(0, code);
        Assert.Contains("你好", lines);
        Assert.Contains("標記", lines);
    }

    [Fact(Timeout = 15000)]
    public async Task CancelStopsTheProcess()
    {
        var python = PythonCommand.FindOnPath("python3") ?? "/usr/bin/python3";
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var code = await new ProcessRunner().RunAsync(new ProcessRequest
        {
            FileName = python,
            Arguments = new[] { "-c", "import time; time.sleep(30)" },
        }, _ => { }, cts.Token);
        Assert.Equal(-1, code);
    }
}
