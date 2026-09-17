using DevInterface;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.Compatibility;

/// <summary>
/// Compatibility shim kept while DevToolRuntime still owns the historical presentation lifecycle
/// call. Rebuilt Objects is page-less and renders through NativeSpatialGizmoView, so there is no
/// legacy representation tree to prune and no Handle.Update hook to install.
///
/// Explicit Vanilla/Legacy ObjectsPage ownership is intentionally left untouched: the original page
/// and all of its representations/handles run normally when the user asks for that fallback.
/// </summary>
internal static class ObjectGizmoPresentationController
{
    internal static void Enable()
    {
    }

    internal static void Disable()
    {
    }

    internal static void Apply(ObjectsPage objectsPage, EditorSession session, bool active)
    {
    }

    internal static void Reset()
    {
    }
}