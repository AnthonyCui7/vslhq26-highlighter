namespace Highlighter.Web.Models;

/// <summary>One message in the studio agent conversation.</summary>
public record AgentChatMessage(string Role, string Text);

/// <summary>
/// UI state for the studio, mirroring the Claude Design component's state machine
/// (view, proj, editorOpen, activeClip, mode, binTab, panel, tool, activeCaption,
/// capStyle, modalOpen, playing, sortOpen, sortBy, snap) plus the states behind
/// the thumbnail picker, the per-clip delivery options, and the agent chat.
/// </summary>
public class StudioState
{
    public string View { get; private set; } = "projects";
    public int Proj { get; private set; }
    public bool EditorOpen { get; private set; }
    public int ActiveClip { get; private set; }
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

    public bool IsProjects => View == "projects";
    public bool IsProject => View == "project";
    public ProjectInfo Project => SampleData.Projects[Proj];
    public string ProjectTitle => Project.Title;
    public string ActiveClipTitle => SampleData.Clips[ActiveClip].Title;
    public string SortLabel => SortBy == "score" ? "Score" : "Timeline";
    public string PlayIcon => Playing ? "❚❚" : "▶";
    public bool ShowTargetLength => Mode is "long" or "both";

    public void GoProjects() => Set(() => { View = "projects"; EditorOpen = false; SortOpen = false; });
    public void OpenProject(int i) => Set(() => { View = "project"; Proj = i; SortOpen = false; });
    public void OpenClip(int idx) =>
        Set(() => { EditorOpen = true; ActiveClip = idx; AgentContext = "short"; });
    public void OpenEditorLong() =>
        Set(() => { EditorOpen = true; ActiveClip = 0; AgentContext = "long"; });
    public void CloseEditor() => Set(() => EditorOpen = false);
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
    public void OpenNewJob() => Set(() => ModalOpen = true);
    public void CloseNewJob() => Set(() => ModalOpen = false);
    public void SetMode(string mode) => Set(() => Mode = mode);

    // ---- Long-form thumbnails ------------------------------------------ //

    public List<ThumbVariant> Thumbnails { get; } = new(SampleData.Thumbnails);
    public int SelectedThumb { get; private set; } = 1;
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

    // ---- Per-clip delivery options -------------------------------------- //

    private readonly Dictionary<int, string> _clipFormats = new();
    private readonly Dictionary<int, bool> _clipCaptions = new();

    public string ClipFormat => _clipFormats.GetValueOrDefault(ActiveClip, "auto");
    public bool ClipCaptionsOn =>
        CaptionsAvailableForFormat && _clipCaptions.GetValueOrDefault(ActiveClip, true);

    /// <summary>Captions exist for the auto vertical and the square render;
    /// the wide 16:9 ships clean, and some clips never got a caption pass.</summary>
    public bool CaptionsAvailableForFormat =>
        ClipFormat is "auto" or "square" && SampleData.Clips[ActiveClip].HasCaptions;

    public void SetClipFormat(string format) => Set(() => _clipFormats[ActiveClip] = format);
    public void ToggleClipCaptions() =>
        Set(() => _clipCaptions[ActiveClip] = !_clipCaptions.GetValueOrDefault(ActiveClip, true));

    // ---- Agent chat ------------------------------------------------------ //

    public List<AgentChatMessage> AgentMessages { get; } = new();
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
