-- Base schema: a user owns projects; a project is one source video (captured
-- livestream, platform VOD, or upload) and holds its transcript chunks and
-- highlight clips.

create extension if not exists pgcrypto;

create table public.projects (
  id uuid primary key default gen_random_uuid(),
  user_id uuid references auth.users(id) on delete cascade,
  name text not null,
  source_type text not null check (source_type in ('livestream', 'video', 'upload')),
  source_url text not null,
  status text not null default 'created'
    check (status in ('created', 'ingesting', 'ready', 'failed', 'stopping', 'cancelled')),
  error text,
  min_clip_score numeric not null default 0
    check (min_clip_score between 0 and 1),
  instance_id text,
  metadata jsonb not null default '{}'::jsonb,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

comment on table public.projects is
  'One source video (captured livestream, platform VOD, or upload) owned by a user; parent of transcript chunks and clips.';
comment on column public.projects.user_id is
  'Owner. Nullable until user-facing creation exists; worker-created rows have no owner yet.';
comment on column public.projects.source_type is
  'livestream = captured live; video = ingested from a platform VOD/video URL; upload = user-uploaded file.';
comment on column public.projects.status is
  'created (backend inserted row) -> ingesting (worker attached) -> ready | failed. Cancel: backend sets stopping; the worker finishes as cancelled. Backend force-cancel sets cancelled directly.';
comment on column public.projects.min_clip_score is
  'Minimum LLM clip score (0..1) the worker will render and store; clips scoring below it are dropped. 0 keeps every detected clip. Written by the backend at project creation.';
comment on column public.projects.instance_id is
  'Identifier of the worker launched for this project. Written by the backend; used for force-cancel/terminate.';

create table public.transcript_chunks (
  id uuid primary key default gen_random_uuid(),
  project_id uuid not null references public.projects(id) on delete cascade,
  chunk_index integer not null check (chunk_index >= 0),
  start_seconds numeric(12,3) not null,
  end_seconds numeric(12,3) not null,
  transcript text not null default '',
  words jsonb not null default '[]'::jsonb,
  metadata jsonb not null default '{}'::jsonb,
  created_at timestamptz not null default now(),
  unique (project_id, chunk_index)
);

comment on table public.transcript_chunks is
  'Timestamped transcript for one ingest chunk of a project''s source video. words carries per-word absolute timings.';

create table public.clips (
  id uuid primary key default gen_random_uuid(),
  project_id uuid not null references public.projects(id) on delete cascade,
  title text not null,
  description text,
  start_seconds numeric(12,3) not null,
  end_seconds numeric(12,3) not null,
  score numeric,
  video_url text,
  status text not null default 'detected'
    check (status in ('detected', 'rendered', 'failed')),
  metadata jsonb not null default '{}'::jsonb,
  created_at timestamptz not null default now()
);

comment on table public.clips is
  'Highlight clips found in a project''s source video. video_url is set once a clip is rendered.';
comment on column public.clips.score is 'Highlight strength used for ranking.';

create index projects_user_idx on public.projects(user_id);
create index transcript_chunks_project_time_idx on public.transcript_chunks(project_id, start_seconds);
create index clips_project_time_idx on public.clips(project_id, start_seconds);

create or replace function public.set_updated_at()
returns trigger as $$
begin
  new.updated_at = now();
  return new;
end;
$$ language plpgsql;

create trigger projects_set_updated_at
  before update on public.projects
  for each row execute function public.set_updated_at();

-- Writes go through the worker/API (service_role). Users can read their own rows.
alter table public.projects enable row level security;
alter table public.projects force row level security;
alter table public.transcript_chunks enable row level security;
alter table public.transcript_chunks force row level security;
alter table public.clips enable row level security;
alter table public.clips force row level security;

revoke all on table public.projects from anon, authenticated;
revoke all on table public.transcript_chunks from anon, authenticated;
revoke all on table public.clips from anon, authenticated;

grant all on table public.projects to service_role;
grant all on table public.transcript_chunks to service_role;
grant all on table public.clips to service_role;

grant select on table public.projects to authenticated;
grant select on table public.transcript_chunks to authenticated;
grant select on table public.clips to authenticated;

create policy projects_owner_select on public.projects
  for select to authenticated
  using (user_id = auth.uid());

create policy transcript_chunks_owner_select on public.transcript_chunks
  for select to authenticated
  using (exists (
    select 1 from public.projects p
    where p.id = project_id and p.user_id = auth.uid()
  ));

create policy clips_owner_select on public.clips
  for select to authenticated
  using (exists (
    select 1 from public.projects p
    where p.id = project_id and p.user_id = auth.uid()
  ));
