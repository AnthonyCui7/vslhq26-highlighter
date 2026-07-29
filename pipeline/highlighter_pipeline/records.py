"""Local project records: a file mirror of what lands in Supabase.

Every run — Supabase-backed or --local-only — writes these under the project
directory, so `outputs/projects/<id>/` is always a self-contained record with
the same shape the database has:

    project.json             the projects row (status, metadata, error)
    transcript_chunks.jsonl  one line per transcript_chunks row (with words)
    clips.jsonl              one line per clips row (worthy, rendered clips)
    decisions.jsonl          full editorial log: every decision, worthy or not
    longform_edits.jsonl     one line per stitched long-form version
    publications.jsonl       one line per social post published from the project
    research.json            content research context, when research ran
    research_short.json      the short fork's research in a combined run
                             (research.json holds the long fork's)
"""

import json
import threading
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


def _now() -> str:
    return datetime.now(timezone.utc).isoformat()


class ProjectRecords:
    def __init__(self, project_dir: Path) -> None:
        self.project_dir = project_dir
        self.project_dir.mkdir(parents=True, exist_ok=True)
        # A combined run writes records from two coordinator emitter threads.
        self._lock = threading.Lock()
        # Resume an existing record (e.g. the revision loop reopening a
        # finished project) instead of starting a fresh row over it.
        project_file = self.project_dir / "project.json"
        if project_file.exists():
            self._project: dict[str, Any] = json.loads(project_file.read_text())
        else:
            self._project = {"created_at": _now()}

    def update_project(self, **fields: Any) -> None:
        """Merge fields into project.json (None values are ignored so a status
        write never erases metadata written earlier)."""
        self._project.update({k: v for k, v in fields.items() if v is not None})
        self._project["updated_at"] = _now()
        self._write_json("project.json", self._project)

    def append_chunk(self, row: dict[str, Any]) -> None:
        self._append_jsonl("transcript_chunks.jsonl", row)

    def append_clip(self, row: dict[str, Any]) -> None:
        self._append_jsonl("clips.jsonl", row)

    def append_decision(self, row: dict[str, Any]) -> None:
        self._append_jsonl("decisions.jsonl", row)

    def append_longform_edit(self, row: dict[str, Any]) -> None:
        self._append_jsonl("longform_edits.jsonl", row)

    def append_publication(self, row: dict[str, Any]) -> None:
        self._append_jsonl("publications.jsonl", row)

    def write_research(self, context: dict[str, Any], *, mode: str | None = None) -> None:
        name = "research.json" if mode is None else f"research_{mode}.json"
        self._write_json(name, context)

    def _write_json(self, name: str, payload: dict[str, Any]) -> None:
        with self._lock:
            (self.project_dir / name).write_text(
                json.dumps(payload, indent=2, default=str)
            )

    def _append_jsonl(self, name: str, row: dict[str, Any]) -> None:
        with self._lock:
            with (self.project_dir / name).open("a") as file:
                file.write(json.dumps(row, default=str) + "\n")
