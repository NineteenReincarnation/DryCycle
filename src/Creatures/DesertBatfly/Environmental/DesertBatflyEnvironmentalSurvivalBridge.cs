using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Applies Task13 same-room Home/Hive/Burrow drives through native FlyAI Dijkstra and
/// Burrow behavior. It never owns cross-room travel and never writes body velocity.
/// Also preserves compatible secondary LightRain moisture when Fog/Heat/etc. owns the
/// dominant environmental profile. HeavyRain moisture is handled by Behavior directly
/// so HeavyRain + LightRain family overlap cannot double-count relief.
/// </summary>
internal static class DesertBatflyEnvironmentalSurvivalBridge
{
    private const int SecondaryMoistureIntervalTicks = 14;

    private delegate void EnvironmentalUpdateOrig(DesertBatfly bat);
    private delegate void EnvironmentalUpdateDetour(EnvironmentalUpdateOrig orig, DesertBatfly bat);

    private sealed class MoistureState
    {
        internal int LastTick = int.MinValue;
    }

    private static Hook updateHook;
    private static ConditionalWeakTable<DesertBatfly, MoistureState> moistureStates = new();

    internal static bool Installed => updateHook != null;

    internal static void Enable()
    {
        if (Installed) return;
        Disable();
        try
        {
            MethodInfo update = typeof(DesertBatflyEnvironmentalBehavior).GetMethod(
                "Update",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                null,
                new[] { typeof(DesertBatfly) },
                null);
            if (update == null) return;
            updateHook = new Hook(update, (EnvironmentalUpdateDetour)UpdateHook);
        }
        catch
        {
            Disable();
        }
    }

    internal static void Disable()
    {
        try { updateHook?.Dispose(); } catch { }
        updateHook = null;
        moistureStates = new ConditionalWeakTable<DesertBatfly, MoistureState>();
    }

    private static void UpdateHook(EnvironmentalUpdateOrig orig, DesertBatfly bat)
    {
        orig(bat);
        ApplySecondaryLightRainMoisture(bat);
        ApplyNativeHomeAndBurrow(bat);
    }

    internal static bool HigherPriorityOwnsLocalGoal(DesertBatfly bat)
    {
        if (bat?.AI == null || bat.DesertAI == null) return false;
        if (DesertBatflyTravelNavigation.HasIntent(bat.abstractCreature)) return true;
        if (bat.DesertAI.HasImmediateDanger || bat.DesertAI.Mode == DesertBatflyAI.Activity.Escape)
            return true;
        if (bat.Injury.IsSeverelyInjured || bat.Injury.IsRecovering ||
            bat.DesertAI.Mode == DesertBatflyAI.Activity.InjuryRecovery)
            return true;
        return false;
    }

    internal static bool ShouldSeekHome(in DesertBatflyEnvironmentalInfluence influence)
        => DB_EnvironmentalPolicy.ShouldSeekHome(influence);

    internal static bool ShouldBurrow(in DesertBatflyEnvironmentalInfluence influence)
        => DB_EnvironmentalPolicy.ShouldBurrow(influence);

    private static void ApplySecondaryLightRainMoisture(DesertBatfly bat)
    {
        if (bat?.room == null || bat.mainBodyChunk == null || bat.DesertState.Thirst <= 0f)
            return;

        DesertBatflyEnvironmentalRoomRuntime.RoomState roomState =
            DesertBatflyEnvironmentalRoomRuntime.For(bat.room);
        if (roomState == null ||
            roomState.Context.Weather is DesertBatflyEnvironmentalWeather.LightRain or DesertBatflyEnvironmentalWeather.HeavyRain ||
            roomState.WeatherAxes.LightRainIntensity <= 0f)
            return;

        int tick = bat.room.game?.clock ?? 0;
        MoistureState state = moistureStates.GetValue(bat, _ => new MoistureState());
        if (state.LastTick != int.MinValue && tick - state.LastTick < SecondaryMoistureIntervalTicks)
            return;
        state.LastTick = tick;

        IntVector2 tile = bat.room.GetTilePosition(bat.mainBodyChunk.pos);
        DesertBatflyEnvironmentalExposureSample exposure =
            DesertBatflyEnvironmentalExposure.Sample(bat.room, tile, 1f);
        if (exposure.RainExposure < 0.55f) return;

        float rain = roomState.WeatherAxes.LightRainIntensity;
        float relief = DesertBatflyTuning.ThirstPerTick * 2.15f * rain * exposure.RainExposure;
        bat.DesertState.Thirst = Mathf.Max(0f, bat.DesertState.Thirst - relief);
    }

    private static void ApplyNativeHomeAndBurrow(DesertBatfly bat)
    {
        if (bat?.room?.aimap == null || bat.AI == null || bat.dead || !bat.Consious ||
            bat.inShortcut || bat.Emergence?.Active == true)
            return;
        if (!DB_BehaviorArbiter.TryGetResolution(bat, out DB_BehaviorResolution ownership) ||
            ownership.PrimaryOwner is not (
                DB_BehaviorOwner.EnvironmentHardSurvival or DB_BehaviorOwner.EnvironmentLocalSurvival))
            return;
        DB_BehaviorOwner owner = ownership.PrimaryOwner;
        if (!DesertBatflyEnvironmentalBehavior.TryGetInfluence(bat, out DesertBatflyEnvironmentalInfluence influence))
            return;
        if (!ShouldSeekHome(influence) && !ShouldBurrow(influence)) return;
        if (bat.room.hives == null || bat.room.hives.Length == 0) return;

        int bestMap = -1;
        int bestHive = -1;
        int bestDistance = int.MaxValue;
        IntVector2 current = bat.room.GetTilePosition(bat.mainBodyChunk.pos);
        for (int i = 0; i < bat.room.hives.Length; i++)
        {
            IntVector2[] hive = bat.room.hives[i];
            if (hive == null || hive.Length == 0) continue;
            int map = bat.room.exitAndDenIndex.Length + i;
            int distance = bat.room.aimap.ExitDistanceForCreature(current, map, bat.Template);
            if (distance < 0 || distance >= bestDistance) continue;
            bestDistance = distance;
            bestHive = i;
            bestMap = map;
        }
        if (bestHive < 0 || bestMap < 0) return;

        bool onHiveTile = bat.room.GetTile(bat.mainBodyChunk.pos).hive;
        if (onHiveTile && ShouldBurrow(influence))
        {
            DesertBatflySocialLife.CancelForPriority(bat, "Task13 environmental Burrow priority");
            bat.DesertAI.CancelAttack();
            bat.AI.ChangeBehavior(FlyAI.Behavior.Burrow);
            bat.burrowOrHangSpot = bat.mainBodyChunk.pos;
            bat.movMode = Fly.MovementMode.Burrow;
            bat.AI.afraid = Mathf.Max(bat.AI.afraid, influence.HardSurvival ? 1.25f : 0.62f);
            return;
        }

        if (!ShouldSeekHome(influence) && influence.BurrowDrive < 0.45f) return;
        if (bat.DesertAI.FormalAttack && !influence.HardSurvival) return;

        DesertBatflySocialLife.CancelForPriority(bat, "Task13 same-room Home/Hive retreat");
        if (influence.HardSurvival) bat.DesertAI.CancelAttack();
        bat.AI.leaveRoomDijkstra = -1;
        bat.AI.followingDijkstraMap = bestMap;
        Vector2 nextGoal = bat.AI.ProgressLocalGoalAlongDijkstraMap(bat.AI.localGoal, bestMap);
        DB_FlightMotor.TryGuideNative(bat, owner, nextGoal);
        bat.AI.afraid = Mathf.Max(bat.AI.afraid, influence.HardSurvival ? 1.10f : 0.35f);
    }
}
