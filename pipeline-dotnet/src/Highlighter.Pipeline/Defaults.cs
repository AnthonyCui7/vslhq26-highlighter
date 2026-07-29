namespace Highlighter.Pipeline;

/// <summary>Port of highlighter_pipeline/defaults.py — same names, same values.</summary>
public static class Defaults
{
    public const string DEFAULT_STREAM_URL = "https://www.twitch.tv/jasontheween";
    public const int DEFAULT_CHUNK_SECONDS = 90;
    public const int DEFAULT_MAX_CHUNKS = 1;
    public const string DEFAULT_STREAMLINK_QUALITY = "best";
    public const string DEFAULT_DEEPGRAM_MODEL = "nova-3";
    public const string DEFAULT_OPENROUTER_MODEL = "google/gemini-3.1-pro-preview";
    public const string DEFAULT_LLM_REASONING_EFFORT = "high";
    public const int DEFAULT_LLM_MARKER_SECONDS = 10;
    public const int DEFAULT_LLM_CONCURRENCY = 8;
    public const int DEFAULT_LLM_CONTEXT_SECONDS = 10;
    public const double DEFAULT_CLIP_MERGE_GAP_SECONDS = 1.0;
    public const double DEFAULT_MAX_CLIP_SECONDS = 120;
    // Shot-boundary detection (TransNetV2) and cut-aligned boundary snapping.
    public const int DEFAULT_SHOT_FPS = 25;
    public const double DEFAULT_SHOT_SNAP_TOLERANCE_SECONDS = 1.5;
    // Long-form mode selects bigger segments but keeps the merge gap tight: the
    // editor deliberately splits a passage around coughs, false starts, and dead
    // air, and a wide gap would glue those micro-cuts (junk included) back in.
    public const double DEFAULT_LONGFORM_MERGE_GAP_SECONDS = 1.0;
    public const double DEFAULT_LONGFORM_MAX_CLIP_SECONDS = 900;
    public const string DEFAULT_TARGET_LENGTH_MINUTES = "7-15";
    public const string DEFAULT_RESEARCH_MODEL = "anthropic/claude-sonnet-5";
    public const string DEFAULT_OUTPUT_ROOT = "outputs";
    // Empty by default: livestream source archiving to S3 is opt-in via S3_BUCKET.
    public const string DEFAULT_S3_BUCKET = "";
    public const string DEFAULT_AWS_REGION = "us-east-1";
    public const string DEFAULT_SUPABASE_CLIPS_BUCKET = "clips";
}
