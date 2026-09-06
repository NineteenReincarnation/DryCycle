using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

// This enum is now a passive diagnostic classifier only.  It no longer
// suppresses, starts, ends or otherwise controls a social-role state machine.
internal enum SocialRoleSuppression
{
    None,
    Unavailable,
    Restrained,
    Emergence,
    VanillaPriority,
    Danger,
    Fear,
    Trauma,
    Grief,
    Vengeance,
    Roost,
    Injury
}

// REJECTED FEATURE COMPATIBILITY SEAM
// -----------------------------------
// The Sentinel / Bully / Opportunist runtime feature was rejected after
// playtesting because role-specific steering could override FlyAI.localGoal and
// produce visibly artificial radial/vertical oscillation.  It also imposed a
// stronger social-organization model than the species needs.
//
// This class intentionally has NO gameplay authority.  It exists only while
// older DesertBatflyAI / Observatory / injury call sites are being structurally
// migrated.  Every behavioral modifier is neutral and every role is None.
// Never reintroduce role scoring, role lifecycle, threat watching, role quotas,
// localGoal writes or velocity steering through this compatibility seam.
internal sealed class DesertBatflySocialRoles
{
    private readonly DesertBatfly bat;

    internal ExpressedSocialRole Role => ExpressedSocialRole.None;
    internal ExpressedSocialRole Expressed => ExpressedSocialRole.None;
    internal DesertBatflyRoleScores Scores => default;
    internal int Commitment => 0;
    internal int Cooldown => 0;
    internal int EvaluationTicks => 0;
    internal float SentinelAlertConfidence => 0f;
    internal int OpportunityTicks => 0;
    internal bool OpportunistRecoveryActive => false;
    internal bool IsBully => false;
    internal bool WatchesInsteadOfInitiating => false;

    // Neutral ordinary-AI values.  These preserve the pre-role behavior of the
    // existing call sites until they are removed entirely.
    internal float HarassThresholdScale => 1f;
    internal float ObserveDurationScale => 1f;
    internal float ObserveRadius => 150f;
    internal float FakeDiveBonus => 0f;

    internal SocialRoleSuppression LastSuppression { get; private set; }

    internal DesertBatflySocialRoles(DesertBatfly bat)
    {
        this.bat = bat;
        Reset();
    }

    // Passive observation for the AI Observatory only.  No result from this
    // property may alter gameplay behavior.
    internal SocialRoleSuppression Suppression
    {
        get
        {
            if (bat == null || bat.dead || bat.slatedForDeletetion || bat.room == null ||
                bat.inShortcut || !bat.Consious)
                return SocialRoleSuppression.Unavailable;
            if (!DesertBatflySocialBond.CanRespond(bat))
                return SocialRoleSuppression.Restrained;
            if (bat.Emergence?.Active == true)
                return SocialRoleSuppression.Emergence;
            if (bat.AI == null || bat.AI.fleeFromRain || bat.AI.behavior == FlyAI.Behavior.Burrow ||
                bat.AI.luredCounter > 0 || bat.safariControlled)
                return SocialRoleSuppression.VanillaPriority;
            if (bat.Injury.BlocksCombat)
                return SocialRoleSuppression.Injury;
            if (DesertBatflyIntimidation.IsExtremeVengeanceActive(bat))
                return SocialRoleSuppression.Vengeance;

            float trauma = Mathf.Max(
                bat.DesertState.PlayerTraumaTicks > 0 ? bat.DesertState.PlayerTraumaStrength : 0f,
                bat.DesertState.PredatorTraumaTicks > 0 ? bat.DesertState.PredatorTraumaStrength : 0f);
            if (trauma >= DesertBatflyTuning.TraumaAggressionBlock)
                return SocialRoleSuppression.Trauma;
            if (bat.DesertState.GriefStrength >= 0.30f)
                return SocialRoleSuppression.Grief;
            if (bat.DesertAI.HasImmediateDanger)
                return SocialRoleSuppression.Danger;
            if (DesertBatflyIntimidation.BlocksSocialRoles(bat))
                return SocialRoleSuppression.Fear;
            if (bat.AI.behavior == FlyAI.Behavior.Chain || bat.DesertAI.Mode == DesertBatflyAI.Activity.Roost)
                return SocialRoleSuppression.Roost;
            return SocialRoleSuppression.None;
        }
    }

    internal void Reset()
    {
        LastSuppression = SocialRoleSuppression.None;
    }

    internal void Tick()
    {
        LastSuppression = Suppression;
    }

    // Legacy call sites may still invoke this method.  It is observation only;
    // unlike the removed implementation it cannot cancel AI actions, arm a
    // recovery window, raise an alarm or change a role.
    internal void CheckSuppression()
    {
        LastSuppression = Suppression;
    }

    internal void Evaluate(DesertBatflyFlockSnapshot flock) { }
    internal void BeginVisibleScan() { }
    internal void ObserveVisible(Creature creature, float distance, bool predator) { }
    internal void EndVisibleScan(DesertBatflyFlockSnapshot flock) { }

    // Retained only for binary/source compatibility with old tests and debug
    // helpers.  No runtime code should use this as a movement target.
    internal static Vector2 WatchGoal(Vector2 pos, Vector2 threat, Vector2 center, bool predator, bool sentinel) => pos;

    // Critical removal point: no role may write FlyAI.localGoal or add velocity.
    internal void BiasOrdinaryFlight(DesertBatflyFlockSnapshot flock) { }
}
