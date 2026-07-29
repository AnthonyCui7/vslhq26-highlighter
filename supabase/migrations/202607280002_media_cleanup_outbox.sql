-- Media cleanup outbox: deleting rows in Postgres must eventually delete the
-- media they point at (S3 source segments, Supabase Storage clip MP4s).
-- Triggers snapshot the pointers BEFORE DELETE; the worker's cleanup command
-- drains pending jobs and performs the actual object deletions.

create table public.media_cleanup_jobs (
  id uuid primary key default gen_random_uuid(),
  origin_table text not null,
  origin_id text not null,
  store text not null check (store in ('s3', 'supabase_storage')),
  bucket text not null,
  object_key text not null,
  is_prefix boolean not null default false,
  status text not null default 'pending'
    check (status in ('pending', 'succeeded', 'failed')),
  attempts integer not null default 0,
  last_error text,
  created_at timestamptz not null default now(),
  completed_at timestamptz
);

comment on table public.media_cleanup_jobs is
  'Outbox of media objects to delete after their DB rows are gone. Drained by the worker cleanup command.';
comment on column public.media_cleanup_jobs.is_prefix is
  'When true, object_key is a prefix and everything under it should be deleted.';

create index media_cleanup_jobs_pending_idx
  on public.media_cleanup_jobs (created_at) where status = 'pending';

alter table public.media_cleanup_jobs enable row level security;
alter table public.media_cleanup_jobs force row level security;
revoke all on table public.media_cleanup_jobs from anon, authenticated;
grant all on table public.media_cleanup_jobs to service_role;

-- Project delete -> enqueue its S3 source archive. Clip rows removed by the
-- cascade fire their own trigger, so clips are not collected here.
create or replace function public.enqueue_project_media_cleanup()
returns trigger
language plpgsql
security definer
set search_path = public
as $$
declare
  archive jsonb := old.metadata -> 'source_archive';
  segment_key text;
  has_keys boolean;
begin
  if archive is null then
    return old;
  end if;

  has_keys := jsonb_typeof(archive -> 'segment_keys') = 'array'
    and jsonb_array_length(archive -> 'segment_keys') > 0;

  if has_keys then
    for segment_key in
      select jsonb_array_elements_text(archive -> 'segment_keys')
    loop
      insert into public.media_cleanup_jobs (origin_table, origin_id, store, bucket, object_key)
      values ('projects', old.id::text, 's3', archive ->> 'bucket', segment_key);
    end loop;
  elsif archive ->> 'prefix' is not null then
    -- Projects that recorded only a prefix: delete everything under it.
    insert into public.media_cleanup_jobs (origin_table, origin_id, store, bucket, object_key, is_prefix)
    values ('projects', old.id::text, 's3', archive ->> 'bucket', archive ->> 'prefix', true);
  end if;

  return old;
end;
$$;

create trigger projects_enqueue_media_cleanup
  before delete on public.projects
  for each row execute function public.enqueue_project_media_cleanup();

-- Clip delete (direct or via project cascade) -> enqueue its rendered MP4.
create or replace function public.enqueue_clip_media_cleanup()
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
    values ('clips', old.id::text, 'supabase_storage', render ->> 'bucket', render ->> 'storage_path');
  end if;
  return old;
end;
$$;

create trigger clips_enqueue_media_cleanup
  before delete on public.clips
  for each row execute function public.enqueue_clip_media_cleanup();
