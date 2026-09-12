using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using MonoMod.RuntimeDetour;

namespace DryCycle.DevUI.DevTool.Preview;

/// <summary>
/// Extends preview ownership beyond Room.AddObject into runtime Futile mutations.
///
/// The tracker never identifies a mod. It learns the exact runtime objects created during the
/// synchronous preview bootstrap, then follows descendants created from those objects through the
/// public Room.AddObject choke point. While one of those objects is actually executing, Futile
/// add/reorder/remove operations are journaled by identity and reversed when hover ends.
///
/// Runtime RoomCamera mutation is treated more conservatively. Direct camera field writes or
/// non-trivial RoomCamera method calls discovered on preview-owned runtime types are rejected before
/// live propagation begins. Synchronous bootstrap-time camera changes remain handled by
/// EffectPreviewSceneStateJournal.
/// </summary>
internal static class EffectPreviewRuntimeVisualOwnership
{
    private static bool enabled;
    private static Hook addChildHook;
    private static Hook addChildAtIndexHook;
    private static Hook removeChildHook;
    private static Hook removeAllChildrenHook;
    private static RuntimeSession active;

    private delegate void OrigAddChild(FContainer self, FNode node);
    private delegate void HookAddChild(OrigAddChild orig, FContainer self, FNode node);
    private delegate void OrigAddChildAtIndex(FContainer self, FNode node, int index);
    private delegate void HookAddChildAtIndex(OrigAddChildAtIndex orig, FContainer self, FNode node, int index);
    private delegate void OrigRemoveChild(FContainer self, FNode node);
    private delegate void HookRemoveChild(OrigRemoveChild orig, FContainer self, FNode node);
    private delegate void OrigRemoveAllChildren(FContainer self);
    private delegate void HookRemoveAllChildren(OrigRemoveAllChildren orig, FContainer self);

    internal static void Enable()
    {
        if (enabled) return;

        try
        {
            MethodInfo addChild = typeof(FContainer).GetMethod(
                nameof(FContainer.AddChild),
                BindingFlags.Instance | BindingFlags.Public,
                null,
                new[] { typeof(FNode) },
                null);
            MethodInfo addChildAtIndex = typeof(FContainer).GetMethod(
                nameof(FContainer.AddChildAtIndex),
                BindingFlags.Instance | BindingFlags.Public,
                null,
                new[] { typeof(FNode), typeof(int) },
                null);
            MethodInfo removeChild = typeof(FContainer).GetMethod(
                nameof(FContainer.RemoveChild),
                BindingFlags.Instance | BindingFlags.Public,
                null,
                new[] { typeof(FNode) },
                null);
            MethodInfo removeAllChildren = typeof(FContainer).GetMethod(
                nameof(FContainer.RemoveAllChildren),
                BindingFlags.Instance | BindingFlags.Public,
                null,
                Type.EmptyTypes,
                null);

            if (addChild != null)
                addChildHook = new Hook(addChild, (HookAddChild)FContainer_AddChild);
            if (addChildAtIndex != null)
                addChildAtIndexHook = new Hook(addChildAtIndex, (HookAddChildAtIndex)FContainer_AddChildAtIndex);
            if (removeChild != null)
                removeChildHook = new Hook(removeChild, (HookRemoveChild)FContainer_RemoveChild);
            if (removeAllChildren != null)
                removeAllChildrenHook = new Hook(
                    removeAllChildren,
                    (HookRemoveAllChildren)FContainer_RemoveAllChildren);

            On.Room.AddObject += Room_AddObject;
            enabled = true;
        }
        catch (Exception error)
        {
            DisposeHooks();
            Plugin.Logger?.LogWarning(
                "DevTool effect preview could not install runtime Futile ownership hooks: " + error.Message);
        }
    }

    internal static void Disable()
    {
        if (!enabled)
        {
            active = null;
            return;
        }

        active = null;
        On.Room.AddObject -= Room_AddObject;
        DisposeHooks();
        RuntimeCameraRiskScanner.Clear();
        enabled = false;
    }

    internal static HashSet<UpdatableAndDeletable> CaptureBaseline(global::Room room)
    {
        HashSet<UpdatableAndDeletable> result =
            new(ReferenceEqualityComparer<UpdatableAndDeletable>.Instance);
        List<UpdatableAndDeletable> list = room?.updateList;
        if (list == null) return result;
        for (int i = 0; i < list.Count; i++)
        {
            UpdatableAndDeletable item = list[i];
            if (item != null) result.Add(item);
        }
        return result;
    }

    internal static bool TryAttach(
        global::Room room,
        HashSet<UpdatableAndDeletable> baseline,
        out string failureReason)
    {
        failureReason = string.Empty;
        active = null;
        if (room == null) return true;

        HashSet<UpdatableAndDeletable> owned =
            new(ReferenceEqualityComparer<UpdatableAndDeletable>.Instance);
        List<UpdatableAndDeletable> live = room.updateList;
        if (live != null)
        {
            for (int i = 0; i < live.Count; i++)
            {
                UpdatableAndDeletable item = live[i];
                if (item == null || baseline?.Contains(item) == true) continue;
                owned.Add(item);
            }
        }

        if (owned.Count == 0)
            return true;

        if (!RuntimeCameraRiskScanner.IsSafe(owned, out failureReason))
            return false;

        active = new RuntimeSession(room, owned);
        return true;
    }

    internal static EffectPreviewRuntimeVisualRollbackReport Rollback(string reason)
    {
        RuntimeSession session = active;
        active = null;
        return session?.Rollback(reason) ?? EffectPreviewRuntimeVisualRollbackReport.Clean;
    }

    internal static void DetachWithoutRollback()
    {
        active = null;
    }

    private static void Room_AddObject(
        On.Room.orig_AddObject orig,
        global::Room self,
        UpdatableAndDeletable obj)
    {
        RuntimeSession session = active;
        bool propagate = session != null && session.IsOwnedExecution(self);

        orig(self, obj);

        if (!propagate || obj == null || obj is PhysicalObject || session == null)
            return;

        if (!ReferenceEquals(self, session.Room))
            return;

        bool attached = ReferenceEquals(obj.room, self) || ContainsReference(self.updateList, obj);
        if (attached)
            session.AddOwnedRuntimeObject(obj);
    }

    private static void FContainer_AddChild(OrigAddChild orig, FContainer self, FNode node)
    {
        RuntimeSession session = active;
        if (session == null || self == null || node == null || !session.IsOwnedExecution(session.Room))
        {
            orig(self, node);
            return;
        }

        NodePlacement before = NodePlacement.Capture(node);
        orig(self, node);
        session.ObserveNodeMutation(node, before, NodePlacement.Capture(node));
    }

    private static void FContainer_AddChildAtIndex(
        OrigAddChildAtIndex orig,
        FContainer self,
        FNode node,
        int index)
    {
        RuntimeSession session = active;
        if (session == null || self == null || node == null || !session.IsOwnedExecution(session.Room))
        {
            orig(self, node, index);
            return;
        }

        NodePlacement before = NodePlacement.Capture(node);
        orig(self, node, index);
        session.ObserveNodeMutation(node, before, NodePlacement.Capture(node));
    }

    private static void FContainer_RemoveChild(OrigRemoveChild orig, FContainer self, FNode node)
    {
        RuntimeSession session = active;
        if (session == null || self == null || node == null || !session.IsOwnedExecution(session.Room))
        {
            orig(self, node);
            return;
        }

        NodePlacement before = NodePlacement.Capture(node);
        orig(self, node);
        session.ObserveNodeMutation(node, before, NodePlacement.Capture(node));
    }

    private static void FContainer_RemoveAllChildren(OrigRemoveAllChildren orig, FContainer self)
    {
        RuntimeSession session = active;
        if (session == null || self == null || !session.IsOwnedExecution(session.Room))
        {
            orig(self);
            return;
        }

        List<NodeMutationProbe> probes = new();
        List<FNode> children = self._childNodes;
        if (children != null)
        {
            for (int i = 0; i < children.Count; i++)
            {
                FNode node = children[i];
                if (node != null)
                    probes.Add(new NodeMutationProbe(node, NodePlacement.Capture(node)));
            }
        }

        orig(self);

        for (int i = 0; i < probes.Count; i++)
        {
            NodeMutationProbe probe = probes[i];
            session.ObserveNodeMutation(
                probe.Node,
                probe.Before,
                NodePlacement.Capture(probe.Node));
        }
    }

    private static void DisposeHooks()
    {
        try { addChildHook?.Dispose(); }
        catch { }
        try { addChildAtIndexHook?.Dispose(); }
        catch { }
        try { removeChildHook?.Dispose(); }
        catch { }
        try { removeAllChildrenHook?.Dispose(); }
        catch { }
        addChildHook = null;
        addChildAtIndexHook = null;
        removeChildHook = null;
        removeAllChildrenHook = null;
    }

    private static bool ContainsReference(List<UpdatableAndDeletable> values, UpdatableAndDeletable target)
    {
        if (values == null || target == null) return false;
        for (int i = 0; i < values.Count; i++)
            if (ReferenceEquals(values[i], target)) return true;
        return false;
    }

    private sealed class RuntimeSession
    {
        private readonly global::Room room;
        private readonly HashSet<UpdatableAndDeletable> ownedRuntimeObjects;
        private readonly HashSet<FNode> ownedNodes =
            new(ReferenceEqualityComparer<FNode>.Instance);
        private readonly List<FNode> ownedNodeOrder = new();
        private readonly Dictionary<FNode, NodeMoveMutation> nodeMoves =
            new(ReferenceEqualityComparer<FNode>.Instance);
        private readonly List<FNode> movedNodeOrder = new();

        internal RuntimeSession(global::Room room, HashSet<UpdatableAndDeletable> ownedRuntimeObjects)
        {
            this.room = room;
            this.ownedRuntimeObjects = ownedRuntimeObjects ??
                                       new HashSet<UpdatableAndDeletable>(
                                           ReferenceEqualityComparer<UpdatableAndDeletable>.Instance);
        }

        internal global::Room Room => room;

        internal void AddOwnedRuntimeObject(UpdatableAndDeletable obj)
        {
            if (obj != null) ownedRuntimeObjects.Add(obj);
        }

        internal bool IsOwnedExecution(global::Room candidateRoom)
        {
            if (room == null || candidateRoom == null || !ReferenceEquals(room, candidateRoom) ||
                ownedRuntimeObjects.Count == 0)
                return false;

            int index = room.updateIndex;
            List<UpdatableAndDeletable> live = room.updateList;
            if (live != null && index >= 0 && index < live.Count)
            {
                UpdatableAndDeletable current = live[index];
                if (current != null && ownedRuntimeObjects.Contains(current))
                    return true;
            }

            StackFrame[] frames;
            try { frames = new StackTrace(2, false).GetFrames(); }
            catch { return false; }
            if (frames == null || frames.Length == 0 || live == null)
                return false;

            for (int i = 0; i < frames.Length; i++)
            {
                MethodBase method;
                try { method = frames[i].GetMethod(); }
                catch { continue; }

                Type declaring = method?.DeclaringType;
                if (declaring == null || declaring == typeof(UpdatableAndDeletable) ||
                    !typeof(UpdatableAndDeletable).IsAssignableFrom(declaring))
                    continue;

                bool found = false;
                bool allOwned = true;
                for (int n = 0; n < live.Count; n++)
                {
                    UpdatableAndDeletable candidate = live[n];
                    if (candidate == null || !declaring.IsAssignableFrom(candidate.GetType()))
                        continue;
                    found = true;
                    if (!ownedRuntimeObjects.Contains(candidate))
                    {
                        allOwned = false;
                        break;
                    }
                }

                if (found && allOwned)
                    return true;
            }

            return false;
        }

        internal void ObserveNodeMutation(FNode node, NodePlacement before, NodePlacement after)
        {
            if (node == null || before.Equals(after))
                return;

            if (ownedNodes.Contains(node))
                return;

            if (before.Container == null && after.Container != null)
            {
                if (ownedNodes.Add(node))
                    ownedNodeOrder.Add(node);
                return;
            }

            if (before.Container == null)
                return;

            if (!nodeMoves.TryGetValue(node, out NodeMoveMutation mutation))
            {
                mutation = new NodeMoveMutation(before, after);
                nodeMoves[node] = mutation;
                movedNodeOrder.Add(node);
            }
            else
            {
                mutation.PreviewPlacement = after;
                nodeMoves[node] = mutation;
            }
        }

        internal EffectPreviewRuntimeVisualRollbackReport Rollback(string reason)
        {
            int nodeLeaks = 0;
            int moveLeaks = 0;
            int ambiguousMoves = 0;

            for (int i = ownedNodeOrder.Count - 1; i >= 0; i--)
            {
                FNode node = ownedNodeOrder[i];
                if (node == null) continue;
                try { node.RemoveFromContainer(); }
                catch { nodeLeaks++; }

                try
                {
                    if (node.container != null)
                        nodeLeaks++;
                }
                catch
                {
                    nodeLeaks++;
                }
            }

            for (int i = movedNodeOrder.Count - 1; i >= 0; i--)
            {
                FNode node = movedNodeOrder[i];
                if (node == null || !nodeMoves.TryGetValue(node, out NodeMoveMutation mutation))
                    continue;

                NodePlacement current = NodePlacement.Capture(node);
                if (current.Equals(mutation.OriginalPlacement))
                    continue;

                if (!current.Equals(mutation.PreviewPlacement))
                {
                    ambiguousMoves++;
                    continue;
                }

                try
                {
                    FContainer original = mutation.OriginalPlacement.Container;
                    if (original == null)
                    {
                        node.RemoveFromContainer();
                    }
                    else
                    {
                        int count = original._childNodes?.Count ?? 0;
                        int index = mutation.OriginalPlacement.Index;
                        if (index < 0) index = count;
                        if (index > count) index = count;
                        original.AddChildAtIndex(node, index);
                    }

                    if (!NodePlacement.Capture(node).Equals(mutation.OriginalPlacement))
                        moveLeaks++;
                }
                catch
                {
                    moveLeaks++;
                }
            }

            bool leak = nodeLeaks > 0 || moveLeaks > 0 || ambiguousMoves > 0;
            string summary = leak
                ? "runtime visual rollback: nodeLeaks=" + nodeLeaks +
                  ", moveLeaks=" + moveLeaks +
                  ", ambiguousMoves=" + ambiguousMoves +
                  (string.IsNullOrWhiteSpace(reason) ? string.Empty : " during " + reason)
                : string.Empty;

            ownedNodes.Clear();
            ownedNodeOrder.Clear();
            nodeMoves.Clear();
            movedNodeOrder.Clear();
            ownedRuntimeObjects.Clear();
            return new EffectPreviewRuntimeVisualRollbackReport(leak, summary);
        }
    }

    private readonly struct NodeMutationProbe
    {
        internal NodeMutationProbe(FNode node, NodePlacement before)
        {
            Node = node;
            Before = before;
        }

        internal FNode Node { get; }
        internal NodePlacement Before { get; }
    }

    private readonly struct NodePlacement : IEquatable<NodePlacement>
    {
        internal NodePlacement(FContainer container, int index)
        {
            Container = container;
            Index = index;
        }

        internal FContainer Container { get; }
        internal int Index { get; }

        internal static NodePlacement Capture(FNode node)
        {
            if (node == null) return new NodePlacement(null, -1);
            FContainer container;
            try { container = node.container; }
            catch { return new NodePlacement(null, -1); }
            if (container == null) return new NodePlacement(null, -1);

            int index = -1;
            try { index = container._childNodes?.IndexOf(node) ?? -1; }
            catch { }
            return new NodePlacement(container, index);
        }

        public bool Equals(NodePlacement other) =>
            ReferenceEquals(Container, other.Container) && Index == other.Index;

        public override bool Equals(object obj) => obj is NodePlacement other && Equals(other);
        public override int GetHashCode() =>
            ((Container == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Container)) * 397) ^ Index;
    }

    private struct NodeMoveMutation
    {
        internal NodeMoveMutation(NodePlacement originalPlacement, NodePlacement previewPlacement)
        {
            OriginalPlacement = originalPlacement;
            PreviewPlacement = previewPlacement;
        }

        internal NodePlacement OriginalPlacement;
        internal NodePlacement PreviewPlacement;
    }

    private static class RuntimeCameraRiskScanner
    {
        private const int MaxDepth = 4;
        private const int MaxMethods = 160;
        private static readonly Dictionary<Type, string> Cache = new();

        internal static bool IsSafe(
            HashSet<UpdatableAndDeletable> objects,
            out string failureReason)
        {
            failureReason = string.Empty;
            if (objects == null || objects.Count == 0) return true;

            HashSet<Type> types = new();
            foreach (UpdatableAndDeletable obj in objects)
            {
                Type type = obj?.GetType();
                if (type != null) types.Add(type);
            }

            foreach (Type type in types)
            {
                string cached;
                lock (Cache)
                {
                    Cache.TryGetValue(type, out cached);
                }

                if (cached == null)
                {
                    cached = ScanType(type);
                    lock (Cache)
                        Cache[type] = cached ?? string.Empty;
                }

                if (string.IsNullOrEmpty(cached))
                    continue;

                failureReason = cached;
                return false;
            }

            return true;
        }

        internal static void Clear()
        {
            lock (Cache)
                Cache.Clear();
        }

        private static string ScanType(Type rootType)
        {
            if (rootType == null) return string.Empty;
            HashSet<MethodBase> visited = new();

            MethodInfo[] methods;
            try
            {
                methods = rootType.GetMethods(
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                    BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            }
            catch
            {
                return string.Empty;
            }

            for (int i = 0; i < methods.Length; i++)
            {
                string risk = ScanMethod(methods[i], rootType.Assembly, visited, 0);
                if (!string.IsNullOrEmpty(risk))
                    return rootType.FullName + ": " + risk;
            }

            return string.Empty;
        }

        private static string ScanMethod(
            MethodBase method,
            Assembly rootAssembly,
            HashSet<MethodBase> visited,
            int depth)
        {
            if (method == null || depth > MaxDepth || visited.Count >= MaxMethods || !visited.Add(method))
                return string.Empty;

            List<DecodedInstruction> il = DecodedInstructionReader.Read(method);
            for (int i = 0; i < il.Count; i++)
            {
                DecodedInstruction instruction = il[i];
                if (instruction.OpCode == OpCodes.Stfld && instruction.Operand is FieldInfo field &&
                    field.DeclaringType != null && typeof(RoomCamera).IsAssignableFrom(field.DeclaringType))
                {
                    return "runtime method " + method.Name +
                           " writes RoomCamera field " + field.Name + " directly";
                }

                if (instruction.Operand is not MethodBase called)
                    continue;

                Type declaring = called.DeclaringType;
                if (declaring != null && typeof(RoomCamera).IsAssignableFrom(declaring) &&
                    !IsSafeCameraCall(called))
                {
                    return "runtime method " + method.Name +
                           " calls mutable RoomCamera API " + called.Name;
                }

                if (called.Module?.Assembly != rootAssembly || called == method)
                    continue;

                string nested = ScanMethod(called, rootAssembly, visited, depth + 1);
                if (!string.IsNullOrEmpty(nested))
                    return nested;
            }

            return string.Empty;
        }

        private static bool IsSafeCameraCall(MethodBase method)
        {
            string name = method?.Name ?? string.Empty;
            if (name.StartsWith("get_", StringComparison.Ordinal)) return true;
            if (name == "ReturnFContainer") return true;
            if (name == "NewObjectInRoom") return true;
            return false;
        }
    }
}

internal readonly struct EffectPreviewRuntimeVisualRollbackReport
{
    internal EffectPreviewRuntimeVisualRollbackReport(bool hasLeak, string summary)
    {
        HasLeak = hasLeak;
        Summary = summary ?? string.Empty;
    }

    internal bool HasLeak { get; }
    internal string Summary { get; }

    internal static EffectPreviewRuntimeVisualRollbackReport Clean => new(false, string.Empty);
}
