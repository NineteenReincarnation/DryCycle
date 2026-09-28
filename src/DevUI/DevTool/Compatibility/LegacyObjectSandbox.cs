using System;
using System.Runtime.CompilerServices;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.Compatibility;

/// <summary>
/// Selected-object compatibility sandbox for PlacedObject types that do not yet have complete native
/// authoring coverage. It materializes one reusable ObjectsPage per session, creates a representation
/// only for the selected target, and temporarily exposes that page to the existing semantic bridge
/// while a capture or action runs.
///
/// The sandbox never calls ObjectsPage.Refresh(), so it never creates representations for every object
/// in the room. Explicit Vanilla/Legacy mode still materializes the real full ObjectsPage normally.
/// </summary>
internal static class LegacyObjectSandbox
{
    private sealed class State
    {
        internal ObjectsPage Page;
        internal PlacedObject Target;
    }

    private static ConditionalWeakTable<EditorSession, State> states = new();

    internal static LegacyControlSnapshot[] Capture(EditorSession session, PlacedObject target)
    {
        return Run(
            session,
            target,
            () => LegacyDevInterfaceBridge.Capture(session.Owner, target),
            Array.Empty<LegacyControlSnapshot>());
    }

    internal static bool Run(
        EditorSession session,
        PlacedObject target,
        Func<bool> action)
    {
        return Run(session, target, action, false);
    }

    internal static void Invalidate(EditorSession session, PlacedObject target)
    {
        if (session == null || target == null || !states.TryGetValue(session, out State state))
            return;
        if (!ReferenceEquals(state.Target, target))
            return;

        DisposeState(state);
        states.Remove(session);
    }

    internal static void Release(EditorSession session)
    {
        if (session == null || !states.TryGetValue(session, out State state))
            return;

        DisposeState(state);
        states.Remove(session);
    }

    internal static void Reset()
    {
        Release(DevToolSessionHub.Current);
        states = new ConditionalWeakTable<EditorSession, State>();
    }

    private static T Run<T>(
        EditorSession session,
        PlacedObject target,
        Func<T> action,
        T fallback)
    {
        if (session?.Owner == null || target?.type == null || action == null)
            return fallback;

        // Explicit legacy ownership already has the authoritative full ObjectsPage. Reuse it rather
        // than creating a second compatibility tree.
        if (session.Owner.activePage is ObjectsPage)
            return action();

        State state = Acquire(session, target);
        if (state?.Page == null)
            return fallback;

        Page previous = session.Owner.activePage;
        try
        {
            // Existing LegacyDevInterfaceBridge semantics are intentionally reused. The swap is
            // synchronous on the DevUI/main thread and no EditorSession synchronization occurs until
            // after this call returns. The sandbox is a semantic backend only: none of its Futile
            // visuals may ever leak into the rebuilt editor.
            HideVisualTree(state.Page);
            session.Owner.activePage = state.Page;
            return action();
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool legacy object sandbox failed: " + error.Message);
            return fallback;
        }
        finally
        {
            HideVisualTree(state.Page);
            session.Owner.activePage = previous;
        }
    }

    private static State Acquire(EditorSession session, PlacedObject target)
    {
        State state = states.GetValue(session, _ => new State());
        if (ReferenceEquals(state.Target, target) && state.Page != null)
            return state;

        DisposeState(state);

        try
        {
            ObjectsPage page = new(
                session.Owner,
                "DryCycle_Legacy_Object_Sandbox",
                null,
                "Objects");

            Page previous = session.Owner.activePage;
            try
            {
                session.Owner.activePage = page;

                // Passing the existing model object is critical: CreateObjRep then constructs only
                // its representation/control tree and does not append another PlacedObject.
                page.CreateObjRep(target.type, target);

                // ObjectsPage construction creates ordinary DevInterface menu/representation sprites
                // even though this page exists only as a compatibility backend. Suppress them in the
                // same frame so selecting an external object can never reveal vanilla DevUI.
                HideVisualTree(page);
            }
            finally
            {
                session.Owner.activePage = previous;
            }

            state.Page = page;
            state.Target = target;
            return state;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning(
                "DevTool could not materialize selected-object legacy sandbox for '" +
                (target.type?.value ?? string.Empty) + "': " + error.Message);
            DisposeState(state);
            return null;
        }
    }

    private static void HideVisualTree(DevUINode node)
    {
        if (node == null)
            return;

        if (node.fSprites != null)
        {
            for (int i = 0; i < node.fSprites.Count; i++)
            {
                if (node.fSprites[i] != null)
                    node.fSprites[i].isVisible = false;
            }
        }

        if (node.fLabels != null)
        {
            for (int i = 0; i < node.fLabels.Count; i++)
            {
                if (node.fLabels[i] != null)
                    node.fLabels[i].isVisible = false;
            }
        }

        if (node.subNodes == null)
            return;

        for (int i = 0; i < node.subNodes.Count; i++)
            HideVisualTree(node.subNodes[i]);
    }

    private static void DisposeState(State state)
    {
        if (state == null) return;
        if (state.Page != null)
        {
            try { state.Page.ClearSprites(); }
            catch { }
        }

        state.Page = null;
        state.Target = null;
    }
}
