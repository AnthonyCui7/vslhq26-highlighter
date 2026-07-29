from highlighter_pipeline.thumbnails import (
    MAX_REFERENCE_FRAMES,
    THUMBNAIL_CONCEPTS_SCHEMA,
    _image_prompt,
    _reference_offsets,
)


def entry(start, end):
    return {"start_seconds": start, "end_seconds": end}


class TestConceptsSchema:
    def test_shape(self):
        assert set(THUMBNAIL_CONCEPTS_SCHEMA["required"]) == set(
            THUMBNAIL_CONCEPTS_SCHEMA["properties"]
        )
        assert THUMBNAIL_CONCEPTS_SCHEMA["additionalProperties"] is False
        item = THUMBNAIL_CONCEPTS_SCHEMA["properties"]["concepts"]["items"]
        assert set(item["required"]) == {"direction", "image_prompt", "overlay_text"}
        assert item["additionalProperties"] is False


class TestReferenceOffsets:
    def test_midpoints_in_output_timeline(self):
        # Segments cut from 100-160 and 300-340 land at 0-60 and 60-100 in the
        # stitched output; midpoints are positions there, not source times.
        offsets = _reference_offsets([entry(100, 160), entry(300, 340)])
        assert offsets == [30.0, 80.0]

    def test_thinned_to_max_frames(self):
        entries = [entry(i * 10, i * 10 + 10) for i in range(10)]
        offsets = _reference_offsets(entries)
        assert len(offsets) == MAX_REFERENCE_FRAMES
        assert offsets[0] == 5.0  # first segment's midpoint kept
        assert offsets[-1] == 95.0  # last segment's midpoint kept
        assert offsets == sorted(offsets)


class TestImagePrompt:
    def test_overlay_text_included(self):
        prompt = _image_prompt(
            {"image_prompt": "Two hosts at a desk.", "overlay_text": "HE'S BACK"},
            "The Reunion",
        )
        assert 'Render the text "HE\'S BACK"' in prompt
        assert '"The Reunion"' in prompt

    def test_no_overlay_requests_textless_image(self):
        prompt = _image_prompt(
            {"image_prompt": "Two hosts at a desk.", "overlay_text": " "}, None
        )
        assert "Do not render any text" in prompt
