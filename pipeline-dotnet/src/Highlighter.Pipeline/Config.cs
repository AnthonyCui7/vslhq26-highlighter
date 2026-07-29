namespace Highlighter.Pipeline;

/// <summary>Port of highlighter_pipeline/config.py.</summary>
public static class Config
{
    /// <summary>Load a .env file. With no explicit path, walk up from the working
    /// directory so the monorepo root .env is found from any subdirectory.</summary>
    public static void LoadEnv(string? path = null)
    {
        var candidates = new List<string>();
        if (path is not null)
        {
            candidates.Add(path);
        }
        else
        {
            var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (directory is not null)
            {
                candidates.Add(Path.Combine(directory.FullName, ".env"));
                directory = directory.Parent!;
                if (directory is null) break;
            }
        }

        var envPath = candidates.FirstOrDefault(File.Exists);
        if (envPath is null) return;

        foreach (var rawLine in File.ReadAllLines(envPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || !line.Contains('=')) continue;

            var separator = line.IndexOf('=');
            var key = line[..separator].Trim();
            if (key.Length == 0 || Environment.GetEnvironmentVariable(key) is not null) continue;

            Environment.SetEnvironmentVariable(key, Unquote(line[(separator + 1)..].Trim()));
        }
    }

    /// <summary>os.environ.get(key): null when unset (an empty value comes back
    /// as "" like Python's).</summary>
    public static string? Env(string key) => Environment.GetEnvironmentVariable(key);

    /// <summary>os.environ.get(key, default): the default only when unset.</summary>
    public static string Env(string key, string defaultValue) =>
        Environment.GetEnvironmentVariable(key) ?? defaultValue;

    /// <summary>os.environ.get(key) or null — Python "or" semantics: empty string
    /// counts as missing.</summary>
    public static string? EnvOrNull(string key)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    public static string RequiredEnv(string key)
    {
        var value = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrEmpty(value))
            throw new PipelineError($"Missing required env var: {key}");
        return value;
    }

    public static string RequiredAnyEnv(IReadOnlyList<string> keys)
    {
        foreach (var key in keys)
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrEmpty(value)) return value;
        }
        throw new PipelineError($"Missing required env var: {string.Join(" or ", keys)}");
    }

    public static int IntEnv(string key, int defaultValue)
    {
        var rawValue = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrEmpty(rawValue)) return defaultValue;

        if (!int.TryParse(rawValue, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
            throw new PipelineError($"{key} must be an integer");
        return value;
    }

    public static double? FloatEnv(string key, double? defaultValue = null)
    {
        var rawValue = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrEmpty(rawValue)) return defaultValue;

        if (!double.TryParse(rawValue, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
            throw new PipelineError($"{key} must be a number");
        return value;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == value[^1] && (value[0] == '\'' || value[0] == '"'))
            return value[1..^1];
        return value;
    }
}
