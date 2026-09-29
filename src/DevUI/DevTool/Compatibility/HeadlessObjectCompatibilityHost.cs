using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.Compatibility;

/// <summary>
/// Headless selected-object Representation host for PlacedObject types that do not yet have complete
/// native authoring coverage. This is an isolated semantic/geometry compatibility backend, never a
/// visible fallback page and never a normal Objects workspace.
///
/// Exactly one target Representation is materialized per session. Its Futile visuals are quarantined
/// from every container, semantic controls are compiled only when the target/tree changes, and normal
/// capture never swaps DevUI.activePage. The hidden ObjectsPage exists only because third-party
/// CreateObjRep hooks conventionally require that constructor boundary.
/// </summary>
internal static class HeadlessObjectCompatibilityHost
{
    private sealed class State
    {
        internal ObjectsPage Page;
        internal PlacedObjectRepresentation Representation;
        internal PlacedObject Target;
        internal readonly FContainer QuarantineContainer = new();
        internal LegacyControlSnapshot[] CachedControls = Array.Empty<LegacyControlSnapshot>();
        internal string ModelFingerprint = string.Empty;
        internal ulong TreeSignature;
        internal int LastModelAuditFrame = int.MinValue;
        internal int LastTreeAuditFrame = int.MinValue;
        internal bool ControlsDirty = true;
        internal bool CompatibilityDirty = true;
        internal bool HasUnsupportedNodes;
        internal string UnsupportedNodeExample = string.Empty;
    }

    // Normal publication must stay O(1). Expensive serialization/tree walks are only used as
    // low-frequency safety audits for third-party code that mutates behind DryCycle's revision
    // system. All mutations routed through this host invalidate immediately.
    private const int ModelAuditIntervalFrames = 30;
    private const int TreeAuditIntervalFrames = 12;

    private static ConditionalWeakTable<EditorSession, State> states = new();
    private static readonly ConcurrentDictionary<Type, FieldInfo[]> ReferencedVisualFields = new();
    private static readonly ConcurrentDictionary<Type, FieldInfo[]> ReferencedRendererFields = new();

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

        AuditModel(state, target);

        // Custom Buttons may create/remove Panels without touching PlacedObject.Data. Operations
        // routed through Run/MutateRepresentation invalidate immediately; this low-frequency audit
        // catches asynchronous/third-party tree changes without scanning the tree every frame.
        if (!state.ControlsDirty && AuditTree(state))
            state.ControlsDirty = true;

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

            RememberTree(state);
            state.CompatibilityDirty = true;
            EnsureCompatibilityAudit(state);
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
        Func<PlacedObjectRepresentation, bool> action) =>
        MutateRepresentation(session, target, action);

    internal static bool HasCompatibilityGap(
        EditorSession session,
        PlacedObject target,
        out string example)
    {
        example = string.Empty;
        if (session?.Owner == null || target?.type == null)
        {
            example = "headless representation host unavailable";
            return true;
        }

        if (session.Owner.activePage is ObjectsPage livePage)
        {
            PlacedObjectRepresentation liveRepresentation =
                FindRepresentation(livePage, target);
            if (liveRepresentation == null)
            {
                example = "live representation unavailable";
                return true;
            }

            bool unsupportedNodes =
                LegacyDevInterfaceBridge.HasUnsupportedNodes(
                    liveRepresentation,
                    out string nodeExample);
            bool unsupportedGeometry =
                HeadlessRepresentationGizmoBridge.HasUnsupportedVisualGeometry(
                    liveRepresentation,
                    out string geometryExample);

            example = unsupportedNodes
                ? nodeExample ?? string.Empty
                : geometryExample ?? string.Empty;
            return unsupportedNodes || unsupportedGeometry;
        }

        State state = Acquire(session, target);
        if (state?.Representation == null)
        {
            example = "headless representation unavailable";
            return true;
        }

        AuditModel(state, target);
        if (AuditTree(state))
        {
            state.ControlsDirty = true;
            state.CompatibilityDirty = true;
        }

        EnsureCompatibilityAudit(state);

        // Objects diagnostics no longer materialize a visible ObjectsPage. Observe the selected
        // quarantined Representation directly so protocol/coverage diagnostics retain the same
        // evidence without reviving the legacy workspace.
        if (DevUiDiagnosticsPolicy.Enabled)
            DevUiMigrationCoverage.ObserveHeadlessRepresentation(state.Representation);

        example = state.UnsupportedNodeExample ?? string.Empty;
        return state.HasUnsupportedNodes;
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

        AuditModel(state, target);

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

        try
        {
            QuarantineVisualTree(state.Page, state.QuarantineContainer);
            bool changed = mutation(state.Representation);
            if (changed)
                MarkDirtyAfterMutation(state, target);
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
                "DryCycle_Headless_Object_Compatibility",
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
            state.LastModelAuditFrame = CurrentFrame();
            state.TreeSignature = ComputeTreeSignature(representation);
            state.LastTreeAuditFrame = state.LastModelAuditFrame;
            state.ControlsDirty = true;
            state.CompatibilityDirty = true;
            state.HasUnsupportedNodes = false;
            state.UnsupportedNodeExample = string.Empty;
            return state;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning(
                "DevTool could not materialize selected-object headless compatibility host for '" +
                (target.type?.value ?? string.Empty) + "': " + error.Message);
            DisposeState(state);
            return null;
        }
    }

    private static void QuarantineVisualTree(
        DevUINode root,
        FContainer quarantine)
    {
        if (root == null || quarantine == null)
            return;

        HashSet<DevUINode> visited = new();
        Stack<DevUINode> stack = new();
        stack.Push(root);
        int remaining = 8192;

        while (stack.Count > 0 && remaining-- > 0)
        {
            DevUINode node = stack.Pop();
            if (node == null || !visited.Add(node))
                continue;

            QuarantineNodeVisuals(node, quarantine);

            if (node.subNodes == null)
                continue;

            for (int i = node.subNodes.Count - 1; i >= 0; i--)
                stack.Push(node.subNodes[i]);
        }
    }

    private static void QuarantineNodeVisuals(
        DevUINode node,
        FContainer quarantine)
    {
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

        QuarantineReferencedVisuals(node, quarantine);
    }

    private static void QuarantineReferencedVisuals(
        DevUINode node,
        FContainer quarantine)
    {
        if (node == null || quarantine == null)
            return;

        FieldInfo[] fields = ReferencedVisualFields.GetOrAdd(
            node.GetType(),
            BuildReferencedVisualFields);

        for (int i = 0; i < fields.Length; i++)
        {
            object raw;
            try { raw = fields[i].GetValue(node); }
            catch { continue; }

            if (raw is FNode visual)
            {
                MoveVisualToQuarantine(visual, quarantine);
                continue;
            }

            if (raw is Array array)
            {
                for (int itemIndex = 0; itemIndex < array.Length; itemIndex++)
                    if (array.GetValue(itemIndex) is FNode item)
                        MoveVisualToQuarantine(item, quarantine);
            }
        }
    }

    private static FieldInfo[] BuildReferencedVisualFields(Type nodeType)
    {
        List<FieldInfo> result = new();
        Type current = nodeType;

        // DevUINode base storage (owner, parent, fSprites/fLabels/subNodes) is handled explicitly.
        // Only inspect fields introduced by concrete/custom node classes.
        while (current != null &&
               current != typeof(DevUINode) &&
               typeof(DevUINode).IsAssignableFrom(current))
        {
            FieldInfo[] fields = current.GetFields(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly);

            for (int i = 0; i < fields.Length; i++)
            {
                Type fieldType = fields[i].FieldType;
                if (typeof(FContainer).IsAssignableFrom(fieldType))
                    continue;

                if (typeof(FNode).IsAssignableFrom(fieldType))
                {
                    result.Add(fields[i]);
                    continue;
                }

                if (fieldType.IsArray)
                {
                    Type elementType = fieldType.GetElementType();
                    if (elementType != null &&
                        typeof(FNode).IsAssignableFrom(elementType) &&
                        !typeof(FContainer).IsAssignableFrom(elementType))
                        result.Add(fields[i]);
                }
            }

            current = current.BaseType;
        }

        return result.ToArray();
    }

    private static FieldInfo[] BuildReferencedRendererFields(Type nodeType)
    {
        List<FieldInfo> result = new();
        Type current = nodeType;

        while (current != null &&
               current != typeof(DevUINode) &&
               typeof(DevUINode).IsAssignableFrom(current))
        {
            FieldInfo[] fields = current.GetFields(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly);

            for (int i = 0; i < fields.Length; i++)
            {
                Type fieldType = fields[i].FieldType;
                if (typeof(UnityEngine.Renderer).IsAssignableFrom(fieldType))
                {
                    result.Add(fields[i]);
                    continue;
                }

                if (fieldType.IsArray &&
                    fieldType.GetElementType() != null &&
                    typeof(UnityEngine.Renderer).IsAssignableFrom(fieldType.GetElementType()))
                    result.Add(fields[i]);
            }

            current = current.BaseType;
        }

        return result.ToArray();
    }

    private static void MoveVisualToQuarantine(
        FNode visual,
        FContainer quarantine)
    {
        if (visual == null ||
            quarantine == null ||
            visual is FContainer)
            return;

        try
        {
            visual.isVisible = false;

            // FGameObjectNode destroys its wrapped Unity GameObject when removed from an on-stage
            // container by default. A headless host still needs that GameObject as a semantic source
            // (for example a LineRenderer), so defer destruction until the headless compatibility host state itself is disposed.
            if (visual is FGameObjectNode gameObjectNode)
                gameObjectNode.shouldDestroyOnRemoveFromStage = false;

            if (!ReferenceEquals(visual.container, quarantine))
            {
                visual.container?.RemoveChild(visual);
                quarantine.AddChild(visual);
            }
        }
        catch { }
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
        DevUINode root,
        PlacedObject target)
    {
        if (root == null || target == null)
            return null;

        HashSet<DevUINode> visited = new();
        Stack<DevUINode> stack = new();
        stack.Push(root);
        int remaining = 8192;

        while (stack.Count > 0 && remaining-- > 0)
        {
            DevUINode node = stack.Pop();
            if (node == null || !visited.Add(node))
                continue;

            if (node is PlacedObjectRepresentation representation &&
                ReferenceEquals(representation.pObj, target))
                return representation;

            if (node.subNodes == null)
                continue;

            for (int i = node.subNodes.Count - 1; i >= 0; i--)
                stack.Push(node.subNodes[i]);
        }

        return null;
    }

    private static void AuditModel(State state, PlacedObject target)
    {
        if (state == null || target == null)
            return;

        int frame = CurrentFrame();
        if (!AuditDue(state.LastModelAuditFrame, frame, ModelAuditIntervalFrames))
            return;

        state.LastModelAuditFrame = frame;
        string fingerprint = Fingerprint(target);
        if (string.Equals(state.ModelFingerprint, fingerprint, StringComparison.Ordinal))
            return;

        state.ModelFingerprint = fingerprint;
        state.ControlsDirty = true;
        state.CompatibilityDirty = true;
        RefreshHeadlessRepresentation(state);
        // Refresh() is allowed to rebuild transient Panels/Handles, so force the next structural
        // comparison to use the post-refresh tree.
        RememberTree(state);
    }

    private static bool AuditTree(State state)
    {
        if (state?.Representation == null)
            return false;

        int frame = CurrentFrame();
        if (!AuditDue(state.LastTreeAuditFrame, frame, TreeAuditIntervalFrames))
            return false;

        state.LastTreeAuditFrame = frame;
        ulong current = ComputeTreeSignature(state.Representation);
        if (state.TreeSignature == 0UL)
        {
            state.TreeSignature = current;
            return false;
        }

        bool changed = state.TreeSignature != current;
        state.TreeSignature = current;
        if (changed)
            state.CompatibilityDirty = true;
        return changed;
    }

    private static void RememberTree(State state)
    {
        if (state?.Representation == null)
            return;

        state.TreeSignature = ComputeTreeSignature(state.Representation);
        state.LastTreeAuditFrame = CurrentFrame();
    }

    private static void MarkDirtyAfterMutation(State state, PlacedObject target)
    {
        if (state == null)
            return;

        state.ControlsDirty = true;
        state.CompatibilityDirty = true;
        state.ModelFingerprint = Fingerprint(target);
        state.LastModelAuditFrame = CurrentFrame();

        // Do not remember the tree here. A Button.Clicked() implementation may create a child Panel
        // immediately or during the following Refresh/Update. Leaving the previous signature intact
        // lets the next compilation/audit observe that structural transition.
        state.LastTreeAuditFrame = int.MinValue;
    }

    private static void EnsureCompatibilityAudit(State state)
    {
        if (state?.Representation == null || !state.CompatibilityDirty)
            return;

        bool unsupportedNodes =
            LegacyDevInterfaceBridge.HasUnsupportedNodes(
                state.Representation,
                out string nodeExample);
        bool unsupportedGeometry =
            HeadlessRepresentationGizmoBridge.HasUnsupportedVisualGeometry(
                state.Representation,
                out string geometryExample);

        state.HasUnsupportedNodes = unsupportedNodes || unsupportedGeometry;
        state.UnsupportedNodeExample = unsupportedNodes
            ? nodeExample ?? string.Empty
            : geometryExample ?? string.Empty;
        state.CompatibilityDirty = false;

        if (state.HasUnsupportedNodes && DevUiDiagnosticsPolicy.Enabled)
        {
            Plugin.Logger?.LogWarning(
                "DevTool headless object compatibility gap: " +
                state.UnsupportedNodeExample);
        }
    }

    private static bool AuditDue(int lastFrame, int currentFrame, int interval)
    {
        if (lastFrame == int.MinValue)
            return true;
        if (currentFrame < lastFrame)
            return true; // Time.frameCount wrapped/reset.
        return currentFrame - lastFrame >= interval;
    }

    private static int CurrentFrame()
    {
        try { return UnityEngine.Time.frameCount; }
        catch { return 0; }
    }

    private static ulong ComputeTreeSignature(DevUINode root)
    {
        if (root == null)
            return 0UL;

        const ulong offset = 1469598103934665603UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;
        int remaining = 4096;
        HashSet<DevUINode> visited = new();
        Stack<DevUINode> stack = new();
        stack.Push(root);

        while (stack.Count > 0 && remaining-- > 0)
        {
            DevUINode node = stack.Pop();
            if (node == null || !visited.Add(node))
                continue;

            hash ^= unchecked((uint)RuntimeHelpers.GetHashCode(node));
            hash *= prime;
            hash ^= unchecked((uint)node.GetType().GetHashCode());
            hash *= prime;

            string id = node.IDstring ?? string.Empty;
            hash ^= unchecked((uint)StringComparer.Ordinal.GetHashCode(id));
            hash *= prime;

            int count = node.subNodes?.Count ?? 0;
            hash ^= unchecked((uint)count);
            hash *= prime;

            // Structural visual identity is part of compatibility too. Do not hash positions,
            // scales or colors: those legitimately change during Refresh(). Identity/element/shader
            // changes, referenced Futile nodes and Renderer replacement are the signals that the
            // set of visual protocols itself changed and needs a fresh compatibility proof.
            int spriteCount = node.fSprites?.Count ?? 0;
            hash ^= unchecked((uint)spriteCount);
            hash *= prime;
            for (int i = 0; i < spriteCount; i++)
            {
                FSprite sprite = node.fSprites[i];
                if (sprite == null)
                {
                    hash *= prime;
                    continue;
                }

                hash ^= unchecked((uint)RuntimeHelpers.GetHashCode(sprite));
                hash *= prime;
                hash ^= unchecked((uint)StringComparer.Ordinal.GetHashCode(
                    sprite.element?.name ?? string.Empty));
                hash *= prime;
                hash ^= unchecked((uint)StringComparer.Ordinal.GetHashCode(
                    sprite.shader?.name ?? string.Empty));
                hash *= prime;
            }

            FieldInfo[] visualFields = ReferencedVisualFields.GetOrAdd(
                node.GetType(),
                BuildReferencedVisualFields);
            for (int i = 0; i < visualFields.Length; i++)
            {
                object raw;
                try { raw = visualFields[i].GetValue(node); }
                catch { raw = null; }

                if (raw is FNode visual)
                {
                    hash ^= unchecked((uint)RuntimeHelpers.GetHashCode(visual));
                    hash *= prime;
                }
                else if (raw is Array visualArray)
                {
                    hash ^= unchecked((uint)visualArray.Length);
                    hash *= prime;
                    for (int itemIndex = 0; itemIndex < visualArray.Length; itemIndex++)
                    {
                        if (visualArray.GetValue(itemIndex) is not FNode item)
                            continue;
                        hash ^= unchecked((uint)RuntimeHelpers.GetHashCode(item));
                        hash *= prime;
                    }
                }
            }

            FieldInfo[] rendererFields = ReferencedRendererFields.GetOrAdd(
                node.GetType(),
                BuildReferencedRendererFields);
            for (int i = 0; i < rendererFields.Length; i++)
            {
                object raw;
                try { raw = rendererFields[i].GetValue(node); }
                catch { raw = null; }

                if (raw is UnityEngine.Renderer renderer)
                {
                    hash ^= unchecked((uint)RuntimeHelpers.GetHashCode(renderer));
                    hash *= prime;
                    hash ^= unchecked((uint)renderer.GetType().GetHashCode());
                    hash *= prime;
                }
                else if (raw is Array rendererArray)
                {
                    hash ^= unchecked((uint)rendererArray.Length);
                    hash *= prime;
                    for (int itemIndex = 0; itemIndex < rendererArray.Length; itemIndex++)
                    {
                        if (rendererArray.GetValue(itemIndex) is not UnityEngine.Renderer item)
                            continue;
                        hash ^= unchecked((uint)RuntimeHelpers.GetHashCode(item));
                        hash *= prime;
                        hash ^= unchecked((uint)item.GetType().GetHashCode());
                        hash *= prime;
                    }
                }
            }

            // Reverse push preserves the same logical child order in the signature.
            for (int i = count - 1; i >= 0; i--)
                stack.Push(node.subNodes[i]);
        }

        // A malformed/cyclic custom tree is still distinguishable from a normal complete walk.
        hash ^= unchecked((uint)remaining);
        hash *= prime;
        return hash;
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

        List<UnityEngine.GameObject> quarantinedGameObjects = new();
        try
        {
            for (int i = 0; i < state.QuarantineContainer.GetChildCount(); i++)
            {
                if (state.QuarantineContainer.GetChildAt(i) is FGameObjectNode gameObjectNode &&
                    gameObjectNode.gameObject != null)
                    quarantinedGameObjects.Add(gameObjectNode.gameObject);
            }
        }
        catch { }

        if (state.Page != null)
        {
            try { state.Page.ClearSprites(); }
            catch { }
        }

        for (int i = 0; i < quarantinedGameObjects.Count; i++)
        {
            try
            {
                if (quarantinedGameObjects[i] != null)
                    UnityEngine.Object.Destroy(quarantinedGameObjects[i]);
            }
            catch { }
        }

        try { state.QuarantineContainer.RemoveAllChildren(); }
        catch { }

        state.Page = null;
        state.Representation = null;
        state.Target = null;
        state.CachedControls = Array.Empty<LegacyControlSnapshot>();
        state.ModelFingerprint = string.Empty;
        state.TreeSignature = 0UL;
        state.LastModelAuditFrame = int.MinValue;
        state.LastTreeAuditFrame = int.MinValue;
        state.ControlsDirty = true;
        state.CompatibilityDirty = true;
        state.HasUnsupportedNodes = false;
        state.UnsupportedNodeExample = string.Empty;
    }
}
