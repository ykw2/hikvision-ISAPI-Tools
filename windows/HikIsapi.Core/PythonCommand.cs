namespace HikIsapi;

public static class PythonCommand
{
    public static IReadOnlyList<string> DefaultCandidates(bool windows)
        => windows ? new[] { "py", "python", "python3" } : new[] { "python3", "python" };

    public static bool IsPyLauncher(string fileName)
    {
        var name = Path.GetFileName(fileName);
        return name.Equals("py", StringComparison.OrdinalIgnoreCase)
            || name.Equals("py.exe", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> InterpreterPrefix(string fileName)
        => IsPyLauncher(fileName) ? new[] { "-3", "-X", "utf8" } : new[] { "-X", "utf8" };

    public static IReadOnlyList<string> ModuleArgs(string fileName)
    {
        var args = new List<string>();
        args.AddRange(InterpreterPrefix(fileName));
        args.Add("-m");
        args.Add("hik_isapi");
        return args;
    }

    public static bool IsExplicitPath(string value)
        => Path.IsPathRooted(value)
            || value.Contains('/')
            || value.Contains('\\')
            || value.Contains(Path.DirectorySeparatorChar);

    public static string? FindOnPath(string name, string? pathEnv = null, bool? windows = null)
    {
        var windowsOs = windows ?? OperatingSystem.IsWindows();
        var path = pathEnv ?? Environment.GetEnvironmentVariable("PATH") ?? "";
        var separator = windowsOs ? ';' : ':';
        var extensions = windowsOs && !Path.HasExtension(name)
            ? new[] { ".exe", ".cmd", ".bat" }
            : new[] { "" };
        foreach (var directory in path.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = directory.Trim().Trim('"');
            if (trimmed.Length == 0)
                continue;
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(trimmed, name + extension);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return null;
    }

    public static string? ResolveExecutable(
        string? configured,
        bool windows,
        Func<string, string?> findOnPath,
        out string? error)
    {
        var requested = (configured ?? "").Trim();
        if (requested.Length > 0)
        {
            if (IsExplicitPath(requested))
            {
                if (!File.Exists(requested))
                {
                    error = "找不到 Python：" + requested;
                    return null;
                }
                error = null;
                return requested;
            }

            var found = findOnPath(requested);
            if (found == null)
            {
                error = "在 PATH 找不到 Python：" + requested;
                return null;
            }
            error = null;
            return found;
        }

        foreach (var candidate in DefaultCandidates(windows))
        {
            var found = findOnPath(candidate);
            if (found != null)
            {
                error = null;
                return found;
            }
        }

        error = "找不到 Python。請安裝 Python 3.11 以上，或按瀏覽選擇 python.exe。";
        return null;
    }
}
