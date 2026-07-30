from highlighter_pipeline.ingest import _measured_segment_start, _window_segment_range


def _starts(mapping: dict[int, float]):
    return lambda index: _measured_segment_start(mapping, index, 90)


class TestMeasuredSegmentStart:
    def test_measured_positions_are_relative_to_segment_zero(self):
        starts = {0: 1.44, 1: 93.25, 2: 183.11}
        assert _measured_segment_start(starts, 0, 90) == 0.0
        assert round(_measured_segment_start(starts, 1, 90), 2) == 91.81
        assert round(_measured_segment_start(starts, 2, 90), 2) == 181.67

    def test_missing_probe_falls_back_to_nominal(self):
        assert _measured_segment_start({0: 1.44}, 2, 90) == 180.0

    def test_missing_base_falls_back_to_nominal(self):
        assert _measured_segment_start({2: 183.11}, 2, 90) == 180.0

    def test_empty_mapping_is_nominal(self):
        assert _measured_segment_start({}, 0, 90) == 0.0
        assert _measured_segment_start({}, 3, 90) == 270.0


class TestWindowSegmentRange:
    def test_nominal_when_no_probes(self):
        assert _window_segment_range(66.0, 97.5, 90, _starts({})) == (0, 1)
        assert _window_segment_range(191.1, 261.48, 90, _starts({})) == (2, 2)

    def test_window_starting_in_previous_segments_tail(self):
        # Segment 1 truly begins at 91.81s, so a 91.0s start lives in segment 0.
        starts = _starts({0: 1.44, 1: 93.25})
        assert _window_segment_range(91.0, 120.0, 90, starts) == (0, 1)

    def test_window_ending_before_a_segments_content(self):
        # Nominal arithmetic wants segment 1 for a 90.5s end, but segment 1's
        # content starts later — segment 0 covers the whole window.
        starts = _starts({0: 1.44, 1: 93.25})
        assert _window_segment_range(30.0, 90.5, 90, starts) == (0, 0)

    def test_exact_boundary_end_is_exclusive(self):
        assert _window_segment_range(0.0, 90.0, 90, _starts({})) == (0, 0)

    def test_range_never_walks_below_zero(self):
        starts = _starts({0: 5.0})
        assert _window_segment_range(1.0, 20.0, 90, starts) == (0, 0)
