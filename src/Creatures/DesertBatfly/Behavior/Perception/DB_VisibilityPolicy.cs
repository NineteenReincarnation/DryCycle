using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal enum DB_VisibilityChannel
{
    Creature,
    Player,
    Social,
    Signal,
    HeldItem,
    Projectile
}

/// <summary>
/// Single policy for Desert Batfly line-of-sight + environmental visual confidence.
///
/// Room.VisualContact remains the terrain authority. DryCycle weather contributes
/// VisibilityConfidence only through the already-validated environmental influence, so a
/// RoomSettings fog/shader cannot activate this policy by itself.
/// </summary>
internal static class DB_VisibilityPolicy
{
    internal const float CloseProjectileFloor = 90f;

    internal static bool CanObserve(
        DesertBatfly observer,
        Vector2 targetPosition,
        float baseRange,
        DB_VisibilityChannel channel,
        bool realProjectile = false)
    {
        if (observer?.room == null || observer.mainBodyChunk == null || baseRange <= 0f)
            return false;

        Vector2 origin = observer.mainBodyChunk.pos;
        if (!observer.room.VisualContact(origin, targetPosition))
            return false;

        float confidence = Mathf.Clamp01(
            DB_EnvironmentRuntime.VisibilityScale(observer));
        float range = EffectiveRange(baseRange, confidence, channel, realProjectile);
        return (targetPosition - origin).sqrMagnitude <= range * range;
    }

    internal static float EffectiveRange(
        float baseRange,
        float visibilityConfidence,
        DB_VisibilityChannel channel,
        bool realProjectile = false)
    {
        baseRange = Mathf.Max(0f, baseRange);
        visibilityConfidence = Mathf.Clamp01(visibilityConfidence);
        if (baseRange <= 0f) return 0f;

        float minimumScale = channel switch
        {
            DB_VisibilityChannel.Projectile => realProjectile ? 0.38f : 0.28f,
            DB_VisibilityChannel.HeldItem => 0.34f,
            DB_VisibilityChannel.Signal => 0.34f,
            DB_VisibilityChannel.Social => 0.42f,
            DB_VisibilityChannel.Player => 0.32f,
            _ => 0.30f
        };

        float effective = baseRange * Mathf.Lerp(minimumScale, 1f, visibilityConfidence);
        if (channel == DB_VisibilityChannel.Projectile && realProjectile)
            effective = Mathf.Max(Mathf.Min(baseRange, CloseProjectileFloor), effective);
        return Mathf.Min(baseRange, effective);
    }
}
