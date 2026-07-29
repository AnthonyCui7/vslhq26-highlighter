#!/bin/bash
# Run the full capture -> ffmpeg -> Deepgram -> OpenRouter pipeline in the
# container WITHOUT touching Supabase. Outputs land as plain files under
# ./data/projects/local-*/
# (audio WAVs, transcript JSON, chunks.jsonl, clip_candidates.jsonl, rendered clips, summary.json).
#
# Usage: scripts/test-pipeline-dry.sh <twitch-or-youtube-url> [max-chunks] [chunk-seconds]
set -euo pipefail
cd "$(dirname "$0")/.."

STREAM_URL="${1:?usage: scripts/test-pipeline-dry.sh <twitch-or-youtube-url> [max-chunks] [chunk-seconds]}"
MAX_CHUNKS="${2:-1}"
CHUNK_SECONDS="${3:-30}"

echo "==> Building image"
docker build -q -t highlighter-pipeline .

echo "==> Running dry-run worker (no DB, ${MAX_CHUNKS} chunk(s) x ${CHUNK_SECONDS}s)"
log="$(mktemp)"
worker_rc=0
docker run --rm --env-file .env \
    -v "$PWD/data:/app/data" \
    -e NO_DB=1 \
    -e S3_BUCKET= \
    -e STREAM_URL="$STREAM_URL" \
    -e MAX_CHUNKS="$MAX_CHUNKS" \
    -e CHUNK_SECONDS="$CHUNK_SECONDS" \
    highlighter-pipeline 2>&1 | tee "$log" || worker_rc=$?

project_id="$(sed -n 's/^Started local project \([^ ]*\).*/\1/p' "$log" | head -1)"
rm -f "$log"
if [ -z "$project_id" ]; then
    echo "Could not find a project id in the worker output" >&2
    exit 1
fi

echo "==> Files produced in data/projects/$project_id"
find "data/projects/$project_id" -type f | sort

echo "==> Summary"
python3 -m json.tool "data/projects/$project_id/summary.json"

exit "$worker_rc"
