// Studio program-monitor interop. Kept tiny: Blazor owns all state; this file
// only touches the <video> element where the DOM API is the only way in.

export function setPlaying(video, playing) {
  if (!video) return;
  if (playing) {
    // Autoplay policy can reject play() outside a user gesture; the UI stays
    // truthful via the element's own play/pause events.
    video.play().catch(() => {});
  } else {
    video.pause();
  }
}

export function seek(video, seconds) {
  if (!video || Number.isNaN(seconds)) return;
  video.currentTime = Math.max(0, seconds);
}

export function getCurrentTime(video) {
  return video ? video.currentTime : 0;
}

export function getDuration(video) {
  return video && Number.isFinite(video.duration) ? video.duration : 0;
}

export function setRate(video, rate) {
  if (video) video.playbackRate = rate;
}

export function setVolume(video, volume) {
  if (video) video.volume = Math.min(1, Math.max(0, volume));
}

// EDL-aware playback loop: skips cut regions and applies per-segment speed.
// segments: [{ start, end, speed }] in SOURCE seconds, chronological.
// dotnet.invokeMethodAsync('OnPlayhead', sourceTime) fires ~4×/second.
const watchers = new WeakMap();

export function watch(video, dotnet, segments) {
  if (!video) return;
  unwatch(video);
  const state = { dotnet, segments: segments ?? [], last: -1 };

  const onTime = () => {
    const t = video.currentTime;
    if (state.segments.length > 0) {
      const seg = state.segments.find(s => t >= s.start - 0.05 && t < s.end);
      if (!seg) {
        const next = state.segments.find(s => s.start >= t);
        if (next) {
          video.currentTime = next.start;
          if (next.speed) video.playbackRate = next.speed;
        } else {
          video.pause();
          video.currentTime = state.segments[0]?.start ?? 0;
        }
        return;
      }
      const speed = seg.speed || 1;
      if (Math.abs(video.playbackRate - speed) > 0.01) video.playbackRate = speed;
    }
    if (Math.abs(t - state.last) >= 0.2) {
      state.last = t;
      state.dotnet.invokeMethodAsync('OnPlayhead', t).catch(() => {});
    }
  };

  state.handler = onTime;
  video.addEventListener('timeupdate', onTime);
  watchers.set(video, state);
}

export function updateSegments(video, segments) {
  const state = watchers.get(video);
  if (state) state.segments = segments ?? [];
}

export function unwatch(video) {
  const state = video && watchers.get(video);
  if (state) {
    video.removeEventListener('timeupdate', state.handler);
    watchers.delete(video);
  }
}
