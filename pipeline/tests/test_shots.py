from highlighter_pipeline.shots import snap_window


def test_no_cuts_leaves_window_unchanged():
    start, end, info = snap_window(10.0, 40.0, cuts=[], word_spans=[])
    assert (start, end) == (10.0, 40.0)
    assert info is None


def test_boundaries_snap_to_nearby_cuts():
    start, end, info = snap_window(10.0, 40.0, cuts=[9.2, 41.1], word_spans=[])
    assert (start, end) == (9.2, 41.1)
    assert info == {
        "original_start": 10.0,
        "original_end": 40.0,
        "snapped_start": 9.2,
        "snapped_end": 41.1,
    }


def test_cut_outside_tolerance_is_ignored():
    start, end, info = snap_window(10.0, 40.0, cuts=[7.0, 44.0], word_spans=[])
    assert (start, end) == (10.0, 40.0)
    assert info is None


def test_nearest_qualifying_cut_wins():
    start, _, _ = snap_window(10.0, 40.0, cuts=[8.8, 10.4], word_spans=[])
    assert start == 10.4


def test_cut_inside_a_word_is_refused():
    # 9.2 falls mid-word; the boundary must not clip speech.
    start, end, info = snap_window(10.0, 40.0, cuts=[9.2], word_spans=[(9.0, 9.5)])
    assert (start, end) == (10.0, 40.0)
    assert info is None


def test_cut_at_word_edge_is_allowed():
    start, _, info = snap_window(10.0, 40.0, cuts=[9.0], word_spans=[(9.0, 9.5)])
    assert start == 9.0
    assert info is not None


def test_snap_that_collapses_the_clip_is_refused():
    # Both boundaries would snap to nearby cuts, but the result is under the
    # minimum clip length, so the original window is kept.
    start, end, info = snap_window(
        10.0, 12.0, cuts=[10.9, 11.4], word_spans=[], min_duration_seconds=2.0
    )
    assert (start, end) == (10.0, 12.0)
    assert info is None


def test_single_boundary_snap():
    start, end, info = snap_window(10.0, 40.0, cuts=[39.5], word_spans=[])
    assert (start, end) == (10.0, 39.5)
    assert info["snapped_start"] == 10.0
