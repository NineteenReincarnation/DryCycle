namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Stateless execution boundary between the frame arbiter and behavior-domain owners.
/// It owns no behavior state and makes no winner decision; it only validates the selected
/// PrimaryOwner and routes that owner to the existing domain implementation.
/// </summary>
internal static class DB_BehaviorExecution
{
    internal static bool TryImmediateDanger(DB_Creature bat, in DB_BehaviorResolution resolution)
    {
        if (bat == null || resolution.PrimaryOwner != DB_BehaviorOwner.ImmediateDanger ||
            !DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.ImmediateDanger))
            return false;
        DB_SocialRuntime.CancelForPriority(bat, "R3 PrimaryOwner=ImmediateDanger");
        return bat.DesertAI.ExecuteImmediateDangerOwned();
    }

    internal static bool TryInjuryRecovery(DB_Creature bat, in DB_BehaviorResolution resolution)
    {
        if (bat == null || resolution.PrimaryOwner != DB_BehaviorOwner.InjuryRecovery)
            return false;
        if (!DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.InjuryRecovery))
            return false;

        DB_SocialRuntime.CancelForPriority(bat, "R3 PrimaryOwner=InjuryRecovery");
        bat.DesertAI.CancelPhysicalAttack();
        return bat.DesertAI.ExecuteInjuryRecoveryOwned();
    }

    internal static bool TryEnvironment(DB_Creature bat, in DB_BehaviorResolution resolution)
    {
        if (bat == null || resolution.PrimaryOwner is not (
                DB_BehaviorOwner.EnvironmentHardSurvival or DB_BehaviorOwner.EnvironmentLocalSurvival))
            return false;
        if (!DB_BehaviorArbiter.IsPrimaryOwner(bat, resolution.PrimaryOwner))
            return false;

        if (resolution.WinningProposal.SuppressSocial)
            DB_SocialRuntime.CancelForPriority(bat, "R3 PrimaryOwner=" + resolution.PrimaryOwner);
        if (resolution.WinningProposal.SuppressCombat)
            bat.DesertAI.CancelPhysicalAttack();

        // Vanilla FlyAI.Update normally owns the final hive-tile -> Burrow transition. When
        // Environment owns the frame that native Update is intentionally skipped, so complete
        // the docking contract here before the ordinary environmental Dijkstra/anchor executor.
        if (DB_EnvironmentalHiveDocking.TryExecute(bat, resolution))
            return true;

        return DB_EnvironmentRuntime.ApplyOwnedBehavior(bat);
    }

    internal static bool TryFear(DB_Creature bat, in DB_BehaviorResolution resolution)
    {
        if (bat == null || resolution.PrimaryOwner != DB_BehaviorOwner.FearResponse ||
            !DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.FearResponse))
            return false;
        DB_SocialRuntime.CancelForPriority(bat, "R3 PrimaryOwner=FearResponse");
        return bat.DesertAI.ExecuteFearOwned(resolution);
    }

    internal static bool TryVengeance(DB_Creature bat, in DB_BehaviorResolution resolution)
    {
        if (bat == null || resolution.PrimaryOwner != DB_BehaviorOwner.Vengeance)
            return false;
        if (!DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.Vengeance))
            return false;

        DB_SocialRuntime.CancelForPriority(bat, "R3 PrimaryOwner=Vengeance");
        bat.DesertAI.CancelAttack();
        return DB_VengeanceRuntime.ExecuteOwned(bat);
    }

    internal static bool TryProjectileEvade(DB_Creature bat, in DB_BehaviorResolution resolution)
    {
        if (bat == null || resolution.PrimaryOwner != DB_BehaviorOwner.ImmediateProjectileEvade)
            return false;
        if (!DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.ImmediateProjectileEvade))
            return false;
        if (!resolution.FinalGoal.HasValue) return false;
        return DB_ThreatTactics.ApplyProjectileEvadeOwned(bat, resolution.FinalGoal.Value);
    }

    internal static bool TryFeeding(DB_Creature bat, in DB_BehaviorResolution resolution)
    {
        if (bat == null || resolution.PrimaryOwner != DB_BehaviorOwner.Feeding ||
            !DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.Feeding))
            return false;
        DB_SocialRuntime.CancelForPriority(bat, "R3 PrimaryOwner=Feeding");
        bat.DesertAI.CancelPhysicalAttack();
        return bat.Feeding.ApplyOwnedBehavior();
    }

    internal static bool TryCombat(DB_Creature bat, in DB_BehaviorResolution resolution)
    {
        if (bat == null || resolution.PrimaryOwner != DB_BehaviorOwner.Combat ||
            !DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.Combat))
            return false;
        DB_SocialRuntime.CancelForPriority(bat, "R3 PrimaryOwner=Combat");

        // Companion rescue is a specialized Combat-domain action, not a second locomotion
        // owner. It gets first execution choice inside Combat and still submits movement only
        // through DB_FlightMotor under the same accepted PrimaryOwner.
        if (bat.Rescue.Active)
            return bat.Rescue.ApplyOwnedBehavior();

        if (!bat.DesertAI.Combat.TryExecuteOwned()) return false;
        DB_ThreatRuntime.ApplyOwnedTacticalModifier(bat);
        return true;
    }

    internal static bool TryRoost(DB_Creature bat, in DB_BehaviorResolution resolution)
    {
        if (bat == null || resolution.PrimaryOwner != DB_BehaviorOwner.Roost ||
            !DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.Roost))
            return false;
        return bat.DesertAI.ExecuteRoostOwned();
    }

    internal static bool TrySocial(DB_Creature bat, in DB_BehaviorResolution resolution)
    {
        if (bat == null || resolution.PrimaryOwner != DB_BehaviorOwner.Social)
            return false;
        if (!DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.Social))
            return false;

        // Discrete Social events get first execution choice. When no event is active,
        // the same Social owner falls through to low-intensity ambient ecology rather than
        // immediately returning the frame to Ordinary/vanilla.
        if (DB_SocialRuntime.ApplyOwnedBehavior(bat))
            return true;
        return DB_AmbientSocialRuntime.ApplyOwnedBehavior(bat);
    }
}
