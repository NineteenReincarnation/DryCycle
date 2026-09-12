using System;
using System.Collections.Generic;

namespace DryCycle.DevUI.DevTool.Preview;

/// <summary>
/// Session-local safety memory for advanced RoomEffect previews.
///
/// The cache is keyed only by RoomEffect.Type value. It deliberately does not remember a mod id,
/// assembly name or namespace. Once an effect demonstrates that its advanced bootstrap cannot be
/// rolled back safely, later hovers in the same plugin session keep the stage-one RoomEffect overlay
/// but skip constructor / HookGen bootstrap work.
/// </summary>
internal static class EffectPreviewSafetyRegistry
{
    private const int MaxEntries = 512;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, SafetyEntry> Entries =
        new(StringComparer.Ordinal);

    internal static bool IsAdvancedPreviewBlocked(string typeName, out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(typeName)) return false;

        lock (Gate)
        {
            if (!Entries.TryGetValue(typeName, out SafetyEntry entry) || !entry.Blocked)
                return false;
            reason = entry.Reason ?? string.Empty;
            return true;
        }
    }

    internal static void MarkUnsafe(string typeName, string reason)
    {
        if (string.IsNullOrWhiteSpace(typeName)) return;
        reason = string.IsNullOrWhiteSpace(reason) ? "unsafe rollback state" : reason.Trim();

        bool firstBlock = false;
        lock (Gate)
        {
            if (!Entries.TryGetValue(typeName, out SafetyEntry entry))
            {
                if (Entries.Count >= MaxEntries)
                    Entries.Clear();
                entry = new SafetyEntry();
                Entries[typeName] = entry;
            }

            firstBlock = !entry.Blocked;
            entry.Blocked = true;
            entry.Reason = reason;
            entry.Failures++;
        }

        if (firstBlock)
        {
            Plugin.Logger?.LogWarning(
                "DevTool disabled advanced hover preview for RoomEffect '" + typeName +
                "' for this session: " + reason + ". Stage-one RoomEffect preview remains enabled.");
        }
    }

    internal static void ObserveRollback(string typeName, EffectPreviewRollbackReport report)
    {
        if (string.IsNullOrWhiteSpace(typeName)) return;

        if (report.RuntimeContaminated || report.HasStrongLeak)
        {
            MarkUnsafe(typeName, report.Summary);
            return;
        }

        lock (Gate)
        {
            if (!Entries.TryGetValue(typeName, out SafetyEntry entry))
            {
                if (!report.SoftBaselineChanged) return;
                if (Entries.Count >= MaxEntries)
                    Entries.Clear();
                entry = new SafetyEntry();
                Entries[typeName] = entry;
            }

            // Count soft differences for diagnostics only. Normal Rain World rooms legitimately
            // create and remove particles during a hover, so a count/identity delta is not enough
            // evidence to blacklist an unknown effect.
            if (report.SoftBaselineChanged)
                entry.SoftMismatches++;
            else
                entry.CleanRollbacks++;
        }
    }

    internal static void Clear()
    {
        lock (Gate)
            Entries.Clear();
    }

    private sealed class SafetyEntry
    {
        internal bool Blocked;
        internal string Reason = string.Empty;
        internal int Failures;
        internal int SoftMismatches;
        internal int CleanRollbacks;
    }
}

internal readonly struct EffectPreviewRollbackReport
{
    internal EffectPreviewRollbackReport(
        bool hasStrongLeak,
        bool softBaselineChanged,
        bool runtimeContaminated,
        string summary)
    {
        HasStrongLeak = hasStrongLeak;
        SoftBaselineChanged = softBaselineChanged;
        RuntimeContaminated = runtimeContaminated;
        Summary = summary ?? string.Empty;
    }

    internal bool HasStrongLeak { get; }
    internal bool SoftBaselineChanged { get; }
    internal bool RuntimeContaminated { get; }
    internal string Summary { get; }

    internal static EffectPreviewRollbackReport Clean =>
        new(false, false, false, string.Empty);
}
