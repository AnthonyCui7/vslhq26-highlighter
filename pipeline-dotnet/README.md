# Highlighter pipeline — C#/.NET port

A behaviorally faithful port of `pipeline/` (Python) to .NET 10 / C#, built for the
VSLive! Microsoft AI Hackathon 2026. Same CLI flags and env vars, same prompts,
same API wire calls, same output record shapes, same failure/degradation
semantics — `pipeline/` remains the reference implementation.

## Layout

```
Highlighter.sln
src/Highlighter.Pipeline/     class library — one file per Python module
                              (Ingest.cs ↔ ingest.py, Scoring.cs ↔ scoring.py, ...)
src/Highlighter.Cli/          `highlighter` console app (subcommand dispatch)
tests/Highlighter.Pipeline.Tests/  xUnit ports of all pipeline/tests/ suites
```

External dependencies stay identical to the Python pipeline: `ffmpeg`/`ffprobe`,
`yt-dlp`, `streamlink` on PATH; Azure AI Speech (Deepgram fallback), the Azure
OpenAI deployments and OpenRouter in the same per-role chains, Supabase, and
optionally S3 via the same env vars (the monorepo root `.env` is discovered by
walking up from the working directory, exactly like the Python `load_env`).

## Build & run

```sh
dotnet build                  # or: dotnet test
dotnet run --project src/Highlighter.Cli -- ingest <url> --pipeline long --local-only
dotnet run --project src/Highlighter.Cli -- revise <project-id> "tighten the middle"
```

Subcommands mirror the Python console scripts one-to-one:

| Python entry point    | C# equivalent            |
|-----------------------|--------------------------|
| `highlighter-ingest`  | `highlighter ingest`     |
| `highlighter-revise`  | `highlighter revise`     |
| `highlighter-reclip`  | `highlighter reclip`     |
| `highlighter-cleanup` | `highlighter cleanup`    |
| `highlighter-db-smoke`| `highlighter db-smoke`   |

Run from the monorepo root so `outputs/` and the `.env` land in the same places
the Python pipeline uses.

## Shot detection (TransNetV2) — the one Python remnant

The PyTorch inference stays in Python behind a long-lived JSON-lines sidecar
(`pipeline/highlighter_pipeline/shots_sidecar.py`, ~60 lines). `Shots.cs` starts
`python3 -m highlighter_pipeline.shots_sidecar` once per run and exchanges one
JSON line per archived segment; cut timestamps are bit-identical to the Python
pipeline's. When python/torch are missing the port logs one line and runs
without scene cuts — the same graceful degradation as the Python optional
import. A later ONNX Runtime port can replace the sidecar without touching any
C# call sites (the interface is path in → cut timestamps out).

## Deliberate divergences (cosmetic only)

- JSON text layout of local records (key order, indent/escaping style) can
  differ from CPython's `json.dumps`; shapes and key names are identical.
- Windows: SIGTERM handling and the 0600 cookie-file chmod are POSIX-only
  (the pipeline targets macOS/Linux, as before).
- Python floats print `10.0` where .NET prints `10` in some log lines.

Deferred, per the migration plan: in-process TransNetV2 (ONNX), the Docker
worker image, and the dev/diagnostic scripts (`generate_metrics.py`,
`pipeline/scripts/*`, `tests/run_pipeline.py`) — use the Python originals.
