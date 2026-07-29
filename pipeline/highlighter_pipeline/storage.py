import mimetypes
import os
from pathlib import Path

from .defaults import DEFAULT_AWS_REGION, DEFAULT_S3_BUCKET

_CONTENT_TYPES = {
    ".ts": "video/mp2t",
    ".wav": "audio/wav",
    ".mp4": "video/mp4",
}


class S3Storage:
    def __init__(self, bucket: str, region: str) -> None:
        import boto3  # deferred so S3-disabled runs don't need it installed

        self.bucket = bucket
        self.region = region
        self._client = boto3.client("s3", region_name=region)

    def upload_file(self, path: Path, key: str) -> str:
        content_type = (
            _CONTENT_TYPES.get(path.suffix.lower())
            or mimetypes.guess_type(path.name)[0]
            or "application/octet-stream"
        )
        self._client.upload_file(
            str(path), self.bucket, key, ExtraArgs={"ContentType": content_type}
        )
        return self.url_for(key)

    def url_for(self, key: str) -> str:
        return f"https://{self.bucket}.s3.{self.region}.amazonaws.com/{key}"

    def download_file(self, key: str, path: Path) -> Path:
        path.parent.mkdir(parents=True, exist_ok=True)
        self._client.download_file(self.bucket, key, str(path))
        return path

    def delete_object(self, key: str) -> None:
        self._client.delete_object(Bucket=self.bucket, Key=key)

    def delete_prefix(self, prefix: str) -> int:
        """Delete every object under a prefix. Returns the number deleted."""
        deleted = 0
        paginator = self._client.get_paginator("list_objects_v2")
        for page in paginator.paginate(Bucket=self.bucket, Prefix=prefix.rstrip("/") + "/"):
            keys = [{"Key": item["Key"]} for item in page.get("Contents", [])]
            if keys:
                self._client.delete_objects(Bucket=self.bucket, Delete={"Objects": keys})
                deleted += len(keys)
        return deleted


def storage_from_env() -> S3Storage | None:
    """S3 storage from S3_BUCKET/AWS_REGION. Empty S3_BUCKET disables uploads."""
    bucket = os.environ.get("S3_BUCKET", DEFAULT_S3_BUCKET)
    if not bucket:
        return None
    return S3Storage(bucket, os.environ.get("AWS_REGION", DEFAULT_AWS_REGION))
