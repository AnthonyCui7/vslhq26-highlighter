using System.Diagnostics;

namespace Highlighter.Pipeline;

/// <summary>subprocess.run equivalents used across the pipeline. Commands are
/// argv lists (never shell strings), stdout/stderr are captured, and a
/// non-zero exit is the caller's business — exactly like the Python code.</summary>
public static class Proc
{
    public static ProcessStartInfo StartInfo(IReadOnlyList<string> command)
    {
        var info = new ProcessStartInfo(command[0])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in command.Skip(1)) info.ArgumentList.Add(arg);
        return info;
    }

    /// <summary>subprocess.run(command, capture_output=True, text=True[, timeout=...]).
    /// A timeout kills the process tree and throws TimeoutException (the port of
    /// subprocess.TimeoutExpired). Defaults to 30 minutes so a wedged subprocess
    /// can never hang the worker forever; pass null for no timeout.</summary>
    public static (int Code, string Stdout, string Stderr) Run(
        IReadOnlyList<string> command, double? timeoutSeconds = 1800)
    {
        using var process = Process.Start(StartInfo(command))
            ?? throw new PipelineError($"Could not start {command[0]}");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (timeoutSeconds is double timeout)
        {
            if (!process.WaitForExit((int)(timeout * 1000)))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                process.WaitForExit();
                throw new TimeoutException(
                    $"Command timed out after {timeout}s: {string.Join(" ", command)}");
            }
            process.WaitForExit(); // flush pending stream reads after the timed wait
        }
        else
        {
            process.WaitForExit();
        }

        return (process.ExitCode, stdoutTask.GetAwaiter().GetResult(),
            stderrTask.GetAwaiter().GetResult());
    }

    /// <summary>subprocess.run with binary stdout (shots frame extraction).</summary>
    public static (int Code, byte[] Stdout, string Stderr) RunBytes(IReadOnlyList<string> command)
    {
        using var process = Process.Start(StartInfo(command))
            ?? throw new PipelineError($"Could not start {command[0]}");
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var memory = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(memory);
        process.WaitForExit();
        return (process.ExitCode, memory.ToArray(), stderrTask.GetAwaiter().GetResult());
    }

    /// <summary>shutil.which</summary>
    public static string? Which(string name)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in pathVar.Split(Path.PathSeparator))
        {
            if (directory.Length == 0) continue;
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}

/// <summary>tempfile.TemporaryDirectory: a unique directory removed (recursively,
/// best-effort) on dispose. Pass parent to mirror `dir=` (temp dirs created next
/// to the output so cross-device moves never happen).</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir(string prefix = "tmp", string? parent = null)
    {
        var root = parent ?? System.IO.Path.GetTempPath();
        Path = System.IO.Path.Combine(root, prefix + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { }
    }
}
