using Highlighter.Api.Services;
using Xunit;

namespace Highlighter.Api.Tests;

public class LogRingBufferTests
{
    [Fact]
    public void SequenceIsMonotonicAcrossEviction()
    {
        var buffer = new LogRingBuffer(capacity: 5);
        for (var i = 1; i <= 7; i++)
            buffer.Append("out", $"line {i}", DateTimeOffset.UtcNow);

        var tail = buffer.Tail(100);

        Assert.Equal(5, tail.Count);
        Assert.Equal(3, tail[0].Seq);
        Assert.Equal(7, tail[^1].Seq);
        Assert.Equal(7, buffer.LastSeq);
        Assert.Equal("line 3", tail[0].Line);
    }

    [Fact]
    public void TailReturnsLastNInOrder()
    {
        var buffer = new LogRingBuffer(capacity: 100);
        for (var i = 1; i <= 10; i++)
            buffer.Append("out", $"line {i}", DateTimeOffset.UtcNow);

        var tail = buffer.Tail(3);

        Assert.Equal(["line 8", "line 9", "line 10"], tail.Select(line => line.Line));
    }

    [Fact]
    public void EmptyBufferBehaves()
    {
        var buffer = new LogRingBuffer(capacity: 10);

        Assert.Empty(buffer.Tail(5));
        Assert.Equal(0, buffer.LastSeq);
    }
}
