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

Modes: `--pipeline short` renders independent short-form clips, each with a
blur-pad 9:16 vertical variant (`--no-reframe` to skip); `--pipeline long`
selects long-form segments with a different editor prompt and stitches them
chronologically into `longform/longform.mp4`. Both accept `--instructions`
(editorial guidance) and run a web-grounded content research agent first
(`--no-research` to skip). Results land in `outputs/projects/<id>/` mirroring
the Supabase shape, and in Supabase unless `--local-only`.

Revise a finished long-form edit with natural language — the agent reads the
transcript, listens to audio, and can rerun research or per-chunk scoring
before committing the next version (`longform_v2.mp4`, `longform_v3.mp4`, ...):

```bash
highlighter-revise <project-id> "tighten the middle and cut the sponsor talk"
```

Key modules:

- `capture.py` — streamlink (live Twitch) / yt-dlp (everything else) piped into
  ffmpeg: 16 kHz mono WAV chunks for transcription, plus optional codec-copied
  source segments for clip rendering.
- `deepgram.py` — chunk transcription (word-level timestamps).
- `llm.py` — clip-candidate detection over transcript + audio via OpenRouter.
- `shots.py` — TransNetV2 shot-boundary detection per segment: scene cuts feed
  the editor prompts and snap final clip boundaries to the source's own edit
  points. Optional dependency: `pip install 'highlighter-pipeline[shots]'`
  (auto-skips with a log line when missing).
- `editor.py` — long-form pass 2: one global editor call makes the final cut
  from every pass-1 candidate, with keep-rate arithmetic precomputed in minutes.
- `revise.py` — long-form revision loop: a tool-calling editor agent with
  access to the transcript, audio, scene cuts, and research; reruns pipeline
  stages on demand and assembles each new version from existing renders (or
  fresh source fetches) into a `longform_edits` row.
- `reframe.py` — short-form auto-reframe: one framing call per clip picks the
  horizontal crop centers; ffmpeg renders the blur-pad 9:16 vertical.
- `research.py` — content research layer (stub; will be a LangGraph workflow).
- `scoring.py` — concurrent scoring coordinator + boundary stitching/merging.
- `render.py` — ffmpeg clip rendering, trims, and thumbnails.
- `reclip.py` — re-cut a new clip window from an archived source.

Ships as a Docker image (`Dockerfile`); also runs directly on a machine with
ffmpeg, streamlink, and yt-dlp installed.
