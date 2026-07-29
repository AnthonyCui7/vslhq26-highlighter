-- Short-form clips render in two shapes: the 16:9 source cut (video_url) and
-- the blur-pad 9:16 vertical. Surface the vertical as its own column so both
-- are directly viewable per row.

alter table public.clips add column vertical_url text;

comment on column public.clips.vertical_url is
  'Blur-pad 9:16 vertical render of the clip, when short-form auto-reframe produced one.';
