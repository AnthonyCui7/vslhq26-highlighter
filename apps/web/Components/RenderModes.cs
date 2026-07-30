using Microsoft.AspNetCore.Components.Web;

namespace Highlighter.Web.Components;

public static class RenderModes
{
    /// <summary>Interactive server WITHOUT prerender: components first render on
    /// the live circuit, so ProtectedLocalStorage (JS interop) works straight
    /// from OnInitializedAsync — no double-render dance, no auth flicker.</summary>
    public static readonly InteractiveServerRenderMode NoPrerender = new(prerender: false);
}
