import json
from pathlib import Path

import highlighter_pipeline.revise as revise
from highlighter_pipeline.revise import (
    _load_state,
    _materialize_selection,
    _validate_selections,
    _window,
)


def test_validate_rejects_garbage_with_a_message():
    selections, problem = _validate_selections(None, 600.0)
    assert selections == [] and problem is not None
    selections, problem = _validate_selections([{"start_seconds": 10, "end_seconds": 11}], 600.0)
    assert selections == [] and problem is not None  # under the 2s minimum


def test_validate_clamps_sorts_and_merges_overlaps():
    selections, problem = _validate_selections(
        [
            {"start_seconds": 500, "end_seconds": 700, "reason": "tail"},
            {"start_seconds": -3, "end_seconds": 20, "reason": "open"},
            {"start_seconds": 15, "end_seconds": 40, "reason": "overlaps open"},
        ],
        600.0,
    )
    assert problem is None
    assert selections == [
        {"start_seconds": 0.0, "end_seconds": 40.0, "reason": "open"},
        {"start_seconds": 500.0, "end_seconds": 600.0, "reason": "tail"},
    ]


def test_window_caps_and_validates():
    state = {"source_end_seconds": 900.0}
    assert _window({"start_seconds": 100, "end_seconds": 2000}, state, 300) == (100.0, 400.0)
    try:
        _window({"start_seconds": 50, "end_seconds": 50}, state, None)
        raise AssertionError("expected a RuntimeError")
    except RuntimeError:
        pass


def _write_project(tmp_path: Path, *, longform_rows=(), with_longform_mp4=False) -> Path:
    project_dir = tmp_path / "projects" / "p1"
    project_dir.mkdir(parents=True)
    (project_dir / "project.json").write_text(
        json.dumps(
            {
                "id": "p1",
                "source_url": "https://example.com/vod",
                "metadata": {"ingest": {"target_length_minutes": "7-15", "source_minutes": 30}},
            }
        )
    )
    chunks = [
        {
            "chunk_index": 0,
            "start_seconds": 0,
            "end_seconds": 90,
            "transcript": "hello",
            "words": [{"absolute_start": 1.0, "absolute_end": 1.5}],
            "metadata": {"scene_cuts": [12.0], "audio_path": "audio/audio_00000.wav"},
        },
        {
            "chunk_index": 1,
            "start_seconds": 90,
            "end_seconds": 180,
            "transcript": "world",
            "words": [],
            "metadata": {},
        },
    ]
    with (project_dir / "transcript_chunks.jsonl").open("w") as file:
        for chunk in chunks:
            file.write(json.dumps(chunk) + "\n")
    clips = [
        {
            "start_seconds": 10.0,
            "end_seconds": 60.0,
            "title": "Kept",
            "status": "rendered",
            "metadata": {"source": "llm", "render": {"local_path": "clips/kept.mp4"}},
        },
        {
            "start_seconds": 20.0,
            "end_seconds": 45.0,
            "title": "Short-fork clip",
            "status": "rendered",
            "metadata": {"source": "llm", "pipeline": "short"},
        },
        {
            "start_seconds": 65.0,
            "end_seconds": 85.0,
            "title": "Long tagged",
            "status": "rendered",
            "metadata": {"source": "llm", "pipeline": "long"},
        },
        {
            "start_seconds": 100.0,
            "end_seconds": 130.0,
            "title": "Failed render",
            "status": "failed",
            "metadata": {"source": "llm"},
        },
    ]
    with (project_dir / "clips.jsonl").open("w") as file:
        for clip in clips:
            file.write(json.dumps(clip) + "\n")
    if longform_rows:
        with (project_dir / "longform_edits.jsonl").open("w") as file:
            for row in longform_rows:
                file.write(json.dumps(row) + "\n")
    if with_longform_mp4:
        (project_dir / "longform").mkdir()
        (project_dir / "longform" / "longform.mp4").write_bytes(b"")
    return project_dir


def test_load_state_versions_and_candidate_filtering(tmp_path):
    state = _load_state(_write_project(tmp_path))
    assert state["current_version"] == 0

    state = _load_state(_write_project(tmp_path / "b", with_longform_mp4=True))
    assert state["current_version"] == 1

    state = _load_state(
        _write_project(tmp_path / "c", longform_rows=[{"version": 1}, {"version": 3}])
    )
    assert state["current_version"] == 3

    # Only rendered long-form LLM candidates survive (an untagged row is an
    # older long-form run; a combined run's short clips are excluded); cuts
    # and word spans flatten.
    assert [candidate["title"] for candidate in state["candidates"]] == [
        "Kept",
        "Long tagged",
    ]
    assert state["cuts"] == [12.0]
    assert state["word_spans"] == [(1.0, 1.5)]
    assert state["source_end_seconds"] == 180
    assert state["target_length"] == "7-15"


def _materialize(state, start, end, monkeypatch=None, calls=None):
    return _materialize_selection(
        state=state,
        start_seconds=start,
        end_seconds=end,
        segments_dir=state["project_dir"] / "longform" / "segments",
        version=2,
        order=0,
    )


def test_materialize_reuses_matching_candidate(tmp_path, monkeypatch):
    project_dir = _write_project(tmp_path)
    (project_dir / "clips").mkdir()
    (project_dir / "clips" / "kept.mp4").write_bytes(b"")
    state = _load_state(project_dir)

    path, source = _materialize(state, 10.1, 59.9)
    assert source["mode"] == "candidate" and source["title"] == "Kept"
    assert path.name == "kept.mp4"


def test_materialize_trims_inside_candidate_window(tmp_path, monkeypatch):
    project_dir = _write_project(tmp_path)
    (project_dir / "clips").mkdir()
    (project_dir / "clips" / "kept.mp4").write_bytes(b"")
    state = _load_state(project_dir)

    trims = []
    monkeypatch.setattr(revise, "trim_clip", lambda **kwargs: trims.append(kwargs))
    path, source = _materialize(state, 20.0, 45.0)
    assert source["mode"] == "trimmed"
    assert trims[0]["start_offset_seconds"] == 10.0
    assert trims[0]["duration_seconds"] == 25.0
    assert path.name == "v2_000.mp4"


def test_materialize_fetches_outside_candidates(tmp_path, monkeypatch):
    project_dir = _write_project(tmp_path)
    state = _load_state(project_dir)

    fetches = []
    monkeypatch.setattr(
        revise, "render_clip_from_video_url", lambda **kwargs: fetches.append(kwargs)
    )
    path, source = _materialize(state, 140.0, 170.0)
    assert source["mode"] == "fetched"
    assert fetches[0]["source_url"] == "https://example.com/vod"
    assert fetches[0]["start_seconds"] == 140.0


def test_materialize_fetches_when_candidate_file_is_missing(tmp_path, monkeypatch):
    # The candidate window matches but its rendered file is gone from disk.
    project_dir = _write_project(tmp_path)
    state = _load_state(project_dir)

    fetches = []
    monkeypatch.setattr(
        revise, "render_clip_from_video_url", lambda **kwargs: fetches.append(kwargs)
    )
    path, source = _materialize(state, 10.0, 60.0)
    assert source["mode"] == "fetched"


class TestInheritedRender:
    def test_newest_version_with_artifacts_wins(self):
        from highlighter_pipeline.revise import _inherited_render

        rows = [
            {
                "version": 1,
                "metadata": {
                    "render": {
                        "title": "Old Title",
                        "thumbnails": {"variants": [{"index": 1, "url": "https://v1/1.png"}],
                                       "selected_index": 0},
                    }
                },
            },
            {"version": 2, "metadata": {"render": {"title": "New Title"}}},
        ]
        inherited = _inherited_render(rows)
        assert inherited["title"] == "New Title"
        assert inherited["thumbnails"]["variants"][0]["url"] == "https://v1/1.png"

    def test_empty_rows_inherit_nothing(self):
        from highlighter_pipeline.revise import _inherited_render

        assert _inherited_render([]) == {}
        assert _inherited_render([{"version": 1, "metadata": {"render": {}}}]) == {}


class TestSelectedVariantUrl:
    def test_selected_index_resolves(self):
        from highlighter_pipeline.revise import _selected_variant_url

        thumbs = {
            "variants": [{"url": "https://a"}, {"url": "https://b"}],
            "selected_index": 1,
        }
        assert _selected_variant_url(thumbs) == "https://b"

    def test_missing_or_bad_index_is_none(self):
        from highlighter_pipeline.revise import _selected_variant_url

        assert _selected_variant_url({"variants": [{"url": "https://a"}]}) is None
        assert _selected_variant_url({"variants": [], "selected_index": 0}) is None
        assert _selected_variant_url({"variants": [{}], "selected_index": 0}) is None
