from highlighter_pipeline.reframe import (
    MAX_SAMPLE_FRAMES,
    _mode_enables,
    _sample_times,
    _validate_keyframes,
    crop_x_expression,
)


def test_validate_falls_back_to_centered_framing():
    fallback = [{"start_seconds": 0.0, "center_x": 0.5, "wide": False}]
    assert _validate_keyframes(None, 30.0) == fallback
    assert _validate_keyframes([], 30.0) == fallback
    assert _validate_keyframes([{"start_seconds": "x", "center_x": None}], 30.0) == fallback


def test_validate_forces_first_keyframe_to_zero_and_sorts():
    keyframes = _validate_keyframes(
        [
            {"start_seconds": 12.0, "center_x": 0.8},
            {"start_seconds": 0.7, "center_x": 0.3},
        ],
        30.0,
    )
    assert keyframes[0] == {"start_seconds": 0.0, "center_x": 0.3, "wide": False}
    assert keyframes[1] == {"start_seconds": 12.0, "center_x": 0.8, "wide": False}


def test_validate_drops_jitter_keyframes():
    keyframes = _validate_keyframes(
        [
            {"start_seconds": 0.0, "center_x": 0.3},
            # Too soon after the previous keyframe.
            {"start_seconds": 0.8, "center_x": 0.9},
            # Move too small to matter.
            {"start_seconds": 5.0, "center_x": 0.31},
            {"start_seconds": 9.0, "center_x": 0.7},
        ],
        30.0,
    )
    assert keyframes == [
        {"start_seconds": 0.0, "center_x": 0.3, "wide": False},
        {"start_seconds": 9.0, "center_x": 0.7, "wide": False},
    ]


def test_validate_drops_keyframes_past_the_clip_and_clamps_center():
    keyframes = _validate_keyframes(
        [
            {"start_seconds": 0.0, "center_x": 1.7},
            {"start_seconds": 45.0, "center_x": 0.5},
        ],
        30.0,
    )
    assert keyframes == [{"start_seconds": 0.0, "center_x": 1.0, "wide": False}]


def test_validate_wide_normalizes_center_and_merges_consecutive_wides():
    keyframes = _validate_keyframes(
        [
            {"start_seconds": 0.0, "center_x": 0.9, "wide": True},
            # Redundant: still wide.
            {"start_seconds": 6.0, "center_x": 0.1, "wide": True},
            {"start_seconds": 12.0, "center_x": 0.7, "wide": False},
        ],
        30.0,
    )
    assert keyframes == [
        {"start_seconds": 0.0, "center_x": 0.5, "wide": True},
        {"start_seconds": 12.0, "center_x": 0.7, "wide": False},
    ]


def test_validate_keeps_mode_changes_with_identical_centers():
    keyframes = _validate_keyframes(
        [
            {"start_seconds": 0.0, "center_x": 0.5, "wide": False},
            {"start_seconds": 5.0, "center_x": 0.5, "wide": True},
            {"start_seconds": 10.0, "center_x": 0.5, "wide": False},
        ],
        30.0,
    )
    assert [keyframe["wide"] for keyframe in keyframes] == [False, True, False]


def test_mode_enables_split_spans_between_framings():
    crop_enable, wide_enable = _mode_enables(
        [
            {"start_seconds": 0.0, "center_x": 0.3, "wide": False},
            {"start_seconds": 5.0, "center_x": 0.5, "wide": True},
            {"start_seconds": 12.5, "center_x": 0.7, "wide": False},
        ]
    )
    assert crop_enable == "between(t,0,5)+between(t,12.5,1e9)"
    assert wide_enable == "between(t,5,12.5)"


def test_mode_enables_without_wides_disables_the_wide_overlay():
    crop_enable, wide_enable = _mode_enables(
        [{"start_seconds": 0.0, "center_x": 0.5, "wide": False}]
    )
    assert crop_enable == "between(t,0,1e9)"
    assert wide_enable == "0"


def test_crop_expression_single_keyframe_is_a_constant():
    expression = crop_x_expression(
        [{"start_seconds": 0.0, "center_x": 0.5}], source_width=1280, source_height=720
    )
    assert expression == "280"  # 0.5 * 1280 - 720/2


def test_crop_expression_builds_nested_piecewise():
    expression = crop_x_expression(
        [
            {"start_seconds": 0.0, "center_x": 0.3},
            {"start_seconds": 5.0, "center_x": 0.7},
            {"start_seconds": 9.5, "center_x": 0.5},
        ],
        source_width=1280,
        source_height=720,
    )
    assert expression == "if(lt(t,5),24,if(lt(t,9.5),536,280))"


def test_crop_expression_clamps_positions_into_frame():
    expression = crop_x_expression(
        [
            {"start_seconds": 0.0, "center_x": 0.0},
            {"start_seconds": 5.0, "center_x": 1.0},
        ],
        source_width=1280,
        source_height=720,
    )
    assert expression == "if(lt(t,5),0,560)"  # 560 = 1280 - 720


def test_sample_times_opener_only_without_cuts():
    assert _sample_times(58.0, []) == [0.2]


def test_sample_times_one_frame_after_each_cut_capped():
    cuts = [float(cut) for cut in range(2, 40, 2)]
    times = _sample_times(60.0, cuts)
    assert len(times) == MAX_SAMPLE_FRAMES
    assert times[0] == 0.2
    assert all(earlier < later for earlier, later in zip(times[1:], times[2:]))
