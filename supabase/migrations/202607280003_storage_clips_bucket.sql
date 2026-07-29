-- Rendered clip media (MP4s, preview frames) is served from the public
-- `clips` Storage bucket. The worker uploads with the service role, which
-- bypasses storage RLS; no storage.objects policies are created, so client
-- keys can neither write nor list -- playback goes through the public object
-- URLs only.

insert into storage.buckets (id, name, public)
values ('clips', 'clips', true)
on conflict (id) do update set public = true;
