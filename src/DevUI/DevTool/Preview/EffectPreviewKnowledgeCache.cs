using System;
using System.Collections.Generic;

namespace DryCycle.DevUI.DevTool.Preview;

/// <summary>
/// Session-local negative knowledge cache for Effect Preview.
///
/// If a RoomEffect has already been probed in the same realized room and advanced preview produced
/// no runtime object, no reversible Room field mutation, no synchronous Camera/Futile delta and no
/// global shader state, repeating the expensive IL / Hook discovery adds no value. That room/effect
/// pair is therefore allowed to use the cheap stage-one RoomEffect overlay directly on later hovers.
///
/// The key intentionally contains only Rain World room identity + RoomEffect.Type. No mod id,
/// assembly name, namespace or third-party registry is involved.
/// </summary>
internal static class EffectPreviewKnowledgeCache
{
    private const int MaxEntries = 1024;
    private static readonly object Gate = new();
    private static readonly HashSet<string> StageOneOnly = new(StringComparer.Ordinal);

    internal static bool ShouldUseStageOneOnly(global::Room room, string typeName)
    {
        string key = Key(room, typeName);
        if (string.IsNullOrEmpty(key)) return false;
        lock (Gate)
            return StageOneOnly.Contains(key);
    }

    internal static void MarkStageOneOnly(global::Room room, string typeName)
    {
        string key = Key(room, typeName);
        if (string.IsNullOrEmpty(key)) return;

        lock (Gate)
        {
            if (StageOneOnly.Count >= MaxEntries)
                StageOneOnly.Clear();
            StageOneOnly.Add(key);
        }
    }

    internal static void Clear()
    {
        lock (Gate)
            StageOneOnly.Clear();
    }

    private static string Key(global::Room room, string typeName)
    {
        if (room == null || string.IsNullOrWhiteSpace(typeName)) return string.Empty;
        string roomName = room.abstractRoom?.name ?? room.roomSettings?.name ?? string.Empty;
        if (string.IsNullOrEmpty(roomName)) return string.Empty;
        return roomName + "\n" + typeName;
    }
}
