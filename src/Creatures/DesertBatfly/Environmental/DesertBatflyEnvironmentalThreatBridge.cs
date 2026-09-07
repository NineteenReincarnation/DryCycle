using System;
using System.Collections.Generic;
using System.Reflection;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Fog/DenseFog bridge for Task11 current visual recognition. Long-range player/held-item
/// recognition shrinks with visibility, while a nearby real thrown projectile can still
/// identify its thrower so immediate projectile danger is never disabled by fog.
/// Persistent Threat Signature memory is not modified here.
/// </summary>
internal static class DesertBatflyEnvironmentalThreatBridge
{
    private delegate Player NearestVisiblePlayerOrig(
        DesertBatfly bat,
        List<Player> players,
        float maxDistance);

    private delegate Player NearestVisiblePlayerDetour(
        NearestVisiblePlayerOrig orig,
        DesertBatfly bat,
        List<Player> players,
        float maxDistance);

    internal const float MinimumVisualRangeScale = 0.42f;
    internal const float CloseProjectileRecognitionRadius = 135f;

    private static Hook nearestVisiblePlayerHook;

    internal static bool Installed => nearestVisiblePlayerHook != null;

    internal static void Enable()
    {
        if (Installed) return;
        Disable();
        try
        {
            MethodInfo method = typeof(DesertBatflyThreatRuntime).GetMethod(
                "NearestVisiblePlayer",
                BindingFlags.Static | BindingFlags.NonPublic,
                null,
                new[] { typeof(DesertBatfly), typeof(List<Player>), typeof(float) },
                null);
            if (method == null) return;
            nearestVisiblePlayerHook = new Hook(method, (NearestVisiblePlayerDetour)NearestVisiblePlayerHook);
        }
        catch
        {
            Disable();
        }
    }

    internal static void Disable()
    {
        try { nearestVisiblePlayerHook?.Dispose(); } catch { }
        nearestVisiblePlayerHook = null;
    }

    private static Player NearestVisiblePlayerHook(
        NearestVisiblePlayerOrig orig,
        DesertBatfly bat,
        List<Player> players,
        float maxDistance)
    {
        if (bat?.room == null || players == null) return orig(bat, players, maxDistance);

        float visibility = DesertBatflyEnvironmentalBehavior.VisibilityScale(bat);
        if (visibility >= 0.98f) return orig(bat, players, maxDistance);

        float scaledRange = maxDistance * Mathf.Lerp(MinimumVisualRangeScale, 1f, visibility);
        Player visible = orig(bat, players, scaledRange);
        if (visible != null) return visible;

        // DenseFog must not erase a spear/rock that is already physically close to the bat.
        // This fallback recognizes only the thrower of a currently thrown nearby weapon;
        // it does not restore long-range player or held-item vision.
        return NearbyProjectileThrower(bat, players);
    }

    private static Player NearbyProjectileThrower(DesertBatfly bat, List<Player> players)
    {
        Room room = bat?.room;
        if (room?.physicalObjects == null || players == null) return null;
        float radiusSq = CloseProjectileRecognitionRadius * CloseProjectileRecognitionRadius;

        for (int layer = 0; layer < room.physicalObjects.Length; layer++)
        {
            List<PhysicalObject> objects = room.physicalObjects[layer];
            if (objects == null) continue;
            for (int i = 0; i < objects.Count; i++)
            {
                if (objects[i] is not Weapon weapon || weapon.mode != Weapon.Mode.Thrown ||
                    weapon.firstChunk == null || weapon.thrownBy is not Player thrower ||
                    thrower.dead || thrower.room != room)
                    continue;
                if ((weapon.firstChunk.pos - bat.mainBodyChunk.pos).sqrMagnitude > radiusSq)
                    continue;

                for (int p = 0; p < players.Count; p++)
                    if (ReferenceEquals(players[p], thrower)) return thrower;
            }
        }
        return null;
    }
}
