using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;

namespace DryCycle.DevUI.DevTool.World;

/// <summary>
/// Small parity layer for the DevTool population preview. Keep the live-reload implementation
/// aligned with Rain World's WorldLoader.GeneratePopulation semantics for timeline position and
/// NONE lineage stages without coupling the main editor code to another copy of WorldLoader.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(WorldCreatureAuthoringRuntimePlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldCreatureLiveReloadParityPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.World.CreatureLiveReloadParity";
    public const string PluginName = "DryCycle Creature Live Reload Parity";
    public const string PluginVersion = global::DryCycle.Plugin.Version;

    private void OnEnable() => WorldCreatureLiveReloadParity.Enable(Logger);
    private void OnDisable() => WorldCreatureLiveReloadParity.Disable();
}

internal static class WorldCreatureLiveReloadParity
{
    private delegate SlugcatStats.Timeline OrigCurrentTimeline(global::World world);
    private delegate SlugcatStats.Timeline HookCurrentTimeline(OrigCurrentTimeline orig, global::World world);
    private delegate void OrigSpawnLineage(global::World world, AbstractRoom room, World.Lineage lineage);
    private delegate void HookSpawnLineage(OrigSpawnLineage orig, global::World world, AbstractRoom room, World.Lineage lineage);

    private static IDisposable timelineHook;
    private static IDisposable lineageHook;
    private static ManualLogSource log;

    internal static void Enable(ManualLogSource logger)
    {
        if (timelineHook != null || lineageHook != null) return;
        log = logger;
        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            MethodInfo currentTimeline = typeof(WorldCreatureLiveReload).GetMethod(
                "CurrentTimeline", flags, null, new[] { typeof(global::World) }, null);
            MethodInfo spawnLineage = typeof(WorldCreatureLiveReload).GetMethod(
                "SpawnLineage", flags, null,
                new[] { typeof(global::World), typeof(AbstractRoom), typeof(World.Lineage) }, null);
            if (currentTimeline == null) throw new MissingMethodException("WorldCreatureLiveReload.CurrentTimeline was not found.");
            if (spawnLineage == null) throw new MissingMethodException("WorldCreatureLiveReload.SpawnLineage was not found.");

            timelineHook = CreateHook(currentTimeline, new HookCurrentTimeline(CurrentTimelineHook));
            lineageHook = CreateHook(spawnLineage, new HookSpawnLineage(SpawnLineageHook));
        }
        catch (Exception error)
        {
            Disable();
            logger?.LogWarning("Creature live-reload parity hooks could not attach: " + error.Message);
        }
    }

    internal static void Disable()
    {
        try { lineageHook?.Dispose(); } catch { }
        try { timelineHook?.Dispose(); } catch { }
        lineageHook = null;
        timelineHook = null;
        log = null;
    }

    private static SlugcatStats.Timeline CurrentTimelineHook(OrigCurrentTimeline orig, global::World world)
    {
        if (world?.game?.IsStorySession == true)
            return world.game.TimelinePoint;
        return orig(world);
    }

    private static void SpawnLineageHook(
        OrigSpawnLineage orig,
        global::World world,
        AbstractRoom room,
        World.Lineage lineage)
    {
        if (world?.game?.session is not StoryGameSession story || lineage == null)
        {
            orig(world, room, lineage);
            return;
        }

        SaveState save = story.saveState;
        if (save == null || world.region == null)
        {
            orig(world, room, lineage);
            return;
        }

        // Let the normal preview path create the current creature first when the current lineage
        // stage is not NONE. This exactly preserves the existing live-reload path for normal stages.
        CreatureTemplate.Type before = null;
        try
        {
            if (save.regionStates[world.region.regionNumber] != null &&
                save.regionStates[world.region.regionNumber].lineageCounters.ContainsKey(lineage.denString))
                before = lineage.CurrentType(save);
        }
        catch
        {
        }

        orig(world, room, lineage);
        if (before != null) return;

        try
        {
            RegionState regionState = save.regionStates[world.region.regionNumber];
            if (regionState == null) return;
            if (!regionState.lineageCounters.ContainsKey(lineage.denString))
                regionState.lineageCounters[lineage.denString] = 0;

            // Vanilla WorldLoader.GeneratePopulation progresses a NONE stage once and marks that
            // spawner for a future respawn. It does not spawn the newly advanced type in this pass.
            lineage.ChanceToProgress(world);
            if (!save.respawnCreatures.Contains(lineage.SpawnerID))
                save.respawnCreatures.Add(lineage.SpawnerID);
        }
        catch (Exception error)
        {
            log?.LogWarning("Live lineage NONE-stage parity failed: " + error.Message);
        }
    }

    private static IDisposable CreateHook(MethodInfo target, Delegate detour)
    {
        Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: true);
        ConstructorInfo constructor = hookType.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
        if (constructor == null)
            throw new MissingMethodException("RuntimeDetour Hook(MethodBase, Delegate) is unavailable.");
        IDisposable hook = constructor.Invoke(new object[] { target, detour }) as IDisposable;
        if (hook == null) throw new InvalidOperationException("RuntimeDetour hook creation failed for " + target.Name + ".");
        return hook;
    }
}
