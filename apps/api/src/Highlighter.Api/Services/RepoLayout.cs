namespace Highlighter.Api.Services;

/// <summary>Resolves the monorepo pieces the API depends on: the repo root (the
/// worker's working directory and .env home), the outputs mirror the pipeline
/// writes, the per-job log directory, and the worker binary itself.</summary>
public sealed class RepoLayout
{
    private readonly PipelineOptions _options;

    public RepoLayout(PipelineOptions options)
    {
        _options = options;
        RepoRoot = !string.IsNullOrWhiteSpace(options.RepoRoot)
            ? Path.GetFullPath(options.RepoRoot)
            : DiscoverRepoRoot()
              ?? throw new InvalidOperationException(
                  "Could not locate the repo root (no .git or .env found walking up); set Pipeline:RepoRoot.");
        JobLogRoot = !string.IsNullOrWhiteSpace(options.JobLogRoot)
            ? Path.GetFullPath(options.JobLogRoot)
            : Path.Combine(OutputsRoot, "api", "jobs");
    }

    public string RepoRoot { get; }

    public string JobLogRoot { get; }

    public string OutputsRoot => Path.Combine(RepoRoot, "outputs");

    public string ApiLogDir => Path.Combine(OutputsRoot, "api");

    public string ProjectsRoot => Path.Combine(OutputsRoot, "projects");

    public string ProjectDir(Guid projectId) => Path.Combine(ProjectsRoot, projectId.ToString());

    /// <summary>revise/publish read this mirror; its presence gates those actions.</summary>
    public bool HasLocalMirror(Guid projectId) =>
        File.Exists(Path.Combine(ProjectDir(projectId), "project.json"));

    /// <summary>The worker launch command: config override, else the built
    /// pipeline-dotnet CLI dll, else `highlighter` on PATH. Null when unresolved —
    /// deliberately NOT `dotnet run --project pipeline-dotnet/...`, which would
    /// rebuild a tree owned by another workstream on every request.</summary>
    public WorkerCommand? ResolveWorkerCommand()
    {
        if (!string.IsNullOrWhiteSpace(_options.WorkerCommand))
        {
            var parts = _options.WorkerCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return new WorkerCommand(parts[0], parts[1..]);
        }
        foreach (var configuration in new[] { "Release", "Debug" })
        {
            var dll = Path.Combine(RepoRoot, "pipeline-dotnet", "src", "Highlighter.Cli",
                "bin", configuration, "net10.0", "highlighter.dll");
            if (File.Exists(dll)) return new WorkerCommand("dotnet", [dll]);
        }
        var onPath = FindOnPath("highlighter");
        return onPath is null ? null : new WorkerCommand(onPath, []);
    }

    public static string? FindOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string? DiscoverRepoRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, ".git"))
                    || File.Exists(Path.Combine(dir.FullName, ".env")))
                    return dir.FullName;
                dir = dir.Parent!;
            }
        }
        return null;
    }
}

public sealed record WorkerCommand(string FileName, IReadOnlyList<string> PrefixArgs)
{
    public string Display => PrefixArgs.Count == 0
        ? FileName
        : $"{FileName} {string.Join(' ', PrefixArgs)}";
}
