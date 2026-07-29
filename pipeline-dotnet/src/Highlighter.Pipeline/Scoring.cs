using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Highlighter.Pipeline;

/// <summary>Port of highlighter_pipeline/scoring.py.
///
/// Parallel clip scoring: a bounded LLM worker pool feeding an ordered emitter.
///
/// Capture (the main thread) stores transcripts and calls AddChunk(); chunk i is
/// dispatched to the pool only once chunk i+1's transcript exists, so the model
/// sees up to contextSeconds of words across both of its boundaries. A single
/// emitter thread consumes scored chunks strictly in chunk order, stitches
/// candidates across chunk boundaries, and runs every downstream effect
/// (threshold gate, render, DB writes, segment cleanup) so clips keep appearing
/// live and in order while scoring runs concurrently.
///
/// Candidates whose windows overlap — including across a chunk boundary, which
/// both neighboring chunks can see thanks to the context margins — are merged
/// into one clip spanning their union. A candidate ending within contextSeconds
/// of its chunk's end is held back one chunk so a continuation proposed by the
/// next chunk can be merged before anything is rendered.</summary>
public static class Scoring
{
    /// <summary>Union candidates whose windows overlap or abut within mergeGapSeconds.
    ///
    /// Returns a new list sorted by start time. A union that would exceed
    /// maxClipSeconds is left unmerged so runaway chains cannot snowball into
    /// one enormous clip.</summary>
    public static List<JsonObject> MergeCandidates(
        IEnumerable<JsonObject> candidates, double mergeGapSeconds, double maxClipSeconds)
    {
        var ordered = candidates
            .OrderBy(c => JsonUtil.Double(c["start_seconds"]))
            .ThenBy(c => JsonUtil.Double(c["end_seconds"]))
            .ToList();
        var merged = new List<JsonObject>();
        foreach (var candidate in ordered)
        {
            if (merged.Count > 0)
            {
                var last = merged[^1];
                var touches = JsonUtil.Double(candidate["start_seconds"])
                    <= JsonUtil.Double(last["end_seconds"]) + mergeGapSeconds;
                var unionLength = Math.Max(
                        JsonUtil.Double(last["end_seconds"]),
                        JsonUtil.Double(candidate["end_seconds"]))
                    - JsonUtil.Double(last["start_seconds"]);
                if (touches && unionLength <= maxClipSeconds)
                {
                    merged[^1] = MergePair(last, candidate);
                    continue;
                }
                if (touches)
                {
                    Console.WriteLine(
                        "Not merging overlapping clip candidates at "
                        + $"{JsonUtil.Str(last["start_seconds"])}s-{JsonUtil.Str(last["end_seconds"])}s and "
                        + $"{JsonUtil.Str(candidate["start_seconds"])}s-{JsonUtil.Str(candidate["end_seconds"])}s: "
                        + $"union would exceed {Py.S(maxClipSeconds)}s");
                }
            }
            merged.Add(candidate);
        }
        return merged;
    }

    /// <summary>Merge two overlapping candidates: union window, best constituent wins
    /// the editorial fields (title/description/reason/score/chunk_index).</summary>
    private static JsonObject MergePair(JsonObject a, JsonObject b)
    {
        var primary = JsonUtil.Double(b["score"]) > JsonUtil.Double(a["score"]) ? b : a;
        var sources = new JsonArray();
        var seenUrls = new HashSet<string>();
        // Every item of a's list then b's list, in order, deduped by url.
        var allSources = ListItems(a["research_sources"])
            .Concat(ListItems(b["research_sources"]))
            .ToList();
        foreach (var source in allSources)
        {
            var url = source is JsonObject sourceObj
                ? JsonUtil.Dumps(sourceObj["url"])
                : JsonUtil.Dumps(source);
            if (!seenUrls.Add(url)) continue;
            sources.Add(JsonUtil.C(source));
        }

        var merged = JsonUtil.CloneObj(primary);
        merged["start_seconds"] = JsonUtil.C(
            JsonUtil.Double(a["start_seconds"]) <= JsonUtil.Double(b["start_seconds"])
                ? a["start_seconds"]
                : b["start_seconds"]);
        merged["end_seconds"] = JsonUtil.C(
            JsonUtil.Double(a["end_seconds"]) >= JsonUtil.Double(b["end_seconds"])
                ? a["end_seconds"]
                : b["end_seconds"]);
        merged["score"] = JsonUtil.C(
            JsonUtil.Double(a["score"]) >= JsonUtil.Double(b["score"])
                ? a["score"]
                : b["score"]);
        merged["research_sources"] = sources;
        var mergedFrom = new JsonArray();
        foreach (var constituent in Constituents(a).Concat(Constituents(b)))
            mergedFrom.Add(JsonUtil.C(constituent));
        merged["merged_from"] = mergedFrom;
        return merged;
    }

    private static IEnumerable<JsonNode?> ListItems(JsonNode? node) =>
        node is JsonArray array ? array : Enumerable.Empty<JsonNode?>();

    private static List<JsonNode?> Constituents(JsonObject decision)
    {
        if (JsonUtil.Truthy(decision.TryGetPropertyValue("merged_from", out var mergedFrom)
                ? mergedFrom
                : null))
            return ListItems(mergedFrom).ToList();
        return new List<JsonNode?>
        {
            new JsonObject
            {
                ["chunk_index"] = JsonUtil.C(decision["chunk_index"]),
                ["start_seconds"] = JsonUtil.C(decision["start_seconds"]),
                ["end_seconds"] = JsonUtil.C(decision["end_seconds"]),
                ["score"] = JsonUtil.C(decision["score"]),
                ["title"] = JsonUtil.C(decision["title"]),
            },
        };
    }

    /// <summary>A full-shape non-worthy decision for chunks that produced no candidates.</summary>
    public static JsonObject NoClipRecord(int chunkIndex, string reason) => new()
    {
        ["chunk_index"] = chunkIndex,
        ["is_clip_worthy"] = false,
        ["title"] = null,
        ["description"] = null,
        ["start_seconds"] = null,
        ["end_seconds"] = null,
        ["score"] = 0.0,
        ["reason"] = reason.Length > 0 ? reason : "No clip-worthy moment in this chunk.",
        ["research_sources"] = new JsonArray(),
    };
}

/// <summary>Dispatches chunk scoring to a thread pool and emits decisions in order.
///
/// All callbacks passed in run on the single emitter thread except
/// scoreChunk, which runs on pool threads and must be thread-safe:
///
/// - scoreChunk(chunk, contextWordsBefore, contextWordsAfter,
///   visibleStartSeconds, visibleEndSeconds) -> (candidates, assessment).
///   Failures are retried once, then downgraded to an empty result — one bad
///   chunk must never kill the run.
/// - emitDecision(decision): every stitched worthy candidate and every
///   no-clip chunk record, in chunk order.
/// - onChunkComplete(chunkIndex, minHeldStartSeconds): after each
///   chunk's decisions were emitted; minHeldStartSeconds is the earliest
///   start among still-held candidates (null when nothing is held) so the
///   caller can retire archive segments nothing can reference anymore.
/// - onFinish(): once, after the final flush, before the emitter exits.
///
/// AddChunk()/Finish() must be called from a single thread, with contiguous
/// chunk indexes starting at 0. RunOnEmitter() is thread-safe.</summary>
public class ClipScoringCoordinator
{
    public delegate (List<JsonObject> Candidates, string Assessment) ScoreChunkFn(
        JsonObject chunk,
        List<JsonObject> contextWordsBefore,
        List<JsonObject> contextWordsAfter,
        int visibleStartSeconds,
        int visibleEndSeconds);

    private abstract record Event;

    private sealed record ScoredEvent(int Index, List<JsonObject> Candidates, string Assessment)
        : Event;

    private sealed record CallEvent(Action Fn) : Event;

    private sealed record FinishEvent : Event;

    private readonly ScoreChunkFn _scoreChunk;
    private readonly Action<JsonObject> _emitDecision;
    private readonly int _chunkSeconds;
    private readonly int _contextSeconds;
    private readonly double _mergeGapSeconds;
    private readonly double _maxClipSeconds;
    private readonly Action<int, double?>? _onChunkComplete;
    private readonly Action? _onFinish;

    private readonly SemaphoreSlim _poolGate;
    private readonly BlockingCollection<Event> _queue = new();
    private readonly Dictionary<int, JsonObject> _chunks = new();
    private readonly Dictionary<int, (int Start, int End)> _chunkBounds = new();
    private readonly Dictionary<int, Task> _futures = new();
    private int? _lastAdded;
    private bool _finished;
    private volatile bool _cancelQueued;
    private List<JsonObject> _held = new();
    private readonly Thread _emitter;

    public ClipScoringCoordinator(
        ScoreChunkFn scoreChunk,
        Action<JsonObject> emitDecision,
        int chunkSeconds,
        int contextSeconds,
        int concurrency,
        double mergeGapSeconds,
        double maxClipSeconds,
        Action<int, double?>? onChunkComplete = null,
        Action? onFinish = null)
    {
        if (contextSeconds < 0)
            throw new PipelineError("context_seconds must be 0 or greater");
        if (chunkSeconds <= 2 * contextSeconds)
        {
            // The stitcher only looks one chunk back; wider context could
            // produce candidates that should merge across non-adjacent chunks.
            throw new PipelineError("chunk_seconds must be more than twice context_seconds");
        }
        _scoreChunk = scoreChunk;
        _emitDecision = emitDecision;
        _chunkSeconds = chunkSeconds;
        _contextSeconds = contextSeconds;
        _mergeGapSeconds = mergeGapSeconds;
        _maxClipSeconds = maxClipSeconds;
        _onChunkComplete = onChunkComplete;
        _onFinish = onFinish;

        _poolGate = new SemaphoreSlim(Math.Max(1, concurrency));
        _emitter = new Thread(RunEmitter) { Name = "clip-emitter", IsBackground = true };
        _emitter.Start();
    }

    /// <summary>Register a transcribed chunk ({chunk_index, start_seconds,
    /// end_seconds, transcript, words}) and dispatch its predecessor, which
    /// now has right-hand context available.</summary>
    public void AddChunk(JsonObject chunk)
    {
        var index = JsonUtil.Int(chunk["chunk_index"]);
        _chunks[index] = chunk;
        _chunkBounds[index] =
            (JsonUtil.Int(chunk["start_seconds"]), JsonUtil.Int(chunk["end_seconds"]));
        _lastAdded = index;
        if (index > 0 && _chunks.ContainsKey(index - 1) && !_futures.ContainsKey(index - 1))
        {
            Dispatch(index - 1, hasNext: true);
            // The tail of chunk index-2 was only needed as left context for
            // the dispatch of index-1; it can go now.
            _chunks.Remove(index - 2);
        }
    }

    /// <summary>Run fn on the emitter thread, ordered with decision emission.</summary>
    public void RunOnEmitter(Action fn) => _queue.Add(new CallEvent(fn));

    /// <summary>Dispatch the final chunk (unless cancelled), drain everything, and
    /// stop the emitter. Safe to call twice; the second call is a no-op.</summary>
    public void Finish(bool cancelled = false)
    {
        if (_finished) return;
        _finished = true;

        if (!cancelled && _lastAdded is int lastAdded && !_futures.ContainsKey(lastAdded))
            Dispatch(lastAdded, hasNext: false);

        // Waits for running scoring calls either way; the cancel flag drops
        // queued ones so a cancel is bounded by the in-flight calls only.
        if (cancelled) _cancelQueued = true;
        Task.WaitAll(_futures.Values.ToArray());
        _queue.Add(new FinishEvent());
        _emitter.Join();
    }

    private void Dispatch(int index, bool hasNext)
    {
        var chunk = _chunks[index];
        var previous = _chunks.GetValueOrDefault(index - 1);
        var nextChunk = hasNext ? _chunks.GetValueOrDefault(index + 1) : null;
        var visibleStart = previous is not null
            ? JsonUtil.Int(chunk["start_seconds"]) - _contextSeconds
            : JsonUtil.Int(chunk["start_seconds"]);
        var visibleEnd = nextChunk is not null
            ? JsonUtil.Int(chunk["end_seconds"]) + _contextSeconds
            : JsonUtil.Int(chunk["end_seconds"]);
        var contextBefore = previous is not null
            ? JsonUtil.Objects(previous["words"])
                .Where(word => JsonUtil.DoubleOr(
                    word.TryGetPropertyValue("absolute_start", out var s) ? s : null, 0)
                    >= visibleStart)
                .ToList()
            : new List<JsonObject>();
        var contextAfter = nextChunk is not null
            ? JsonUtil.Objects(nextChunk["words"])
                .Where(word => JsonUtil.DoubleOr(
                    word.TryGetPropertyValue("absolute_start", out var s) ? s : null, 0)
                    < visibleEnd)
                .ToList()
            : new List<JsonObject>();
        var scoreChunk = JsonUtil.CloneObj(chunk);
        var audioContext = new JsonArray();
        foreach (var contextChunk in new[] { previous, chunk, nextChunk })
        {
            if (contextChunk is null) continue;
            if (!JsonUtil.Truthy(
                    contextChunk.TryGetPropertyValue("audio_path", out var audioPath)
                        ? audioPath
                        : null))
                continue;
            audioContext.Add(new JsonObject
            {
                ["path"] = JsonUtil.C(contextChunk["audio_path"]),
                ["start_seconds"] = JsonUtil.C(contextChunk["start_seconds"]),
                ["end_seconds"] = JsonUtil.C(contextChunk["end_seconds"]),
            });
        }
        if (audioContext.Count > 0) scoreChunk["audio_context"] = audioContext;

        _futures[index] = Task.Run(() =>
            ScoreAndPost(scoreChunk, contextBefore, contextAfter, visibleStart, visibleEnd));
    }

    private void ScoreAndPost(
        JsonObject chunk,
        List<JsonObject> contextBefore,
        List<JsonObject> contextAfter,
        int visibleStart,
        int visibleEnd)
    {
        var index = JsonUtil.Int(chunk["chunk_index"]);
        _poolGate.Wait();
        try
        {
            if (_cancelQueued)
            {
                // The port of ThreadPoolExecutor's cancel_futures: work that had
                // not started when the cancel arrived never runs.
                _queue.Add(new ScoredEvent(index, new List<JsonObject>(), "Cancelled before scoring."));
                return;
            }
            List<JsonObject> candidates;
            string assessment;
            try
            {
                try
                {
                    (candidates, assessment) = _scoreChunk(
                        chunk, contextBefore, contextAfter, visibleStart, visibleEnd);
                }
                catch (Exception exc)
                {
                    Console.WriteLine($"Scoring chunk {index} failed ({exc.Message}); retrying once");
                    Thread.Sleep(2000);
                    (candidates, assessment) = _scoreChunk(
                        chunk, contextBefore, contextAfter, visibleStart, visibleEnd);
                }
            }
            catch (Exception exc)
            {
                Console.WriteLine($"Scoring chunk {index} failed after retry: {exc.Message}");
                candidates = new List<JsonObject>();
                assessment = $"LLM scoring failed: {exc.Message}";
            }
            _queue.Add(new ScoredEvent(index, candidates, assessment));
        }
        finally
        {
            _poolGate.Release();
        }
    }

    private void RunEmitter()
    {
        var buffered = new Dictionary<int, (List<JsonObject> Candidates, string Assessment)>();
        var nextIndex = 0;
        while (true)
        {
            var evt = _queue.Take();
            if (evt is ScoredEvent scored)
                buffered[scored.Index] = (scored.Candidates, scored.Assessment);
            else if (evt is CallEvent call)
                Safe(call.Fn);
            while (buffered.TryGetValue(nextIndex, out var result))
            {
                buffered.Remove(nextIndex);
                ProcessChunkResult(nextIndex, result.Candidates, result.Assessment);
                nextIndex++;
            }
            if (evt is FinishEvent)
            {
                if (buffered.Count > 0)
                {
                    Console.WriteLine(
                        "Warning: scored chunks were left unemitted "
                        + $"(missing an earlier chunk): [{string.Join(", ", buffered.Keys.OrderBy(k => k))}]");
                }
                break;
            }
        }
        foreach (var decision in _held)
        {
            var captured = decision;
            Safe(() => _emitDecision(captured));
        }
        _held = new List<JsonObject>();
        if (_onFinish is not null) Safe(_onFinish);
    }

    private void ProcessChunkResult(int index, List<JsonObject> candidates, string assessment)
    {
        var (_, chunkEnd) = _chunkBounds[index];
        if (candidates.Count == 0)
            Safe(() => _emitDecision(Scoring.NoClipRecord(index, assessment)));

        var merged = Scoring.MergeCandidates(
            _held.Concat(candidates),
            mergeGapSeconds: _mergeGapSeconds,
            maxClipSeconds: _maxClipSeconds);
        // Anything ending near this chunk's boundary could still merge with a
        // continuation the next chunk proposes; hold it one chunk.
        var holdAfter = chunkEnd - _contextSeconds;
        _held = merged.Where(c => JsonUtil.Double(c["end_seconds"]) > holdAfter).ToList();
        foreach (var decision in merged)
        {
            if (JsonUtil.Double(decision["end_seconds"]) <= holdAfter)
            {
                var captured = decision;
                Safe(() => _emitDecision(captured));
            }
        }

        if (_onChunkComplete is not null)
        {
            double? minHeldStart = _held.Count > 0
                ? _held.Min(c => JsonUtil.Double(c["start_seconds"]))
                : null;
            Safe(() => _onChunkComplete(index, minHeldStart));
        }
    }

    private static void Safe(Action fn)
    {
        try
        {
            fn();
        }
        catch (Exception exc)
        {
            Console.WriteLine("Clip emitter callback failed:");
            Console.Error.WriteLine(exc.ToString());
        }
    }
}
