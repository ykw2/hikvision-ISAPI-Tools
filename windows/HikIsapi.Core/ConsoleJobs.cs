using System.Globalization;
using System.Text;

namespace HikIsapi;

public static class ConsoleJobs
{
    public static async Task<int> RunAsync(
        ConsoleTask task,
        ConsoleInput input,
        string jobDirectory,
        CancellationToken cancellationToken,
        Func<ResolvedCamera, IsapiProfile, IsapiSession>? openSession = null)
    {
        if (!ConsoleLaunch.TryPrepare(task, input, out var addresses, out var error))
            throw new InvalidOperationException(error ?? "無法開始作業");
        var username = string.IsNullOrWhiteSpace(input.Username) ? "admin" : input.Username.Trim();
        Directory.CreateDirectory(jobDirectory);
        var profilePath = Path.Combine(jobDirectory, "profile.yaml");
        File.WriteAllText(profilePath, ConsoleLaunch.BuildProfile(task, input, username), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (task == ConsoleTask.Manual && !string.IsNullOrWhiteSpace(input.Body))
            File.WriteAllText(Path.Combine(jobDirectory, "body.xml"), input.Body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var profile = ProfileFile.Load(profilePath);
        var cameras = new List<CameraRow>();
        for (var index = 0; index < addresses.Count; index++)
        {
            cameras.Add(new CameraRow
            {
                Id = "cam-" + (index + 1).ToString(CultureInfo.InvariantCulture),
                Host = addresses[index],
            });
        }
        var report = await IsapiBatch.RunAsync(profile, cameras, new BatchRunRequest
        {
            Password = input.Password,
            Username = username,
            Environment = new Dictionary<string, string>(StringComparer.Ordinal),
            OpenSession = openSession,
            CancellationToken = cancellationToken,
        }).ConfigureAwait(false);
        report.WriteJson(Path.Combine(jobDirectory, "report.json"));
        if (report.Cancelled || cancellationToken.IsCancellationRequested)
            return -1;
        return report.ExitCode;
    }
}
