from highlighter_pipeline.llm import _normalize_candidates


def normalize(clips, assessment="assessed", **kwargs):
    defaults = {
        "chunk_index": 1,
        "chunk_start_seconds": 90,
        "chunk_end_seconds": 180,
        "visible_start_seconds": 80,
        "visible_end_seconds": 190,
        "model": "test-model",
        "reasoning_effort": "low",
    }
    defaults.update(kwargs)
    return _normalize_candidates(
        {"clips": clips, "chunk_assessment": assessment}, **defaults
    )


def clip(start, end, score=0.5, **extra):
    return {
        "title": "t",
        "description": "d",
        "start_seconds": start,
        "end_seconds": end,
        "score": score,
        "reason": "r",
        "research_sources": [],
        **extra,
    }


def test_empty_clips_returns_assessment():
    candidates, assessment = normalize([], assessment="all filler")
    assert candidates == []
    assert assessment == "all filler"


def test_multiple_clips_normalized():
    candidates, _ = normalize([clip(95, 120), clip(150, 170, score=0.9)])
    assert len(candidates) == 2
    assert all(c["is_clip_worthy"] for c in candidates)
    assert all(c["chunk_index"] == 1 for c in candidates)
    assert candidates[1]["score"] == 0.9


def test_clamped_to_visible_window_not_chunk_window():
    candidates, _ = normalize([clip(60, 200)])
    assert candidates[0]["start_seconds"] == 80
    assert candidates[0]["end_seconds"] == 190


def test_boundary_spill_is_preserved():
    candidates, _ = normalize([clip(85, 95)])
    assert candidates[0]["start_seconds"] == 85  # 5s into the left margin


def test_margin_only_clip_dropped():
    # Lives entirely in the left context margin: the previous chunk owns it.
    candidates, _ = normalize([clip(80, 90)])
    assert candidates == []


def test_invalid_window_dropped():
    candidates, _ = normalize([clip(120, 100)])
    assert candidates == []


def test_score_clamped_to_unit_interval():
    candidates, _ = normalize([clip(100, 120, score=7)])
    assert candidates[0]["score"] == 1.0


def test_malformed_items_skipped():
    candidates, _ = normalize(["nonsense", clip(100, 120)])
    assert len(candidates) == 1


def test_missing_clips_key_is_empty():
    candidates, assessment = _normalize_candidates(
        {},
        chunk_index=0,
        chunk_start_seconds=0,
        chunk_end_seconds=90,
        visible_start_seconds=0,
        visible_end_seconds=90,
        model="m",
        reasoning_effort="low",
    )
    assert candidates == []
    assert assessment == ""
