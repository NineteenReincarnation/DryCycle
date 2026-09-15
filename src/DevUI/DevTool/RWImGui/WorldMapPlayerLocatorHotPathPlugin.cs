using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Input;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Removes the last per-frame allocation stream from the World Map player locator.
///
/// The compatibility locator originally rebuilt a List&lt;Marker&gt;, one Marker object per player and
/// a final Marker[] on every BepInEx Update, even when DevTools was open on another tool. The draw
/// hook only consumes immutable marker snapshots, so retain a small ring of complete snapshots and
/// publish the next slot only after it has been filled. This keeps the renderer-side immutability
/// contract while making a stable player count allocation-free.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(WorldMapPlayerLocatorPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldMapPlayerLocatorHotPathPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMapPlayerLocator.HotPath";
    public const string PluginName = "DryCycle DevTool World Map Player Locator Hot Path";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => WorldMapPlayerLocatorHotPath.Enable(Logger);
    private void OnDisable() => WorldMapPlayerLocatorHotPath.Disable();
}

internal static class WorldMapPlayerLocatorHotPath
{
    private const int SnapshotRingSize = 3;

    private delegate void OrigUpdateFromMainThread();
    private delegate void HookUpdateFromMainThread(OrigUpdateFromMainThread orig);
    private delegate void MarkerArraySetter(WorldMapPlayerLocator.Marker[] value);
    private delegate Color ResolvePlayerColorDelegate(Player player, PlayerState state);
    private delegate WorldMapPlayerLocator.PixelIcon ResolveIconDelegate(string slugcatId);

    private static readonly HookUpdateFromMainThread UpdateHookDelegate = UpdateFromMainThreadHook;
    private static readonly WorldMapPlayerLocator.Marker[] EmptyMarkers =
        Array.Empty<WorldMapPlayerLocator.Marker>();

    private static ManualLogSource log;
    private static IDisposable updateHook;
    private static MarkerArraySetter publishMarkers;
    private static ResolvePlayerColorDelegate resolvePlayerColor;
    private static ResolveIconDelegate resolveIcon;
    private static WorldMapPlayerLocator.Marker[][] snapshots =
        Array.Empty<WorldMapPlayerLocator.Marker[]>();
    private static int snapshotLength = -1;
    private static int snapshotCursor;
    private static bool publishedEmpty = true;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;

        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            Type locatorType = typeof(WorldMapPlayerLocator);
            MethodInfo update = locatorType.GetMethod("UpdateFromMainThread", flags, null, Type.EmptyTypes, null);
            FieldInfo currentMarkers = locatorType.GetField("currentMarkers", flags);
            MethodInfo colorMethod = locatorType.GetMethod(
                "ResolvePlayerColor", flags, null,
                new[] { typeof(Player), typeof(PlayerState) }, null);
            MethodInfo iconMethod = locatorType.GetMethod(
                "ResolveIcon", flags, null, new[] { typeof(string) }, null);

            if (update == null || currentMarkers == null || colorMethod == null || iconMethod == null)
                throw new MissingMemberException("World Map player-locator hot-path targets were not found.");

            publishMarkers = BuildMarkerArraySetter(currentMarkers);
            resolvePlayerColor = (ResolvePlayerColorDelegate)colorMethod.CreateDelegate(
                typeof(ResolvePlayerColorDelegate));
            resolveIcon = (ResolveIconDelegate)iconMethod.CreateDelegate(typeof(ResolveIconDelegate));
            if (publishMarkers == null || resolvePlayerColor == null || resolveIcon == null)
                throw new InvalidOperationException("World Map player-locator delegates were not created.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            if (hookType == null)
                throw new TypeLoadException("MonoMod.RuntimeDetour.Hook is unavailable.");
            ConstructorInfo constructor = hookType.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            updateHook = constructor.Invoke(new object[] { update, UpdateHookDelegate }) as IDisposable;
            if (updateHook == null)
                throw new InvalidOperationException("World Map player-locator update hook was not created.");

            ResetBuffers(publishEmpty: false);
            enabled = true;
            log?.LogInfo("World Map retained player-marker snapshots enabled.");
        }
        catch (Exception error)
        {
            string message = Unwrap(error).Message;
            Disable();
            logger?.LogWarning("World Map player-locator hot path could not attach: " + message);
        }
    }

    internal static void Disable()
    {
        DisposeHook(ref updateHook);
        try { publishMarkers?.Invoke(EmptyMarkers); }
        catch { }
        publishMarkers = null;
        resolvePlayerColor = null;
        resolveIcon = null;
        ResetBuffers(publishEmpty: false);
        enabled = false;
        log = null;
    }

    private static void UpdateFromMainThreadHook(OrigUpdateFromMainThread orig)
    {
        if (!enabled)
        {
            orig();
            return;
        }

        EditorSession session = DevToolRuntime.ActiveSession;
        bool visible =
            DevToolSessionHub.IsCurrentSessionLive &&
            EditorInputRouter.FrontendAttached &&
            session?.ToolMode == EditorToolMode.Map &&
            !EditorUiModeState.UseVanilla &&
            !EditorUiModeState.OverlayHidden &&
            !session.LegacyUiVisible;

        if (!visible)
        {
            PublishEmptyOnce();
            return;
        }

        try
        {
            RainWorldGame game = session.Owner?.game;
            List<AbstractCreature> players = game?.Players;
            if (players == null || players.Count == 0)
            {
                PublishEmptyOnce();
                return;
            }

            int validCount = CountValidPlayers(players);
            if (validCount <= 0)
            {
                PublishEmptyOnce();
                return;
            }

            EnsureSnapshotRing(validCount);
            WorldMapPlayerLocator.Marker[] target = snapshots[snapshotCursor];
            snapshotCursor = (snapshotCursor + 1) % SnapshotRingSize;

            int cursor = 0;
            for (int i = 0; i < players.Count; i++)
            {
                AbstractCreature abstractPlayer = players[i];
                if (!TryReadPlayer(abstractPlayer, i, target[cursor], out bool accepted))
                    continue;
                if (accepted) cursor++;
            }

            // CountValidPlayers and TryReadPlayer use the same structural acceptance rules. In case
            // an exotic mod mutates the list during Update, fall back to the original implementation
            // instead of ever publishing a partially initialized array.
            if (cursor != validCount)
            {
                orig();
                return;
            }

            publishMarkers(target);
            publishedEmpty = false;
        }
        catch (Exception error)
        {
            PublishEmptyOnce();
            log?.LogDebug("Retained player marker capture skipped: " + error.Message);
        }
    }

    private static int CountValidPlayers(List<AbstractCreature> players)
    {
        int count = 0;
        for (int i = 0; i < players.Count; i++)
        {
            AbstractCreature abstractPlayer = players[i];
            if (abstractPlayer == null) continue;
            Player realized = abstractPlayer.realizedCreature as Player;
            int roomIndex = realized?.room?.abstractRoom?.index ?? abstractPlayer.pos.room;
            if (roomIndex >= 0) count++;
        }
        return count;
    }

    private static bool TryReadPlayer(
        AbstractCreature abstractPlayer,
        int fallbackNumber,
        WorldMapPlayerLocator.Marker marker,
        out bool accepted)
    {
        accepted = false;
        if (abstractPlayer == null || marker == null) return false;

        Player realized = abstractPlayer.realizedCreature as Player;
        PlayerState state = realized?.playerState ?? abstractPlayer.state as PlayerState;
        int roomIndex = realized?.room?.abstractRoom?.index ?? abstractPlayer.pos.room;
        if (roomIndex < 0) return false;

        bool hasTile = false;
        float tileX = 0f;
        float tileY = 0f;
        if (realized?.mainBodyChunk != null && realized.room != null)
        {
            tileX = realized.mainBodyChunk.pos.x / 20f;
            tileY = realized.mainBodyChunk.pos.y / 20f;
            hasTile = true;
        }
        else if (abstractPlayer.pos.TileDefined)
        {
            tileX = abstractPlayer.pos.x + 0.5f;
            tileY = abstractPlayer.pos.y + 0.5f;
            hasTile = true;
        }

        string slugcatId = state?.slugcatCharacter?.value ?? string.Empty;
        WorldMapPlayerLocator.PixelIcon icon = resolveIcon(slugcatId);
        if (icon == null) return false;

        marker.RoomIndex = roomIndex;
        marker.TileX = tileX;
        marker.TileY = tileY;
        marker.HasTilePosition = hasTile;
        marker.PlayerNumber = state?.playerNumber ?? fallbackNumber;
        marker.SlugcatId = slugcatId;
        marker.Tint = resolvePlayerColor(realized, state);
        marker.Dead = state?.dead == true;
        marker.Icon = icon;
        accepted = true;
        return true;
    }

    private static void EnsureSnapshotRing(int length)
    {
        if (snapshotLength == length && snapshots.Length == SnapshotRingSize) return;

        WorldMapPlayerLocator.Marker[][] next =
            new WorldMapPlayerLocator.Marker[SnapshotRingSize][];
        for (int slot = 0; slot < SnapshotRingSize; slot++)
        {
            WorldMapPlayerLocator.Marker[] array = new WorldMapPlayerLocator.Marker[length];
            for (int i = 0; i < length; i++)
                array[i] = new WorldMapPlayerLocator.Marker();
            next[slot] = array;
        }

        snapshots = next;
        snapshotLength = length;
        snapshotCursor = 0;
    }

    private static void PublishEmptyOnce()
    {
        if (publishedEmpty) return;
        try { publishMarkers?.Invoke(EmptyMarkers); }
        catch { }
        publishedEmpty = true;
    }

    private static MarkerArraySetter BuildMarkerArraySetter(FieldInfo field)
    {
        if (field == null || !field.IsStatic ||
            field.FieldType != typeof(WorldMapPlayerLocator.Marker[]))
            return null;

        DynamicMethod method = new(
            "PublishWorldMapPlayerMarkers",
            typeof(void),
            new[] { typeof(WorldMapPlayerLocator.Marker[]) },
            typeof(WorldMapPlayerLocatorHotPath).Module,
            true);
        ILGenerator il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Stsfld, field);
        il.Emit(OpCodes.Ret);
        return (MarkerArraySetter)method.CreateDelegate(typeof(MarkerArraySetter));
    }

    private static void ResetBuffers(bool publishEmpty)
    {
        if (publishEmpty)
        {
            try { publishMarkers?.Invoke(EmptyMarkers); }
            catch { }
        }
        snapshots = Array.Empty<WorldMapPlayerLocator.Marker[]>();
        snapshotLength = -1;
        snapshotCursor = 0;
        publishedEmpty = true;
    }

    private static void DisposeHook(ref IDisposable hook)
    {
        try { hook?.Dispose(); }
        catch { }
        hook = null;
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }
}
