using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Realized-only companion rescue state. Rescue owns victim selection, motivation,
/// concurrency and collision confirmation, while ordinary flight remains under the existing
/// Combat PrimaryOwner and DB_FlightMotor. No second locomotion controller is introduced.
/// </summary>
internal sealed class DB_RescueRuntime
{
    private readonly DB_Creature bat;
    private DB_Creature victim;
    private Player holder;
    private int role;
    private int activeTicks;
    private int cooldown;
    private int nextScanTick;
    private int scanSerial;
    private float motivation;

    internal bool Active => victim != null && holder != null;
    internal DB_Creature Victim => victim;
    internal Player Holder => holder;
    internal float Motivation => motivation;
    internal Vector2 Goal => holder?.mainBodyChunk?.pos ?? bat.mainBodyChunk?.pos ?? Vector2.zero;
    internal float Commitment => Active ? Mathf.Lerp(0.76f, 0.96f, motivation) : 0f;

    internal DB_RescueRuntime(DB_Creature bat)
    {
        this.bat = bat;
    }

    internal void RefreshState()
    {
        if (cooldown > 0) cooldown--;

        if (Active)
        {
            if (!CurrentAssignmentValid())
            {
                Cancel(false);
                return;
            }

            // Do not occupy a rescue slot while this individual is physically incapable or
            // under a stronger survival response. The victim remains discoverable by others.
            if (bat.dead || !bat.Consious || bat.stun > 0 || bat.inShortcut ||
                bat.Injury.BlocksCombat || bat.DesertAI.HasImmediateDanger ||
                DB_FearRuntime.HasActiveFearSuppression(bat))
                Cancel(false);
            return;
        }

        if (cooldown > 0 || !CanConsiderRescue()) return;
        int clock = bat.room?.game?.clock ?? 0;
        if (nextScanTick == 0)
            nextScanTick = clock + StableInt(0x43A9, DB_Tuning.RescueScanMinTicks, DB_Tuning.RescueScanMaxTicks + 1);
        if (clock < nextScanTick) return;
        nextScanTick = clock + StableInt(
            0x51C7 + (++scanSerial * 37),
            DB_Tuning.RescueScanMinTicks,
            DB_Tuning.RescueScanMaxTicks + 1);

        DB_RoomContext context = DB_RoomContext.For(bat.room);
        if (context == null) return;
        var bats = context.Bats;
        DB_Creature bestVictim = null;
        Player bestHolder = null;
        float bestScore = DB_Tuning.RescueMotivationThreshold;
        int bestRole = -1;

        for (int i = 0; i < bats.Count; i++)
        {
            DB_Creature candidate = bats[i];
            if (candidate == null || candidate == bat || candidate.dead ||
                candidate.room != bat.room || !candidate.Restraint.IsPlayerHeld)
                continue;

            Player candidateHolder = candidate.Restraint.Holder;
            if (candidateHolder == null || candidateHolder.dead || candidateHolder.room != bat.room ||
                candidateHolder.mainBodyChunk == null || candidate.mainBodyChunk == null)
                continue;

            float distance = Vector2.Distance(bat.mainBodyChunk.pos, candidate.mainBodyChunk.pos);
            if (distance > DB_Tuning.RescueRange) continue;
            bool seesVictim = DB_VisibilityPolicy.CanObserve(
                bat, candidate.mainBodyChunk.pos, DB_Tuning.RescueRange, DB_VisibilityChannel.Creature);
            bool seesHolder = DB_VisibilityPolicy.CanObserve(
                bat, candidateHolder.mainBodyChunk.pos, DB_Tuning.RescueRange, DB_VisibilityChannel.Player);
            if (!seesVictim && !seesHolder) continue;

            int occupied = CountActiveRescuers(bats, candidate);
            if (occupied >= DB_Tuning.RescueMaxConcurrent) continue;

            float score = EvaluateMotivation(candidate, candidateHolder, distance);
            if (score <= bestScore) continue;
            bestScore = score;
            bestVictim = candidate;
            bestHolder = candidateHolder;
            bestRole = occupied;
        }

        if (bestVictim != null)
            Begin(bestVictim, bestHolder, bestRole, bestScore);
    }

    internal bool ApplyOwnedBehavior()
    {
        if (!Active || !DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.Combat) ||
            !CurrentAssignmentValid())
            return false;

        activeTicks++;
        if (activeTicks > DB_Tuning.RescueCommitTicks)
        {
            Cancel(true);
            return false;
        }

        // Rescue supersedes ordinary harassment inside the Combat owner. Clear only current
        // physical attack state; persistent grab/trauma memories remain owned by their domains.
        bat.DesertAI.CancelPhysicalAttack();
        DB_SocialRuntime.CancelForPriority(bat, "companion rescue owns Combat frame");

        Vector2 playerPos = holder.mainBodyChunk.pos;
        Vector2 predicted = playerPos + holder.mainBodyChunk.vel * 0.90f;
        float side = ((bat.Personality.VisualSeed & 1) == 0 ? -1f : 1f);

        // The backup occupies a nearby staging lane briefly so two rescuers do not hit the
        // exact same body chunk on the same frame. It commits if the primary has not freed the victim.
        if (role > 0 && activeTicks < DB_Tuning.RescueBackupDelayTicks)
        {
            Vector2 staging = playerPos + new Vector2(side * 72f, 56f);
            return DB_FlightMotor.TrySteer(
                bat,
                DB_BehaviorOwner.Combat,
                staging,
                Mathf.Lerp(8.5f, 10.5f, motivation));
        }

        Vector2 approach = Custom.DirVec(bat.mainBodyChunk.pos, predicted);
        if (approach.sqrMagnitude < 0.01f) approach = Vector2.down;
        Vector2 target = predicted + approach * Mathf.Lerp(4f, 18f, 1f - motivation);
        float speed = Mathf.Lerp(DB_Tuning.RescueSpeedMin, DB_Tuning.RescueSpeedMax, motivation);
        return DB_FlightMotor.TrySteer(bat, DB_BehaviorOwner.Combat, target, speed, response: 0.34f);
    }

    internal void AfterPhysics()
    {
        if (!Active || bat.room == null || holder?.bodyChunks == null || bat.mainBodyChunk == null ||
            !DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.Combat) || !CurrentAssignmentValid())
            return;

        BodyChunk contact = null;
        for (int i = 0; i < holder.bodyChunks.Length; i++)
        {
            BodyChunk chunk = holder.bodyChunks[i];
            if (chunk == null) continue;
            float radius = chunk.rad + bat.mainBodyChunk.rad + DB_Tuning.RescueContactPadding;
            if ((chunk.pos - bat.mainBodyChunk.pos).sqrMagnitude <= radius * radius)
            {
                contact = chunk;
                break;
            }
        }
        if (contact == null) return;

        Vector2 towardPlayer = Custom.DirVec(bat.mainBodyChunk.pos, contact.pos);
        if (towardPlayer.sqrMagnitude < 0.01f) towardPlayer = Vector2.right;
        Vector2 relative = bat.mainBodyChunk.vel - contact.vel;
        float closingSpeed = Mathf.Max(0f, Vector2.Dot(relative, towardPlayer));
        if (closingSpeed < DB_Tuning.RescueImpactMinSpeed) return;

        float impactT = Mathf.InverseLerp(
            DB_Tuning.RescueImpactMinSpeed,
            DB_Tuning.RescueStrongImpactSpeed,
            closingSpeed);
        float push = Mathf.Lerp(DB_Tuning.RescuePlayerImpulseMin, DB_Tuning.RescuePlayerImpulseMax, impactT);
        for (int i = 0; i < holder.bodyChunks.Length; i++)
            if (holder.bodyChunks[i] != null)
                holder.bodyChunks[i].vel += towardPlayer * push;
        bat.mainBodyChunk.vel -= towardPlayer * Mathf.Lerp(
            DB_Tuning.RescueRecoilMin,
            DB_Tuning.RescueRecoilMax,
            impactT);

        DB_Creature savedVictim = victim;
        Player struckHolder = holder;
        bool released = savedVictim.Restraint.RegisterRescueImpact(bat, struckHolder, closingSpeed);
        if (released)
            DB_SocialBond.OnSuccessfulRescue(bat, savedVictim);

        Vector2 escapeFrom = struckHolder.mainBodyChunk?.pos ?? bat.mainBodyChunk.pos - towardPlayer;
        Cancel(true);
        bat.DesertAI.BeginCombatEscape(escapeFrom, released ? 70 : 50);
    }

    internal void ClearTransient()
    {
        victim = null;
        holder = null;
        role = 0;
        activeTicks = 0;
        motivation = 0f;
        cooldown = 0;
        nextScanTick = 0;
        scanSerial = 0;
    }

    private void Begin(DB_Creature targetVictim, Player targetHolder, int targetRole, float score)
    {
        victim = targetVictim;
        holder = targetHolder;
        role = Mathf.Clamp(targetRole, 0, DB_Tuning.RescueMaxConcurrent - 1);
        activeTicks = 0;
        motivation = Mathf.Clamp01(score);
        bat.DesertAI.CancelPhysicalAttack();
        DB_SocialRuntime.CancelForPriority(bat, "companion captured -> rescue response");
    }

    private void Cancel(bool applyCooldown)
    {
        victim = null;
        holder = null;
        role = 0;
        activeTicks = 0;
        motivation = 0f;
        if (applyCooldown)
            cooldown = Mathf.Max(cooldown, DB_Tuning.RescueCooldownTicks);
    }

    private bool CurrentAssignmentValid()
    {
        return victim != null && holder != null && !victim.dead && !holder.dead &&
               victim.room == bat.room && holder.room == bat.room &&
               victim.Restraint.IsPlayerHeld && victim.Restraint.Holder == holder;
    }

    private bool CanConsiderRescue()
    {
        if (bat?.room == null || bat.mainBodyChunk == null || bat.dead || !bat.Consious ||
            bat.stun > 0 || bat.inShortcut || bat.Injury.BlocksCombat ||
            bat.DesertAI.RestrainedByNonFly() || bat.DesertAI.HasImmediateDanger ||
            DB_FearRuntime.HasActiveFearSuppression(bat) || DB_VengeanceRuntime.IsActive(bat))
            return false;
        if (DB_EnvironmentRuntime.TryGetInfluence(bat, out DB_EnvironmentInfluence environment) &&
            environment.HardSurvival)
            return false;
        return true;
    }

    private float EvaluateMotivation(DB_Creature targetVictim, Player threat, float distance)
    {
        float drive = bat.Personality.RescueDrive * 0.48f;
        float bond = DB_SocialBond.GetBondStrength(bat, targetVictim) * 0.30f;
        float social = Mathf.Clamp01(DB_SocialBond.Motivation(bat, targetVictim, threat)) * 0.55f;
        float distanceSupport = Mathf.Lerp(0.14f, 0f, Mathf.Clamp01(distance / DB_Tuning.RescueRange));

        int playerNumber = threat.playerState?.playerNumber ?? 0;
        DB_State state = bat.DesertState;
        float trauma = state.PlayerTraumaTicks > 0 && state.PlayerTraumaPlayer == playerNumber
            ? state.PlayerTraumaStrength
            : 0f;
        float fearPenalty = trauma * Mathf.Lerp(0.52f, 0.82f, 1f - bat.Personality.Nerve);
        float capability = Mathf.Clamp01(bat.Injury.PhysicalCapability) *
                           Mathf.Lerp(1f, 0.55f, bat.Injury.PostStunShock);

        return Mathf.Clamp01((drive + bond + social + distanceSupport - fearPenalty) * capability);
    }

    private int CountActiveRescuers(System.Collections.Generic.IReadOnlyList<DB_Creature> bats, DB_Creature target)
    {
        int count = 0;
        for (int i = 0; i < bats.Count; i++)
        {
            DB_Creature candidate = bats[i];
            if (candidate == null || candidate == bat) continue;
            if (candidate.Rescue.Active && candidate.Rescue.Victim == target && ++count >= DB_Tuning.RescueMaxConcurrent)
                break;
        }
        return count;
    }

    private int StableInt(int salt, int minInclusive, int maxExclusive)
    {
        if (maxExclusive <= minInclusive) return minInclusive;
        return minInclusive + Mathf.FloorToInt(Stable01(salt) * (maxExclusive - minInclusive));
    }

    private float Stable01(int salt)
    {
        unchecked
        {
            uint x = (uint)(bat.Personality.VisualSeed * 1103515245 + salt * 12345);
            x ^= x >> 16;
            x *= 0x7FEB352Du;
            x ^= x >> 15;
            x *= 0x846CA68Bu;
            x ^= x >> 16;
            return (x & 0x00FFFFFFu) / 16777215f;
        }
    }
}
