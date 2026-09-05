using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

internal enum SocialRoleSuppression { None, Unavailable, Restrained, Emergence, VanillaPriority, Danger, Fear, Trauma, Grief, Vengeance, Roost }

// Owned by the existing DesertAI, with a fixed number of counters and one visible threat.
internal sealed class DesertBatflySocialRoles
{
    private readonly DesertBatfly bat;
    internal ExpressedSocialRole Role { get; private set; }
    internal DesertBatflyRoleScores Scores { get; private set; }
    internal int Commitment { get; private set; }
    internal int Cooldown { get; private set; }
    internal int EvaluationTicks { get; private set; }
    internal float SentinelAlertConfidence { get; private set; }
    internal int OpportunityTicks { get; private set; }
    internal SocialRoleSuppression LastSuppression { get; private set; }
    private Creature visibleThreat, scanThreat;
    private bool visiblePredator, scanPredator, opportunistRecovery;
    private float scanDistance;
    private int watchTicks, clearSightTicks, alarmCooldown;

    internal DesertBatflySocialRoles(DesertBatfly bat) { this.bat = bat; Reset(); }
    internal ExpressedSocialRole Expressed => Suppression == SocialRoleSuppression.None ? Role : ExpressedSocialRole.None;
    internal bool IsBully => Expressed == ExpressedSocialRole.Bully;
    internal bool WatchesInsteadOfInitiating => Expressed is ExpressedSocialRole.Sentinel or ExpressedSocialRole.Opportunist;
    internal bool OpportunistRecoveryActive => opportunistRecovery && OpportunityTicks > 0;
    internal float HarassThresholdScale => IsBully ? 0.82f : 1f;
    internal float ObserveDurationScale => IsBully ? 0.82f : 1f;
    internal float ObserveRadius => IsBully ? 115f : 150f;
    internal float FakeDiveBonus => IsBully ? 0.14f : 0f;

    internal SocialRoleSuppression Suppression
    {
        get
        {
            if (bat == null || bat.dead || bat.slatedForDeletetion || bat.room == null || bat.inShortcut || !bat.Consious)
                return SocialRoleSuppression.Unavailable;
            // Fly-on-Fly grabbedBy entries are the vanilla hanging chain. Only a real
            // non-Fly restraint belongs here; chain/roost is classified below.
            if (!DesertBatflySocialBond.CanRespond(bat)) return SocialRoleSuppression.Restrained;
            if (bat.Emergence?.Active == true) return SocialRoleSuppression.Emergence;
            if (bat.AI == null || bat.AI.fleeFromRain || bat.AI.behavior == FlyAI.Behavior.Burrow ||
                bat.AI.luredCounter > 0 || bat.safariControlled) return SocialRoleSuppression.VanillaPriority;
            if (DesertBatflyIntimidation.IsExtremeVengeanceActive(bat)) return SocialRoleSuppression.Vengeance;
            if (Trauma >= DesertBatflyTuning.TraumaAggressionBlock) return SocialRoleSuppression.Trauma;
            if (bat.DesertState.GriefStrength >= 0.30f) return SocialRoleSuppression.Grief;
            if (bat.DesertAI.HasImmediateDanger) return SocialRoleSuppression.Danger;
            if (DesertBatflyIntimidation.BlocksSocialRoles(bat)) return SocialRoleSuppression.Fear;
            if (bat.AI.behavior == FlyAI.Behavior.Chain || bat.DesertAI.Mode == DesertBatflyAI.Activity.Roost)
                return SocialRoleSuppression.Roost;
            return SocialRoleSuppression.None;
        }
    }
    private float Trauma => Mathf.Max(bat.DesertState.PlayerTraumaTicks > 0 ? bat.DesertState.PlayerTraumaStrength : 0f,
        bat.DesertState.PredatorTraumaTicks > 0 ? bat.DesertState.PredatorTraumaStrength : 0f);

    internal void Reset()
    {
        Role = ExpressedSocialRole.None;
        Scores = default;
        Commitment = Cooldown = OpportunityTicks = watchTicks = clearSightTicks = alarmCooldown = 0;
        EvaluationTicks = 1 + (int)((uint)bat.Personality.VisualSeed % 120u);
        visibleThreat = scanThreat = null;
        opportunistRecovery = false;
        SentinelAlertConfidence = 0f;
        LastSuppression = SocialRoleSuppression.None;
    }

    // Exactly once from the realized creature update, even if vanilla skips its AI.
    internal void Tick()
    {
        if (EvaluationTicks > 0) EvaluationTicks--;
        if (Commitment > 0) Commitment--;
        if (Cooldown > 0) Cooldown--;
        if (OpportunityTicks > 0 && --OpportunityTicks == 0) opportunistRecovery = false;
        if (alarmCooldown > 0) alarmCooldown--;
        CheckSuppression();
        if (EvaluationTicks == 0 && LastSuppression != SocialRoleSuppression.None)
            EvaluationTicks = 120; // Preserve the phase while suppressed, rather than synchronize on recovery.
    }

    internal void CheckSuppression()
    {
        LastSuppression = Suppression;
        if (LastSuppression == SocialRoleSuppression.None) return;
        if (LastSuppression is SocialRoleSuppression.Danger or SocialRoleSuppression.Fear)
        {
            // A currently expressed Opportunist may remember that it was interrupted by
            // this threat. The role itself still ends and receives normal cooldown; only
            // a bounded, safety-gated return bias survives so cooldown cannot erase the
            // defining "comes back early" behavior.
            if (Role == ExpressedSocialRole.Opportunist) opportunistRecovery = true;
            OpportunityTicks = 600; // Evidence of a past threat; never authority to cancel fear.
        }
        else
        {
            // Unrelated high-priority states invalidate an old threat-recovery window.
            opportunistRecovery = false;
        }
        EndExpression();
        SentinelAlertConfidence = 0f;
        watchTicks = 0;
        if (LastSuppression == SocialRoleSuppression.Unavailable)
            visibleThreat = scanThreat = null;
    }

    private void EndExpression()
    {
        if (Role == ExpressedSocialRole.None) return;
        Role = ExpressedSocialRole.None;
        Commitment = 0;
        // Transition only: suppression must not refresh this timer every frame.
        Cooldown = 240 + (int)((uint)bat.Personality.VisualSeed % 121u);
    }

    internal void Evaluate(DesertBatflyFlockSnapshot flock)
    {
        CheckSuppression();
        if (LastSuppression != SocialRoleSuppression.None || EvaluationTicks > 0) return;
        EvaluationTicks = 120;
        float opportunity = SafeOpportunity(flock) ? 1f : 0f;
        Scores = DesertBatflyRoleScores.Calculate(bat.Personality, flock.PanicRatio,
            bat.DesertState.GriefStrength, Trauma, opportunity);
        if (Role != ExpressedSocialRole.None)
        {
            if (Commitment <= 0 || Scores.For(Role) < 0.72f) EndExpression();
            return;
        }
        // Never acquire a new expression in the middle of an already committed attack.
        if (Cooldown > 0 || bat.DesertAI.FormalAttack || flock.ActiveCount == 0) return;
        ExpressedSocialRole candidate = Scores.Select(flock.ActiveCount, flock.ExpressedRoleCount);
        // Watching must not silently take over an ordinary attack already in progress.
        if (candidate != ExpressedSocialRole.Bully && bat.DesertAI.Target != null) return;
        Role = candidate;
        if (Role != ExpressedSocialRole.None)
        {
            opportunistRecovery = false;
            Commitment = 800 + (int)((uint)bat.Personality.VisualSeed % 301u);
        }
    }

    internal void BeginVisibleScan() { scanThreat = null; scanDistance = float.MaxValue; scanPredator = false; }
    internal void ObserveVisible(Creature creature, float distance, bool predator)
    {
        if (!predator && creature is not Player) return;
        // A nearer player must not hide a visible Peach from the watch correction.
        if (scanPredator && !predator) return;
        if (scanPredator == predator && distance >= scanDistance) return;
        scanThreat = creature; scanDistance = distance; scanPredator = predator;
    }
    internal void EndVisibleScan(DesertBatflyFlockSnapshot flock)
    {
        if (scanThreat != visibleThreat)
        {
            if (visibleThreat != null) OpportunityTicks = 600;
            watchTicks = 0;
            SentinelAlertConfidence = 0f;
        }
        visibleThreat = scanThreat;
        visiblePredator = scanPredator;
        // Clear-sight time only counts after all role suppression has actually ended.
        // Scans performed while retreating or under Fear must not pre-pay the 40-tick
        // safety confirmation and cause an instant return on the first safe frame.
        clearSightTicks = visibleThreat == null && Suppression == SocialRoleSuppression.None
            ? Mathf.Min(600, clearSightTicks + 8)
            : 0;
        if (Expressed != ExpressedSocialRole.Sentinel || visibleThreat == null) return;
        watchTicks += 8;
        Vector2 towardFlock = Custom.DirVec(visibleThreat.mainBodyChunk.pos, flock.Center);
        float closing = Vector2.Dot(visibleThreat.mainBodyChunk.vel, towardFlock);
        bool weapon = false;
        if (visibleThreat is Player player && player.grasps != null)
            foreach (var grasp in player.grasps)
                if (grasp?.grabbed is Weapon) { weapon = true; break; }
        bool evidence = visiblePredator || closing > 1.2f || weapon ||
            (scanDistance < 205f && closing > 0.2f) || flock.PanicRatio > 0.15f;
        SentinelAlertConfidence = Mathf.Clamp01(SentinelAlertConfidence + (evidence ? 0.18f : -0.10f));
        if (watchTicks >= 24 && SentinelAlertConfidence >= 0.72f && alarmCooldown == 0)
        {
            alarmCooldown = 480;
            // A suspicion is one existing local alarm/escape, never a death/capture broadcast.
            bat.DesertAI.Threatened(visibleThreat, false);
            CheckSuppression();
        }
    }

    private bool SafeOpportunity(DesertBatflyFlockSnapshot flock) => OpportunityTicks > 0 && clearSightTicks >= 40 &&
        flock.PanicRatio <= 0.10f && flock.PanicRatio <= flock.PreviousPanicRatio &&
        Suppression == SocialRoleSuppression.None && Trauma < 0.15f && !DesertBatflyIntimidation.BlocksSocialRoles(bat);

    internal static Vector2 WatchGoal(Vector2 pos, Vector2 threat, Vector2 center, bool predator, bool sentinel)
    {
        float distance = Vector2.Distance(pos, threat);
        float standOff = predator ? Mathf.Max(340f, distance) : sentinel ? 230f : 270f;
        Vector2 goal = threat + Custom.DirVec(threat, pos) * standOff;
        goal = center + Vector2.ClampMagnitude(goal - center, 260f);
        // Perimeter clamping must not inadvertently turn an outward retreat into an approach.
        if (predator && Vector2.Dot(goal - pos, threat - pos) > 0f) return pos;
        if (Vector2.Distance(goal, threat) < Mathf.Min(distance, standOff)) return pos;
        return goal;
    }

    internal void BiasOrdinaryFlight(DesertBatflyFlockSnapshot flock)
    {
        ExpressedSocialRole role = Expressed;
        bool safeOpportunity = (role == ExpressedSocialRole.Opportunist || OpportunistRecoveryActive) && SafeOpportunity(flock);
        bool recoveryReturn = role == ExpressedSocialRole.None && OpportunistRecoveryActive && safeOpportunity;
        if ((role is not (ExpressedSocialRole.Sentinel or ExpressedSocialRole.Opportunist) && !recoveryReturn) ||
            flock.ActiveCount < 2 || bat.DesertAI.Target != null ||
            bat.DesertAI.Mode is not (DesertBatflyAI.Activity.Flight or DesertBatflyAI.Activity.Cooldown) ||
            (bat.AI.behavior != FlyAI.Behavior.Idle && bat.AI.behavior != FlyAI.Behavior.Swarm)) return;
        Vector2 pos = bat.mainBodyChunk.pos;
        Vector2 outward = pos - flock.Center;
        if (outward.sqrMagnitude < 1f) outward = Custom.DegToVec((uint)bat.Personality.VisualSeed % 360u);
        Vector2 goal;
        if (visibleThreat != null && visibleThreat.room == bat.room && !visibleThreat.dead &&
            !visibleThreat.inShortcut && !visibleThreat.slatedForDeletetion &&
            bat.room.VisualContact(pos, visibleThreat.mainBodyChunk.pos))
        {
            goal = WatchGoal(pos, visibleThreat.mainBodyChunk.pos, flock.Center, visiblePredator,
                role == ExpressedSocialRole.Sentinel);
        }
        else if (role == ExpressedSocialRole.Sentinel)
            goal = flock.Center + outward.normalized * 190f;
        else if (safeOpportunity)
            goal = flock.Center + outward.normalized * 95f;
        else return;
        // Small reachable correction only. Vanilla still owns locomotion and pathing.
        Vector2 step = pos + Vector2.ClampMagnitude(goal - pos, 24f);
        if (bat.room.aimap == null || !bat.AI.ValidSwarmPosition(step) ||
            bat.room.GetTile(step).Solid || (bat.room.terrain != null && bat.room.terrain.Contains(step)) ||
            !bat.room.VisualContact(pos, step)) return;
        bat.AI.localGoal = step;
        Vector2 desired = Vector2.ClampMagnitude(goal - pos, 3f);
        bat.mainBodyChunk.vel += Vector2.ClampMagnitude(desired - bat.mainBodyChunk.vel, 0.16f);
    }
}