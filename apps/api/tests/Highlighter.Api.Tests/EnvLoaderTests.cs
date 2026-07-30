using Highlighter.Api.Services;
using Xunit;

namespace Highlighter.Api.Tests;

public class EnvLoaderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"envloader-{Guid.NewGuid():N}");

    public EnvLoaderTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void FindFrom_WalksUpAndFirstFileWins()
    {
        var nested = Path.Combine(_root, "a", "b");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(_root, ".env"), "X=root");

        Assert.Equal(Path.Combine(_root, ".env"), EnvLoader.FindFrom(nested));

        File.WriteAllText(Path.Combine(nested, ".env"), "X=nested");
        Assert.Equal(Path.Combine(nested, ".env"), EnvLoader.FindFrom(nested));
    }

    [Fact]
    public void Apply_SetsVariablesAndSkipsCommentsAndBlanks()
    {
        var key1 = TempKey();
        var key2 = TempKey();
        var path = Path.Combine(_root, ".env");
        File.WriteAllText(path, $"""
            # comment
            {key1}=hello

            not-a-pair
            {key2}=with=equals
            """);

        EnvLoader.Apply(path);

        Assert.Equal("hello", Environment.GetEnvironmentVariable(key1));
        Assert.Equal("with=equals", Environment.GetEnvironmentVariable(key2));
    }

    [Fact]
    public void Apply_ProcessEnvironmentWins()
    {
        var key = TempKey();
        Environment.SetEnvironmentVariable(key, "already-set");
        var path = Path.Combine(_root, ".env");
        File.WriteAllText(path, $"{key}=from-file");

        EnvLoader.Apply(path);

        Assert.Equal("already-set", Environment.GetEnvironmentVariable(key));
    }

    [Theory]
    [InlineData("\"quoted\"", "quoted")]
    [InlineData("'single'", "single")]
    [InlineData("\"mismatched'", "\"mismatched'")]
    [InlineData("plain", "plain")]
    [InlineData("\"\"", "")]
    public void Unquote_StripsOnlyMatchingQuotes(string input, string expected) =>
        Assert.Equal(expected, EnvLoader.Unquote(input));

    private static string TempKey() => $"HL_API_TEST_{Guid.NewGuid():N}";
}
