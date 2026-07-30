# Highlighter API (`apps/api`)

ASP.NET Core **Minimal API (.NET 10)** that drives the Highlighter pipeline: submit a
job, watch progress live, browse clips and long-form cuts, revise with natural
language, publish to social platforms, and keep storage tidy. The pipeline worker
(`pipeline-dotnet`, CLI `highlighter`) runs as a supervised child process; Supabase
(Postgres + the public `clips` bucket) stays the source of truth.

## Run

```bash
# one-time: build the worker binary the API spawns
dotnet build pipeline-dotnet

# start the API on http://localhost:5199
cd apps/api && dotnet run --project src/Highlighter.Api
```

- **API docs (Scalar UI):** http://localhost:5199/scalar · OpenAPI at `/openapi/v1.json`
- Secrets come from the repo-root `.env` — the exact same contract as the worker
  (`SUPABASE_URL` + `SUPABASE_ANON_KEY` + `SUPABASE_SERVICE_ROLE_KEY`, Azure/Deepgram/OpenRouter
  keys, …). `GET /healthz` reports what is present/missing as booleans, never values.
- Tests: `dotnet test apps/api` — no network, Supabase stubbed.
- Smoke the whole flow end-to-end: `scripts/e2e-smoke.sh <short youtube VOD url>`
  (signs up an ephemeral account first — the API requires auth).

## Auth

Every `/api/*` route except `/api/auth` requires `Authorization: Bearer <token>`,
where the token is a Supabase user JWT obtained from this API's own auth surface
(the browser never sees a Supabase key; the API validates tokens offline against
the project's ES256 JWKS):

| Route | Purpose |
|---|---|
| POST `/api/auth/signup` `{email, password}` | Creates the user **in Supabase** (`auth.users`, pre-confirmed via the admin API) and returns a session |
| POST `/api/auth/login` | Password grant → `{accessToken, refreshToken, expiresAt, user}` |
| POST `/api/auth/refresh` `{refreshToken}` | Rotates the session (GoTrue refresh tokens are single-use) |
| POST `/api/auth/logout` | Best-effort GoTrue sign-out, always 204 |

**Strict ownership:** projects are stamped with the creator's `user_id` and every
read/write is scoped to it — another user's project (and legacy ownerless rows)
read as 404. Set `Api:RequireAuth=false` in `appsettings.json` (or
`Api__RequireAuth=false` env) for tokenless scripting; queries are then unscoped,
matching pre-auth behavior. Jobs endpoints are gated but not per-user filtered.

## Endpoints

| Method | Route | Purpose |
|---|---|---|
| GET | `/healthz` | Config/binary/DB checks, outbox counts, orphaned-run count (503 when core is degraded) |
| POST | `/api/projects` | Create a project row + launch ingest (`{sourceUrl, pipeline: short\|long\|both, instructions?, targetMinutes?, minClipScore?, maxChunks?, chunkSeconds?, no*` flags`}`) |
| GET | `/api/projects` | Projects with clip/chunk/long-form counts and derived progress |
| GET | `/api/projects/{id}` | Full detail: clips, long-form versions, publications, local-mirror flag |
| DELETE | `/api/projects/{id}?force=` | Delete row (DB cascades + media-cleanup outbox), remove local mirror, kick a drain |
| POST | `/api/projects/{id}/cancel?force=` | Guarded `stopping` write; worker converges to `cancelled`. `force` escalates SIGTERM → SIGKILL for a tracked process |
| GET | `/api/projects/{id}/clips?pipeline=&status=&order=score\|start` | Clips (filterable) |
| GET | `/api/projects/{id}/longform` | Long-form versions, newest first |
| GET | `/api/projects/{id}/publications` | Social posts |
| GET | `/api/projects/{id}/transcript?includeWords=` | Transcript chunks (word timings optional) |
| POST | `/api/projects/{id}/revise` | `{request}` → tool-calling revision agent → `longform_v<N>.mp4` |
| POST | `/api/projects/{id}/publish` | `{target: clip\|longform, clipId?, version?, platforms[], title?, thumbnail?, plain?, dryRun?}` |
| POST | `/api/projects/{id}/reclip` | `{startSeconds, endSeconds, title?}` — needs an archived livestream source |
| POST | `/api/projects/{id}/research` | `{mode?: short\|long, focus?}` → job (worker `research` verb) |
| POST | `/api/projects/{id}/thumbnails` | `{prompt?, version?}` → job — generate more long-form thumbnail concepts |
| POST | `/api/projects/{id}/thumbnails/select` | `{index, version?}` — make a variant the video's thumbnail (short-wait job) |
| POST | `/api/projects/{id}/thumbnails/import` | `{fileName, contentBase64, version?}` — upload your own, stored + selected |
| POST | `/api/projects/{id}/clips/{clipId}/reformat` | `{format: "square", captions?}` → job (worker `reformat` verb) |
| GET/PUT | `/api/projects/{id}/clips/{clipId}/editor` | The clip's timeline-editor document (EDL; seeded from transcript words) |
| POST | `/api/projects/{id}/clips/{clipId}/editor/export` | Render the EDL (ffmpeg in-process job) → storage + `metadata.editor.render` |
| GET/PUT | `/api/projects/{id}/longform/editor?version=` | Long-form editor draft (stored on the version row) |
| POST | `/api/projects/{id}/longform/editor/export` | Render the draft → a NEW long-form version row |
| GET | `/api/jobs` · `/api/jobs/{id}` | Worker + in-process job registry |
| GET | `/api/jobs/{id}/logs?tail=` | Log tail (falls back to the on-disk file after an API restart) |
| GET | `/api/jobs/{id}/logs/stream` | **SSE**: replay + live worker output, terminal `end` event |
| POST | `/api/admin/cleanup` | Manual outbox drain (`{limit}`) |

Errors are RFC 7807 ProblemDetails everywhere. CORS allows the Blazor dev origins.
Setting `Api:ApiKey` in `appsettings.json` turns on an `X-Api-Key` gate for `/api/*`
(off by default — everything binds to localhost).

## Architecture notes

- **Self-contained by design.** No project reference into `pipeline-dotnet` — the
  worker is an external binary plus a shared Postgres status contract, so worker
  build breakage can never take the API down. The URL classifier is a small port
  pinned to the worker's behavior by tests.
- **Attach mode.** POST inserts the row at `status='created'` (with
  `instance_id = job id` and request details under `metadata.api` — the one
  top-level key the worker's shallow metadata merge never touches), then spawns
  `highlighter ingest --project-id <id>`. The row's `source_url`/`min_clip_score`
  override argv; run knobs travel as flags. `--max-chunks` is always explicit
  (API default 0 = unlimited) so the worker's dev default of 1 can't truncate runs.
- **Cancel contract.** The API writes `stopping` (guarded compare-and-set PATCH);
  the worker polls the row every ~10 s and finishes as `cancelled` itself. The API
  never writes `cancelled` while a worker may be alive — only after observing the
  process exit (force-cancel, or the exit reconciliation below).
- **Exit reconciliation.** When an ingest worker exits without a terminal row
  status (crash, SIGKILL), the API applies a guarded fixup: `created`/`ingesting`
  → `failed` (+error naming the job), `stopping` → `cancelled`.
- **Restart semantics.** The job registry is in-memory. After an API restart the
  rows stay authoritative, cooperative cancel still works (the worker polls the
  DB), job logs serve from disk, and `/healthz` counts orphaned runs. Force-kill
  only ever targets processes this API instance spawned.
- **Media cleanup.** Deleting rows fills the `media_cleanup_jobs` outbox via DB
  triggers; a background scheduler (and every project delete) drains it by running
  the worker's `cleanup` verb — single-flight, since the outbox has no lease.

## Debugging a failed run

The trail, in order — nothing here requires the API process that ran the job to
still be alive:

1. `GET /api/projects/{id}` → `project.error` and `status` (worker failures land
   here: `failed` + a readable error).
2. `GET /api/jobs?projectId={id}` → job `state`, `failureReason`, `exitCode`,
   `logPath`.
3. **The per-job log file** — `outputs/api/jobs/<utc>-<kind>-<proj8>-<jobId>.log`:
   full worker stdout/stderr with timestamps, plus an `[api]` header (exact
   command, workdir) and footer (exit code, duration, row reconciliation result).
   Also served via `GET /api/jobs/{id}/logs` even after a restart.
4. The worker's own mirror — `outputs/projects/{id}/` (`project.json` status +
   error, `decisions.jsonl`, `clips.jsonl`, `transcript_chunks.jsonl`).
5. The API's log — `outputs/api/api-<date>.log` (Serilog: request log, job
   lifecycle, every Supabase failure with status + body prefix, never keys).

## The timeline editor's render path

The studio's editor stores an EDL (segments/cuts/speed, captions with word
timings, text overlays, markers, zoom/pan reframe, voice/music gains) in the
schema's existing jsonb room — `clips.metadata.editor` for clips, a draft on the
long-form version row — and exports run as in-process jobs: fetch the clean
source render, composite with ffmpeg, upload to the public bucket. Because this
machine's ffmpeg ships without libass/drawtext, caption lines and text overlays
are rasterized to PNGs in-process (ImageSharp) and composited with the `overlay`
filter — karaoke as one image per word-state (capped at 240 inputs, degrading to
line-level). Clip exports overwrite a deterministic `{project}/editor/…` path;
long-form exports insert a new version row whose `metadata.render` the existing
DB cleanup trigger already understands. Project DELETE additionally sweeps the
`{project}/editor/` and `{project}/thumbnails/` storage prefixes.

## Known limitations

- One active job per project (a second request 409s naming the blocker) —
  deliberate v1 simplicity.
- The in-memory job list empties on restart; job *logs* survive on disk and the DB
  keeps all run state.
- SSE subscribers that stall drop oldest lines (bounded buffers); the file has
  everything.
- Embedded-count queries assume PostgREST aggregates stay enabled (verified live);
  `SupabaseDb` isolates the query so the fallback is a one-line swap.
