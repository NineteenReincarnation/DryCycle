using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace DryCycle.DevUI.DevTool.Preview;

/// <summary>
/// Session-local negative knowledge cache for Effect Preview.
///
/// If a RoomEffect has already been probed in the same realized Room instance and advanced preview
/// produced no runtime object, no reversible Room field mutation, no synchronous Camera/Futile delta
/// and no global shader state, repeating the expensive IL / Hook discovery adds no value. Later
/// hovers in that exact realization can use the cheap stage-one RoomEffect overlay directly.
///
/// The cache is keyed by the Rain World Room object itself plus RoomEffect.Type. Re-realizing a room
/// automatically gets a fresh cache entry, and no mod id, assembly name, namespace or third-party
/// registry is involved.
/// </summary>
internal static class EffectPreviewKnowledgeCache
{
    private static readonly object Gate = new();
    private static ConditionalWeakTable<global::Room, RoomKnowledge> rooms = new();

    internal static bool ShouldUseStageOneOnly(global::Room room, string typeName)
    {
        if (room == null || string.IsNullOrWhiteSpace(typeName)) return false;
        lock (Gate)
        {
            return rooms.TryGetValue(room, out RoomKnowledge knowledge) &&
                   knowledge.StageOneOnly.Contains(typeName);
        }
    }

    internal static void MarkStageOneOnly(global::Room room, string typeName)
    {
        if (room == null || string.IsNullOrWhiteSpace(typeName)) return;
        lock (Gate)
        {
            RoomKnowledge knowledge = rooms.GetValue(room, _ => new RoomKnowledge());
            knowledge.StageOneOnly.Add(typeName);
        }
    }

    internal static void Clear()
    {
        lock (Gate)
            rooms = new ConditionalWeakTable<global::Room, RoomKnowledge>();
    }

    private sealed class RoomKnowledge
    {
        internal HashSet<string> StageOneOnly { get; } = new(StringComparer.Ordinal);
    }
}
