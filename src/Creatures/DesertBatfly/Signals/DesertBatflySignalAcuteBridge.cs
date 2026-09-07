using System;
using System.Reflection;
using MonoMod.RuntimeDetour;
using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Converts Task11 acute events into one accurately positioned Task12 Alarm root.
/// During the original Task11 broadcast, DesertBatflyAI.Threatened may run for several
/// directly affected bats. Those calls keep their Task11 Acute/Evidence effects, but their
/// legacy RaiseLocalAlarm output is suppressed. Afterward one real affected/nearby bat
/// emits the social alarm at the actual explosion/startle/casualty position.
/// </summary>
internal static class DesertBatflySignalAcuteBridge
{
    private delegate void ExplosionOrig(Explosion explosion);
    private delegate void ExplosionDetour(ExplosionOrig orig, Explosion explosion);
    private delegate void StartleOrig(Room room, Player player, FirecrackerPlant source, Vector2 position);
    private delegate void StartleDetour(StartleOrig orig, Room room, Player player, FirecrackerPlant source, Vector2 position);
    private delegate void MassCasualtyOrig(Room room, Player player, Vector2 position);
    private delegate void MassCasualtyDetour(MassCasualtyOrig orig, Room room, Player player, Vector2 position);

    private static Hook explosionHook;
    private static Hook startleHook;
    private static Hook massCasualtyHook;
    private static FieldInfo suppressAlarmField;

    internal static bool Installed =>
        explosionHook != null && startleHook != null && massCasualtyHook != null;

    internal static void Enable()
    {
        if (Installed) return;
        Disable();
        try
        {
            Type threat = typeof(DesertBatflyThreatRuntime);
            MethodInfo explosion = threat.GetMethod(
                "ReportExplosion", BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo startle = threat.GetMethod(
                "BroadcastStartle", BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo mass = threat.GetMethod(
                "BroadcastMassCasualty", BindingFlags.NonPublic | BindingFlags.Static);
            suppressAlarmField = typeof(DesertBatflySignalIntegration).GetField(
                "suppressAlarmEmission", BindingFlags.NonPublic | BindingFlags.Static);

            if (explosion == null || startle == null || mass == null || suppressAlarmField == null)
                return;

            explosionHook = new Hook(explosion, (ExplosionDetour)ExplosionHook);
            startleHook = new Hook(startle, (StartleDetour)StartleHook);
            massCasualtyHook = new Hook(mass, (MassCasualtyDetour)MassCasualtyHook);
        }
        catch
        {
            Disable();
        }
    }

    internal static void Disable()
    {
        try { massCasualtyHook?.Dispose(); } catch { }
        try { startleHook?.Dispose(); } catch { }
        try { explosionHook?.Dispose(); } catch { }
        massCasualtyHook = null;
        startleHook = null;
        explosionHook = null;
        suppressAlarmField = null;
    }

    private static void ExplosionHook(ExplosionOrig orig, Explosion explosion)
    {
        if (explosion?.room == null)
        {
            orig(explosion);
            return;
        }

        WithAlarmSuppressed(() => orig(explosion));
        Creature threat = explosion.killTagHolder;
        float intensity = Mathf.Clamp01(
            0.60f + Mathf.Clamp01(explosion.damage) * 0.16f +
            Mathf.InverseLerp(80f, 360f, explosion.rad) * 0.16f);
        EmitAcuteAlarm(
            explosion.room,
            threat,
            explosion.pos,
            Mathf.Max(0.62f, intensity),
            "Task11 acute explosion -> Task12 AlarmFlutter at real explosion center");
    }

    private static void StartleHook(
        StartleOrig orig,
        Room room,
        Player player,
        FirecrackerPlant source,
        Vector2 position)
    {
        WithAlarmSuppressed(() => orig(room, player, source, position));
        EmitAcuteAlarm(
            room,
            player,
            position,
            0.82f,
            "Task11 firecracker/startle -> Task12 AlarmFlutter at real startle center");
    }

    private static void MassCasualtyHook(
        MassCasualtyOrig orig,
        Room room,
        Player player,
        Vector2 position)
    {
        WithAlarmSuppressed(() => orig(room, player, position));
        EmitAcuteAlarm(
            room,
            player,
            position,
            0.96f,
            "Task11 mass casualty -> high urgency Task12 AlarmFlutter");
    }

    private static void WithAlarmSuppressed(Action action)
    {
        int previous = 0;
        try
        {
            if (suppressAlarmField?.GetValue(null) is int value) previous = value;
            suppressAlarmField?.SetValue(null, previous + 1);
            action?.Invoke();
        }
        finally
        {
            suppressAlarmField?.SetValue(null, previous);
        }
    }

    private static void EmitAcuteAlarm(
        Room room,
        Creature threat,
        Vector2 position,
        float intensity,
        string reason)
    {
        DesertBatfly emitter = FindEmitter(room, position);
        if (emitter == null) return;

        Vector2 direction = Custom.DirVec(emitter.mainBodyChunk.pos, position);
        DesertBatflySignalRuntime.EmitAlarm(
            emitter,
            threat,
            position,
            direction,
            intensity,
            reason);
    }

    private static DesertBatfly FindEmitter(Room room, Vector2 position)
    {
        if (room == null) return null;
        DesertBatfly best = null;
        float bestScore = float.MaxValue;
        foreach (Fly member in DesertSwarmRoom.For(room).Hive.flies)
        {
            if (member is not DesertBatfly bat || bat.dead || bat.slatedForDeletetion ||
                !bat.Consious || bat.room != room || bat.inShortcut)
                continue;

            float distance = Vector2.Distance(bat.mainBodyChunk.pos, position);
            if (distance > 480f) continue;
            bool visual = room.VisualContact(bat.mainBodyChunk.pos, position);
            float score = distance + (visual ? 0f : 95f);
            if (score >= bestScore) continue;
            bestScore = score;
            best = bat;
        }
        return best;
    }
}
