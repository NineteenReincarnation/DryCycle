using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.World;

/// <summary>
/// Room-local live preview for world creature authoring. Existing unrelated spawner indices are
/// never shifted. The selected room's vanilla SimpleSpawner/Lineage slots are reused and additional
/// preview slots are appended only when necessary.
/// </summary>
internal static class WorldCreatureLiveReload
{
    private sealed class RoomState
    {
        internal readonly List<int> Slots = new();
        internal readonly HashSet<int> SpawnerIds = new();
        internal readonly List<QuantifiedContribution> Quantified = new();
        internal bool Claimed;
    }

    private sealed class QuantifiedContribution
    {
        internal int RoomIndex;
        internal int Node;
        internal CreatureTemplate.Type Type;
        internal int Amount;
    }

    private static ConditionalWeakTable<global::World, Dictionary<string, RoomState>> states = new();

    internal static string LastStatus { get; private set; } = string.Empty;
    internal static bool LastSucceeded { get; private set; } = true;

    internal static void ReloadRoom(string region, string roomName)
    {
        try
        {
            global::World world = DevToolRuntime.ActiveSession?.World;
            if (world == null || !string.Equals(world.name, region, StringComparison.OrdinalIgnoreCase)) return;
            AbstractRoom room = world.GetAbstractRoom(roomName);
            if (room == null) return;

            Dictionary<string, RoomState> byRoom = states.GetValue(
                world,
                _ => new Dictionary<string, RoomState>(StringComparer.OrdinalIgnoreCase));
            if (!byRoom.TryGetValue(room.name, out RoomState state))
            {
                state = new RoomState();
                byRoom[room.name] = state;
            }

            ClaimRoomSpawners(world, room, state);
            RemoveManagedCreatures(world, state.SpawnerIds);
            RemoveQuantified(world, state);

            List<World.CreatureSpawner> definitions = BuildDefinitions(world, room);
            ApplySlots(world, room, state, definitions);
            RefreshWorldLineages(world, room, state);
            SpawnDefinitions(world, room, state, definitions);

            LastSucceeded = true;
            LastStatus = "Live reload · " + room.name + " · " + definitions.Count + " spawner(s)";
        }
        catch (Exception error)
        {
            LastSucceeded = false;
            LastStatus = "Live reload failed: " + error.Message;
            global::DryCycle.Plugin.Logger?.LogWarning("Creature live reload failed: " + error);
        }
    }

    internal static void Reset()
    {
        states = new ConditionalWeakTable<global::World, Dictionary<string, RoomState>>();
        LastStatus = string.Empty;
        LastSucceeded = true;
    }

    private static void ClaimRoomSpawners(global::World world, AbstractRoom room, RoomState state)
    {
        if (state.Claimed) return;
        state.Claimed = true;
        World.CreatureSpawner[] current = world.spawners ?? Array.Empty<World.CreatureSpawner>();
        for (int i = 0; i < current.Length; i++)
        {
            World.CreatureSpawner spawner = current[i];
            if (spawner == null || spawner.den.room != room.index) continue;
            if (spawner is not World.SimpleSpawner && spawner is not World.Lineage) continue;
            state.Slots.Add(i);
            state.SpawnerIds.Add(spawner.SpawnerID);

            // Quantified creatures do not carry the source spawner ID, so remove the original
            // contribution once when this room is first claimed for live authoring.
            if (spawner is World.SimpleSpawner simple)
            {
                CreatureTemplate template = StaticWorld.GetCreatureTemplate(simple.creatureType);
                if (template?.quantified == true && room.realizedRoom == null)
                {
                    for (int n = 0; n < simple.amount; n++)
                        room.RemoveQuantifiedCreature(simple.den.abstractNode, simple.creatureType);
                }
            }
        }
    }

    private static void RemoveManagedCreatures(global::World world, HashSet<int> spawnerIds)
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

    private static void RemoveQuantified(global::World world, RoomState state)
    {
        for (int i = 0; i < state.Quantified.Count; i++)
        {
            QuantifiedContribution q = state.Quantified[i];
            AbstractRoom room = world.GetAbstractRoom(q.RoomIndex);
            if (room == null || room.realizedRoom != null) continue;
            for (int n = 0; n < q.Amount; n++) room.RemoveQuantifiedCreature(q.Node, q.Type);
        }
        state.Quantified.Clear();
    }

    private static List<World.CreatureSpawner> BuildDefinitions(global::World world, AbstractRoom room)
    {
        List<World.CreatureSpawner> result = new();
        SlugcatStats.Timeline timeline = CurrentTimeline(world);

        WorldCreatureSpawnRecord[] ordinary = WorldTextRegistry.GetCreatureSpawns(world.name, room.name);
        for (int i = 0; i < ordinary.Length; i++)
        {
            WorldCreatureSpawnRecord record = ordinary[i];
            if (!TimelineMatches(record.TimelineFilter, record.ExcludeTimeline, timeline)) continue;
            CreatureTemplate.Type type = WorldLoader.CreatureTypeFromString(record.Creature);
            if (type == null) continue;
            string spawnData = string.IsNullOrWhiteSpace(record.SpawnData) ? null : "{" + record.SpawnData + "}";
            result.Add(new World.SimpleSpawner(
                world.region.regionNumber,
                -1,
                new WorldCoordinate(room.index, -1, -1, record.DenNode),
                type,
                spawnData,
                Math.Max(1, record.Amount)));
        }

        WorldLineageRecord[] lineages = WorldLineageRegistry.GetLineages(world.name, room.name);
        Dictionary<int, int> sameDenCounts = new();
        for (int i = 0; i < lineages.Length; i++)
        {
            WorldLineageRecord record = lineages[i];
            if (!TimelineMatches(record.TimelineFilter, record.ExcludeTimeline, timeline)) continue;
            sameDenCounts.TryGetValue(record.DenNode, out int count);
            int conflict = count == 0 ? 0 : count + 1;
            sameDenCounts[record.DenNode] = count + 1;

            int[] types = new int[record.Stages.Count];
            float[] chances = new float[record.Stages.Count];
            string[] spawnData = new string[record.Stages.Count];
            for (int s = 0; s < record.Stages.Count; s++)
            {
                WorldLineageStageRecord stage = record.Stages[s];
                if (string.Equals(stage.Creature, "NONE", StringComparison.OrdinalIgnoreCase))
                {
                    types[s] = -1;
                }
                else
                {
                    CreatureTemplate.Type type = WorldLoader.CreatureTypeFromString(stage.Creature);
                    types[s] = type?.Index ?? -1;
                }
                chances[s] = Math.Max(0f, Math.Min(1f, stage.Chance));
                spawnData[s] = string.IsNullOrWhiteSpace(stage.SpawnData) ? null : "{" + stage.SpawnData + "}";
            }

            World.Lineage lineage = new(
                world.region.regionNumber,
                -1,
                new WorldCoordinate(room.index, -1, -1, record.DenNode),
                types,
                chances,
                spawnData,
                conflict);
            lineage.nightCreature = record.NightCreature;
            result.Add(lineage);
        }
        return result;
    }

    private static void ApplySlots(
        global::World world,
        AbstractRoom room,
        RoomState state,
        List<World.CreatureSpawner> definitions)
    {
        List<World.CreatureSpawner> array = new(world.spawners ?? Array.Empty<World.CreatureSpawner>());
        while (state.Slots.Count < definitions.Count)
        {
            state.Slots.Add(array.Count);
            array.Add(null);
        }

        state.SpawnerIds.Clear();
        for (int i = 0; i < state.Slots.Count; i++)
        {
            int slot = state.Slots[i];
            if (i < definitions.Count)
            {
                World.CreatureSpawner spawner = definitions[i];
                spawner.inRegionSpawnerIndex = slot;
                spawner.region = world.region.regionNumber;
                array[slot] = spawner;
                state.SpawnerIds.Add(spawner.SpawnerID);
            }
            else
            {
                // Tombstone rather than remove the array element: all unrelated spawner IDs stay stable.
                World.SimpleSpawner disabled = new(
                    world.region.regionNumber,
                    slot,
                    new WorldCoordinate(room.index, -1, -1, 0),
                    CreatureTemplate.Type.Fly,
                    null,
                    0);
                array[slot] = disabled;
                state.SpawnerIds.Add(disabled.SpawnerID);
            }
        }
        world.spawners = array.ToArray();
    }

    private static void RefreshWorldLineages(global::World world, AbstractRoom room, RoomState state)
    {
        List<World.Lineage> lineages = new();
        World.Lineage[] current = world.lineages ?? Array.Empty<World.Lineage>();
        for (int i = 0; i < current.Length; i++)
            if (current[i] != null && current[i].den.room != room.index) lineages.Add(current[i]);

        for (int i = 0; i < state.Slots.Count; i++)
        {
            int slot = state.Slots[i];
            if (slot >= 0 && slot < world.spawners.Length && world.spawners[slot] is World.Lineage lineage)
                lineages.Add(lineage);
        }
        world.lineages = lineages.ToArray();
    }

    private static void SpawnDefinitions(
        global::World world,
        AbstractRoom room,
        RoomState state,
        List<World.CreatureSpawner> definitions)
    {
        if (world.game == null) return;
        for (int i = 0; i < definitions.Count; i++)
        {
            World.CreatureSpawner spawner = definitions[i];
            int node = spawner.den.abstractNode;
            if (node < 0 || node >= room.nodes.Length) continue;
            AbstractRoomNode.Type nodeType = room.nodes[node].type;
            if (nodeType != AbstractRoomNode.Type.Den && nodeType != AbstractRoomNode.Type.GarbageHoles) continue;

            if (spawner is World.SimpleSpawner simple)
            {
                CreatureTemplate template = StaticWorld.GetCreatureTemplate(simple.creatureType);
                if (template == null) continue;
                if (template.quantified)
                {
                    room.AddQuantifiedCreature(node, simple.creatureType, simple.amount);
                    state.Quantified.Add(new QuantifiedContribution
                    {
                        RoomIndex = room.index,
                        Node = node,
                        Type = simple.creatureType,
                        Amount = simple.amount
                    });
                    continue;
                }
                for (int n = 0; n < simple.amount; n++)
                    SpawnAbstract(world, room, simple.den, simple.creatureType, simple.spawnDataString, simple.nightCreature, simple.SpawnerID);
            }
            else if (spawner is World.Lineage lineage)
            {
                SpawnLineage(world, room, lineage);
            }
        }
    }

    private static void SpawnLineage(global::World world, AbstractRoom room, World.Lineage lineage)
    {
        if (world.game.session is not StoryGameSession story || world.region == null) return;
        SaveState save = story.saveState;
        if (save == null) return;
        if (save.regionStates[world.region.regionNumber] == null)
            save.regionStates[world.region.regionNumber] = new RegionState(save, world);

        RegionState regionState = save.regionStates[world.region.regionNumber];
        if (!regionState.lineageCounters.ContainsKey(lineage.denString))
            regionState.lineageCounters[lineage.denString] = 0;
        int max = Math.Max(0, lineage.creatureTypes.Length - 1);
        regionState.lineageCounters[lineage.denString] = Math.Max(0, Math.Min(max, regionState.lineageCounters[lineage.denString]));

        CreatureTemplate.Type type = lineage.CurrentType(save);
        if (type != null)
            SpawnAbstract(world, room, lineage.den, type, lineage.CurrentSpawnData(save), lineage.nightCreature, lineage.SpawnerID);
    }

    private static void SpawnAbstract(
        global::World world,
        AbstractRoom room,
        WorldCoordinate den,
        CreatureTemplate.Type type,
        string spawnData,
        bool night,
        int spawnerId)
    {
        CreatureTemplate template = StaticWorld.GetCreatureTemplate(type);
        if (template == null) return;
        AbstractCreature creature = new(world, template, null, den, world.game.GetNewID(spawnerId));
        creature.spawnData = spawnData;
        creature.nightCreature = night;
        creature.setCustomFlags();
        room.MoveEntityToDen(creature);
    }

    private static SlugcatStats.Timeline CurrentTimeline(global::World world)
    {
        if (world?.game?.IsStorySession != true) return null;
        return SlugcatStats.SlugcatToTimeline(world.game.StoryCharacter);
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

    private void OnEnable() => WorldCreatureAuthoringHooks.Enable(Logger);
    private void OnDisable() => WorldCreatureAuthoringHooks.Disable();
}

/// <summary>
/// Adds lineage dirty/save semantics to the existing WorldTextRegistry and live-reloads ordinary
/// creature edits. The existing lossless WorldDocument remains the sole owner of ordinary rows.
/// </summary>
internal static class WorldCreatureAuthoringHooks
{
    private delegate bool OrigDirty();
    private delegate bool HookDirty(OrigDirty orig);
    private delegate bool OrigSave();
    private delegate bool HookSave(OrigSave orig);
    private delegate bool OrigAdd(string region, string roomName, int denNode, string creature, int amount, string spawnData, string timelineFilter, bool excludeTimeline, out int spawnId, out string error);
    private delegate bool HookAdd(OrigAdd orig, string region, string roomName, int denNode, string creature, int amount, string spawnData, string timelineFilter, bool excludeTimeline, out int spawnId, out string error);
    private delegate bool OrigUpdate(string region, int spawnId, int denNode, string creature, int amount, string spawnData, string timelineFilter, bool excludeTimeline, out string error);
    private delegate bool HookUpdate(OrigUpdate orig, string region, int spawnId, int denNode, string creature, int amount, string spawnData, string timelineFilter, bool excludeTimeline, out string error);
    private delegate bool OrigDelete(string region, int spawnId, out string error);
    private delegate bool HookDelete(OrigDelete orig, string region, int spawnId, out string error);

    private static readonly List<IDisposable> hooks = new();
    private static ManualLogSource log;

    internal static void Enable(ManualLogSource logger)
    {
        if (hooks.Count > 0) return;
        log = logger;
        try
        {
            Hook("get_Dirty", new HookDirty(DirtyHook), Type.EmptyTypes);
            Hook("Save", new HookSave(SaveHook), Type.EmptyTypes);
            Hook("TryAddCreatureSpawn", new HookAdd(AddHook), new[]
            {
                typeof(string), typeof(string), typeof(int), typeof(string), typeof(int), typeof(string), typeof(string), typeof(bool),
                typeof(int).MakeByRefType(), typeof(string).MakeByRefType()
            });
            Hook("TryUpdateCreatureSpawn", new HookUpdate(UpdateHook), new[]
            {
                typeof(string), typeof(int), typeof(int), typeof(string), typeof(int), typeof(string), typeof(string), typeof(bool),
                typeof(string).MakeByRefType()
            });
            Hook("TryDeleteCreatureSpawn", new HookDelete(DeleteHook), new[]
            {
                typeof(string), typeof(int), typeof(string).MakeByRefType()
            });
        }
        catch (Exception error)
        {
            Disable();
            logger?.LogWarning("Creature authoring hooks could not attach: " + error.Message);
        }
    }

    internal static void Disable()
    {
        for (int i = hooks.Count - 1; i >= 0; i--) try { hooks[i]?.Dispose(); } catch { }
        hooks.Clear();
        WorldCreatureLiveReload.Reset();
        log = null;
    }

    private static bool DirtyHook(OrigDirty orig) => orig() || WorldLineageRegistry.Dirty;

    private static bool SaveHook(OrigSave orig)
    {
        string region = WorldTextRegistry.LoadedRegion;
        if (!orig()) return false;
        if (!WorldLineageRegistry.Dirty) return true;
        if (!WorldLineageRegistry.Save(out string error))
        {
            log?.LogWarning("Lineage save failed: " + error);
            return false;
        }

        // WorldDocument deliberately ignores LINEAGE. Reparse after patching so its raw-line snapshot
        // cannot restore an older lineage row during a later ordinary creature save.
        if (!string.IsNullOrWhiteSpace(region)) WorldTextRegistry.Reload(region);
        return true;
    }

    private static bool AddHook(
        OrigAdd orig,
        string region,
        string roomName,
        int denNode,
        string creature,
        int amount,
        string spawnData,
        string timelineFilter,
        bool excludeTimeline,
        out int spawnId,
        out string error)
    {
        bool ok = orig(region, roomName, denNode, creature, amount, spawnData, timelineFilter, excludeTimeline, out spawnId, out error);
        if (ok) WorldCreatureLiveReload.ReloadRoom(region, roomName);
        return ok;
    }

    private static bool UpdateHook(
        OrigUpdate orig,
        string region,
        int spawnId,
        int denNode,
        string creature,
        int amount,
        string spawnData,
        string timelineFilter,
        bool excludeTimeline,
        out string error)
    {
        string room = FindSpawnRoom(region, spawnId);
        bool ok = orig(region, spawnId, denNode, creature, amount, spawnData, timelineFilter, excludeTimeline, out error);
        if (ok && room.Length > 0) WorldCreatureLiveReload.ReloadRoom(region, room);
        return ok;
    }

    private static bool DeleteHook(OrigDelete orig, string region, int spawnId, out string error)
    {
        string room = FindSpawnRoom(region, spawnId);
        bool ok = orig(region, spawnId, out error);
        if (ok && room.Length > 0) WorldCreatureLiveReload.ReloadRoom(region, room);
        return ok;
    }

    private static string FindSpawnRoom(string region, int spawnId)
    {
        global::World world = DevToolRuntime.ActiveSession?.World;
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

    private static void Hook(string name, Delegate detour, Type[] parameters)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        MethodInfo method = typeof(WorldTextRegistry).GetMethod(name, flags, null, parameters, null);
        if (method == null) throw new MissingMethodException("WorldTextRegistry." + name + " was not found.");
        Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: true);
        ConstructorInfo constructor = hookType.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
        if (constructor == null) throw new MissingMethodException("RuntimeDetour Hook(MethodBase, Delegate) is unavailable.");
        IDisposable instance = constructor.Invoke(new object[] { method, detour }) as IDisposable;
        if (instance == null) throw new InvalidOperationException("Could not create hook for " + name + ".");
        hooks.Add(instance);
    }
}
