using System;
using System.Collections.Generic;
using ImGuiNET;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Stable scope names for shared scalar numeric editors.
/// State identity is (scope, instance, key), so callers do not need per-frame concatenated IDs.
/// </summary>
internal static class DevToolNumericScope
{
    internal const string Room = "Room";
    internal const string Relationship = "Relationship";
    internal const string Trigger = "Trigger";
    internal const string SoundRoom = "SoundRoom";
    internal const string SoundItem = "SoundItem";
    internal const string Universal = "Universal";
    internal const string ObjectTransform = "ObjectTransform";
    internal const string ObjectProperty = "ObjectProperty";
    internal const string ObjectLegacy = "ObjectLegacy";
}

internal readonly struct DevToolNumericEditResult<T>
{
    internal DevToolNumericEditResult(T value, bool committed)
    {
        Value = value;
        Committed = committed;
    }

    internal T Value { get; }
    internal bool Committed { get; }
}

/// <summary>
/// Shared human-facing edit transaction for scalar ImGui numeric controls.
///
/// Semantics:
/// - Drag / Ctrl+Click text entry owns a local dirty value while the widget is active.
/// - Mouse release, Enter, or clicking elsewhere commits exactly once.
/// - A committed value remains visible until the authoritative backend snapshot acknowledges it,
///   preventing the one-frame "snap back" that made typed values look rejected.
/// - Slider text entry is clamped to the same range as dragging.
///
/// Only active dirty/pending edits are retained. A low-frequency stale audit removes abandoned
/// transactions when a control disappears immediately after editing (delete, page switch, etc.).
/// </summary>
internal static class DevToolNumericWidgets
{
    private const int PendingAckFrameLimit = 12;
    private const int StaleStateFrameLimit = 600;
    private const int PruneIntervalFrames = 120;

    private readonly struct EditKey : IEquatable<EditKey>
    {
        internal EditKey(string scope, int instance, string key)
        {
            Scope = scope ?? string.Empty;
            Instance = instance;
            Key = key ?? string.Empty;
        }

        internal string Scope { get; }
        internal int Instance { get; }
        internal string Key { get; }

        public bool Equals(EditKey other) =>
            Instance == other.Instance &&
            string.Equals(Scope, other.Scope, StringComparison.Ordinal) &&
            string.Equals(Key, other.Key, StringComparison.Ordinal);

        public override bool Equals(object obj) => obj is EditKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = StringComparer.Ordinal.GetHashCode(Scope);
                hash = (hash * 397) ^ Instance;
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(Key);
                return hash;
            }
        }
    }

    private struct FloatState
    {
        internal float LocalValue;
        internal bool Dirty;
        internal bool Pending;
        internal float PendingValue;
        internal int SubmittedFrame;
        internal int LastTouchedFrame;
    }

    private struct IntState
    {
        internal int LocalValue;
        internal bool Dirty;
        internal bool Pending;
        internal int PendingValue;
        internal int SubmittedFrame;
        internal int LastTouchedFrame;
    }

    private static readonly Dictionary<EditKey, FloatState> FloatStates = new();
    private static readonly Dictionary<EditKey, IntState> IntStates = new();
    private static readonly List<EditKey> StaleKeys = new();
    private static int nextPruneFrame;

    internal static DevToolNumericEditResult<float> SliderFloat(
        string scope,
        string stateKey,
        string label,
        float authoritativeValue,
        float min,
        float max,
        string format = "%.3f",
        int instance = -1)
    {
        int frame = PrepareFrame();
        EditKey key = new(scope, instance, stateKey);
        FloatState state = BeginFloat(key, authoritativeValue, frame);
        float value = state.LocalValue;
        bool changed = ImGui.SliderFloat(
            label,
            ref value,
            min,
            max,
            format,
            ImGuiSliderFlags.AlwaysClamp);
        return EndFloat(key, authoritativeValue, value, changed, state, frame);
    }

    internal static DevToolNumericEditResult<float> InputFloat(
        string scope,
        string stateKey,
        string label,
        float authoritativeValue,
        float step = 0.1f,
        string format = "%.3f",
        int instance = -1)
    {
        int frame = PrepareFrame();
        EditKey key = new(scope, instance, stateKey);
        FloatState state = BeginFloat(key, authoritativeValue, frame);
        float value = state.LocalValue;
        bool changed = ImGui.InputFloat(label, ref value, step, 0f, format);
        return EndFloat(key, authoritativeValue, value, changed, state, frame);
    }

    internal static DevToolNumericEditResult<int> SliderInt(
        string scope,
        string stateKey,
        string label,
        int authoritativeValue,
        int min,
        int max,
        string format = "%d",
        int instance = -1)
    {
        int frame = PrepareFrame();
        EditKey key = new(scope, instance, stateKey);
        IntState state = BeginInt(key, authoritativeValue, frame);
        int value = state.LocalValue;
        bool changed = ImGui.SliderInt(
            label,
            ref value,
            min,
            max,
            format,
            ImGuiSliderFlags.AlwaysClamp);
        return EndInt(key, authoritativeValue, value, changed, state, frame);
    }

    internal static DevToolNumericEditResult<int> InputInt(
        string scope,
        string stateKey,
        string label,
        int authoritativeValue,
        int step = 1,
        int stepFast = 10,
        int instance = -1,
        int? min = null,
        int? max = null)
    {
        int frame = PrepareFrame();
        EditKey key = new(scope, instance, stateKey);
        IntState state = BeginInt(key, authoritativeValue, frame);
        int value = state.LocalValue;
        bool changed = ImGui.InputInt(label, ref value, step, stepFast);
        if (min.HasValue && value < min.Value) value = min.Value;
        if (max.HasValue && value > max.Value) value = max.Value;
        return EndInt(key, authoritativeValue, value, changed, state, frame);
    }

    internal static void Discard(string scope, string stateKey, int instance = -1)
    {
        EditKey key = new(scope, instance, stateKey);
        FloatStates.Remove(key);
        IntStates.Remove(key);
    }

    internal static void Reset()
    {
        FloatStates.Clear();
        IntStates.Clear();
        StaleKeys.Clear();
        nextPruneFrame = 0;
    }

    private static int PrepareFrame()
    {
        int frame = ImGui.GetFrameCount();
        if (frame < nextPruneFrame) return frame;
        nextPruneFrame = frame + PruneIntervalFrames;
        PruneStaleStates(frame);
        return frame;
    }

    private static void PruneStaleStates(int frame)
    {
        if (FloatStates.Count > 0)
        {
            StaleKeys.Clear();
            foreach (KeyValuePair<EditKey, FloatState> pair in FloatStates)
            {
                if (frame - pair.Value.LastTouchedFrame >= StaleStateFrameLimit)
                    StaleKeys.Add(pair.Key);
            }
            for (int i = 0; i < StaleKeys.Count; i++)
                FloatStates.Remove(StaleKeys[i]);
        }

        if (IntStates.Count > 0)
        {
            StaleKeys.Clear();
            foreach (KeyValuePair<EditKey, IntState> pair in IntStates)
            {
                if (frame - pair.Value.LastTouchedFrame >= StaleStateFrameLimit)
                    StaleKeys.Add(pair.Key);
            }
            for (int i = 0; i < StaleKeys.Count; i++)
                IntStates.Remove(StaleKeys[i]);
        }

        StaleKeys.Clear();
    }

    private static FloatState BeginFloat(EditKey key, float authoritativeValue, int frame)
    {
        if (!FloatStates.TryGetValue(key, out FloatState state))
        {
            state.LocalValue = authoritativeValue;
            state.LastTouchedFrame = frame;
            return state;
        }

        state.LastTouchedFrame = frame;
        if (state.Dirty)
            return state;

        if (!state.Pending)
        {
            state.LocalValue = authoritativeValue;
            return state;
        }

        int age = frame - state.SubmittedFrame;
        if (NearlyEqual(authoritativeValue, state.PendingValue) || age >= PendingAckFrameLimit)
        {
            state.Pending = false;
            state.LocalValue = authoritativeValue;
        }
        else
        {
            state.LocalValue = state.PendingValue;
        }
        return state;
    }

    private static DevToolNumericEditResult<float> EndFloat(
        EditKey key,
        float authoritativeValue,
        float value,
        bool changed,
        FloatState state,
        int frame)
    {
        state.LastTouchedFrame = frame;
        if (changed)
        {
            state.LocalValue = value;
            state.Dirty = true;
            state.Pending = false;
        }

        bool committed = false;
        if (!ImGui.IsItemActive() && state.Dirty)
        {
            state.Dirty = false;
            if (!NearlyEqual(value, authoritativeValue))
            {
                state.Pending = true;
                state.PendingValue = value;
                state.SubmittedFrame = frame;
                state.LocalValue = value;
                committed = true;
            }
            else
            {
                state.LocalValue = authoritativeValue;
            }
        }

        if (state.Dirty || state.Pending)
            FloatStates[key] = state;
        else
            FloatStates.Remove(key);

        return new DevToolNumericEditResult<float>(state.LocalValue, committed);
    }

    private static IntState BeginInt(EditKey key, int authoritativeValue, int frame)
    {
        if (!IntStates.TryGetValue(key, out IntState state))
        {
            state.LocalValue = authoritativeValue;
            state.LastTouchedFrame = frame;
            return state;
        }

        state.LastTouchedFrame = frame;
        if (state.Dirty)
            return state;

        if (!state.Pending)
        {
            state.LocalValue = authoritativeValue;
            return state;
        }

        int age = frame - state.SubmittedFrame;
        if (authoritativeValue == state.PendingValue || age >= PendingAckFrameLimit)
        {
            state.Pending = false;
            state.LocalValue = authoritativeValue;
        }
        else
        {
            state.LocalValue = state.PendingValue;
        }
        return state;
    }

    private static DevToolNumericEditResult<int> EndInt(
        EditKey key,
        int authoritativeValue,
        int value,
        bool changed,
        IntState state,
        int frame)
    {
        state.LastTouchedFrame = frame;
        if (changed)
        {
            state.LocalValue = value;
            state.Dirty = true;
            state.Pending = false;
        }

        bool committed = false;
        if (!ImGui.IsItemActive() && state.Dirty)
        {
            state.Dirty = false;
            if (value != authoritativeValue)
            {
                state.Pending = true;
                state.PendingValue = value;
                state.SubmittedFrame = frame;
                state.LocalValue = value;
                committed = true;
            }
            else
            {
                state.LocalValue = authoritativeValue;
            }
        }

        if (state.Dirty || state.Pending)
            IntStates[key] = state;
        else
            IntStates.Remove(key);

        return new DevToolNumericEditResult<int>(state.LocalValue, committed);
    }

    private static bool NearlyEqual(float left, float right) => Math.Abs(left - right) <= 0.0001f;
}
