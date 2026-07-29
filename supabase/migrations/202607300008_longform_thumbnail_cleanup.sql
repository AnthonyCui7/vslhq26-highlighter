-- Long-form runs now generate thumbnail concept variants alongside the
-- midpoint frame. Their storage paths live under render.thumbnails.variants;
-- extend the delete trigger so every variant is enqueued for cleanup.

create or replace function public.enqueue_longform_edit_media_cleanup()
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
  if render ->> 'storage_path' is not null then
    insert into public.media_cleanup_jobs (origin_table, origin_id, store, bucket, object_key)
    values ('longform_edits', old.id::text, 'supabase_storage', render ->> 'bucket', render ->> 'storage_path');
  end if;
  if render ->> 'thumbnail_storage_path' is not null then
    insert into public.media_cleanup_jobs (origin_table, origin_id, store, bucket, object_key)
    values ('longform_edits', old.id::text, 'supabase_storage', render ->> 'bucket', render ->> 'thumbnail_storage_path');
  end if;
  if jsonb_typeof(render -> 'thumbnails' -> 'variants') = 'array' then
    for object_key in
      select variant ->> 'storage_path'
      from jsonb_array_elements(render -> 'thumbnails' -> 'variants') as variant
    loop
      if object_key is not null then
        insert into public.media_cleanup_jobs (origin_table, origin_id, store, bucket, object_key)
        values ('longform_edits', old.id::text, 'supabase_storage', render ->> 'bucket', object_key);
      end if;
    end loop;
  end if;
  return old;
end;
$$;
