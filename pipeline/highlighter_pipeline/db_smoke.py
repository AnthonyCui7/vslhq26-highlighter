from .config import load_env
from .supabase_client import SupabaseClient


def main() -> None:
    load_env()
    db = SupabaseClient()
    project_id = db.create_project(
        name="DB smoke test",
        source_type="upload",
        source_url="smoke://local",
        metadata={"kind": "db-smoke"},
    )
    db.insert_transcript_chunk(
        project_id=project_id,
        chunk_index=0,
        start_seconds=0,
        end_seconds=90,
        transcript="Smoke test transcript for a 90 second chunk.",
        words=[
            {
                "word": "smoke",
                "start": 0,
                "end": 0.5,
                "absolute_start": 0,
                "absolute_end": 0.5,
            }
        ],
        metadata={"smoke": True},
    )
    db.insert_clip(
        project_id=project_id,
        title="Smoke test highlight",
        description="Fake clip inserted by db_smoke.",
        start_seconds=10,
        end_seconds=25,
        score=42,
        metadata={"smoke": True},
    )
    db.set_project_status(project_id, status="ready", metadata={"kind": "db-smoke", "chunks": 1})
    print(f"Inserted smoke project {project_id} with one transcript_chunks row and one clips row.")


if __name__ == "__main__":
    main()
