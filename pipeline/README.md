# pipeline

Core Highlighter processing pipeline: capture a livestream or VOD, transcribe
it in chunks with Deepgram, detect clip-worthy moments with an audio-capable
LLM, render clip MP4s with ffmpeg, and persist the results.

```bash
# From the repo root — the end-to-end runner (see tests/run_pipeline.py --help):
python tests/run_pipeline.py short https://www.twitch.tv/<channel>
python tests/run_pipeline.py long "https://www.youtube.com/watch?v=XXXX" --target-minutes 10

# Or invoke the module directly; see --help for all flags
python -m highlighter_pipeline.ingest <url> --pipeline short --max-chunks 4
```

Modes: `--pipeline short` renders independent short-form clips; `--pipeline
long` selects long-form segments with a different editor prompt and stitches
them chronologically into `longform/longform.mp4`. Both accept
`--instructions` (editorial guidance) and run a web-grounded content research
agent first (`--no-research` to skip). Results land in
`outputs/projects/<id>/` mirroring the Supabase shape, and in Supabase unless
`--local-only`.

Key modules:

- `capture.py` — streamlink (live Twitch) / yt-dlp (everything else) piped into
  ffmpeg: 16 kHz mono WAV chunks for transcription, plus optional codec-copied
  source segments for clip rendering.
- `deepgram.py` — chunk transcription (word-level timestamps).
- `llm.py` — clip-candidate detection over transcript + audio via OpenRouter.
- `research.py` — content research layer (stub; will be a LangGraph workflow).
- `scoring.py` — concurrent scoring coordinator + boundary stitching/merging.
- `render.py` — ffmpeg clip rendering and thumbnails.
- `reclip.py` — re-cut a new clip window from an archived source.

Ships as a Docker image (`Dockerfile`); also runs directly on a machine with
ffmpeg, streamlink, and yt-dlp installed.
