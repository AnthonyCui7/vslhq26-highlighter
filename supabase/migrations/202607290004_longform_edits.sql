-- Long-form edits: one row per stitched long-form video version. Version 1 is
-- the initial pipeline run; the revision loop appends version 2, 3, ... so a
-- project keeps its full edit history. Clips stay in public.clips; this table
-- owns the assembled long-form outputs.

create table public.longform_edits (
  id uuid primary key default gen_random_uuid(),
  project_id uuid not null references public.projects(id) on delete cascade,
  version integer not null check (version >= 1),
  status text not null default 'rendered'
    check (status in ('rendered', 'failed')),
  video_url text,
  thumbnail_url text,
  duration_seconds numeric(12,3),
  segments jsonb not null default '[]'::jsonb,
  editor jsonb,
  revision jsonb,
  metadata jsonb not null default '{}'::jsonb,
  created_at timestamptz not null default now(),
  unique (project_id, version)
);

comment on table public.longform_edits is
  'Stitched long-form video versions for a project. Version 1 is the initial run; revisions append higher versions.';
comment on column public.longform_edits.segments is
  'The stitched segment windows, chronological: [{chunk_index?, title?, start_seconds, end_seconds}, ...].';
comment on column public.longform_edits.editor is
  'Pass-2 editor metadata for the version that produced this cut (notes, arithmetic, kept/dropped counts).';
comment on column public.longform_edits.revision is
  'For revised versions: {request, notes, tools_used}. Null on version 1.';

-- Writes go through the worker/API (service_role). Users can read their own rows.
alter table public.longform_edits enable row level security;
alter table public.longform_edits force row level security;

revoke all on table public.longform_edits from anon, authenticated;
grant all on table public.longform_edits to service_role;
grant select on table public.longform_edits to authenticated;

create policy longform_edits_owner_select on public.longform_edits
  for select to authenticated
  using (exists (
    select 1 from public.projects p
    where p.id = project_id and p.user_id = auth.uid()
  ));

-- Long-form edit delete (direct or via project cascade) -> enqueue its stored
-- video and thumbnail, mirroring the clips cleanup trigger.
create or replace function public.enqueue_longform_edit_media_cleanup()
returns trigger
language plpgsql
security definer
set search_path = public
as $$
declare
  render jsonb := old.metadata -> 'render';
begin
  if render ->> 'bucket' is not null and render ->> 'storage_path' is not null then
    insert into public.media_cleanup_jobs (origin_table, origin_id, store, bucket, object_key)
    values ('longform_edits', old.id::text, 'supabase_storage', render ->> 'bucket', render ->> 'storage_path');
  end if;
  if render ->> 'bucket' is not null and render ->> 'thumbnail_storage_path' is not null then
    insert into public.media_cleanup_jobs (origin_table, origin_id, store, bucket, object_key)
    values ('longform_edits', old.id::text, 'supabase_storage', render ->> 'bucket', render ->> 'thumbnail_storage_path');
  end if;
  return old;
end;
$$;

create trigger longform_edits_enqueue_media_cleanup
  before delete on public.longform_edits
  for each row execute function public.enqueue_longform_edit_media_cleanup();
