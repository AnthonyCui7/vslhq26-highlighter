# pipeline

Core Highlighter processing pipeline: capture a livestream or VOD, transcribe
it in chunks with Azure AI Speech, detect clip-worthy moments with an
audio-capable LLM through the pipeline's Azure OpenAI-anchored model layer,
render clip MP4s with ffmpeg, and persist the results.

```bash
# From the repo root — the end-to-end runner (see tests/run_pipeline.py --help):
python tests/run_pipeline.py short https://www.twitch.tv/<channel>
python tests/run_pipeline.py long "https://www.youtube.com/watch?v=XXXX" --target-minutes 10
python tests/run_pipeline.py both "https://www.youtube.com/watch?v=XXXX"

# Or invoke the module directly; see --help for all flags
python -m highlighter_pipeline.ingest <url> --pipeline short --max-chunks 4
```

Modes: `--pipeline short` renders independent short-form clips, each with a
blur-pad 9:16 vertical variant (`--no-reframe` to skip) plus a burned-caption
copy of that vertical when pycaps is installed (`--no-captions` to skip);
`--pipeline long` selects long-form segments with a different editor prompt,
stitches them chronologically into `longform/longform.mp4`, titles the video
in pass 2, and generates three concept thumbnails (`--no-thumbnails` to skip);
`--pipeline both` runs the two editorial forks in parallel off one shared
capture/transcription/shot-detection pass, producing the short clips and the
long-form edit in a single project.
Every mode takes a livestream or a VOD — a livestream's long-form edit is cut
when the stream ends. All accept `--instructions` (editorial guidance) and run
a per-mode-specialized web-grounded content research call first
(`--no-research` to skip). Results land in `outputs/projects/<id>/` mirroring
the Supabase shape, and in Supabase unless `--local-only`.

Revise a finished long-form edit with natural language — the agent reads the
transcript, listens to audio, and can rerun research or per-chunk scoring
before committing the next version (`longform_v2.mp4`, `longform_v3.mp4`, ...):

```bash
highlighter-revise <project-id> "tighten the middle and cut the sponsor talk"
```

Publish a finished clip or long-form edit through upload-post.com (create a
profile there, link the social accounts, and set `UPLOAD_POST_API_KEY` +
`UPLOAD_POST_USER`). Short clips post the captioned vertical by default
(`--plain` for the clean one); a longform target takes `--thumbnail 1|2|3` (a
generated variant) or a path to your own image; `x` rides along as a
model-written promo post linking the published video; `--dry-run` prints what
would be posted without calling the API:

```bash
highlighter-publish <project-id> longform --platforms youtube,x --thumbnail 2
highlighter-publish <project-id> clip_00003_10000_20500_short.mp4 --platforms tiktok,instagram
```

After a run, three verbs keep working the finished project: regenerate
long-form thumbnails with human guidance (or just switch the selection),
rerun the content research, and add a square 1:1 center-crop render
(optionally captioned) to a clip's media set:

```bash
highlighter-thumbnails <project-id> --prompt "lean into the reunion emotion"
highlighter-thumbnails <project-id> --select 2
highlighter-research <project-id> --mode long --focus "what retains viewers here"
highlighter-reformat <project-id> clip_00003_10000_20500_short.mp4 --captions
```

Burned captions use the `pycaps` CLI (not on PyPI; runs auto-skip with a log
line when it's missing):

```bash
pip install "git+https://github.com/francozanardi/pycaps.git#egg=pycaps[base]"
python3 -m playwright install chromium
```

Key modules:

- `capture.py` — streamlink (live Twitch) / yt-dlp (everything else) piped into
  ffmpeg: 16 kHz mono WAV chunks for transcription, plus optional codec-copied
  source segments for clip rendering.
- `transcribe.py` — chunk transcription with Azure AI Speech fast
  transcription (`AZURE_SPEECH_KEY` +
  `AZURE_SPEECH_REGION`/`AZURE_SPEECH_ENDPOINT`), returning word-timestamped
  output.
- `providers.py` — the model seam every LLM call goes through, built around
  the project's Azure OpenAI deployments: the gpt-5 editor deployment
  (text/vision: pass 2, reframe, research, thumbnail concepts) and the
  gpt-audio deployment (the audio-capable scoring roles). Azure env vars:
  `AZURE_EDITOR_*` / `AZURE_AUDIO_*`, or the shared
  `AZURE_OPENAI_ENDPOINT`/`AZURE_OPENAI_API_KEY` with
  `AZURE_OPENAI_EDIT_DEPLOYMENT`/`AZURE_OPENAI_AUDIO_DEPLOYMENT`. Reasoning
  deployments run at the highest effort their family accepts
  (`AZURE_REASONING_EFFORT` to override).
- `agents.py` — Microsoft Agent Framework orchestration over those providers:
  research, pass 2, thumbnail concepts, and the revision loop run as
  framework agents (sessions plus framework-run function-invocation loops)
  through a pipeline chat client that keeps the exact request wire format.
- `llm.py` — clip-candidate detection over transcript + audio.
- `shots.py` — TransNetV2 shot-boundary detection per segment: scene cuts feed
  the editor prompts and snap final clip boundaries to the source's own edit
  points. Optional dependency: `pip install 'highlighter-pipeline[shots]'`
  (auto-skips with a log line when missing).
- `editor.py` — long-form pass 2: one global editor call makes the final cut
  from every pass-1 candidate, with keep-rate arithmetic precomputed in minutes.
- `revise.py` — long-form revision loop: an Agent Framework editor agent with
  access to the transcript, audio, scene cuts, and research; reruns pipeline
  stages on demand and assembles each new version from existing renders (or
  fresh source fetches) into a `longform_edits` row. New versions inherit the
  newest generated thumbnails and title — a re-cut keeps what it doesn't
  touch.
- `reframe.py` — short-form auto-reframe: one framing call per clip (frames
  sampled every 5s plus one after each scene cut; `--reframe-interval`) picks
  the horizontal crop centers — or keeps a span wide when the frame's
  information doesn't fit a square (boards, screens, split layouts); ffmpeg
  renders the blur-pad 9:16 vertical either way.
- `reformat.py` — square 1:1 center-crop renders for finished clips
  (`highlighter-reformat`), optionally with the same burned captions.
- `captions.py` — burned captions on the verticals: the chunk word timings are
  reshaped into Whisper's JSON and handed to the pycaps CLI, which renders a
  captioned copy next to each clean vertical (`clips.captioned_url`).
- `thumbnails.py` — long-form thumbnails: one editor-model call designs three
  distinct concepts from the research + title + kept segments, then the
  Azure-hosted image deployment (gpt-image-2) renders each over real frames
  from the stitched video; a random variant becomes the `longform_edits`
  thumbnail and all three are kept for the publish-time pick.
- `publish.py` — social publishing via upload-post.com: multipart video posts
  to TikTok/Instagram/YouTube, an LLM-drafted X promo post with the published
  link, and a `publications` row per successful post.
- `research.py` — content research layer: one structured, source-cited call
  per editorial fork on the editor deployment, with the prompt and schema
  specialized per mode (short form: clip formats, hooks, platform norms; long
  form: structure, pacing, retention).
- `scoring.py` — concurrent scoring coordinator + boundary stitching/merging.
- `render.py` — ffmpeg clip rendering, trims, and thumbnails.
- `reclip.py` — re-cut a new clip window from an archived source.

Ships as a Docker image (`Dockerfile`); also runs directly on a machine with
ffmpeg, streamlink, and yt-dlp installed.
