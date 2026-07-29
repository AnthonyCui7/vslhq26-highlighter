# Highlighter.Web

Blazor Web App (Interactive Server, .NET 10) + Tailwind CSS v4 frontend for Highlighter.
Static replica of the Claude Design mockup (`Highlighter v3.dc.html`) — no backend, no
Supabase; all data is in-memory sample data.

## Run

```sh
export PATH="$HOME/.dotnet:$PATH"   # .NET SDK is user-local on this machine
dotnet run
```

`wwwroot/css/app.css` is committed prebuilt, so `dotnet run` works without npm.

## Styling

Tailwind v4, CSS-first config in `Styles/app.css` (design palette + fonts as `@theme`
tokens). Rebuild after editing components or styles:

```sh
npm install        # once
npm run css:build  # or css:watch during development
```

## Layout

- `Components/Pages/Home.razor` — root screen, swaps Projects / Project views and
  layers the Editor overlay + New Project modal (matches the design's single-component
  state machine).
- `Components/Studio/*` — TopBar, ProjectsView, ProjectDetailView, EditorOverlay
  (MediaBin · ProgramMonitor · RightPanel · TimelinePanel), NewProjectModal.
- `Models/StudioState.cs` — UI state + actions (scoped DI service, mirrors the mockup's
  `state`/`renderVals` handlers).
- `Models/SampleData.cs` — the mockup's sample projects/clips/captions/tracks, including
  the deterministic waveform generator.

Static styles are Tailwind utility classes; data-driven values (slider positions, track
block geometry, waveform bars, score badge opacity) are inline `style=` attributes, same
split as the original template.

## Later

- Backend: `apps/api` (ASP.NET) — this app's render mode is Interactive Server, so it
  can host or call an ASP.NET API without restructuring; swap `SampleData` for real
  queries when that lands.
