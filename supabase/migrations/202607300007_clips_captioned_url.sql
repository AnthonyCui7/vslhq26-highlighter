-- Short-form clips can ship a third render: the blur-pad vertical with
-- burned-in captions. Store its public URL beside video_url/vertical_url and
-- teach the delete trigger about the extra storage object.

alter table public.clips add column captioned_url text;

comment on column public.clips.captioned_url is
  'Public URL of the captioned 9:16 vertical render, when burned captions were generated.';

create or replace function public.enqueue_clip_media_cleanup()
returns trigger
language plpgsql
security definer
set search_path = public
as $$
declare
  render jsonb := old.metadata -> 'render';
  object_key text;
begin
  if render ->> 'bucket' is null then
    return old;
  end if;
  foreach object_key in array array[
    render ->> 'storage_path',
    render ->> 'thumbnail_storage_path',
    render ->> 'vertical_storage_path',
    render ->> 'captioned_storage_path'
  ]
  loop
    if object_key is not null then
      insert into public.media_cleanup_jobs (origin_table, origin_id, store, bucket, object_key)
      values ('clips', old.id::text, 'supabase_storage', render ->> 'bucket', object_key);
    end if;
  end loop;
  return old;
end;
$$;
