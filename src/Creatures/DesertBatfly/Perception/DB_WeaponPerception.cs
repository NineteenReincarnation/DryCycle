using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal readonly struct DB_WeaponObservation
{
    internal readonly Weapon Weapon;
    internal readonly Creature Instigator;
    internal readonly Vector2 Position;
    internal readonly Vector2 Velocity;
    internal readonly float ClosestApproachSqr;
    internal readonly bool Thrown;
    internal readonly bool HeldMovingSpear;

    internal DB_WeaponObservation(
        Weapon weapon,
        Creature instigator,
        Vector2 position,
        Vector2 velocity,
        float closestApproachSqr,
        bool thrown,
        bool heldMovingSpear)
    {
        Weapon = weapon;
        Instigator = instigator;
        Position = position;
        Velocity = velocity;
        ClosestApproachSqr = Mathf.Max(0f, closestApproachSqr);
        Thrown = thrown;
        HeldMovingSpear = heldMovingSpear;
    }
}

internal readonly struct DB_HeldThreatObservation
{
    internal readonly bool VisibleSpear;
    internal readonly bool VisibleRock;
    internal readonly bool VisibleExplosive;
    internal readonly bool VisibleStartle;
    internal readonly bool VisibleShock;

    internal DB_HeldThreatObservation(
        bool visibleSpear,
        bool visibleRock,
        bool visibleExplosive,
        bool visibleStartle,
        bool visibleShock)
    {
        VisibleSpear = visibleSpear;
        VisibleRock = visibleRock;
        VisibleExplosive = visibleExplosive;
        VisibleStartle = visibleStartle;
        VisibleShock = visibleShock;
    }

    internal bool Any => VisibleSpear || VisibleRock || VisibleExplosive ||
                         VisibleStartle || VisibleShock;
}

/// <summary>
/// Shared weapon observation over DB_RoomContext. This class never trains Threat memory and
/// never writes movement; it only validates currently observable held/thrown weapon facts.
/// </summary>
internal static class DB_WeaponPerception
{
    internal static bool TryFindIncomingProjectile(
        DesertBatfly observer,
        float maxDistance,
        float missRadius,
        float minimumVelocitySqr,
        out DB_WeaponObservation observation)
        => TryFindIncomingProjectileFrom(
            observer,
            null,
            maxDistance,
            missRadius,
            minimumVelocitySqr,
            out observation);

    internal static bool TryFindIncomingProjectileFrom(
        DesertBatfly observer,
        Creature requiredInstigator,
        float maxDistance,
        float missRadius,
        float minimumVelocitySqr,
        out DB_WeaponObservation observation)
    {
        observation = default;
        DB_RoomContext context = DB_RoomContext.For(observer?.room);
        if (observer?.mainBodyChunk == null || context == null) return false;

        var weapons = context.ThrownWeapons;
        float maxDistanceSqr = maxDistance * maxDistance;
        float missRadiusSqr = missRadius * missRadius;

        for (int i = 0; i < weapons.Count; i++)
        {
            Weapon weapon = weapons[i];
            if (weapon == null || weapon.slatedForDeletetion || weapon.firstChunk == null ||
                weapon.mode != Weapon.Mode.Thrown || weapon.thrownBy == observer)
                continue;

            Creature instigator = ResolveInstigator(weapon);
            if (requiredInstigator != null && !ReferenceEquals(instigator, requiredInstigator))
                continue;

            Vector2 position = weapon.firstChunk.pos;
            Vector2 velocity = weapon.firstChunk.vel;
            if (velocity.sqrMagnitude < minimumVelocitySqr) continue;

            Vector2 delta = observer.mainBodyChunk.pos - position;
            if (delta.sqrMagnitude > maxDistanceSqr) continue;

            float time = Mathf.Clamp(
                Vector2.Dot(delta, velocity) / Mathf.Max(1f, velocity.sqrMagnitude),
                0f,
                5f);
            float closest = (delta - velocity * time).sqrMagnitude;
            if (closest >= missRadiusSqr) continue;
            if (!DB_VisibilityPolicy.CanObserve(
                    observer,
                    position,
                    maxDistance,
                    DB_VisibilityChannel.Projectile,
                    realProjectile: true))
                continue;

            observation = new DB_WeaponObservation(
                weapon,
                instigator,
                position,
                velocity,
                closest,
                thrown: true,
                heldMovingSpear: false);
            return true;
        }

        return false;
    }

    internal static bool TryFindImmediateThreat(
        DesertBatfly observer,
        out DB_WeaponObservation observation)
    {
        observation = default;
        DB_RoomContext context = DB_RoomContext.For(observer?.room);
        if (observer?.mainBodyChunk == null || context == null) return false;

        // Preserve the old close moving-held-spear reaction without rescanning physicalObjects
        // for every bat. This path is intentionally stronger than general held-item caution.
        var weapons = context.Weapons;
        for (int i = 0; i < weapons.Count; i++)
        {
            Weapon weapon = weapons[i];
            if (weapon is not Spear || weapon.slatedForDeletetion || weapon.firstChunk == null ||
                weapon.grabbedBy == null || weapon.grabbedBy.Count == 0 ||
                weapon.grabbedBy[0]?.grabber == observer)
                continue;

            Vector2 movement = weapon.firstChunk.pos - weapon.firstChunk.lastPos;
            if (movement.sqrMagnitude <= 36f ||
                !DB_VisibilityPolicy.CanObserve(
                    observer,
                    weapon.firstChunk.pos,
                    65f,
                    DB_VisibilityChannel.Projectile,
                    realProjectile: true))
                continue;

            observation = new DB_WeaponObservation(
                weapon,
                weapon.grabbedBy[0]?.grabber,
                weapon.firstChunk.pos,
                movement,
                0f,
                thrown: false,
                heldMovingSpear: true);
            return true;
        }

        return TryFindIncomingProjectile(observer, 170f, 32f, 0f, out observation);
    }

    internal static bool TryObserveHeldThreats(
        DesertBatfly observer,
        Player player,
        float baseRange,
        out DB_HeldThreatObservation observation)
    {
        observation = default;
        if (observer?.room == null || player == null || player.dead ||
            player.room != observer.room || player.grasps == null ||
            !DB_VisibilityPolicy.CanObserve(
                observer,
                player.mainBodyChunk.pos,
                baseRange,
                DB_VisibilityChannel.HeldItem))
            return false;

        bool spear = false;
        bool rock = false;
        bool explosive = false;
        bool startle = false;
        bool shock = false;

        for (int i = 0; i < player.grasps.Length; i++)
        {
            PhysicalObject held = player.grasps[i]?.grabbed;
            if (held == null) continue;
            DB_ThreatEvidence evidence =
                DB_ThreatClassifier.Classify(held, null, 0f, 0f, false);
            spear |= held is Spear;
            rock |= held is Rock;
            explosive |= evidence.Explosion > 0.15f;
            startle |= evidence.Startle > 0.15f;
            shock |= evidence.Shock > 0.15f;
        }

        observation = new DB_HeldThreatObservation(spear, rock, explosive, startle, shock);
        return observation.Any;
    }

    private static Creature ResolveInstigator(Weapon weapon)
    {
        if (weapon?.thrownBy != null) return weapon.thrownBy;
        if (weapon?.grabbedBy != null && weapon.grabbedBy.Count > 0)
            return weapon.grabbedBy[0]?.grabber;
        return null;
    }
}
