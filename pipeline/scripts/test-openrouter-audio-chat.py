#!/usr/bin/env python3
"""Call OpenRouter chat completions with MP3 input and print usage or the API error."""

from __future__ import annotations

import argparse
import asyncio
import base64
import json
import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from highlighter_pipeline.config import load_env, required_env  # noqa: E402


async def main() -> None:
    os.chdir(ROOT)
    load_env()
    args = parse_args()

    try:
        from openai import APIStatusError, AsyncOpenAI
    except ImportError as exc:
        raise RuntimeError("Install the OpenAI Python SDK first: pip install openai") from exc

    audio_b64 = base64.b64encode(args.audio_path.read_bytes()).decode("ascii")
    client = AsyncOpenAI(
        base_url="https://openrouter.ai/api/v1",
        api_key=required_env("OPENROUTER_API_KEY"),
        timeout=240.0,
        default_headers={
            "HTTP-Referer": "http://localhost",
            "X-OpenRouter-Title": "highlighter audio token test",
        },
    )

    try:
        response = await client.chat.completions.create(
            model=args.model,
            messages=[
                {
                    "role": "system",
                    "content": "You are measuring audio input usage. Return only compact JSON.",
                },
                {
                    "role": "user",
                    "content": [
                        {
                            "type": "text",
                            "text": (
                                "Listen to this audio and return "
                                '{"heard_speech": true, "summary": "under 12 words"}.'
                            ),
                        },
                        {
                            "type": "input_audio",
                            "input_audio": {
                                "data": audio_b64,
                                "format": args.audio_format,
                            },
                        },
                    ],
                },
            ],
            temperature=0,
            max_tokens=args.max_tokens,
            user=args.user_id,
            extra_body={
                "provider": {
                    "order": args.provider,
                    "allow_fallbacks": args.allow_fallbacks,
                },
                "reasoning": {"effort": args.reasoning_effort},
            },
        )
    except APIStatusError as exc:
        print_result(
            {
                "endpoint": "https://openrouter.ai/api/v1/chat/completions",
                "model": args.model,
                "provider": args.provider,
                "allow_fallbacks": args.allow_fallbacks,
                "reasoning_effort": args.reasoning_effort,
                "audio_file": str(args.audio_path),
                "audio_format": args.audio_format,
                "audio_bytes": args.audio_path.stat().st_size,
                "http_error": exc.status_code,
                "body": exc.response.text[:12000],
            }
        )
        raise SystemExit(1)

    print_result(
        {
            "endpoint": "https://openrouter.ai/api/v1/chat/completions",
            "model": args.model,
            "provider": args.provider,
            "allow_fallbacks": args.allow_fallbacks,
            "reasoning_effort": args.reasoning_effort,
            "audio_file": str(args.audio_path),
            "audio_format": args.audio_format,
            "audio_bytes": args.audio_path.stat().st_size,
            "response_model": response.model,
            "usage": response.usage.model_dump(mode="json") if response.usage else None,
            "message": (
                response.choices[0].message.content if response.choices else None
            ),
        }
    )


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Send an MP3 to OpenRouter audio-capable chat completions and print usage."
    )
    parser.add_argument("audio_path", type=Path, help="MP3 file to send.")
    parser.add_argument(
        "--model",
        default="google/gemini-3.1-pro-preview",
        help="OpenRouter model ID.",
    )
    parser.add_argument(
        "--provider",
        action="append",
        default=None,
        help="OpenRouter provider slug. Repeat to set provider order.",
    )
    parser.add_argument(
        "--allow-fallbacks",
        action="store_true",
        help="Allow OpenRouter to fall through to another endpoint if the first route fails.",
    )
    parser.add_argument("--audio-format", default="mp3", help="input_audio format.")
    parser.add_argument("--max-tokens", type=int, default=512, help="Maximum output tokens.")
    parser.add_argument("--reasoning-effort", default="medium", help="OpenRouter reasoning effort.")
    parser.add_argument("--user-id", default="audio-token-test", help="OpenRouter user tracking ID.")
    args = parser.parse_args()
    args.provider = args.provider or ["google-vertex"]
    return args


def print_result(result: dict) -> None:
    print(json.dumps(result, indent=2)[:12000])


if __name__ == "__main__":
    asyncio.run(main())
