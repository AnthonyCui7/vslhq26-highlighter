import json

import pytest

from highlighter_pipeline.publish import (
    _multipart_body,
    _parse_platforms,
    _pick_clip_media,
    _resolve_thumbnail,
    _result_url,
)


class TestResolveThumbnail:
    def test_newest_version_variants_win(self, tmp_path):
        rows = [
            {
                "version": 1,
                "metadata": {
                    "render": {
                        "thumbnails": {"variants": [{"index": 2, "url": "https://old/2.png"}]}
                    }
                },
            },
            {
                "version": 2,
                "metadata": {
                    "render": {
                        "thumbnails": {"variants": [{"index": 2, "url": "https://new/2.png"}]}
                    }
                },
            },
        ]
        path = tmp_path / "longform_edits.jsonl"
        path.write_text("".join(json.dumps(row) + "\n" for row in rows))
        url, upload = _resolve_thumbnail(
            "2", project_dir=tmp_path, project_id="p", db=None, dry_run=True
        )
        assert url == "https://new/2.png"
        assert upload is None

    def test_falls_back_to_older_version_with_variants(self, tmp_path):
        rows = [
            {
                "version": 1,
                "metadata": {
                    "render": {
                        "thumbnails": {"variants": [{"index": 1, "url": "https://v1/1.png"}]}
                    }
                },
            },
            {"version": 2, "metadata": {"render": {}}},
        ]
        path = tmp_path / "longform_edits.jsonl"
        path.write_text("".join(json.dumps(row) + "\n" for row in rows))
        url, _ = _resolve_thumbnail(
            "1", project_dir=tmp_path, project_id="p", db=None, dry_run=True
        )
        assert url == "https://v1/1.png"


class TestParsePlatforms:
    def test_valid_list_deduped_in_order(self):
        assert _parse_platforms("youtube, tiktok,youtube") == ["youtube", "tiktok"]

    def test_case_insensitive(self):
        assert _parse_platforms("TikTok,X,Instagram") == ["tiktok", "x", "instagram"]

    def test_unknown_platform_rejected(self):
        with pytest.raises(RuntimeError, match="Unknown platform 'facebook'"):
            _parse_platforms("youtube,facebook")

    def test_x_alone_rejected(self):
        with pytest.raises(RuntimeError, match="promo post"):
            _parse_platforms("x")

    def test_x_with_video_platform_ok(self):
        assert _parse_platforms("x,youtube") == ["x", "youtube"]

    def test_empty_rejected(self):
        with pytest.raises(RuntimeError, match="No platforms"):
            _parse_platforms(" , ")


class TestPickClipMedia:
    RENDER = {
        "local_path": "clips/c.mp4",
        "vertical_path": "clips/c_vertical.mp4",
        "captioned_path": "clips/c_vertical_captions.mp4",
    }

    def test_captioned_preferred(self):
        path, label = _pick_clip_media(self.RENDER, plain=False)
        assert path == "clips/c_vertical_captions.mp4"
        assert label == "captioned vertical"

    def test_plain_skips_captioned(self):
        path, label = _pick_clip_media(self.RENDER, plain=True)
        assert path == "clips/c_vertical.mp4"
        assert label == "vertical"

    def test_falls_back_through_the_chain(self):
        path, _ = _pick_clip_media({"local_path": "clips/c.mp4"}, plain=False)
        assert path == "clips/c.mp4"
        assert _pick_clip_media({}, plain=False) == (None, "16:9 clip")


class TestMultipartBody:
    def test_repeated_platform_fields(self, tmp_path):
        video = tmp_path / "v.mp4"
        video.write_bytes(b"12345")
        body, content_type = _multipart_body(
            [("title", "T"), ("user", "u"), ("platform[]", "tiktok"), ("platform[]", "youtube")],
            file_path=video,
        )
        boundary = content_type.split("boundary=")[1]
        assert body.count(b"--" + boundary.encode()) == 6  # 5 parts + closer
        assert body.count(b'name="platform[]"') == 2
        assert b'name="video"; filename="v.mp4"' in body
        assert b"12345" in body
        assert body.endswith(b"--" + boundary.encode() + b"--\r\n")

    def test_fields_only(self):
        body, _ = _multipart_body([("title", "hello"), ("user", "u")])
        assert b'name="title"\r\n\r\nhello\r\n' in body
        assert b'name="video"' not in body


class TestResultUrl:
    def test_url_key_variants(self):
        assert _result_url({"url": "https://a"}) == "https://a"
        assert _result_url({"post_url": "https://b"}) == "https://b"
        assert _result_url({"video_url": "https://c"}) == "https://c"
        assert _result_url({"success": True}) is None
