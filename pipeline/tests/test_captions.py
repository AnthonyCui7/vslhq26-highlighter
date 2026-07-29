from highlighter_pipeline.captions import build_whisper_transcript


def word(start, end, text, punctuated=None):
    return {
        "word": text,
        "punctuated_word": punctuated or text,
        "absolute_start": start,
        "absolute_end": end,
    }


class TestBuildWhisperTranscript:
    def test_times_relative_to_clip(self):
        transcript = build_whisper_transcript(
            words=[word(10.5, 10.9, "hey", "Hey"), word(11.0, 11.4, "there", "there.")],
            clip_start_seconds=10.0,
            clip_end_seconds=20.0,
        )
        segment = transcript["segments"][0]
        assert segment["words"] == [
            {"word": "Hey", "start": 0.5, "end": 0.9},
            {"word": "there.", "start": 1.0, "end": 1.4},
        ]
        assert segment["text"] == "Hey there."
        assert segment["start"] == 0.5
        assert segment["end"] == 1.4

    def test_words_outside_window_dropped(self):
        transcript = build_whisper_transcript(
            words=[
                word(8.0, 9.0, "before"),
                word(10.2, 10.8, "inside"),
                word(21.0, 22.0, "after"),
            ],
            clip_start_seconds=10.0,
            clip_end_seconds=20.0,
        )
        assert [w["word"] for w in transcript["segments"][0]["words"]] == ["inside"]

    def test_boundary_word_clamped(self):
        transcript = build_whisper_transcript(
            words=[word(9.8, 10.4, "straddle"), word(19.7, 20.6, "tail")],
            clip_start_seconds=10.0,
            clip_end_seconds=20.0,
        )
        words = [w for segment in transcript["segments"] for w in segment["words"]]
        assert words[0]["start"] == 0.0  # clamped, never negative
        assert words[-1]["end"] == 10.0  # clamped to clip end

    def test_segments_split_on_silence(self):
        transcript = build_whisper_transcript(
            words=[
                word(10.0, 10.5, "one"),
                word(10.6, 11.0, "two"),
                word(13.0, 13.5, "three"),  # 2.0s gap > 1.5s threshold
            ],
            clip_start_seconds=10.0,
            clip_end_seconds=20.0,
        )
        assert len(transcript["segments"]) == 2
        assert transcript["segments"][0]["text"] == "one two"
        assert transcript["segments"][1]["text"] == "three"
        assert [segment["id"] for segment in transcript["segments"]] == [0, 1]

    def test_no_words_returns_none(self):
        assert (
            build_whisper_transcript(
                words=[word(1.0, 2.0, "far")], clip_start_seconds=10.0, clip_end_seconds=20.0
            )
            is None
        )
        assert (
            build_whisper_transcript(words=[], clip_start_seconds=0.0, clip_end_seconds=5.0)
            is None
        )
