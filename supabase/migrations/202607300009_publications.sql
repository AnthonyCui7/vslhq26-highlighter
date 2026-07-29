-- Social publications: one row per platform a clip or long-form edit was
-- published to through the publish CLI. Clip rows are referenced by filename
-- in metadata (clip inserts return no id to the worker); long-form rows by
-- version.

create table public.publications (
  id uuid primary key default gen_random_uuid(),
  project_id uuid not null references public.projects(id) on delete cascade,
  target text not null check (target in ('clip', 'longform')),
  platform text not null,
  url text,
  title text,
  metadata jsonb not null default '{}'::jsonb,
  created_at timestamptz not null default now()
);

comment on table public.publications is
  'Social posts published from a project: one row per platform per publish.';
comment on column public.publications.metadata is
  'Publish details: {filename? , longform_version?, thumbnail_bucket?, thumbnail_storage_path?, post_text?}.';

create index publications_project_idx on public.publications (project_id);

-- Writes go through the worker/API (service_role). Users can read their own rows.
alter table public.publications enable row level security;
alter table public.publications force row level security;

revoke all on table public.publications from anon, authenticated;
grant all on table public.publications to service_role;
grant select on table public.publications to authenticated;

create policy publications_owner_select on public.publications
  for select to authenticated
  using (exists (
    select 1 from public.projects p
    where p.id = project_id and p.user_id = auth.uid()
  ));

-- A publish can upload a custom thumbnail object; enqueue it when the row
-- goes away. Two rows pointing at the same object would enqueue it while the
-- other still links it — acceptable for a user-supplied one-off upload.
create or replace function public.enqueue_publication_media_cleanup()
returns trigger
language plpgsql
security definer
set search_path = public
as $$
begin
  if old.metadata ->> 'thumbnail_bucket' is not null
     and old.metadata ->> 'thumbnail_storage_path' is not null then
    insert into public.media_cleanup_jobs (origin_table, origin_id, store, bucket, object_key)
    values ('publications', old.id::text, 'supabase_storage',
            old.metadata ->> 'thumbnail_bucket', old.metadata ->> 'thumbnail_storage_path');
  end if;
  return old;
end;
$$;

create trigger publications_enqueue_media_cleanup
  before delete on public.publications
  for each row execute function public.enqueue_publication_media_cleanup();
