using System;
using System.Reflection;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Applies Task13 same-room Home/Hive/Burrow drives through native FlyAI Dijkstra and
/// Burrow behavior. It never owns cross-room travel and never writes body velocity.
/// </summary>
internal static class DesertBatflyEnvironmentalSurvivalBridge
{
    private delegate void EnvironmentalUpdateOrig(DesertBatfly bat);
    private delegate void EnvironmentalUpdateDetour(EnvironmentalUpdateOrig orig, DesertBatfly bat);

    private static Hook updateHook;

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
    }

    private static void UpdateHook(EnvironmentalUpdateOrig orig, DesertBatfly bat)
    {
        orig(bat);
        ApplyNativeHomeAndBurrow(bat);
    }

    internal static bool ShouldSeekHome(in DesertBatflyEnvironmentalInfluence influence)
    {
        if (influence.HardSurvival && influence.HomeReturnDrive >= 0.35f) return true;
        if (influence.HomeReturnDrive < 0.58f) return false;

        // Heat may temporarily make active aggression stronger than withdrawal. Only let
        // Home return win once shelter/exhaustion materially overtakes agitation.
        if (influence.Weather is DesertBatflyEnvironmentalWeather.HeatWave or
            DesertBatflyEnvironmentalWeather.IntenseHeat)
        {
            float retreat = Mathf.Max(influence.HeatShelterDrive, influence.ThermalExhaustion);
            return retreat + 0.10f >= influence.HeatAgitation;
        }
        return true;
    }

    internal static bool ShouldBurrow(in DesertBatflyEnvironmentalInfluence influence)
    {
        if (influence.HardSurvival && influence.BurrowDrive >= 0.30f) return true;
        return influence.BurrowDrive >= 0.68f;
    }

    private static void ApplyNativeHomeAndBurrow(DesertBatfly bat)
    {
        if (bat?.room?.aimap == null || bat.AI == null || bat.dead || !bat.Consious ||
            bat.inShortcut || bat.Emergence?.Active == true ||
            DesertBatflyTravelNavigation.HasIntent(bat.abstractCreature) ||
            bat.DesertAI.HasImmediateDanger || bat.Injury.IsSeverelyInjured || bat.Injury.IsRecovering)
            return;
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
        if (bat.DesertAI.IsFormalAttackActive && !influence.HardSurvival) return;

        DesertBatflySocialLife.CancelForPriority(bat, "Task13 same-room Home/Hive retreat");
        if (influence.HardSurvival) bat.DesertAI.CancelAttack();
        bat.AI.leaveRoomDijkstra = -1;
        bat.AI.followingDijkstraMap = bestMap;
        bat.AI.localGoal = bat.AI.ProgressLocalGoalAlongDijkstraMap(bat.AI.localGoal, bestMap);
        bat.AI.afraid = Mathf.Max(bat.AI.afraid, influence.HardSurvival ? 1.10f : 0.35f);
    }
}
