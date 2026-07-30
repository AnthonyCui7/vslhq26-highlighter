using Highlighter.Web.Models;

namespace Highlighter.Web.Services;

/// <summary>IStudioBackend over the studio's sample data: jobs complete after
/// a short simulated delay and mutate the shared state, so the whole surface
/// (thumbnail picker, delivery options, agent tools) behaves end to end
/// before the API hookup.</summary>
public class SampleStudioBackend(StudioState state) : IStudioBackend
{
    private static readonly string[] ThumbFills =
    [
        "linear-gradient(135deg, #223047 0%, #3A2B52 60%, #D9F24B 140%)",
        "linear-gradient(150deg, #2E1C1C 0%, #513B16 55%, #EAF87A 135%)",
        "linear-gradient(115deg, #14321E 0%, #1D1B2E 60%, #4A7A50 120%)",
    ];

    private int _jobCounter;
    private readonly Dictionary<string, string> _jobs = new();

    private string StartJob(string kind)
    {
        var id = $"job-{++_jobCounter:000}-{kind}";
        _jobs[id] = "running";
        return id;
    }

    private void FinishJob(string id) => _jobs[id] = "succeeded";

    public Task<string> GetProjectStatusAsync()
    {
        var project = state.Project;
        return Task.FromResult(
            $"Project \"{project.Title}\" ({project.Kind}, {project.Dur}): status {project.Status}. "
            + $"Outputs: {project.Outputs}. Long-form title: \"{SampleData.LongformTitle}\" (v2).");
    }

    public Task<string> ListClipsAsync()
    {
        var lines = SampleData.Clips.Select(clip =>
            $"- [{clip.Index}] \"{clip.Title}\" ({clip.Dur}, score {clip.Score}"
            + $"{(clip.HasCaptions ? ", captioned" : ", no captions")})");
        return Task.FromResult(string.Join("\n", lines));
    }

    public Task<string> GetLongformVersionsAsync() =>
        Task.FromResult(
            $"v1: initial cut (24:36). v2 (current): \"{SampleData.LongformTitle}\" — "
            + "tightened opening, 24:36. Thumbnails: "
            + string.Join(", ", state.Thumbnails.Select(t => $"#{t.Index} {t.Direction}"))
            + $". Selected: #{state.SelectedThumb}.");

    public async Task<string> RerunResearchAsync(string focus)
    {
        var job = StartJob("research");
        await Task.Delay(1200);
        FinishJob(job);
        return $"Research refreshed ({job}) with focus \"{focus}\": the audience responds to "
            + "cold-open failure stories, concrete dollar amounts in titles, and split-screen "
            + "debate thumbnails; avoid tool-list roundups — they are saturated this month.";
    }

    public async Task<string> GenerateThumbnailsAsync(string? prompt)
    {
        var job = StartJob("thumbnails");
        state.SetThumbJob(true);
        await Task.Delay(1800);
        var index = state.Thumbnails.Count + 1;
        var direction = string.IsNullOrWhiteSpace(prompt)
            ? $"Alternate Take {index}"
            : Cap($"{prompt!.Split(',')[0].Trim()}", 28);
        state.AddThumb(new ThumbVariant(
            index, direction, "", ThumbFills[(index - 1) % ThumbFills.Length]));
        state.SetThumbJob(false);
        FinishJob(job);
        return $"Generated thumbnail #{index} (\"{direction}\") ({job}). "
            + "It is in the thumbnail strip on the project page.";
    }

    public Task<string> SelectThumbnailAsync(int index)
    {
        if (state.Thumbnails.All(t => t.Index != index))
            return Task.FromResult($"There is no thumbnail #{index} — the set has "
                + $"{state.Thumbnails.Count}.");
        state.SelectThumb(index);
        return Task.FromResult($"Thumbnail #{index} is now the selected thumbnail.");
    }

    public Task<string> ImportThumbnailAsync(string fileName)
    {
        var index = state.Thumbnails.Count + 1;
        state.AddThumb(new ThumbVariant(index, $"Imported · {Cap(fileName, 20)}", "",
            "linear-gradient(135deg, #1C1B19 0%, #33302C 100%)"));
        state.SelectThumb(index);
        return Task.FromResult($"Imported {fileName} as thumbnail #{index} and selected it.");
    }

    public async Task<string> ReviseLongformAsync(string request)
    {
        var job = StartJob("revise");
        await Task.Delay(2200);
        FinishJob(job);
        return $"Revision job {job} finished: v3 rendered from the request \"{request}\". "
            + "The previous thumbnail and title carried over; open the project page to review it.";
    }

    public async Task<string> ReformatClipAsync(string format, bool captions)
    {
        var job = StartJob("reformat");
        await Task.Delay(1500);
        FinishJob(job);
        return $"Reformat job {job} finished: rendered the {format} copy"
            + $"{(captions ? " with burned captions" : "")} for \"{state.ActiveClipTitle}\".";
    }

    public Task<string> GetJobStatusAsync(string jobId) =>
        Task.FromResult(_jobs.TryGetValue(jobId, out var status)
            ? $"{jobId}: {status}"
            : $"No job named {jobId}.");

    private static string Cap(string text, int max) =>
        text.Length <= max ? text : text[..max].TrimEnd() + "…";
}
