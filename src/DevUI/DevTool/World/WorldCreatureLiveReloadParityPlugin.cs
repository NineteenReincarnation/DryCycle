using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using RWWorld = global::World;
using RWCreatureSpawner = global::World.CreatureSpawner;

namespace DryCycle.DevUI.DevTool.World;

/// <summary>
/// Keeps the DevTool live population preview aligned with vanilla handling of NONE lineage stages.
///
/// PUBLIC-Assembly-CSharp does not consistently expose World.Lineage as a compile-time nested type,
/// so this compatibility layer intentionally works through World.CreatureSpawner and reflects the
/// runtime World+Lineage members only after the game has loaded them.
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
    private delegate void OrigSpawnLineage(RWWorld world, AbstractRoom room, RWCreatureSpawner lineage);
    private delegate void HookSpawnLineage(OrigSpawnLineage orig, RWWorld world, AbstractRoom room, RWCreatureSpawner lineage);

    private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static IDisposable lineageHook;
    private static ManualLogSource log;
    private static Type lineageRuntimeType;
    private static FieldInfo denStringField;
    private static MethodInfo currentTypeMethod;
    private static MethodInfo chanceToProgressMethod;
    private static bool reflectionResolved;

    internal static void Enable(ManualLogSource logger)
    {
        if (lineageHook != null) return;
        log = logger;
        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            MethodInfo spawnLineage = typeof(WorldCreatureLiveReload).GetMethod(
                "SpawnLineage",
                flags,
                null,
                new[] { typeof(RWWorld), typeof(AbstractRoom), typeof(RWCreatureSpawner) },
                null);
            if (spawnLineage == null)
                throw new MissingMethodException("WorldCreatureLiveReload.SpawnLineage(World, AbstractRoom, CreatureSpawner) was not found.");

            lineageHook = CreateHook(spawnLineage, new HookSpawnLineage(SpawnLineageHook));
        }
        catch (Exception error)
        {
            Disable();
            logger?.LogWarning("Creature live-reload parity hook could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        try { lineageHook?.Dispose(); } catch { }
        lineageHook = null;
        lineageRuntimeType = null;
        denStringField = null;
        currentTypeMethod = null;
        chanceToProgressMethod = null;
        reflectionResolved = false;
        log = null;
    }

    private static void SpawnLineageHook(
        OrigSpawnLineage orig,
        RWWorld world,
        AbstractRoom room,
        RWCreatureSpawner lineage)
    {
        if (world?.game?.session is not StoryGameSession story || lineage == null || !IsLineage(lineage))
        {
            orig(world, room, lineage);
            return;
        }

        SaveState save = story.saveState;
        if (save == null || world.region == null || currentTypeMethod == null)
        {
            orig(world, room, lineage);
            return;
        }

        // Determine whether the current authored lineage stage is NONE before the normal preview
        // path runs. Normal stages are left completely to WorldCreatureLiveReload.
        CreatureTemplate.Type before = null;
        try
        {
            EnsureCounterExists(save, world, lineage);
            before = currentTypeMethod.Invoke(lineage, new object[] { save }) as CreatureTemplate.Type;
        }
        catch (Exception error)
        {
            log?.LogDebug("Could not inspect live lineage stage: " + Unwrap(error).Message);
        }

        orig(world, room, lineage);
        if (before != null) return;

        try
        {
            if (chanceToProgressMethod == null) return;

            // Vanilla WorldLoader.GeneratePopulation gives a NONE stage one ChanceToProgress roll
            // and queues the lineage for a future respawn. It does not immediately spawn the newly
            // advanced type in the same population pass.
            chanceToProgressMethod.Invoke(lineage, new object[] { world });
            if (!save.respawnCreatures.Contains(lineage.SpawnerID))
                save.respawnCreatures.Add(lineage.SpawnerID);
        }
        catch (Exception error)
        {
            log?.LogWarning("Live lineage NONE-stage parity failed: " + Unwrap(error).Message);
        }
    }

    private static void EnsureCounterExists(SaveState save, RWWorld world, RWCreatureSpawner lineage)
    {
        if (save == null || world?.region == null) return;
        ResolveReflection();
        if (denStringField == null) return;

        if (save.regionStates[world.region.regionNumber] == null)
            save.regionStates[world.region.regionNumber] = new RegionState(save, world);

        RegionState state = save.regionStates[world.region.regionNumber];
        string denString = denStringField.GetValue(lineage) as string;
        if (!string.IsNullOrEmpty(denString) && !state.lineageCounters.ContainsKey(denString))
            state.lineageCounters[denString] = 0;
    }

    private static bool IsLineage(RWCreatureSpawner spawner)
    {
        if (spawner == null) return false;
        ResolveReflection();
        return lineageRuntimeType != null && lineageRuntimeType.IsInstanceOfType(spawner);
    }

    private static void ResolveReflection()
    {
        if (reflectionResolved) return;
        reflectionResolved = true;

        try
        {
            lineageRuntimeType = typeof(RWWorld).GetNestedType(
                "Lineage",
                BindingFlags.Public | BindingFlags.NonPublic);
            if (lineageRuntimeType == null) return;

            denStringField = lineageRuntimeType.GetField("denString", AnyInstance);
            currentTypeMethod = lineageRuntimeType.GetMethod(
                "CurrentType",
                AnyInstance,
                null,
                new[] { typeof(SaveState) },
                null);
            chanceToProgressMethod = lineageRuntimeType.GetMethod(
                "ChanceToProgress",
                AnyInstance,
                null,
                new[] { typeof(RWWorld) },
                null);
        }
        catch (Exception error)
        {
            lineageRuntimeType = null;
            denStringField = null;
            currentTypeMethod = null;
            chanceToProgressMethod = null;
            log?.LogWarning("Lineage parity reflection setup failed: " + Unwrap(error).Message);
        }
    }

    private static IDisposable CreateHook(MethodInfo target, Delegate detour)
    {
        Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: true);
        ConstructorInfo constructor = hookType.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
        if (constructor == null)
            throw new MissingMethodException("RuntimeDetour Hook(MethodBase, Delegate) is unavailable.");
        IDisposable hook = constructor.Invoke(new object[] { target, detour }) as IDisposable;
        if (hook == null)
            throw new InvalidOperationException("RuntimeDetour hook creation failed for " + target.Name + ".");
        return hook;
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }
}
