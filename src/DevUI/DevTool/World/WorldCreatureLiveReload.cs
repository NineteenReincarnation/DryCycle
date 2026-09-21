using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Core;
using RWWorld = global::World;
using RWCreatureSpawner = global::World.CreatureSpawner;
using RWSimpleSpawner = global::World.SimpleSpawner;

namespace DryCycle.DevUI.DevTool.World;

/// <summary>
/// Safe room-local creature authoring preview.
///
/// This intentionally does NOT mutate World.spawners, World.lineages, lineage counters,
/// respawnCreatures, or any private Rain World population structure. The editor only suppresses
/// the already-instantiated population owned by the selected room's original spawners and creates
/// temporary preview creatures from the edited world.txt model. A normal region/world reload is
/// still the authority that rebuilds Rain World's real population tables from disk.
/// </summary>
internal static class WorldCreatureLiveReload
{
    private sealed class RoomState
    {
        internal readonly HashSet<int> OriginalSpawnerIds = new();
        internal readonly List<AbstractCreature> PreviewCreatures = new();
        internal readonly List<QuantifiedContribution> PreviewQuantified = new();
        internal bool OriginalPopulationSuppressed;
    }

    private sealed class QuantifiedContribution
    {
        internal int RoomIndex;
        internal int Node;
        internal CreatureTemplate.Type Type;
        internal int Amount;
    }

    private static ConditionalWeakTable<RWWorld, Dictionary<string, RoomState>> states = new();

    internal static string LastStatus { get; private set; } = string.Empty;
    internal static bool LastSucceeded { get; private set; } = true;

    internal static void ReloadRoom(string region, string roomName)
    {
        try
        {
            RWWorld world = DevToolRuntime.ActiveSession?.World;
            if (world == null || !string.Equals(world.name, region, StringComparison.OrdinalIgnoreCase)) return;
            AbstractRoom room = world.GetAbstractRoom(roomName);
            if (room == null || world.game == null) return;

            Dictionary<string, RoomState> byRoom = states.GetValue(
                world,
                _ => new Dictionary<string, RoomState>(StringComparer.OrdinalIgnoreCase));
            if (!byRoom.TryGetValue(room.name, out RoomState state))
            {
                state = new RoomState();
                byRoom[room.name] = state;
            }

            RemovePreviewCreatures(world, state);
            RemovePreviewQuantified(world, state);
            SuppressOriginalPopulation(world, room, state);

            int ordinary = SpawnOrdinaryPreview(world, room, state);
            int lineage = SpawnLineagePreview(world, room, state);

            LastSucceeded = true;
            LastStatus = "Live preview · " + room.name + " · " + ordinary + " ordinary + " + lineage + " lineage";
        }
        catch (Exception error)
        {
            LastSucceeded = false;
            LastStatus = "Live preview failed: " + error.Message;
            global::DryCycle.Plugin.Logger?.LogWarning("Creature live preview failed: " + error);
        }
    }

    internal static void Reset()
    {
        // Do not touch the live World here. Reset only forgets editor bookkeeping; a normal
        // world/region reload remains the authoritative way to restore the original population.
        states = new ConditionalWeakTable<RWWorld, Dictionary<string, RoomState>>();
        LastStatus = string.Empty;
        LastSucceeded = true;
    }

    private static void SuppressOriginalPopulation(RWWorld world, AbstractRoom room, RoomState state)
    {
        if (state.OriginalPopulationSuppressed) return;
        state.OriginalPopulationSuppressed = true;

        RWCreatureSpawner[] spawners = world.spawners ?? Array.Empty<RWCreatureSpawner>();
        for (int i = 0; i < spawners.Length; i++)
        {
            RWCreatureSpawner spawner = spawners[i];
            if (spawner == null || spawner.den.room != room.index) continue;

            // Only touch known vanilla population spawner shapes. Lineage is identified by runtime
            // type name only; no private fields, constructors or methods are accessed.
            bool ordinary = spawner is RWSimpleSpawner;
            bool lineage = string.Equals(spawner.GetType().Name, "Lineage", StringComparison.Ordinal);
            if (!ordinary && !lineage) continue;

            state.OriginalSpawnerIds.Add(spawner.SpawnerID);

            if (spawner is RWSimpleSpawner simple)
            {
                CreatureTemplate template = StaticWorld.GetCreatureTemplate(simple.creatureType);
                if (template?.quantified == true && room.realizedRoom == null)
                {
                    for (int n = 0; n < simple.amount; n++)
                        room.RemoveQuantifiedCreature(simple.den.abstractNode, simple.creatureType);
                }
            }
        }

        RemoveCreaturesBySpawnerId(world, state.OriginalSpawnerIds);
    }

    private static void RemoveCreaturesBySpawnerId(RWWorld world, HashSet<int> spawnerIds)
    {
        if (spawnerIds.Count == 0 || world.abstractRooms == null) return;
        for (int r = 0; r < world.abstractRooms.Length; r++)
        {
            AbstractRoom room = world.abstractRooms[r];
            if (room == null) continue;

            for (int i = room.entities.Count - 1; i >= 0; i--)
            {
                if (room.entities[i] is not AbstractCreature creature || !spawnerIds.Contains(creature.ID.spawner)) continue;
                creature.realizedCreature?.Destroy();
                room.RemoveEntity(creature);
                creature.Destroy();
            }

            for (int i = room.entitiesInDens.Count - 1; i >= 0; i--)
            {
                if (room.entitiesInDens[i] is not AbstractCreature creature || !spawnerIds.Contains(creature.ID.spawner)) continue;
                creature.realizedCreature?.Destroy();
                room.RemoveEntity(creature);
                creature.Destroy();
            }
        }
    }

    private static void RemovePreviewCreatures(RWWorld world, RoomState state)
    {
        if (state.PreviewCreatures.Count == 0 || world.abstractRooms == null) return;
        HashSet<AbstractCreature> preview = new(state.PreviewCreatures);

        for (int r = 0; r < world.abstractRooms.Length; r++)
        {
            AbstractRoom room = world.abstractRooms[r];
            if (room == null) continue;

            for (int i = room.entities.Count - 1; i >= 0; i--)
            {
                if (room.entities[i] is not AbstractCreature creature || !preview.Contains(creature)) continue;
                creature.realizedCreature?.Destroy();
                room.RemoveEntity(creature);
                creature.Destroy();
            }

            for (int i = room.entitiesInDens.Count - 1; i >= 0; i--)
            {
                if (room.entitiesInDens[i] is not AbstractCreature creature || !preview.Contains(creature)) continue;
                creature.realizedCreature?.Destroy();
                room.RemoveEntity(creature);
                creature.Destroy();
            }
        }

        state.PreviewCreatures.Clear();
    }

    private static void RemovePreviewQuantified(RWWorld world, RoomState state)
    {
        for (int i = 0; i < state.PreviewQuantified.Count; i++)
        {
            QuantifiedContribution q = state.PreviewQuantified[i];
            AbstractRoom room = world.GetAbstractRoom(q.RoomIndex);
            if (room == null || room.realizedRoom != null) continue;
            for (int n = 0; n < q.Amount; n++) room.RemoveQuantifiedCreature(q.Node, q.Type);
        }
        state.PreviewQuantified.Clear();
    }

    private static int SpawnOrdinaryPreview(RWWorld world, AbstractRoom room, RoomState state)
    {
        int spawned = 0;
        SlugcatStats.Timeline timeline = CurrentTimeline(world);
        WorldCreatureSpawnRecord[] records = WorldTextRegistry.GetCreatureSpawns(world.name, room.name);

        for (int i = 0; i < records.Length; i++)
        {
            WorldCreatureSpawnRecord record = records[i];
            if (!TimelineMatches(record.TimelineFilter, record.ExcludeTimeline, timeline)) continue;
            if (!ValidDen(room, record.DenNode)) continue;

            CreatureTemplate.Type type = WorldLoader.CreatureTypeFromString(record.Creature);
            if (type == null) continue;
            CreatureTemplate template = StaticWorld.GetCreatureTemplate(type);
            if (template == null) continue;

            int amount = Math.Max(1, record.Amount);
            if (template.quantified)
            {
                room.AddQuantifiedCreature(record.DenNode, type, amount);
                state.PreviewQuantified.Add(new QuantifiedContribution
                {
                    RoomIndex = room.index,
                    Node = record.DenNode,
                    Type = type,
                    Amount = amount
                });
                spawned += amount;
                continue;
            }

            string spawnData = WrapSpawnData(record.SpawnData);
            WorldCoordinate den = new(room.index, -1, -1, record.DenNode);
            for (int n = 0; n < amount; n++)
            {
                AbstractCreature creature = SpawnPreviewCreature(world, room, den, type, spawnData, false);
                if (creature != null)
                {
                    state.PreviewCreatures.Add(creature);
                    spawned++;
                }
            }
        }

        return spawned;
    }

    private static int SpawnLineagePreview(RWWorld world, AbstractRoom room, RoomState state)
    {
        int spawned = 0;
        SlugcatStats.Timeline timeline = CurrentTimeline(world);
        WorldLineageRecord[] lineages = WorldLineageRegistry.GetLineages(world.name, room.name);
        Dictionary<int, int> sameDenCounts = new();

        for (int i = 0; i < lineages.Length; i++)
        {
            WorldLineageRecord record = lineages[i];
            if (!TimelineMatches(record.TimelineFilter, record.ExcludeTimeline, timeline)) continue;
            if (!ValidDen(room, record.DenNode) || record.Stages.Count == 0) continue;

            sameDenCounts.TryGetValue(record.DenNode, out int existing);
            int conflictNumber = existing == 0 ? 0 : existing + 1;
            sameDenCounts[record.DenNode] = existing + 1;

            int stageIndex = GetLineagePreviewStage(world, room, record, conflictNumber);
            if (stageIndex < 0 || stageIndex >= record.Stages.Count) stageIndex = 0;
            WorldLineageStageRecord stage = record.Stages[stageIndex];
            if (stage == null || string.Equals(stage.Creature, "NONE", StringComparison.OrdinalIgnoreCase)) continue;

            CreatureTemplate.Type type = WorldLoader.CreatureTypeFromString(stage.Creature);
            if (type == null || StaticWorld.GetCreatureTemplate(type) == null) continue;

            WorldCoordinate den = new(room.index, -1, -1, record.DenNode);
            AbstractCreature creature = SpawnPreviewCreature(
                world,
                room,
                den,
                type,
                WrapSpawnData(stage.SpawnData),
                record.NightCreature);
            if (creature != null)
            {
                state.PreviewCreatures.Add(creature);
                spawned++;
            }
        }

        return spawned;
    }

    private static int GetLineagePreviewStage(
        RWWorld world,
        AbstractRoom room,
        WorldLineageRecord record,
        int conflictNumber)
    {
        if (world?.game?.session is not StoryGameSession story || world.region == null) return 0;
        SaveState save = story.saveState;
        if (save?.regionStates == null || world.region.regionNumber < 0 || world.region.regionNumber >= save.regionStates.Length)
            return 0;

        RegionState regionState = save.regionStates[world.region.regionNumber];
        if (regionState?.lineageCounters == null) return 0;

        WorldCoordinate den = new(room.index, -1, -1, record.DenNode);
        string denString = den.SaveToString();
        if (conflictNumber > 0) denString += ";" + conflictNumber;

        return regionState.lineageCounters.TryGetValue(denString, out int stage) ? stage : 0;
    }

    private static AbstractCreature SpawnPreviewCreature(
        RWWorld world,
        AbstractRoom room,
        WorldCoordinate den,
        CreatureTemplate.Type type,
        string spawnData,
        bool nightCreature)
    {
        CreatureTemplate template = StaticWorld.GetCreatureTemplate(type);
        if (template == null) return null;

        AbstractCreature creature = new(world, template, null, den, world.game.GetNewID());
        creature.spawnData = spawnData;
        creature.nightCreature = nightCreature;
        creature.setCustomFlags();
        room.MoveEntityToDen(creature);
        return creature;
    }

    private static bool ValidDen(AbstractRoom room, int node)
    {
        if (room?.nodes == null || node < 0 || node >= room.nodes.Length) return false;
        AbstractRoomNode.Type type = room.nodes[node].type;
        return type == AbstractRoomNode.Type.Den || type == AbstractRoomNode.Type.GarbageHoles;
    }

    private static string WrapSpawnData(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string trimmed = value.Trim();
        if (trimmed.StartsWith("{", StringComparison.Ordinal) && trimmed.EndsWith("}", StringComparison.Ordinal))
            return trimmed;
        return "{" + trimmed.Trim('{', '}') + "}";
    }

    private static SlugcatStats.Timeline CurrentTimeline(RWWorld world)
    {
        if (world?.game?.IsStorySession != true) return null;
        return world.game.TimelinePoint ?? SlugcatStats.SlugcatToTimeline(world.game.StoryCharacter);
    }

    private static bool TimelineMatches(string filter, bool exclude, SlugcatStats.Timeline timeline)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        return WorldLoader.Preprocessing.TimelineMatch((exclude ? "X-" : string.Empty) + filter, timeline);
    }
}

[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(global::DryCycle.Plugin.ModId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldCreatureAuthoringRuntimePlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.World.CreatureAuthoringRuntime";
    public const string PluginName = "DryCycle World Creature Authoring Runtime";
    public const string PluginVersion = global::DryCycle.Plugin.Version;

    private void OnEnable() =>
        global::DryCycle.AuxiliaryPluginStartupGuard.Enable(
            PluginName + ".OnEnable",
            () => WorldCreatureAuthoringHooks.Enable(Logger),
            WorldCreatureAuthoringHooks.Disable);
    private void OnDisable() =>
        global::DryCycle.AuxiliaryPluginStartupGuard.Disable(
            PluginName + ".OnDisable",
            WorldCreatureAuthoringHooks.Disable);
}

/// <summary>
/// Direct extension service for lineage dirty/save semantics and room-local creature preview.
/// WorldTextRegistry calls this service explicitly at its authoritative mutation/save boundaries;
/// no DryCycle-owned registry method is RuntimeDetoured.
/// </summary>
internal static class WorldCreatureAuthoringHooks
{
    private static ManualLogSource log;
    private static bool enabled;

    internal static bool AdditionalDirty => enabled && WorldLineageRegistry.Dirty;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        log = logger;
        logger?.LogInfo("Creature authoring integration enabled through direct WorldTextRegistry calls; no self-detours attached.");
    }

    internal static void Disable()
    {
        WorldCreatureLiveReload.Reset();
        enabled = false;
        log = null;
    }

    internal static bool SaveAdditional(string region)
    {
        if (!enabled || !WorldLineageRegistry.Dirty)
            return true;

        if (!WorldLineageRegistry.Save(out string error))
        {
            log?.LogWarning("Lineage save failed: " + error);
            return false;
        }

        // WorldDocument deliberately ignores LINEAGE. Reparse after patching so its raw-line
        // snapshot cannot restore an older lineage row during a later ordinary creature save.
        if (!string.IsNullOrWhiteSpace(region) && !WorldTextRegistry.Reload(region))
        {
            log?.LogWarning("Lineage save succeeded, but world.txt could not be reloaded for region '" + region + "'.");
            return false;
        }

        return true;
    }

    internal static void OnCreatureAdded(string region, string roomName)
    {
        if (enabled && !string.IsNullOrWhiteSpace(roomName))
            WorldCreatureLiveReload.ReloadRoom(region, roomName);
    }

    internal static string FindSpawnRoom(string region, int spawnId)
    {
        if (!enabled) return string.Empty;

        RWWorld world = DevToolRuntime.ActiveSession?.World;
        if (world?.abstractRooms == null) return string.Empty;
        for (int i = 0; i < world.abstractRooms.Length; i++)
        {
            AbstractRoom room = world.abstractRooms[i];
            if (room == null) continue;
            WorldCreatureSpawnRecord[] spawns = WorldTextRegistry.GetCreatureSpawns(region, room.name);
            for (int j = 0; j < spawns.Length; j++)
                if (spawns[j].Id == spawnId) return room.name;
        }
        return string.Empty;
    }

    internal static void OnCreatureEdited(string region, string roomName)
    {
        if (enabled && !string.IsNullOrWhiteSpace(roomName))
            WorldCreatureLiveReload.ReloadRoom(region, roomName);
    }
}
