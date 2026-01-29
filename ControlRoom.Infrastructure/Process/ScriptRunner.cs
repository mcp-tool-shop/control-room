using System.Diagnostics;
using System.Text;

namespace ControlRoom.Infrastructure.Process;

public sealed record ScriptRunSpec(
    string FilePath,
    string Arguments,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string>? Env
);

public sealed record ScriptRunResult(
    int? ExitCode,
    bool WasCanceled,
    string? ResolvedCommandLine = null  // Full command line for reproducibility
);

public interface IScriptRunner
{
    Task<ScriptRunResult> RunAsync(
        ScriptRunSpec spec,
        Func<bool, string, Task> onLine,
        CancellationToken ct);
}

public sealed class ScriptRunner : IScriptRunner
{
    public async Task<ScriptRunResult> RunAsync(
        ScriptRunSpec spec,
        Func<bool, string, Task> onLine,
        CancellationToken ct)
    {
        if (!File.Exists(spec.FilePath))
            throw new FileNotFoundException("Script not found", spec.FilePath);

        var launcher = ResolveLauncher(spec.FilePath);
        var args = BuildArgs(spec.FilePath, spec.Arguments);
        var resolvedCommandLine = $"{launcher} {args}".Trim();

        var psi = new ProcessStartInfo
        {
            FileName = launcher,
            Arguments = args,
            WorkingDirectory = spec.WorkingDirectory ?? Path.GetDirectoryName(spec.FilePath)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (spec.Env is not null)
        {
            foreach (var kv in spec.Env)
                psi.Environment[kv.Key] = kv.Value;
        }

        using var p = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };

        p.Start();

        using var reg = ct.Register(() =>
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
            catch { /* best effort */ }
        });

        var outTask = PumpAsync(p.StandardOutput, isErr: false, onLine, ct);
        var errTask = PumpAsync(p.StandardError, isErr: true, onLine, ct);

        try
        {
            await Task.WhenAll(outTask, errTask, p.WaitForExitAsync(ct));
            return new ScriptRunResult(p.ExitCode, WasCanceled: false, resolvedCommandLine);
        }
        catch (OperationCanceledException)
        {
            return new ScriptRunResult(ExitCode: null, WasCanceled: true, resolvedCommandLine);
        }
    }

    private static async Task PumpAsync(
        StreamReader reader,
        bool isErr,
        Func<bool, string, Task> onLine,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            await onLine(isErr, line);
        }
    }

    private static string ResolveLauncher(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".ps1" => "pwsh",
            ".cmd" or ".bat" => "cmd.exe",
            ".py" => "python",
            ".sh" => "bash",
            _ => filePath
        };
    }

    private static string BuildArgs(string filePath, string args)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".ps1" => $"-NoProfile -ExecutionPolicy Bypass -File \"{filePath}\" {args}",
            ".cmd" or ".bat" => $"/c \"\"{filePath}\" {args}\"",
            ".py" => $"\"{filePath}\" {args}",
            ".sh" => $"\"{filePath}\" {args}",
            _ => args
        };
    }
}
