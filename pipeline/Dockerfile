FROM python:3.11-slim

RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/* \
    && pip install --no-cache-dir streamlink yt-dlp boto3 "botocore[crt]" openai langgraph

WORKDIR /app
COPY highlighter_pipeline ./highlighter_pipeline
COPY docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh

# One-shot worker: run until the stream ends unless the launcher says otherwise.
ENV MAX_CHUNKS=0
# Unbuffered stdout so `docker logs` shows pipeline progress live.
ENV PYTHONUNBUFFERED=1

ENTRYPOINT ["docker-entrypoint.sh"]
