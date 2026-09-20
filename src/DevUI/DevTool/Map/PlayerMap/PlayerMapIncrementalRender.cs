using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.World;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Map.PlayerMap;

public enum PlayerMapRenderStage
{
    Idle = 0,
    PreflightRooms = 1,
    PreflightConnections = 2,
    PrepareOutput = 3,
    ClearOutput = 4,
    ComposeRooms = 5,
    BuildPreview = 6,
    PrepareExport = 7,
    EncodePng = 8,
    WriteMetadata = 9,
    CommitFiles = 10,
    Completed = 11,
    Cancelled = 12,
    Failed = 13
}

public sealed class PlayerMapRenderProgressSnapshot
{
    public static readonly PlayerMapRenderProgressSnapshot Idle = new();

    public bool Running { get; init; }
    public bool ExportRequested { get; init; }
    public bool CanCancel { get; init; }
    public PlayerMapRenderStage Stage { get; init; }
    public float Progress { get; init; }
    public float StageProgress { get; init; }
    public long CompletedUnits { get; init; }
    public long TotalUnits { get; init; }
    public string StageLabel { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
}

/// <summary>
/// Incremental, deterministic Player Map renderer. Work is split over editor frames so large regions
/// do not freeze the DevTool and the right inspector can report real progress. The job freezes its
/// input snapshot at start, validates exact multi-pipe endpoints, composes into a CPU Color32 buffer,
/// then stages and atomically commits the vanilla-compatible PNG/metadata pair.
/// </summary>
internal static class PlayerMapRenderScheduler
{
    private sealed class Job
    {
        internal EditorSession Session;
        internal MapPage Page;
        internal string Region = string.Empty;
        internal bool Export;
        internal int TopologyRevision;
        internal int WorldTextRevision;
        internal int BakeRevision;
        internal PlayerMapRoomSnapshot[] SourceRooms = Array.Empty<PlayerMapRoomSnapshot>();
        internal PlayerMapDefMaterialSnapshot[] SourceDefs = Array.Empty<PlayerMapDefMaterialSnapshot>();
        internal EditorMapConnectionSnapshot[] Connections = Array.Empty<EditorMapConnectionSnapshot>();

        internal readonly PlayerMapRenderPlan Plan = new();
        internal readonly Dictionary<int, PlayerMapRenderRoom> RoomsByIndex = new();
        internal readonly HashSet<string> ClaimedEndpoints = new(StringComparer.Ordinal);
        internal readonly List<string> Errors = new();
        internal readonly List<PlayerMapPreviewRun> PreviewRuns = new();

        internal float MinX = float.MaxValue;
        internal float MinY = float.MaxValue;
        internal float MaxX = float.MinValue;
        internal float MaxY = float.MinValue;
        internal int RoomCursor;
        internal int ConnectionCursor;
        internal int ClearCursor;
        internal int ComposeRoomCursor;
        internal int ComposePixelCursor;
        internal long TotalComposePixels;
        internal long ComposedPixels;
        internal int PreviewRow;
        internal Color32[] Pixels;
        internal PlayerMapRenderStage Stage = PlayerMapRenderStage.PreflightRooms;
        internal bool CancelRequested;

        internal string PngPath = string.Empty;
        internal string MetaPath = string.Empty;
        internal string TempPng = string.Empty;
        internal string TempMeta = string.Empty;
    }

    private static readonly Color32 Background = new(0, 255, 0, 255);
    private static readonly Color32 SolidColor = new(0, 0, 0, 255);
    private static readonly Color32 BrightDry = new(255, 0, 0, 255);
    private static readonly Color32 BrightWet = new(255, 0, 255, 255);
    private static readonly Color32 StructureDry = new(153, 0, 0, 255);
    private static readonly Color32 StructureWet = new(153, 0, 255, 255);

    private const int RoomsPerFrame = 16;
    private const int ConnectionsPerFrame = 32;
    private const int ClearPixelsPerFrame = 1_000_000;
    private const int ComposePixelsPerFrame = 180_000;
    private const int PreviewRowsPerFrame = 56;
    private const int MaxDimension = 16384;
    private const long MaxPixels = 160_000_000L;

    private static Job active;
    private static PlayerMapRenderProgressSnapshot progress = PlayerMapRenderProgressSnapshot.Idle;
    private static PlayerMapRenderReport lastReport = PlayerMapRenderReport.Empty;
    private static PlayerMapRenderPreview lastPreview = PlayerMapRenderPreview.Empty;
    private static EditorSession lastResultSession;
    private static string lastResultRegion = string.Empty;
    private static int lastStepFrame = -1;

    internal static bool IsRunning => active != null;
    internal static PlayerMapRenderProgressSnapshot Progress => progress;

    internal static bool Begin(EditorSession session, MapPage page, PlayerMapPresentationSnapshot snapshot, bool export)
    {
        if (session == null || page?.world == null || snapshot?.Available != true)
            return false;
        if (active != null)
            return false;

        PlayerMapRenderSchedulerJobCleanup();
        lastReport = PlayerMapRenderReport.Empty;
        lastPreview = PlayerMapRenderPreview.Empty;
        lastResultSession = session;
        lastResultRegion = snapshot.RegionName ?? string.Empty;

        active = new Job
        {
            Session = session,
            Page = page,
            Region = snapshot.RegionName ?? string.Empty,
            Export = export,
            TopologyRevision = WorldTopologyRegistry.Revision,
            WorldTextRevision = WorldTextRegistry.Revision,
            BakeRevision = RoomMapBakeCache.Revision,
            SourceRooms = snapshot.Rooms == null
                ? Array.Empty<PlayerMapRoomSnapshot>()
                : (PlayerMapRoomSnapshot[])snapshot.Rooms.Clone(),
            SourceDefs = snapshot.DefaultMaterials == null
                ? Array.Empty<PlayerMapDefMaterialSnapshot>()
                : (PlayerMapDefMaterialSnapshot[])snapshot.DefaultMaterials.Clone(),
            Connections = MapEditorPresentationHub.Current?.Connections == null
                ? Array.Empty<EditorMapConnectionSnapshot>()
                : (EditorMapConnectionSnapshot[])MapEditorPresentationHub.Current.Connections.Clone()
        };

        PublishProgress(active, 0f, 0f, 0L, Math.Max(1, active.SourceRooms.Length),
            "Preflight · Rooms", "Checking room placement and baked map geometry.");
        return true;
    }

    internal static void RequestCancel()
    {
        if (active != null && active.Stage < PlayerMapRenderStage.CommitFiles)
            active.CancelRequested = true;
    }

    internal static void InvalidateResult(EditorSession session, string reason)
    {
        if (active != null && ReferenceEquals(active.Session, session))
            active.CancelRequested = true;
        if (ReferenceEquals(lastResultSession, session))
        {
            lastReport = PlayerMapRenderReport.Empty;
            lastPreview = PlayerMapRenderPreview.Empty;
            lastResultRegion = string.Empty;
        }
    }

    internal static void Reset()
    {
        PlayerMapRenderSchedulerJobCleanup();
        active = null;
        progress = PlayerMapRenderProgressSnapshot.Idle;
        lastReport = PlayerMapRenderReport.Empty;
        lastPreview = PlayerMapRenderPreview.Empty;
        lastResultSession = null;
        lastResultRegion = string.Empty;
        lastStepFrame = -1;
    }

    internal static void Step(EditorSession session)
    {
        Job job = active;
        if (job == null || !ReferenceEquals(job.Session, session)) return;
        if (lastStepFrame == Time.frameCount) return;
        lastStepFrame = Time.frameCount;

        if (job.CancelRequested)
        {
            Cancel(job, "Render cancelled. Existing output files were preserved.");
            return;
        }

        if (session?.Owner?.activePage is not MapPage currentPage || !ReferenceEquals(currentPage, job.Page) ||
            currentPage.world == null ||
            !string.Equals(currentPage.world.name ?? string.Empty, job.Region, StringComparison.OrdinalIgnoreCase))
        {
            Cancel(job, "Render cancelled because the active map/region changed.");
            return;
        }

        if (WorldTopologyRegistry.Revision != job.TopologyRevision || WorldTextRegistry.Revision != job.WorldTextRevision)
        {
            Cancel(job, "Render cancelled because world connection topology changed.");
            return;
        }

        try
        {
            switch (job.Stage)
            {
                case PlayerMapRenderStage.PreflightRooms:
                    StepPreflightRooms(job);
                    break;
                case PlayerMapRenderStage.PreflightConnections:
                    StepPreflightConnections(job);
                    break;
                case PlayerMapRenderStage.PrepareOutput:
                    StepPrepareOutput(job);
                    break;
                case PlayerMapRenderStage.ClearOutput:
                    StepClearOutput(job);
                    break;
                case PlayerMapRenderStage.ComposeRooms:
                    StepCompose(job);
                    break;
                case PlayerMapRenderStage.BuildPreview:
                    StepPreview(job);
                    break;
                case PlayerMapRenderStage.PrepareExport:
                    StepPrepareExport(job);
                    break;
                case PlayerMapRenderStage.EncodePng:
                    StepEncodePng(job);
                    break;
                case PlayerMapRenderStage.WriteMetadata:
                    StepWriteMetadata(job);
                    break;
                case PlayerMapRenderStage.CommitFiles:
                    StepCommit(job);
                    break;
            }
        }
        catch (Exception error)
        {
            Fail(job, "Player Map render failed. Existing output files were preserved.", error.Message);
        }
    }

    internal static PlayerMapPresentationSnapshot ProjectPresentation(
        EditorSession session,
        PlayerMapPresentationSnapshot source)
    {
        if (source == null || !ReferenceEquals(lastResultSession, session) ||
            !string.Equals(source.RegionName ?? string.Empty, lastResultRegion ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            return source;

        if (ReferenceEquals(lastReport, PlayerMapRenderReport.Empty) && ReferenceEquals(lastPreview, PlayerMapRenderPreview.Empty))
            return source;

        return new PlayerMapPresentationSnapshot
        {
            Available = source.Available,
            RegionName = source.RegionName,
            Dirty = source.Dirty,
            Revision = source.Revision,
            SelectedRoomIndex = source.SelectedRoomIndex,
            Rooms = source.Rooms,
            DefaultMaterials = source.DefaultMaterials,
            RenderReport = lastReport,
            Preview = lastPreview
        };
    }

    private static void StepPreflightRooms(Job job)
    {
        int stop = Math.Min(job.SourceRooms.Length, job.RoomCursor + RoomsPerFrame);
        for (; job.RoomCursor < stop; job.RoomCursor++)
        {
            PlayerMapRoomSnapshot room = job.SourceRooms[job.RoomCursor];
            if (room == null || room.Disabled) continue;
            if (!Finite(room.EffectivePosition.x) || !Finite(room.EffectivePosition.y))
            {
                job.Errors.Add((room.Name ?? room.RoomIndex.ToString()) + ": Canon Position contains NaN/Infinity.");
                continue;
            }
            if (!RoomMapBakeCache.TryGetReady(room.RoomIndex, out RoomMapBake bake))
            {
                RoomMapBakeSnapshot status = RoomMapBakeCache.GetSnapshot(room.RoomIndex);
                job.Errors.Add((room.Name ?? room.RoomIndex.ToString()) + ": room bake is " + status.Status +
                               (string.IsNullOrEmpty(status.Error) ? "." : " (" + status.Error + ")"));
                continue;
            }

            PlayerMapRenderRoom renderRoom = new()
            {
                RoomIndex = room.RoomIndex,
                Name = room.Name ?? string.Empty,
                Layer = Mathf.Clamp(room.Layer, 0, PlayerMapCoordinateSystem.LayerCount - 1),
                CanonicalPosition = room.EffectivePosition,
                Bake = bake
            };
            job.Plan.Rooms.Add(renderRoom);
            job.RoomsByIndex[renderRoom.RoomIndex] = renderRoom;

            float halfW = bake.Width * PlayerMapCoordinateSystem.CanonPixelsPerTile * 0.5f;
            float halfH = bake.Height * PlayerMapCoordinateSystem.CanonPixelsPerTile * 0.5f;
            job.MinX = Mathf.Min(job.MinX, renderRoom.CanonicalPosition.x - halfW);
            job.MinY = Mathf.Min(job.MinY, renderRoom.CanonicalPosition.y - halfH);
            job.MaxX = Mathf.Max(job.MaxX, renderRoom.CanonicalPosition.x + halfW);
            job.MaxY = Mathf.Max(job.MaxY, renderRoom.CanonicalPosition.y + halfH);
            job.TotalComposePixels += (long)bake.Width * bake.Height;
        }

        float stage = job.SourceRooms.Length == 0 ? 1f : (float)job.RoomCursor / job.SourceRooms.Length;
        PublishProgress(job, Lerp(0f, 0.10f, stage), stage, job.RoomCursor, Math.Max(1, job.SourceRooms.Length),
            "Preflight · Rooms", "Checking room placement and baked map geometry.");

        if (job.RoomCursor < job.SourceRooms.Length) return;
        if (job.Plan.Rooms.Count == 0 && job.Errors.Count == 0)
            job.Errors.Add("No enabled rooms are available for map rendering.");
        if (job.Errors.Count > 0)
        {
            Fail(job, "Player Map preflight failed. No output was written.", job.Errors.ToArray());
            return;
        }
        job.Stage = PlayerMapRenderStage.PreflightConnections;
        PublishProgress(job, 0.10f, 0f, 0L, Math.Max(1, job.Connections.Length),
            "Preflight · Connections", "Validating exact node-to-node pipe endpoints.");
    }

    private static void StepPreflightConnections(Job job)
    {
        int stop = Math.Min(job.Connections.Length, job.ConnectionCursor + ConnectionsPerFrame);
        for (; job.ConnectionCursor < stop; job.ConnectionCursor++)
        {
            EditorMapConnectionSnapshot connection = job.Connections[job.ConnectionCursor];
            if (connection == null ||
                !job.RoomsByIndex.TryGetValue(connection.FromRoomIndex, out PlayerMapRenderRoom from) ||
                !job.RoomsByIndex.TryGetValue(connection.ToRoomIndex, out PlayerMapRenderRoom to))
                continue;

            if (connection.Ambiguous || connection.FromNodeIndex < 0 || connection.ToNodeIndex < 0)
            {
                job.Plan.Warnings.Add(
                    "Unresolved connection " + from.Name + ":" + connection.FromNodeIndex + " -> " +
                    to.Name + ":" + connection.ToNodeIndex +
                    ": exact node mapping is required to distinguish repeated room-to-room pipes.");
                continue;
            }

            ValidateEndpoint(job, connection, from, connection.FromNodeIndex, "A");
            ValidateEndpoint(job, connection, to, connection.ToNodeIndex, "B");
            ClaimEndpoint(job, connection.FromRoomIndex, connection.FromNodeIndex, from.Name);
            ClaimEndpoint(job, connection.ToRoomIndex, connection.ToNodeIndex, to.Name);
        }

        float stage = job.Connections.Length == 0 ? 1f : (float)job.ConnectionCursor / job.Connections.Length;
        PublishProgress(job, Lerp(0.10f, 0.14f, stage), stage, job.ConnectionCursor,
            Math.Max(1, job.Connections.Length), "Preflight · Connections", "Validating exact node-to-node pipe endpoints.");

        if (job.ConnectionCursor < job.Connections.Length) return;
        if (job.Errors.Count > 0)
        {
            Fail(job, "Player Map connection preflight failed. No output was written.", job.Errors.ToArray());
            return;
        }
        job.Stage = PlayerMapRenderStage.PrepareOutput;
    }

    private static void StepPrepareOutput(Job job)
    {
        job.Plan.Rooms.Sort((a, b) =>
        {
            int layer = a.Layer.CompareTo(b.Layer);
            if (layer != 0) return layer;
            int index = a.RoomIndex.CompareTo(b.RoomIndex);
            return index != 0 ? index : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
        });

        job.Plan.CanonMinX = job.MinX;
        job.Plan.CanonMinY = job.MinY;
        job.Plan.Width = (int)((job.MaxX - job.MinX) / PlayerMapCoordinateSystem.CanonPixelsPerTile) +
                         PlayerMapCoordinateSystem.OutputPadding * 2;
        job.Plan.LayerHeight = (int)((job.MaxY - job.MinY) / PlayerMapCoordinateSystem.CanonPixelsPerTile) +
                               PlayerMapCoordinateSystem.OutputPadding * 2;
        job.Plan.Height = checked(job.Plan.LayerHeight * PlayerMapCoordinateSystem.LayerCount);

        if (job.Plan.Width <= 0 || job.Plan.Height <= 0 || job.Plan.Width > MaxDimension || job.Plan.Height > MaxDimension ||
            (long)job.Plan.Width * job.Plan.Height > MaxPixels)
        {
            Fail(job, "Player Map preflight failed. No output was written.",
                "Output dimensions are unsafe: " + job.Plan.Width + "x" + job.Plan.Height + ".");
            return;
        }

        for (int i = 0; i < job.Plan.Rooms.Count; i++)
        {
            PlayerMapRenderRoom room = job.Plan.Rooms[i];
            float left = room.CanonicalPosition.x - room.Bake.Width * PlayerMapCoordinateSystem.CanonPixelsPerTile * 0.5f;
            float bottom = room.CanonicalPosition.y - room.Bake.Height * PlayerMapCoordinateSystem.CanonPixelsPerTile * 0.5f;
            room.PixelX = (int)((left - job.MinX) / PlayerMapCoordinateSystem.CanonPixelsPerTile) + PlayerMapCoordinateSystem.OutputPadding;
            room.PixelYInLayer = (int)((bottom - job.MinY) / PlayerMapCoordinateSystem.CanonPixelsPerTile) + PlayerMapCoordinateSystem.OutputPadding;
            if (room.PixelX < 0 || room.PixelYInLayer < 0 ||
                room.PixelX + room.Bake.Width > job.Plan.Width || room.PixelYInLayer + room.Bake.Height > job.Plan.LayerHeight)
                job.Errors.Add(room.Name + ": computed output rectangle exceeds render bounds.");
        }
        if (job.Errors.Count > 0)
        {
            Fail(job, "Player Map preflight failed. No output was written.", job.Errors.ToArray());
            return;
        }

        for (int i = 0; i < job.SourceDefs.Length; i++)
        {
            PlayerMapDefMaterialSnapshot source = job.SourceDefs[i];
            job.Plan.DefaultMaterials.Add(new PlayerMapDefMaterialState
            {
                Id = source.Id,
                A = source.A,
                B = source.B,
                Air = source.Air,
                PanelPosition = source.A
            });
        }
        DetectOverlaps(job.Plan);

        job.Pixels = new Color32[checked(job.Plan.Width * job.Plan.Height)];
        job.Stage = PlayerMapRenderStage.ClearOutput;
        PublishProgress(job, 0.14f, 0f, 0L, Math.Max(1, job.Pixels.Length),
            "Prepare Output", job.Plan.Width + " × " + job.Plan.Height + " pixels");
    }

    private static void StepClearOutput(Job job)
    {
        int stop = Math.Min(job.Pixels.Length, job.ClearCursor + ClearPixelsPerFrame);
        for (int i = job.ClearCursor; i < stop; i++) job.Pixels[i] = Background;
        job.ClearCursor = stop;
        float stage = job.Pixels.Length == 0 ? 1f : (float)job.ClearCursor / job.Pixels.Length;
        PublishProgress(job, Lerp(0.14f, 0.20f, stage), stage, job.ClearCursor, Math.Max(1, job.Pixels.Length),
            "Clear Output", "Initializing deterministic output buffer.");
        if (job.ClearCursor < job.Pixels.Length) return;
        job.Stage = PlayerMapRenderStage.ComposeRooms;
    }

    private static void StepCompose(Job job)
    {
        int budget = ComposePixelsPerFrame;
        while (budget > 0 && job.ComposeRoomCursor < job.Plan.Rooms.Count)
        {
            PlayerMapRenderRoom room = job.Plan.Rooms[job.ComposeRoomCursor];
            int roomPixels = room.Bake.Width * room.Bake.Height;
            int take = Math.Min(budget, roomPixels - job.ComposePixelCursor);
            int end = job.ComposePixelCursor + take;
            int layerBase = room.Layer * job.Plan.LayerHeight;

            for (int ordinal = job.ComposePixelCursor; ordinal < end; ordinal++)
            {
                int y = ordinal / room.Bake.Width;
                int x = ordinal - y * room.Bake.Width;
                int targetX = room.PixelX + x;
                int targetYInLayer = room.PixelYInLayer + y;
                int targetY = layerBase + targetYInLayer;
                int targetIndex = targetY * job.Plan.Width + targetX;
                RoomMapPixel source = room.Bake.Pixels[ordinal];
                int materialMode = MaterialMode(job.Plan.DefaultMaterials,
                    new Vector2(
                        job.Plan.CanonMinX + targetX * PlayerMapCoordinateSystem.CanonPixelsPerTile,
                        job.Plan.CanonMinY + targetYInLayer * PlayerMapCoordinateSystem.CanonPixelsPerTile));
                bool targetBackground = IsBackground(job.Pixels[targetIndex]);

                if (source.Kind == RoomMapPixelKind.Solid || source.Kind == RoomMapPixelKind.UnknownShortcut)
                {
                    if (materialMode != 1 || targetBackground)
                        job.Pixels[targetIndex] = SolidColor;
                    continue;
                }

                if (materialMode == 2 && !targetBackground) continue;
                job.Pixels[targetIndex] = Encode(source);
            }

            budget -= take;
            job.ComposePixelCursor = end;
            job.ComposedPixels += take;
            if (job.ComposePixelCursor >= roomPixels)
            {
                job.ComposeRoomCursor++;
                job.ComposePixelCursor = 0;
            }
        }

        float stage = job.TotalComposePixels <= 0 ? 1f : (float)job.ComposedPixels / job.TotalComposePixels;
        string detail = job.ComposeRoomCursor < job.Plan.Rooms.Count
            ? job.Plan.Rooms[job.ComposeRoomCursor].Name
            : job.Plan.Rooms.Count + " rooms";
        PublishProgress(job, Lerp(0.20f, 0.76f, stage), stage, job.ComposedPixels,
            Math.Max(1L, job.TotalComposePixels), "Compose Rooms", detail);

        if (job.ComposeRoomCursor < job.Plan.Rooms.Count) return;
        job.Stage = PlayerMapRenderStage.BuildPreview;
    }

    private static void StepPreview(Job job)
    {
        int stop = Math.Min(job.Plan.Height, job.PreviewRow + PreviewRowsPerFrame);
        for (int y = job.PreviewRow; y < stop; y++)
        {
            int x = 0;
            while (x < job.Plan.Width)
            {
                Color32 color = job.Pixels[y * job.Plan.Width + x];
                int end = x + 1;
                while (end < job.Plan.Width && SameColor(color, job.Pixels[y * job.Plan.Width + end])) end++;
                if (!IsBackground(color)) job.PreviewRuns.Add(new PlayerMapPreviewRun(x, y, end - x, color));
                x = end;
            }
        }
        job.PreviewRow = stop;
        float stage = job.Plan.Height == 0 ? 1f : (float)job.PreviewRow / job.Plan.Height;
        PublishProgress(job, Lerp(0.76f, 0.90f, stage), stage, job.PreviewRow, Math.Max(1, job.Plan.Height),
            "Build Preview", "Compressing final pixels into preview runs.");
        if (job.PreviewRow < job.Plan.Height) return;

        lastPreview = new PlayerMapRenderPreview
        {
            Available = true,
            Width = job.Plan.Width,
            Height = job.Plan.Height,
            Runs = job.PreviewRuns.ToArray()
        };

        if (!job.Export)
        {
            Complete(job, exported: false, outputPath: string.Empty,
                "Player Map preview built from final output pixels.");
            return;
        }
        job.Stage = PlayerMapRenderStage.PrepareExport;
    }

    private static void StepPrepareExport(Job job)
    {
        string mapConfig = job.Page.filePath;
        if (string.IsNullOrWhiteSpace(mapConfig))
            throw new InvalidOperationException("Map config path is unavailable.");
        string directory = Path.GetDirectoryName(mapConfig);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("Map output directory is unavailable.");
        Directory.CreateDirectory(directory);

        job.PngPath = Path.ChangeExtension(mapConfig, ".png");
        string fileName = Path.GetFileNameWithoutExtension(job.PngPath);
        string metaName = fileName.StartsWith("map_", StringComparison.OrdinalIgnoreCase)
            ? "map_image_" + fileName.Substring(4) + ".txt"
            : fileName + "_image.txt";
        job.MetaPath = Path.Combine(directory, metaName);
        job.TempPng = job.PngPath + ".drycycle.tmp";
        job.TempMeta = job.MetaPath + ".drycycle.tmp";
        TryDelete(job.TempPng);
        TryDelete(job.TempMeta);
        job.Stage = PlayerMapRenderStage.EncodePng;
        PublishProgress(job, 0.92f, 0f, 0L, 1L, "Export · PNG", "Encoding staged PNG. Existing output remains untouched.");
    }

    private static void StepEncodePng(Job job)
    {
        Texture2D texture = null;
        try
        {
            texture = new Texture2D(job.Plan.Width, job.Plan.Height, TextureFormat.RGBA32, mipChain: false);
            texture.SetPixels32(job.Pixels);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Point;
            texture.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            PNGSaver.SaveTextureToFile(texture, job.TempPng);
        }
        finally
        {
            if (texture != null) UnityEngine.Object.Destroy(texture);
        }
        if (!File.Exists(job.TempPng)) throw new IOException("PNG staging file was not created.");
        job.Stage = PlayerMapRenderStage.WriteMetadata;
        PublishProgress(job, 0.96f, 1f, 1L, 1L, "Export · Metadata", "Writing map_image metadata from the same render plan.");
    }

    private static void StepWriteMetadata(Job job)
    {
        List<string> meta = new(job.Plan.Rooms.Count);
        for (int i = 0; i < job.Plan.Rooms.Count; i++)
        {
            PlayerMapRenderRoom room = job.Plan.Rooms[i];
            int y = room.Layer * job.Plan.LayerHeight + room.PixelYInLayer;
            meta.Add(room.Name + ": " +
                     room.PixelX.ToString(CultureInfo.InvariantCulture) + "," +
                     y.ToString(CultureInfo.InvariantCulture) + "," +
                     room.Bake.Width.ToString(CultureInfo.InvariantCulture) + "," +
                     room.Bake.Height.ToString(CultureInfo.InvariantCulture));
        }
        File.WriteAllLines(job.TempMeta, meta.ToArray());
        if (!File.Exists(job.TempMeta)) throw new IOException("Metadata staging file was not created.");
        job.Stage = PlayerMapRenderStage.CommitFiles;
        PublishProgress(job, 0.985f, 1f, meta.Count, Math.Max(1, meta.Count),
            "Export · Commit", "Atomically replacing the PNG/metadata pair.");
    }

    private static void StepCommit(Job job)
    {
        CommitPair(job.TempPng, job.PngPath, job.TempMeta, job.MetaPath);
        job.TempPng = string.Empty;
        job.TempMeta = string.Empty;
        Complete(job, exported: true, outputPath: job.PngPath, "Player Map rendered and exported.");
    }

    private static void ValidateEndpoint(
        Job job,
        EditorMapConnectionSnapshot connection,
        PlayerMapRenderRoom room,
        int nodeIndex,
        string side)
    {
        if (!room.Bake.TryGetNodeAnchor(nodeIndex, out RoomMapNodeAnchorSnapshot anchor))
        {
            job.Errors.Add(
                "Connection " + (connection.ConnectionId ?? "<unnamed>") + " endpoint " + side + " " +
                room.Name + ":" + nodeIndex + " has no baked shortcut mouth. Render was blocked to avoid a wrong multi-pipe map.");
            return;
        }
        if (anchor.Kind != RoomMapPixelKind.RoomExit)
        {
            job.Errors.Add(
                "Connection " + (connection.ConnectionId ?? "<unnamed>") + " endpoint " + side + " " +
                room.Name + ":" + nodeIndex + " resolves to " + anchor.Kind + " instead of RoomExit.");
        }
    }

    private static void ClaimEndpoint(Job job, int roomIndex, int nodeIndex, string roomName)
    {
        string key = roomIndex.ToString(CultureInfo.InvariantCulture) + ":" + nodeIndex.ToString(CultureInfo.InvariantCulture);
        if (!job.ClaimedEndpoints.Add(key))
            job.Plan.Warnings.Add("Multiple map connections claim endpoint " + roomName + ":" + nodeIndex + ".");
    }

    private static void DetectOverlaps(PlayerMapRenderPlan plan)
    {
        for (int i = 0; i < plan.Rooms.Count; i++)
        {
            PlayerMapRenderRoom a = plan.Rooms[i];
            int ax2 = a.PixelX + a.Bake.Width;
            int ay2 = a.PixelYInLayer + a.Bake.Height;
            for (int j = i + 1; j < plan.Rooms.Count; j++)
            {
                PlayerMapRenderRoom b = plan.Rooms[j];
                if (b.Layer != a.Layer) continue;
                int bx2 = b.PixelX + b.Bake.Width;
                int by2 = b.PixelYInLayer + b.Bake.Height;
                if (a.PixelX >= bx2 || b.PixelX >= ax2 || a.PixelYInLayer >= by2 || b.PixelYInLayer >= ay2) continue;
                plan.Warnings.Add("Overlap on L" + a.Layer + ": " + a.Name + " / " + b.Name +
                                  ". Deterministic RoomIndex order will be used.");
            }
        }
    }

    private static int MaterialMode(List<PlayerMapDefMaterialState> defs, Vector2 canonPoint)
    {
        int mode = 1;
        for (int i = 0; i < defs.Count; i++)
        {
            PlayerMapDefMaterialState item = defs[i];
            float left = Mathf.Min(item.A.x, item.B.x);
            float right = Mathf.Max(item.A.x, item.B.x);
            float bottom = Mathf.Min(item.A.y, item.B.y);
            float top = Mathf.Max(item.A.y, item.B.y);
            if (canonPoint.x >= left && canonPoint.x <= right && canonPoint.y >= bottom && canonPoint.y <= top)
                mode = item.Air ? 2 : 0;
        }
        return mode;
    }

    private static Color32 Encode(RoomMapPixel pixel)
    {
        if (pixel.Kind == RoomMapPixelKind.Structure)
            return pixel.Water ? StructureWet : StructureDry;
        return pixel.Water ? BrightWet : BrightDry;
    }

    private static bool IsBackground(Color32 value) => value.r == 0 && value.g == 255 && value.b == 0;
    private static bool SameColor(Color32 a, Color32 b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static float Lerp(float a, float b, float t) => a + (b - a) * Mathf.Clamp01(t);

    private static void Complete(Job job, bool exported, string outputPath, string message)
    {
        lastReport = new PlayerMapRenderReport
        {
            Attempted = true,
            Success = true,
            Exported = exported,
            Width = job.Plan.Width,
            Height = job.Plan.Height,
            IncludedRooms = job.Plan.Rooms.Count,
            OutputPath = outputPath ?? string.Empty,
            Message = message ?? string.Empty,
            Warnings = job.Plan.Warnings.ToArray()
        };
        progress = new PlayerMapRenderProgressSnapshot
        {
            Running = false,
            ExportRequested = job.Export,
            CanCancel = false,
            Stage = PlayerMapRenderStage.Completed,
            Progress = 1f,
            StageProgress = 1f,
            CompletedUnits = 1,
            TotalUnits = 1,
            StageLabel = "Completed",
            Detail = message ?? string.Empty
        };
        TryDelete(job.TempPng);
        TryDelete(job.TempMeta);
        active = null;
    }

    private static void Cancel(Job job, string message)
    {
        TryDelete(job.TempPng);
        TryDelete(job.TempMeta);
        lastReport = new PlayerMapRenderReport
        {
            Attempted = true,
            Success = false,
            Exported = false,
            Width = job.Plan.Width,
            Height = job.Plan.Height,
            IncludedRooms = job.Plan.Rooms.Count,
            Message = message,
            Warnings = job.Plan.Warnings.ToArray()
        };
        progress = new PlayerMapRenderProgressSnapshot
        {
            Running = false,
            ExportRequested = job.Export,
            CanCancel = false,
            Stage = PlayerMapRenderStage.Cancelled,
            Progress = progress.Progress,
            StageProgress = progress.StageProgress,
            StageLabel = "Cancelled",
            Detail = message
        };
        active = null;
    }

    private static void Fail(Job job, string message, params string[] errors)
    {
        TryDelete(job.TempPng);
        TryDelete(job.TempMeta);
        lastReport = new PlayerMapRenderReport
        {
            Attempted = true,
            Success = false,
            Exported = false,
            Width = job.Plan.Width,
            Height = job.Plan.Height,
            IncludedRooms = job.Plan.Rooms.Count,
            Message = message,
            Errors = errors ?? Array.Empty<string>(),
            Warnings = job.Plan.Warnings.ToArray()
        };
        progress = new PlayerMapRenderProgressSnapshot
        {
            Running = false,
            ExportRequested = job.Export,
            CanCancel = false,
            Stage = PlayerMapRenderStage.Failed,
            Progress = progress.Progress,
            StageProgress = progress.StageProgress,
            StageLabel = "Failed",
            Detail = message
        };
        active = null;
    }

    private static void PublishProgress(
        Job job,
        float overall,
        float stage,
        long completed,
        long total,
        string label,
        string detail)
    {
        progress = new PlayerMapRenderProgressSnapshot
        {
            Running = true,
            ExportRequested = job.Export,
            CanCancel = job.Stage < PlayerMapRenderStage.CommitFiles,
            Stage = job.Stage,
            Progress = Mathf.Clamp01(overall),
            StageProgress = Mathf.Clamp01(stage),
            CompletedUnits = completed,
            TotalUnits = Math.Max(1L, total),
            StageLabel = label ?? string.Empty,
            Detail = detail ?? string.Empty
        };
    }

    private static void CommitPair(string tempA, string targetA, string tempB, string targetB)
    {
        if (!File.Exists(tempA) || !File.Exists(tempB))
            throw new IOException("Render staging files are incomplete.");

        string backupA = targetA + ".drycycle.bak";
        string backupB = targetB + ".drycycle.bak";
        TryDelete(backupA);
        TryDelete(backupB);
        bool backedA = false;
        bool backedB = false;
        bool installedA = false;
        bool installedB = false;
        try
        {
            if (File.Exists(targetA)) { File.Move(targetA, backupA); backedA = true; }
            if (File.Exists(targetB)) { File.Move(targetB, backupB); backedB = true; }
            File.Move(tempA, targetA); installedA = true;
            File.Move(tempB, targetB); installedB = true;
            TryDelete(backupA);
            TryDelete(backupB);
        }
        catch
        {
            if (installedA) TryDelete(targetA);
            if (installedB) TryDelete(targetB);
            if (backedA && File.Exists(backupA)) File.Move(backupA, targetA);
            if (backedB && File.Exists(backupB)) File.Move(backupB, targetB);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    private static void PlayerMapRenderSchedulerJobCleanup()
    {
        if (active == null) return;
        TryDelete(active.TempPng);
        TryDelete(active.TempMeta);
        active = null;
    }
}

[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(PlayerMapRuntimePlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class PlayerMapIncrementalRenderPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.PlayerMap.IncrementalRender";
    public const string PluginName = "DryCycle Player Map Incremental Render";
    public const string PluginVersion = global::DryCycle.Plugin.Version;

    private void OnEnable() => PlayerMapIncrementalRenderHooks.Enable(Logger);
    private void OnDisable() => PlayerMapIncrementalRenderHooks.Disable();
}

internal static class PlayerMapIncrementalRenderHooks
{
    private static ManualLogSource log;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        log = logger;
        logger?.LogInfo("Player Map incremental render scheduler enabled through direct runtime calls; no self-detours attached.");
    }

    internal static void Disable()
    {
        PlayerMapRenderScheduler.Reset();
        enabled = false;
        log = null;
    }

    internal static bool TryHandleExecute(EditorSession session, PlayerMapCommand command)
    {
        if (!enabled) return false;

        if (command.Kind == PlayerMapCommandKind.BuildPreview || command.Kind == PlayerMapCommandKind.RenderAndExport)
        {
            if (PlayerMapRenderScheduler.IsRunning) return true;
            if (session?.Owner?.activePage is not MapPage page) return true;
            PlayerMapPresentationSnapshot snapshot = PlayerMapWorkspaceRuntime.GetPresentation(session);
            PlayerMapRenderScheduler.Begin(session, page, snapshot,
                command.Kind == PlayerMapCommandKind.RenderAndExport);
            return true;
        }

        if (IsRenderAffectingMutation(command.Kind))
            PlayerMapRenderScheduler.InvalidateResult(session, "Player Map data changed.");
        return false;
    }

    internal static void AfterSynchronize(EditorSession session)
    {
        if (enabled) PlayerMapRenderScheduler.Step(session);
    }

    internal static PlayerMapPresentationSnapshot ProjectPresentation(
        EditorSession session,
        PlayerMapPresentationSnapshot source) =>
        enabled ? PlayerMapRenderScheduler.ProjectPresentation(session, source) : source;

    private static bool IsRenderAffectingMutation(PlayerMapCommandKind kind) =>
        kind == PlayerMapCommandKind.SetEffectivePosition ||
        kind == PlayerMapCommandKind.SetOffset ||
        kind == PlayerMapCommandKind.SetPlacementMode ||
        kind == PlayerMapCommandKind.ResetOffset ||
        kind == PlayerMapCommandKind.SetLayer ||
        kind == PlayerMapCommandKind.CreateDefaultMaterial ||
        kind == PlayerMapCommandKind.SetDefaultMaterialRect ||
        kind == PlayerMapCommandKind.SetDefaultMaterialAir ||
        kind == PlayerMapCommandKind.DeleteDefaultMaterial;

}
