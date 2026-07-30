using Highlighter.Web.Services;

namespace Highlighter.Web.Models;

/// <summary>One message in the studio agent conversation.</summary>
public record AgentChatMessage(string Role, string Text);

/// <summary>
/// UI state for the studio — the same state machine the static design used
/// (view, editorOpen, mode, binTab, panel, tool, activeCaption, capStyle,
/// modalOpen, playing, sortOpen, sortBy, snap) plus the thumbnail picker,
/// per-clip delivery options, and the agent chat — now backed by live API data:
/// Guid-keyed projects/clips, async loads, and a processing poll loop.
/// Mutations go through Set(...) which raises Changed; components subscribe via
/// StudioComponent.
/// </summary>
public class StudioState : IDisposable
{
    private readonly ApiClient api;
    private readonly CancellationTokenSource _cts = new();
    private bool _pollStarted;

    public StudioState(ApiClient api)
    {
        this.api = api;
        Edit = new EditorSession(api, () => Changed?.Invoke());
    }

    /// <summary>The timeline editor's working state (doc, selection, undo,
    /// autosave, export) — loaded when the editor overlay opens.</summary>
    public EditorSession Edit { get; }

    public string View { get; private set; } = "projects";
    public bool EditorOpen { get; private set; }
    public string Mode { get; private set; } = "short";
    public string BinTab { get; private set; } = "media";
    public string Panel { get; private set; } = "inspector";
    public string Tool { get; private set; } = "select";
    public int ActiveCaption { get; private set; } = 1;
    public string CapStyle { get; private set; } = "boxed";
    public bool ModalOpen { get; private set; }
    public bool Playing { get; private set; }
    public bool SortOpen { get; private set; }
    public string SortBy { get; private set; } = "score";
    public bool Snap { get; private set; } = true;

    public event Action? Changed;
    private void Set(Action mutate)
    {
        mutate();
        Changed?.Invoke();
    }

    // ---- live data ------------------------------------------------------- //

    public IReadOnlyList<ProjectSummaryDto>? Projects { get; private set; }
    public string? ProjectsError { get; private set; }
    public Guid? ProjectId { get; private set; }
    public ProjectDetailDto? Detail { get; private set; }
    public string? DetailError { get; private set; }
    public string Search { get; set; } = "";
    public bool CreateBusy { get; private set; }
    public string? CreateError { get; private set; }
    public ClipDto? ActiveClip { get; private set; }
    public LongformEditDto? ActiveLongform { get; private set; }

    public bool IsProjects => View == "projects";
    public bool IsProject => View == "project";

    public ProjectSummaryDto? Project =>
        Detail?.Project ?? Projects?.FirstOrDefault(p => p.Id == ProjectId);
    public string ProjectTitle => Project?.Name ?? "";

    /// <summary>Latest playable long-form cut (API orders version.desc).</summary>
    public LongformEditDto? LatestLongform => Detail?.LongformEdits
        .FirstOrDefault(edit => edit.Status == "rendered" && edit.VideoUrl is not null);

    public string LongformTitle =>
        LatestLongform?.Title ?? (ProjectTitle.Length > 0 ? ProjectTitle : "Final cut");
    public string ActiveClipTitle => ActiveClip?.Title ?? LongformTitle;

    public IEnumerable<ClipDto> SortedClips => SortBy == "score"
        ? (Detail?.Clips ?? []).OrderByDescending(clip => clip.Score ?? 0)
        : (Detail?.Clips ?? []).OrderBy(clip => clip.StartSeconds);

    public IEnumerable<ProjectSummaryDto> FilteredProjects =>
        (Projects ?? []).Where(p => Search.Trim().Length == 0
            || p.Name.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>What the program monitor plays: the clean clip render (vertical
    /// preferred — editor captions layer on top), else the long-form cut.</summary>
    public string? ActiveMediaUrl => ActiveClip is { } clip
        ? clip.VerticalUrl ?? clip.VideoUrl ?? clip.CaptionedUrl
        : ActiveLongform?.VideoUrl;
    public string? ActivePosterUrl => ActiveClip?.ThumbnailUrl ?? ActiveLongform?.ThumbnailUrl;

    public string SortLabel => SortBy == "score" ? "Score" : "Timeline";
    public string PlayIcon => Playing ? "❚❚" : "▶";
    public bool ShowTargetLength => Mode is "long" or "both";

    // ---- navigation ------------------------------------------------------ //

    public void GoProjects()
    {
        Set(() => { View = "projects"; EditorOpen = false; SortOpen = false; });
        _ = GuardedAsync(RefreshProjectsQuietAsync);
    }

    public void OpenProject(Guid id)
    {
        Set(() =>
        {
            View = "project";
            ProjectId = id;
            Detail = null;
            DetailError = null;
            SortOpen = false;
        });
        _ = GuardedAsync(() => LoadDetailAsync(id));
    }

    public void OpenClip(ClipDto clip)
    {
        Set(() =>
        {
            EditorOpen = true;
            ActiveClip = clip;
            ActiveLongform = null;
            AgentContext = "short";
            Playing = false;
        });
        if (ProjectId is { } id)
        {
            Edit.Bind(id);
            _ = GuardedAsync(() => Edit.LoadAsync(id, clip.Id));
        }
    }

    public void OpenEditorLong()
    {
        Set(() =>
        {
            EditorOpen = true;
            ActiveClip = null;
            ActiveLongform = LatestLongform;
            AgentContext = "long";
            Playing = false;
        });
        if (ProjectId is { } id)
        {
            Edit.Bind(id);
            _ = GuardedAsync(() => Edit.LoadAsync(id, null));
        }
    }

    public void CloseEditor()
    {
        Edit.Close();
        Set(() => { EditorOpen = false; Playing = false; });
        _ = GuardedAsync(RefreshDetailAsync);
    }
    public void ToggleSort() => Set(() => SortOpen = !SortOpen);
    public void SortByScore() => Set(() => { SortBy = "score"; SortOpen = false; });
    public void SortByTimeline() => Set(() => { SortBy = "timeline"; SortOpen = false; });
    public void SetBinTab(string key) => Set(() => BinTab = key);
    public void SetPanel(string key) => Set(() => Panel = key);
    public void SetTool(string key) => Set(() => Tool = key);
    public void SelectCaption(int i) => Set(() => ActiveCaption = i);
    public void SetCapStyle(string key) => Set(() => CapStyle = key);
    public void ToggleSnap() => Set(() => Snap = !Snap);
    public void TogglePlay() => Set(() => Playing = !Playing);
    public void SetPlaying(bool playing)
    {
        if (Playing != playing) Set(() => Playing = playing);
    }
    public void OpenNewJob() => Set(() => { ModalOpen = true; CreateError = null; });
    public void CloseNewJob() => Set(() => ModalOpen = false);
    public void SetMode(string mode) => Set(() => Mode = mode);

    // ---- loads + polling ------------------------------------------------- //

    public async Task LoadProjectsAsync()
    {
        Set(() => { Projects = null; ProjectsError = null; });
        try
        {
            var list = await api.ListProjectsAsync(_cts.Token);
            Set(() => Projects = list);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ApiException exception)
        {
            Set(() => ProjectsError = exception.UserMessage);
        }
        catch (HttpRequestException)
        {
            Set(() => ProjectsError = "Can't reach the Highlighter API — is it running on :5199?");
        }
        EnsurePolling();
    }

    private async Task LoadDetailAsync(Guid id)
    {
        try
        {
            var detail = await api.GetProjectAsync(id, _cts.Token);
            if (ProjectId == id)
                Set(() =>
                {
                    Detail = detail;
                    HydrateThumbnails();
                });
        }
        catch (OperationCanceledException)
        {
        }
        catch (ApiException exception)
        {
            if (ProjectId == id) Set(() => DetailError = exception.UserMessage);
        }
        catch (HttpRequestException)
        {
            if (ProjectId == id)
                Set(() => DetailError = "Can't reach the Highlighter API — is it running on :5199?");
        }
        EnsurePolling();
    }

    /// <summary>Quiet re-fetch of the open project (poll ticks, job completions).
    /// Never clears Detail — the UI keeps rendering the last good data.</summary>
    public async Task RefreshDetailAsync()
    {
        if (ProjectId is not { } id) return;
        try
        {
            var detail = await api.GetProjectAsync(id, _cts.Token);
            if (ProjectId == id)
                Set(() =>
                {
                    Detail = detail;
                    DetailError = null;
                    HydrateThumbnails();
                    if (ActiveClip is { } open)
                        ActiveClip = detail.Clips.FirstOrDefault(c => c.Id == open.Id) ?? open;
                    if (ActiveLongform is not null)
                        ActiveLongform = LatestLongform ?? ActiveLongform;
                });
        }
        catch (Exception exception) when (
            exception is ApiException or HttpRequestException or OperationCanceledException)
        {
        }
    }

    public async Task RefreshProjectsQuietAsync()
    {
        try
        {
            var list = await api.ListProjectsAsync(_cts.Token);
            Set(() => { Projects = list; ProjectsError = null; });
        }
        catch (Exception exception) when (
            exception is ApiException or HttpRequestException or OperationCanceledException)
        {
        }
    }

    public async Task CreateProjectAsync(string sourceUrl, string? instructions, string? targetMinutes)
    {
        if (CreateBusy) return;
        Set(() => { CreateBusy = true; CreateError = null; });
        try
        {
            var request = new CreateProjectRequest(
                sourceUrl.Trim(),
                Mode,
                Instructions: string.IsNullOrWhiteSpace(instructions) ? null : instructions.Trim(),
                TargetMinutes: ShowTargetLength ? targetMinutes : null);
            var created = await api.CreateProjectAsync(request, _cts.Token);
            Set(() =>
            {
                Projects = [created, .. (Projects ?? [])];
                ModalOpen = false;
                CreateBusy = false;
            });
            EnsurePolling();
        }
        catch (ApiException exception)
        {
            Set(() => { CreateError = exception.UserMessage; CreateBusy = false; });
        }
        catch (HttpRequestException)
        {
            Set(() =>
            {
                CreateError = "Can't reach the Highlighter API — is it running on :5199?";
                CreateBusy = false;
            });
        }
    }

    private void EnsurePolling()
    {
        if (_pollStarted) return;
        _pollStarted = true;
        _ = GuardedAsync(() => PollLoopAsync(_cts.Token));
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(4));
        while (await timer.WaitForNextTickAsync(ct))
        {
            if (EditorOpen) continue; // don't churn renders during playback
            try
            {
                if (View == "project" && ProjectId is { } id
                    && Detail?.Project.Processing != false)
                {
                    await RefreshDetailAsync();
                }
                else if (View == "projects" && Projects?.Any(p => p.Processing) == true)
                {
                    await RefreshProjectsQuietAsync();
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Transient — next tick retries.
            }
        }
    }

    /// <summary>Fire-and-forget wrapper: an unobserved exception would tear the
    /// circuit down, so nothing escapes.</summary>
    private static async Task GuardedAsync(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---- Long-form thumbnails ------------------------------------------ //

    public List<ThumbVariant> Thumbnails { get; } = [];
    public int SelectedThumb { get; private set; }
    public bool ThumbPromptOpen { get; private set; }
    public string ThumbPrompt { get; set; } = "";
    public bool ThumbJobRunning { get; private set; }
    public string? ThumbError { get; private set; }

    public void SelectThumb(int index) => Set(() => SelectedThumb = index);
    public void ToggleThumbPrompt() => Set(() => { ThumbPromptOpen = !ThumbPromptOpen; ThumbError = null; });
    public void SetThumbJob(bool running, string? error = null) =>
        Set(() => { ThumbJobRunning = running; ThumbError = error; });
    public void AddThumb(ThumbVariant variant) =>
        Set(() => { Thumbnails.Add(variant); ThumbPromptOpen = false; ThumbPrompt = ""; });

    /// <summary>Real thumbnail variants from the latest long-form edit; called
    /// whenever Detail lands. Fill carries the rendered image when hosted.</summary>
    private void HydrateThumbnails()
    {
        Thumbnails.Clear();
        var edit = LatestLongform ?? Detail?.LongformEdits.FirstOrDefault();
        foreach (var variant in edit?.Thumbnails ?? [])
        {
            Thumbnails.Add(new ThumbVariant(
                variant.Index,
                variant.Direction ?? $"Concept {variant.Index}",
                variant.Url is null ? variant.OverlayText ?? "" : "",
                variant.Url is { } url
                    ? $"#191817 url('{url}') center/cover no-repeat"
                    : "linear-gradient(135deg, #223047 0%, #3A2B52 60%, #D9F24B 140%)"));
        }
        SelectedThumb = edit?.SelectedThumbnail
            ?? (Thumbnails.Count > 0 ? Thumbnails[0].Index : 0);
    }

    // ---- Per-clip delivery options -------------------------------------- //

    private readonly Dictionary<Guid, string> _clipFormats = [];
    private readonly Dictionary<Guid, bool> _clipCaptions = [];
    private Guid ActiveClipKey => ActiveClip?.Id ?? Guid.Empty;

    public string ClipFormat => _clipFormats.GetValueOrDefault(ActiveClipKey, "auto");
    public bool ClipCaptionsOn =>
        CaptionsAvailableForFormat && _clipCaptions.GetValueOrDefault(ActiveClipKey, true);

    /// <summary>Captions ride the vertical and square renders; the wide 16:9
    /// ships clean. A clip without a captioned render has none to offer.</summary>
    public bool CaptionsAvailableForFormat =>
        ClipFormat is "auto" or "square" && ActiveClip?.CaptionedUrl is not null;

    public void SetClipFormat(string format) => Set(() => _clipFormats[ActiveClipKey] = format);
    public void ToggleClipCaptions() =>
        Set(() => _clipCaptions[ActiveClipKey] = !_clipCaptions.GetValueOrDefault(ActiveClipKey, true));

    // ---- Agent chat ------------------------------------------------------ //

    public List<AgentChatMessage> AgentMessages { get; } = [];
    public string AgentInput { get; set; } = "";
    public bool AgentBusy { get; private set; }

    /// <summary>"long" when the editor is open on the long-form cut, "short"
    /// for a highlight clip — the agent's capabilities differ per context.</summary>
    public string AgentContext { get; set; } = "short";

    public void AddAgentMessage(string role, string text) =>
        Set(() => AgentMessages.Add(new AgentChatMessage(role, text)));
    public void SetAgentBusy(bool busy) => Set(() => AgentBusy = busy);
    public void Notify() => Changed?.Invoke();
}
