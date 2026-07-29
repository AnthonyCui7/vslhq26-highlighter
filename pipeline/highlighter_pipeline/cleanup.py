"""Drain the media_cleanup_jobs outbox: delete the S3/Supabase Storage objects
that deleted DB rows pointed at. Safe to run repeatedly; deletions are idempotent."""

import argparse

from .config import load_env
from .storage import S3Storage
from .supabase_client import SupabaseClient


def main() -> None:
    load_env()
    args = _parse_args()

    db = SupabaseClient()
    jobs = db.pending_cleanup_jobs(limit=args.limit)
    if not jobs:
        print("No pending media cleanup jobs.")
        return

    s3_clients: dict[str, S3Storage] = {}
    succeeded = failed = 0

    for job in jobs:
        db.bump_cleanup_attempts(job["id"], job["attempts"] + 1)
        try:
            _delete_media(job, db=db, s3_clients=s3_clients)
        except Exception as exc:
            failed += 1
            db.finish_cleanup_job(job["id"], status="failed", error=str(exc))
            print(f"FAILED {_describe(job)}: {exc}")
        else:
            succeeded += 1
            db.finish_cleanup_job(job["id"], status="succeeded")
            print(f"deleted {_describe(job)}")

    print(f"Cleanup finished: {succeeded} succeeded, {failed} failed.")
    if failed:
        raise SystemExit(1)


def _delete_media(job: dict, *, db: SupabaseClient, s3_clients: dict[str, S3Storage]) -> None:
    if job["store"] == "s3":
        client = s3_clients.get(job["bucket"])
        if client is None:
            client = s3_clients[job["bucket"]] = S3Storage(job["bucket"], _s3_region())
        if job["is_prefix"]:
            client.delete_prefix(job["object_key"])
        else:
            client.delete_object(job["object_key"])
    elif job["store"] == "supabase_storage":
        db.delete_storage_object(bucket=job["bucket"], key=job["object_key"])
    else:
        raise RuntimeError(f"Unknown media store: {job['store']}")


def _s3_region() -> str:
    import os

    from .defaults import DEFAULT_AWS_REGION

    return os.environ.get("AWS_REGION", DEFAULT_AWS_REGION)


def _describe(job: dict) -> str:
    suffix = "/*" if job["is_prefix"] else ""
    return f"{job['store']}://{job['bucket']}/{job['object_key']}{suffix} (from {job['origin_table']} {job['origin_id']})"


def _parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Delete media objects referenced by pending media_cleanup_jobs rows.",
    )
    parser.add_argument(
        "--limit",
        type=int,
        default=100,
        help="Maximum jobs to process in one run.",
    )
    return parser.parse_args()


if __name__ == "__main__":
    main()
