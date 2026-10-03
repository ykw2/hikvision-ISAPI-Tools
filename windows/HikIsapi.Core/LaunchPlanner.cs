namespace HikIsapi;

public static class LaunchPlanner
{
    public static bool TryCreateBatch(
        BatchCommand command,
        BatchOptions options,
        string? pythonPath,
        string? password,
        bool windows,
        Func<string, string?> findOnPath,
        out LaunchPlan? plan,
        out string? error)
    {
        plan = null;
        error = CliPlan.CheckBatch(command, options);
        if (error != null)
            return false;

        var work = CliPlan.ResolveBatchDirectory(options);
        if (string.IsNullOrEmpty(work) || !Directory.Exists(work))
        {
            error = "無法決定工作目錄";
            return false;
        }

        return TryCreate(
            pythonPath,
            password,
            options.PasswordEnv,
            windows,
            findOnPath,
            work,
            CliPlan.BuildBatchArgs(command, options),
            out plan,
            out error);
    }

    public static bool TryCreateGet(
        GetOptions options,
        string? pythonPath,
        string? password,
        bool windows,
        Func<string, string?> findOnPath,
        out LaunchPlan? plan,
        out string? error)
    {
        plan = null;
        error = CliPlan.CheckGet(options);
        if (error != null)
            return false;

        var work = !string.IsNullOrWhiteSpace(options.WorkDirectory)
            ? Path.GetFullPath(options.WorkDirectory.Trim())
            : AppContext.BaseDirectory;
        return TryCreate(
            pythonPath,
            password,
            options.PasswordEnv,
            windows,
            findOnPath,
            work,
            CliPlan.BuildGetArgs(options),
            out plan,
            out error);
    }

    private static bool TryCreate(
        string? pythonPath,
        string? password,
        string? passwordEnv,
        bool windows,
        Func<string, string?> findOnPath,
        string workingDirectory,
        IReadOnlyList<string> commandArgs,
        out LaunchPlan? plan,
        out string? error)
    {
        plan = null;
        var python = PythonCommand.ResolveExecutable(pythonPath, windows, findOnPath, out error);
        if (python == null)
            return false;

        var arguments = new List<string>();
        arguments.AddRange(PythonCommand.ModuleArgs(python));
        arguments.AddRange(commandArgs);
        var environment = BuildEnvironment(passwordEnv, password);
        plan = new LaunchPlan
        {
            FileName = python,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            Environment = environment,
            DisplayCommand = CliPlan.FormatCommand(python, arguments),
        };
        error = null;
        return true;
    }

    public static Dictionary<string, string> BuildEnvironment(string? passwordEnv, string? password)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PYTHONUTF8"] = "1",
            ["PYTHONIOENCODING"] = "utf-8",
        };
        if (!string.IsNullOrEmpty(password))
        {
            var name = string.IsNullOrWhiteSpace(passwordEnv) ? "HIK_PASSWORD" : passwordEnv.Trim();
            environment[name] = password;
        }
        return environment;
    }
}
