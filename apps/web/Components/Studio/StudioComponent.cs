using Highlighter.Web.Models;
using Microsoft.AspNetCore.Components;

namespace Highlighter.Web.Components.Studio;

/// <summary>
/// Base for all studio components: injects the shared <see cref="StudioState"/> and
/// re-renders on every state change. Blazor skips re-rendering parameterless children
/// when a parent re-renders, so each component must observe the state itself —
/// cross-component updates (e.g. bin caption click → timeline highlight) depend on it.
/// </summary>
public abstract class StudioComponent : ComponentBase, IDisposable
{
    [Inject] protected StudioState S { get; set; } = default!;

    protected override void OnInitialized() => S.Changed += OnStateChanged;

    private void OnStateChanged() => InvokeAsync(StateHasChanged);

    public void Dispose() => S.Changed -= OnStateChanged;
}
