namespace Highlighter.Api.Services;

/// <summary>Repo-root .env loader mirroring the pipeline worker's Config.LoadEnv
/// semantics (pipeline-dotnet/src/Highlighter.Pipeline/Config.cs): walk up from a
/// start directory, first .env found wins, already-set process env vars are never
/// overwritten, values may be wrapped in matching single or double quotes.</summary>
public static class EnvLoader
{
    /// <summary>Walks up from the current directory (then the app base directory)
    /// and applies the first .env found. Returns the path used, or null.</summary>
    public static string? Load()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var found = FindFrom(start);
            if (found is not null)
            {
                Apply(found);
                return found;
            }
        }
        return null;
    }

    public static string? FindFrom(string startDirectory)
    {
        var dir = new DirectoryInfo(startDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, ".env");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent!;
        }
        return null;
    }

    public static void Apply(string path)
    {
        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            var key = line[..separator].Trim();
            if (key.Length == 0) continue;
            if (Environment.GetEnvironmentVariable(key) is not null) continue;
            Environment.SetEnvironmentVariable(key, Unquote(line[(separator + 1)..].Trim()));
        }
    }

    public static string Unquote(string value) =>
        value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0]
            ? value[1..^1]
            : value;
}
