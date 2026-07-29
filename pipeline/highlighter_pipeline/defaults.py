DEFAULT_STREAM_URL = "https://www.twitch.tv/jasontheween"
DEFAULT_CHUNK_SECONDS = 90
DEFAULT_MAX_CHUNKS = 1
DEFAULT_STREAMLINK_QUALITY = "best"
DEFAULT_DEEPGRAM_MODEL = "nova-3"
DEFAULT_OPENROUTER_MODEL = "google/gemini-3.1-pro-preview"
DEFAULT_LLM_REASONING_EFFORT = "high"
DEFAULT_LLM_MARKER_SECONDS = 10
DEFAULT_LLM_CONCURRENCY = 8
DEFAULT_LLM_CONTEXT_SECONDS = 10
DEFAULT_CLIP_MERGE_GAP_SECONDS = 1.0
DEFAULT_MAX_CLIP_SECONDS = 120
# Auto-reframe frame sampling: one frame every this many seconds, plus one
# just after each scene cut (0 disables the periodic frames).
DEFAULT_REFRAME_FRAME_INTERVAL_SECONDS = 5.0
# Shot-boundary detection (TransNetV2) and cut-aligned boundary snapping.
DEFAULT_SHOT_FPS = 25
DEFAULT_SHOT_SNAP_TOLERANCE_SECONDS = 1.5
# Long-form mode selects bigger segments but keeps the merge gap tight: the
# editor deliberately splits a passage around coughs, false starts, and dead
# air, and a wide gap would glue those micro-cuts (junk included) back in.
DEFAULT_LONGFORM_MERGE_GAP_SECONDS = 1.0
DEFAULT_LONGFORM_MAX_CLIP_SECONDS = 900
DEFAULT_TARGET_LENGTH_MINUTES = "7-15"
DEFAULT_RESEARCH_MODEL = "anthropic/claude-sonnet-5"
DEFAULT_THUMBNAIL_MODEL = "google/gemini-3.1-flash-image"
DEFAULT_CAPTION_TEMPLATE = "instagram"
# Azure AI integration: Azure Speech transcription runs first with Deepgram
# as the fallback, and the Azure OpenAI deployments sit in every model chain
# (see providers.py for the per-role order). Reasoning-capable deployments
# run at the highest effort their family accepts (AZURE_REASONING_EFFORT
# overrides).
DEFAULT_AZURE_SPEECH_LOCALE = "en-US"
DEFAULT_OUTPUT_ROOT = "outputs"
# Empty by default: livestream source archiving to S3 is opt-in via S3_BUCKET.
DEFAULT_S3_BUCKET = ""
DEFAULT_AWS_REGION = "us-east-1"
DEFAULT_SUPABASE_CLIPS_BUCKET = "clips"
