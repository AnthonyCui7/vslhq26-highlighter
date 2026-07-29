#!/bin/sh
set -eu

# Fail fast instead of falling back to the dev-default stream in defaults.py.
# PROJECT_ID runs read the source URL from the pre-created project row instead.
if [ -z "${STREAM_URL:-}" ] && [ -z "${PROJECT_ID:-}" ]; then
    echo "STREAM_URL or PROJECT_ID is required" >&2
    exit 1
fi
: "${DEEPGRAM_API_KEY:?DEEPGRAM_API_KEY is required}"
if [ -z "${NO_DB:-}" ]; then
    : "${SUPABASE_URL:?SUPABASE_URL is required}"
    if [ -z "${SUPABASE_SERVICE_ROLE_KEY:-}" ] && [ -z "${SUPABASE_SECRET_KEY:-}" ]; then
        echo "SUPABASE_SERVICE_ROLE_KEY or SUPABASE_SECRET_KEY is required" >&2
        exit 1
    fi
fi
if [ -z "${NO_LLM:-}" ]; then
    : "${OPENROUTER_API_KEY:?OPENROUTER_API_KEY is required unless NO_LLM=1}"
fi

exec python -m highlighter_pipeline.ingest "$@"
