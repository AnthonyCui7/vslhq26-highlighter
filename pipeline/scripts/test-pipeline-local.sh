#!/bin/bash
# Mock the EC2 one-shot worker locally: build the image, capture a short slice
# of a live stream in the container, then read back what landed in Supabase.
#
# Usage: scripts/test-pipeline-local.sh <twitch-or-youtube-url> [max-chunks] [chunk-seconds]
set -euo pipefail
cd "$(dirname "$0")/.."

STREAM_URL="${1:?usage: scripts/test-pipeline-local.sh <twitch-or-youtube-url> [max-chunks] [chunk-seconds]}"
MAX_CHUNKS="${2:-1}"
CHUNK_SECONDS="${3:-30}"

timestamp() {
    printf '%s +%ss' "$(date '+%Y-%m-%d %H:%M:%S')" "$SECONDS"
}

log_step() {
    printf '[%s] ==> %s\n' "$(timestamp)" "$*"
}

log_step "Building image"
docker build -q -t highlighter-pipeline .

log_step "Running one-shot worker (${MAX_CHUNKS} chunk(s) x ${CHUNK_SECONDS}s from ${STREAM_URL})"
log="$(mktemp)"
worker_rc=0
# Short-lived AWS creds from the host CLI (works with aws login/SSO/keys);
# avoids mounting ~/.aws, whose token cache the container can't refresh.
if command -v aws >/dev/null 2>&1; then
    set +e
    aws_exports="$(aws configure export-credentials --format env 2>/dev/null)"
    aws_rc=$?
    set -e
    if [ "$aws_rc" -eq 0 ]; then
        eval "$aws_exports"
        log_step "Exported host AWS credentials for container"
    else
        log_step "Skipping AWS credential export"
    fi
else
    log_step "Skipping AWS credential export"
fi

set +e
docker run --rm --env-file .env \
    -e AWS_ACCESS_KEY_ID -e AWS_SECRET_ACCESS_KEY -e AWS_SESSION_TOKEN \
    -e PYTHONUNBUFFERED=1 \
    -e STREAM_URL="$STREAM_URL" \
    -e MAX_CHUNKS="$MAX_CHUNKS" \
    -e CHUNK_SECONDS="$CHUNK_SECONDS" \
    highlighter-pipeline 2>&1 | while IFS= read -r line; do
        printf '[%s] %s\n' "$(timestamp)" "$line"
    done | tee "$log"
worker_rc="${PIPESTATUS[0]}"
set -e
log_step "Worker exited with status $worker_rc"

project_id="$(sed -n 's/^.*Started project \([^ ]*\).*$/\1/p' "$log" | head -1)"
rm -f "$log"
if [ -z "$project_id" ]; then
    echo "Could not find a project id in the worker output" >&2
    exit 1
fi

log_step "Reading project $project_id back from Supabase"
set -a
source .env
set +a
key="${SUPABASE_SERVICE_ROLE_KEY:-${SUPABASE_SECRET_KEY:-}}"
base="${SUPABASE_URL%/}"

log_step "Fetching project metadata"
curl -sf "$base/rest/v1/projects?id=eq.$project_id&select=name,source_type,status,error,metadata,created_at,updated_at" \
    -H "apikey: $key" -H "Authorization: Bearer $key" | python3 -m json.tool

log_step "Fetching transcript chunks"
curl -sf "$base/rest/v1/transcript_chunks?project_id=eq.$project_id&select=chunk_index,start_seconds,end_seconds,transcript&order=chunk_index" \
    -H "apikey: $key" -H "Authorization: Bearer $key" | python3 -m json.tool

log_step "Fetching clips"
curl -sf "$base/rest/v1/clips?project_id=eq.$project_id&select=title,start_seconds,end_seconds,score,status,video_url&order=start_seconds" \
    -H "apikey: $key" -H "Authorization: Bearer $key" | python3 -m json.tool

exit "$worker_rc"
