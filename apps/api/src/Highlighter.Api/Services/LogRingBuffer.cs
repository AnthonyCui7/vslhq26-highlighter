using Highlighter.Api.Contracts;

namespace Highlighter.Api.Services;

/// <summary>Fixed-capacity, thread-safe log window with a monotonic sequence.
/// The durable record is the per-job file; this serves tails and live streams.</summary>
public sealed class LogRingBuffer(int capacity)
{
    private readonly Queue<JobLogLineDto> _lines = new();
    private long _seq;

    public long LastSeq
    {
        get { lock (_lines) return _seq; }
    }

    public JobLogLineDto Append(string stream, string line, DateTimeOffset at)
    {
        lock (_lines)
        {
            var dto = new JobLogLineDto(++_seq, at, stream, line);
            _lines.Enqueue(dto);
            while (_lines.Count > capacity) _lines.Dequeue();
            return dto;
        }
    }

    public List<JobLogLineDto> Tail(int count)
    {
        lock (_lines)
            return _lines.Skip(Math.Max(0, _lines.Count - count)).ToList();
    }
}
