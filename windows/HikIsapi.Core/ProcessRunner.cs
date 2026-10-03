using System.Diagnostics;
using System.Text;

namespace HikIsapi;

public sealed class ProcessRequest
{
    public required string FileName { get; init; }
    public required IReadOnlyList<string> Arguments { get; init; }
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
}

public sealed class ProcessRunner
{
    public async Task<int> RunAsync(ProcessRequest request, Action<string> onLine, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo
        {
            FileName = request.FileName,
            WorkingDirectory = string.IsNullOrEmpty(request.WorkingDirectory)
                ? Environment.CurrentDirectory
                : request.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        foreach (var argument in request.Arguments)
            info.ArgumentList.Add(argument);
        if (request.Environment != null)
        {
            foreach (var pair in request.Environment)
                info.Environment[pair.Key] = pair.Value;
        }

        using var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("無法啟動：" + request.FileName);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException("找不到程式：" + request.FileName, ex);
        }

        using var registration = cancellationToken.Register(() => Kill(process));
        var pumpOut = PumpAsync(process.StandardOutput, onLine, cancellationToken);
        var pumpErr = PumpAsync(process.StandardError, onLine, cancellationToken);
        try
        {
            await Task.WhenAll(pumpOut, pumpErr, process.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            try
            {
                await Task.WhenAll(pumpOut, pumpErr).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
                // 行程已被停止，讀取剩下的輸出即可。
            }
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
                // 行程已經結束。
            }
            return -1;
        }

        return process.ExitCode;
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> onLine, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line == null)
                    return;
                onLine(line);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException)
        {
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}
