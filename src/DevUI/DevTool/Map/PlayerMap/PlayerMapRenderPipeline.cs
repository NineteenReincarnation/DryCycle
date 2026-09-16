using System;
using System.Collections.Generic;
using DevInterface;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Map.PlayerMap;

public readonly struct PlayerMapPreviewRun
{
    public PlayerMapPreviewRun(int x, int y, int length, Color32 color)
    {
        X = x;
        Y = y;
        Length = length;
        Color = color;
    }

    public int X { get; }
    public int Y { get; }
    public int Length { get; }
    public Color32 Color { get; }
}

public sealed class PlayerMapRenderPreview
{
    public static readonly PlayerMapRenderPreview Empty = new();

    public bool Available { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public PlayerMapPreviewRun[] Runs { get; init; } = Array.Empty<PlayerMapPreviewRun>();
}

public sealed class PlayerMapRenderReport
{
    public static readonly PlayerMapRenderReport Empty = new();

    public bool Attempted { get; init; }
    public bool Success { get; init; }
    public bool Exported { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int IncludedRooms { get; init; }
    public string OutputPath { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string[] Errors { get; init; } = Array.Empty<string>();
    public string[] Warnings { get; init; } = Array.Empty<string>();

    internal static PlayerMapRenderReport Failure(string message, params string[] errors) => new()
    {
        Attempted = true,
        Success = false,
        Message = message ?? "Render failed.",
        Errors = errors ?? Array.Empty<string>()
    };
}

/// <summary>
/// Frozen room entry used only by the authoritative incremental Render scheduler.
/// </summary>
internal sealed class PlayerMapRenderRoom
{
    internal int RoomIndex;
    internal string Name = string.Empty;
    internal int Layer;
    internal Vector2 CanonicalPosition;
    internal RoomMapBake Bake;
    internal int PixelX;
    internal int PixelYInLayer;
}

/// <summary>
/// Immutable-in-practice output plan shared by incremental preflight, composition, metadata and
/// preview generation. PNG and map_image are therefore derived from the exact same pixel rectangles.
/// </summary>
internal sealed class PlayerMapRenderPlan
{
    internal int Width;
    internal int LayerHeight;
    internal int Height;
    internal float CanonMinX;
    internal float CanonMinY;
    internal readonly List<PlayerMapRenderRoom> Rooms = new();
    internal readonly List<PlayerMapDefMaterialState> DefaultMaterials = new();
    internal readonly List<string> Warnings = new();
}

/// <summary>
/// Transitional command result type retained because PlayerMapWorkspaceRuntime.Render still has an
/// internal compatibility call site. It no longer contains a second renderer.
/// </summary>
internal sealed class PlayerMapRenderOutcome
{
    internal PlayerMapRenderReport Report = PlayerMapRenderReport.Empty;
    internal PlayerMapRenderPreview Preview = PlayerMapRenderPreview.Empty;
}

/// <summary>
/// Compatibility boundary only. The former all-at-once renderer has been deleted; all real Player
/// Map rendering is owned by PlayerMapRenderScheduler. Reaching this method means scheduler command
/// interception was unavailable, so fail closed without writing or previewing stale output.
///
/// This fallback intentionally remains callable without an Obsolete attribute: it is a runtime
/// safety boundary used by the workspace when the scheduler hook is unavailable, not a supported
/// rendering implementation. Keeping it warning-free avoids flagging the deliberate fail-closed
/// compatibility call as if the removed synchronous renderer were still in use.
/// </summary>
internal static class PlayerMapRenderPipeline
{
    internal static PlayerMapRenderOutcome Build(MapPage page, PlayerMapSessionState state, bool export) => new()
    {
        Report = PlayerMapRenderReport.Failure(
            "Player Map render was blocked before output.",
            "The obsolete synchronous Render Map path has been removed; the incremental scheduler must own rendering."),
        Preview = PlayerMapRenderPreview.Empty
    };
}
