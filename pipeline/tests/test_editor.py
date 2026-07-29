from highlighter_pipeline.editor import (
    _budget_arithmetic,
    _cap_candidates,
    _validate_selections,
)
from highlighter_pipeline.llm import parse_target_minutes


def candidate(start, end, score=0.5, title="t"):
    return {
        "start_seconds": start,
        "end_seconds": end,
        "score": score,
        "title": title,
        "description": "d",
        "reason": "r",
    }


def selections_of(raw, candidates):
    offered = dict(enumerate(candidates))
    return _validate_selections({"selections": raw}, candidates, offered)


class TestParseTargetMinutes:
    def test_single_number(self):
        assert parse_target_minutes("10") == (10.0, 10.0)

    def test_range(self):
        assert parse_target_minutes("7-15") == (7.0, 15.0)

    def test_whitespace(self):
        assert parse_target_minutes(" 7 - 15 ") == (7.0, 15.0)

    def test_garbage_and_empty(self):
        assert parse_target_minutes("abc") is None
        assert parse_target_minutes("") is None
        assert parse_target_minutes(None) is None

    def test_inverted_range_rejected(self):
        assert parse_target_minutes("15-7") is None


class TestValidateSelections:
    def test_unknown_index_dropped(self):
        candidates = [candidate(0, 60)]
        result = selections_of(
            [{"index": 5, "start_seconds": 0, "end_seconds": 60, "reason": ""}], candidates
        )
        assert result == []

    def test_duplicate_index_first_wins(self):
        candidates = [candidate(0, 60)]
        result = selections_of(
            [
                {"index": 0, "start_seconds": 0, "end_seconds": 30, "reason": "first"},
                {"index": 0, "start_seconds": 30, "end_seconds": 60, "reason": "second"},
            ],
            candidates,
        )
        assert len(result) == 1
        assert result[0]["reason"] == "first"

    def test_bounds_clamped_into_candidate_window(self):
        # The editor may only tighten, never extend.
        candidates = [candidate(100, 160)]
        result = selections_of(
            [{"index": 0, "start_seconds": 80, "end_seconds": 200, "reason": ""}], candidates
        )
        assert result[0]["start_seconds"] == 100
        assert result[0]["end_seconds"] == 160

    def test_degenerate_window_dropped(self):
        candidates = [candidate(100, 160)]
        result = selections_of(
            [{"index": 0, "start_seconds": 150, "end_seconds": 151, "reason": ""}], candidates
        )
        assert result == []

    def test_sorted_chronologically(self):
        candidates = [candidate(300, 360), candidate(0, 60)]
        result = selections_of(
            [
                {"index": 0, "start_seconds": 300, "end_seconds": 360, "reason": ""},
                {"index": 1, "start_seconds": 0, "end_seconds": 60, "reason": ""},
            ],
            candidates,
        )
        assert [s["index"] for s in result] == [1, 0]

    def test_capped_out_candidate_not_selectable(self):
        candidates = [candidate(0, 60)]
        result = _validate_selections(
            {"selections": [{"index": 0, "start_seconds": 0, "end_seconds": 60, "reason": ""}]},
            candidates,
            offered={},
        )
        assert result == []


class TestCapCandidates:
    def test_under_cap_keeps_original_indexes(self):
        candidates = [candidate(0, 60), candidate(60, 120)]
        indexed, dropped = _cap_candidates(candidates)
        assert dropped == 0
        assert [index for index, _ in indexed] == [0, 1]

    def test_over_cap_drops_weakest_keeps_order(self):
        candidates = [candidate(i * 60, i * 60 + 30, score=i / 200) for i in range(160)]
        indexed, dropped = _cap_candidates(candidates)
        assert dropped == 10
        assert len(indexed) == 150
        # The 10 lowest-scoring candidates are the earliest ones here.
        assert [index for index, _ in indexed] == list(range(10, 160))


class TestBudgetArithmetic:
    def test_minutes_and_keep_rate(self):
        candidates = [candidate(0, 600), candidate(1000, 1600), candidate(2000, 3200)]
        arithmetic = _budget_arithmetic(candidates, source_minutes=180, target_length="10")
        assert arithmetic["candidate_count"] == 3
        assert arithmetic["candidate_minutes"] == 40.0
        assert arithmetic["keep_percent"] == 25

    def test_unparseable_target_omits_keep_rate(self):
        arithmetic = _budget_arithmetic(
            [candidate(0, 600)], source_minutes=180, target_length="whatever"
        )
        assert "keep_percent" not in arithmetic
