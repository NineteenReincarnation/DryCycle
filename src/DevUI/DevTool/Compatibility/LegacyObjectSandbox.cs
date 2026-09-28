using System;
using System.Runtime.CompilerServices;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.Compatibility;

/// <summary>
/// Headless selected-object Representation host for PlacedObject types that do not yet have complete
/// native authoring coverage. The historical class name is retained to avoid spreading a migration
/// through callers, but this is no longer a visible "legacy page" fallback.
///
/// Exactly one target Representation is materialized per session. Its Futile visuals are quarantined
/// from every container, semantic controls are compiled only when the target/tree changes, and normal
/// capture never swaps DevUI.activePage. The hidden ObjectsPage exists only because third-party
/// CreateObjRep hooks conventionally require that constructor boundary.
/// </summary>
internal static class LegacyObjectSandbox
{
    private sealed class State
    {
        internal ObjectsPage Page;
        internal PlacedObjectRepresentation Representation;
        internal PlacedObject Target;
        internal readonly FContainer QuarantineContainer = new();
        internal LegacyControlSnapshot[] CachedControls = Array.Empty<LegacyControlSnapshot>();
        internal string ModelFingerprint = string.Empty;
        internal bool ControlsDirty = true;
    }

    private static ConditionalWeakTable<EditorSession, State> states = new();

    internal static LegacyControlSnapshot[] Capture(EditorSession session, PlacedObject target)
    {
        if (session?.Owner == null || target?.type == null)
            return Array.Empty<LegacyControlSnapshot>();

        // Explicit Vanilla/Legacy mode already owns the real ObjectsPage and remains authoritative.
        if (session.Owner.activePage is ObjectsPage)
            return LegacyDevInterfaceBridge.Capture(session.Owner, target);

        State state = Acquire(session, target);
        if (state?.Representation == null)
            return Array.Empty<LegacyControlSnapshot>();

        string fingerprint = Fingerprint(target);
        if (!string.Equals(state.ModelFingerprint, fingerprint, StringComparison.Ordinal))
        {
            state.ModelFingerprint = fingerprint;
            state.ControlsDirty = true;
            RefreshHeadlessRepresentation(state);
        }

        if (!state.ControlsDirty)
            return state.CachedControls ?? Array.Empty<LegacyControlSnapshot>();

        try
        {
            QuarantineVisualTree(state.Page, state.QuarantineContainer);
            state.CachedControls =
                LegacyDevInterfaceBridge.CaptureRoot(state.Representation) ??
                Array.Empty<LegacyControlSnapshot>();

            // Full protocol scanning is diagnostics-only. Production object editing pays no
            // migration-audit reflection cost.
            if (DevUiDiagnosticsPolicy.Enabled)
                DevUiMigrationCoverage.ObserveHeadlessRepresentation(state.Representation);

            state.ControlsDirty = false;
            return state.CachedControls;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning(
                "DevTool headless object control compilation failed: " + error.Message);
            state.CachedControls = Array.Empty<LegacyControlSnapshot>();
            state.ControlsDirty = false;
            return state.CachedControls;
        }
        finally
        {
            QuarantineVisualTree(state.Page, state.QuarantineContainer);
        }
    }

    internal static bool Run(
        EditorSession session,
        PlacedObject target,
        Func<bool> action)
    {
        return Run(session, target, action, false);
    }

    internal static T InspectRepresentation<T>(
        EditorSession session,
        PlacedObject target,
        Func<PlacedObjectRepresentation, T> projection,
        T fallback)
    {
        if (session?.Owner == null || target?.type == null || projection == null)
            return fallback;

        if (session.Owner.activePage is ObjectsPage livePage)
        {
            PlacedObjectRepresentation liveRepresentation =
                FindRepresentation(livePage, target);
            return liveRepresentation == null ? fallback : projection(liveRepresentation);
        }

        State state = Acquire(session, target);
        if (state?.Representation == null)
            return fallback;

        string fingerprint = Fingerprint(target);
        if (!string.Equals(state.ModelFingerprint, fingerprint, StringComparison.Ordinal))
        {
            state.ModelFingerprint = fingerprint;
            state.ControlsDirty = true;
            RefreshHeadlessRepresentation(state);
        }

        try
        {
            QuarantineVisualTree(state.Page, state.QuarantineContainer);
            return projection(state.Representation);
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning(
                "DevTool headless representation projection failed: " + error.Message);
            return fallback;
        }
        finally
        {
            QuarantineVisualTree(state.Page, state.QuarantineContainer);
        }
    }

    internal static bool MutateRepresentation(
        EditorSession session,
        PlacedObject target,
        Func<PlacedObjectRepresentation, bool> mutation)
    {
        if (session?.Owner == null || target?.type == null || mutation == null)
            return false;

        if (session.Owner.activePage is ObjectsPage livePage)
        {
            PlacedObjectRepresentation liveRepresentation =
                FindRepresentation(livePage, target);
            if (liveRepresentation == null)
                return false;

            try { return mutation(liveRepresentation); }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning(
                    "DevTool live representation mutation failed: " + error.Message);
                return false;
            }
        }

        State state = Acquire(session, target);
        if (state?.Representation == null)
            return false;

        Page previous = session.Owner.activePage;
        try
        {
            QuarantineVisualTree(state.Page, state.QuarantineContainer);
            session.Owner.activePage = state.Page;
            bool changed = mutation(state.Representation);
            if (changed)
            {
                state.ControlsDirty = true;
                state.ModelFingerprint = Fingerprint(target);
            }
            return changed;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning(
                "DevTool headless representation mutation failed: " + error.Message);
            return false;
        }
        finally
        {
            QuarantineVisualTree(state.Page, state.QuarantineContainer);
            session.Owner.activePage = previous;
        }
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
            QuarantineVisualTree(state.Page, state.QuarantineContainer);
            session.Owner.activePage = state.Page;
            T result = action();
            state.ControlsDirty = true;
            state.ModelFingerprint = Fingerprint(target);
            return result;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool legacy object sandbox failed: " + error.Message);
            return fallback;
        }
        finally
        {
            QuarantineVisualTree(state.Page, state.QuarantineContainer);
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
                QuarantineVisualTree(page, state.QuarantineContainer);
            }
            finally
            {
                session.Owner.activePage = previous;
            }

            PlacedObjectRepresentation representation = FindRepresentation(page, target);
            if (representation == null)
            {
                try { page.ClearSprites(); }
                catch { }
                return null;
            }

            state.Page = page;
            state.Representation = representation;
            state.Target = target;
            state.CachedControls = Array.Empty<LegacyControlSnapshot>();
            state.ModelFingerprint = Fingerprint(target);
            state.ControlsDirty = true;
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

    private static void QuarantineVisualTree(
        DevUINode node,
        FContainer quarantine)
    {
        if (node == null || quarantine == null)
            return;

        if (node.fSprites != null)
        {
            for (int i = 0; i < node.fSprites.Count; i++)
            {
                FSprite sprite = node.fSprites[i];
                if (sprite == null) continue;
                sprite.isVisible = false;
                try
                {
                    sprite.RemoveFromContainer();
                    quarantine.AddChild(sprite);
                }
                catch { }
            }
        }

        if (node.fLabels != null)
        {
            for (int i = 0; i < node.fLabels.Count; i++)
            {
                FLabel label = node.fLabels[i];
                if (label == null) continue;
                label.isVisible = false;
                try
                {
                    label.RemoveFromContainer();
                    quarantine.AddChild(label);
                }
                catch { }
            }
        }

        if (node.subNodes == null)
            return;

        for (int i = 0; i < node.subNodes.Count; i++)
            QuarantineVisualTree(node.subNodes[i], quarantine);
    }

    private static void RefreshHeadlessRepresentation(State state)
    {
        if (state?.Representation == null)
            return;

        try
        {
            state.Representation.Refresh();
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning(
                "DevTool headless object representation refresh failed: " + error.Message);
        }
        finally
        {
            QuarantineVisualTree(state.Page, state.QuarantineContainer);
        }
    }

    private static PlacedObjectRepresentation FindRepresentation(
        DevUINode node,
        PlacedObject target)
    {
        if (node == null || target == null)
            return null;

        if (node is PlacedObjectRepresentation representation &&
            ReferenceEquals(representation.pObj, target))
            return representation;

        if (node.subNodes == null)
            return null;

        for (int i = 0; i < node.subNodes.Count; i++)
        {
            PlacedObjectRepresentation found =
                FindRepresentation(node.subNodes[i], target);
            if (found != null)
                return found;
        }

        return null;
    }

    private static string Fingerprint(PlacedObject target)
    {
        if (target == null)
            return string.Empty;

        string data;
        try { data = target.data?.ToString() ?? string.Empty; }
        catch { data = "<serialization-failed>"; }

        return (target.type?.value ?? string.Empty) + "|" +
               target.pos.x.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "|" +
               target.pos.y.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "|" +
               (target.active ? "1" : "0") + "|" + data;
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
        state.Representation = null;
        state.Target = null;
        state.CachedControls = Array.Empty<LegacyControlSnapshot>();
        state.ModelFingerprint = string.Empty;
        state.ControlsDirty = true;
    }
}
