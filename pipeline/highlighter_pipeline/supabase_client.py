import json
import mimetypes
from datetime import datetime, timezone
from pathlib import Path
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.parse import quote, urlencode
from urllib.request import Request, urlopen

from .config import required_any_env, required_env


class SupabaseClient:
    def __init__(self) -> None:
        self.base_url = required_env("SUPABASE_URL").rstrip("/")
        self.key = required_any_env(["SUPABASE_SERVICE_ROLE_KEY", "SUPABASE_SECRET_KEY"])

    def create_project(
        self,
        *,
        name: str,
        source_type: str,
        source_url: str,
        status: str = "ingesting",
        metadata: dict[str, Any] | None = None,
    ) -> str:
        rows = self._request(
            "POST",
            "projects",
            query={"select": "id"},
            body={
                "name": name,
                "source_type": source_type,
                "source_url": source_url,
                "status": status,
                "metadata": metadata or {},
            },
            prefer="return=representation",
        )
        return rows[0]["id"]

    def set_project_status(
        self,
        project_id: str,
        *,
        status: str,
        metadata: dict[str, Any] | None = None,
        error: str | None = None,
    ) -> None:
        body: dict[str, Any] = {"status": status}
        if metadata is not None:
            body["metadata"] = metadata
        if error is not None:
            body["error"] = error

        self._request(
            "PATCH",
            "projects",
            query={"id": f"eq.{project_id}"},
            body=body,
            prefer="return=minimal",
        )

    def update_project_status_guarded(
        self,
        project_id: str,
        *,
        status: str,
        when_status_in: list[str],
        metadata: dict[str, Any] | None = None,
        error: str | None = None,
    ) -> dict[str, Any] | None:
        """PATCH the project only while its status is one of when_status_in, so the
        worker never overwrites a terminal/cancellation status the router wrote.
        Returns the updated row, or None when the guard matched nothing."""
        body: dict[str, Any] = {"status": status}
        if metadata is not None:
            body["metadata"] = metadata
        if error is not None:
            body["error"] = error

        rows = self._request(
            "PATCH",
            "projects",
            query={
                "id": f"eq.{project_id}",
                "status": f"in.({','.join(when_status_in)})",
            },
            body=body,
            prefer="return=representation",
        )
        return rows[0] if rows else None

    def get_project_status(self, project_id: str) -> str | None:
        rows = self._request(
            "GET",
            "projects",
            query={"id": f"eq.{project_id}", "select": "status"},
        )
        return rows[0]["status"] if rows else None

    def insert_transcript_chunk(
        self,
        *,
        project_id: str,
        chunk_index: int,
        start_seconds: float,
        end_seconds: float,
        transcript: str,
        words: list[dict[str, Any]],
        metadata: dict[str, Any] | None = None,
    ) -> None:
        self._request(
            "POST",
            "transcript_chunks",
            body={
                "project_id": project_id,
                "chunk_index": chunk_index,
                "start_seconds": start_seconds,
                "end_seconds": end_seconds,
                "transcript": transcript,
                "words": words,
                "metadata": metadata or {},
            },
            prefer="return=minimal",
        )

    def insert_clip(
        self,
        *,
        project_id: str,
        title: str,
        start_seconds: float,
        end_seconds: float,
        description: str | None = None,
        score: float | None = None,
        video_url: str | None = None,
        status: str | None = None,
        metadata: dict[str, Any] | None = None,
    ) -> None:
        body: dict[str, Any] = {
            "project_id": project_id,
            "title": title,
            "start_seconds": start_seconds,
            "end_seconds": end_seconds,
            "description": description,
            "score": score,
            "video_url": video_url,
            "metadata": metadata or {},
        }
        if status is not None:
            body["status"] = status

        self._request(
            "POST",
            "clips",
            body=body,
            prefer="return=minimal",
        )

    def get_project(self, project_id: str) -> dict[str, Any]:
        rows = self._request(
            "GET",
            "projects",
            query={"id": f"eq.{project_id}", "select": "*"},
        )
        if not rows:
            raise RuntimeError(f"Project not found: {project_id}")
        return rows[0]

    def pending_cleanup_jobs(self, *, limit: int = 100) -> list[dict[str, Any]]:
        return (
            self._request(
                "GET",
                "media_cleanup_jobs",
                query={"status": "eq.pending", "order": "created_at", "limit": str(limit)},
            )
            or []
        )

    def finish_cleanup_job(self, job_id: str, *, status: str, error: str | None = None) -> None:
        body: dict[str, Any] = {
            "status": status,
            "completed_at": datetime.now(timezone.utc).isoformat(),
        }
        if error is not None:
            body["last_error"] = error

        self._request(
            "PATCH",
            "media_cleanup_jobs",
            query={"id": f"eq.{job_id}"},
            body=body,
            prefer="return=minimal",
        )

    def bump_cleanup_attempts(self, job_id: str, attempts: int) -> None:
        self._request(
            "PATCH",
            "media_cleanup_jobs",
            query={"id": f"eq.{job_id}"},
            body={"attempts": attempts},
            prefer="return=minimal",
        )

    def delete_storage_object(self, *, bucket: str, key: str) -> None:
        storage_path = f"{quote(bucket, safe='')}/{quote(key, safe='/')}"
        request = Request(
            f"{self.base_url}/storage/v1/object/{storage_path}",
            headers={
                "apikey": self.key,
                "Authorization": f"Bearer {self.key}",
            },
            method="DELETE",
        )

        try:
            with urlopen(request, timeout=60) as response:
                response.read()
        except HTTPError as exc:
            if exc.code == 404:
                return  # already gone — deletion is idempotent
            message = exc.read().decode("utf-8", errors="replace")
            raise RuntimeError(f"Supabase storage delete failed: {message}") from exc
        except URLError as exc:
            raise RuntimeError(f"Supabase storage delete failed: {exc.reason}") from exc

    def upload_storage_object(self, *, bucket: str, key: str, path: Path) -> str:
        content_type = mimetypes.guess_type(path.name)[0] or "application/octet-stream"
        storage_path = f"{quote(bucket, safe='')}/{quote(key, safe='/')}"
        request = Request(
            f"{self.base_url}/storage/v1/object/{storage_path}",
            data=path.read_bytes(),
            headers={
                "apikey": self.key,
                "Authorization": f"Bearer {self.key}",
                "Content-Type": content_type,
                "x-upsert": "true",
            },
            method="POST",
        )

        try:
            with urlopen(request, timeout=120) as response:
                response.read()
        except HTTPError as exc:
            message = exc.read().decode("utf-8", errors="replace")
            raise RuntimeError(f"Supabase storage upload failed: {message}") from exc
        except URLError as exc:
            raise RuntimeError(f"Supabase storage upload failed: {exc.reason}") from exc

        return self.public_storage_url(bucket=bucket, key=key)

    def public_storage_url(self, *, bucket: str, key: str) -> str:
        storage_path = f"{quote(bucket, safe='')}/{quote(key, safe='/')}"
        return f"{self.base_url}/storage/v1/object/public/{storage_path}"

    def _request(
        self,
        method: str,
        table: str,
        *,
        query: dict[str, str] | None = None,
        body: dict[str, Any] | None = None,
        prefer: str | None = None,
    ) -> Any:
        query_string = f"?{urlencode(query)}" if query else ""
        data = json.dumps(body).encode("utf-8") if body is not None else None
        headers = {
            "apikey": self.key,
            "Authorization": f"Bearer {self.key}",
            "Content-Type": "application/json",
        }
        if prefer:
            headers["Prefer"] = prefer

        request = Request(
            f"{self.base_url}/rest/v1/{table}{query_string}",
            data=data,
            headers=headers,
            method=method,
        )

        try:
            with urlopen(request, timeout=30) as response:
                response_body = response.read().decode("utf-8")
        except HTTPError as exc:
            message = exc.read().decode("utf-8", errors="replace")
            raise RuntimeError(f"Supabase {method} {table} failed: {message}") from exc
        except URLError as exc:
            raise RuntimeError(f"Supabase {method} {table} failed: {exc.reason}") from exc

        if not response_body:
            return None
        return json.loads(response_body)
