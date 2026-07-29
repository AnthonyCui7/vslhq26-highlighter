namespace Highlighter.Web.Models;

/// <summary>
/// UI state for the studio, mirroring the Claude Design component's state machine
/// (view, proj, editorOpen, activeClip, mode, binTab, panel, tool, activeCaption,
/// capStyle, modalOpen, playing, sortOpen, sortBy, snap).
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
    public void OpenClip(int idx) => Set(() => { EditorOpen = true; ActiveClip = idx; });
    public void OpenEditorLong() => Set(() => { EditorOpen = true; ActiveClip = 0; });
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
}
