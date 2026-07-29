namespace Highlighter.Pipeline;

/// <summary>The port's stand-in for Python's RuntimeError: every raise
/// RuntimeError(...) in the pipeline becomes a PipelineError with the same
/// message text.</summary>
public class PipelineError : Exception
{
    public PipelineError(string message) : base(message) { }

    public PipelineError(string message, Exception inner) : base(message, inner) { }
}
