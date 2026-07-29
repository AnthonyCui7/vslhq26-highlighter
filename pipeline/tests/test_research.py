from highlighter_pipeline.records import ProjectRecords
from highlighter_pipeline.render import clip_filename
from highlighter_pipeline.research import research_schema, research_system_prompt

CORE_FIELDS = {
    "creator_profile",
    "content_context",
    "target_audience",
    "inside_references",
    "recent_context",
    "thumbnail_patterns",
    "avoid",
    "sources",
}


def test_short_schema_researches_clip_craft():
    schema = research_schema("short")
    fields = set(schema["properties"])
    assert CORE_FIELDS <= fields
    assert {"successful_clip_patterns", "useful_hooks", "platform_notes"} <= fields
    assert "structure_patterns" not in fields
    assert set(schema["required"]) == fields
    assert schema["additionalProperties"] is False


def test_long_schema_researches_structure_and_retention():
    schema = research_schema("long")
    fields = set(schema["properties"])
    assert CORE_FIELDS <= fields
    assert {"structure_patterns", "pacing_and_retention", "title_patterns"} <= fields
    assert "useful_hooks" not in fields
    assert set(schema["required"]) == fields
    assert schema["additionalProperties"] is False


def test_system_prompts_specialize_per_mode():
    short_prompt = research_system_prompt("short")
    long_prompt = research_system_prompt("long")
    assert short_prompt != long_prompt
    assert "short-form vertical clips" in short_prompt
    assert "TikTok/Reels/Shorts/X" in short_prompt
    assert "retention" not in short_prompt
    assert "long-form edit" in long_prompt
    assert "retention" in long_prompt
    assert "TikTok" not in long_prompt
    # The shared framing survives in both.
    for prompt in (short_prompt, long_prompt):
        assert "Inside references" in prompt
        assert "Return ONLY one JSON object" in prompt


def test_write_research_filenames_per_mode(tmp_path):
    records = ProjectRecords(tmp_path)
    records.write_research({"creator_profile": "long fork"})
    records.write_research({"creator_profile": "short fork"}, mode="short")
    assert (tmp_path / "research.json").exists()
    assert (tmp_path / "research_short.json").exists()
    assert "long fork" in (tmp_path / "research.json").read_text()
    assert "short fork" in (tmp_path / "research_short.json").read_text()


def test_clip_filename_suffix_disambiguates_forks():
    plain = clip_filename(chunk_index=3, start_seconds=10.0, end_seconds=20.5)
    assert plain == "clip_00003_10000_20500.mp4"
    short = clip_filename(
        chunk_index=3, start_seconds=10.0, end_seconds=20.5, suffix="_short"
    )
    long = clip_filename(
        chunk_index=3, start_seconds=10.0, end_seconds=20.5, suffix="_long"
    )
    assert short == "clip_00003_10000_20500_short.mp4"
    assert long == "clip_00003_10000_20500_long.mp4"
    assert len({plain, short, long}) == 3
