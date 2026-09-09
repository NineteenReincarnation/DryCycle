using DryCycle.Thirst;
using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Per-bat dehydration-feeding state. Room-level capacity/target facts belong to
/// DB_FeedingCoordinator; this runtime owns only this individual's approach/cloud/attach
/// commitment and submits ordinary flight through the accepted Feeding PrimaryOwner.
/// </summary>
internal sealed class DB_DehydrationFeedingRuntime
{
    private readonly DB_Creature bat;
    private DB_FeedingAssignment assignment;
    private bool attached;
    private int activeTicks;
    private int attachedTicks;
    private int cooldownTicks;
    private int scanTicks;
    private int scanSerial;
    private int shakeSerial;

    internal bool Active => assignment.Valid;
    internal bool Attached => attached && assignment.Role == DB_FeedingRole.Attach;
    internal Player Target => assignment.Target;
    internal DB_FeedingRole Role => assignment.Role;
    internal float Motivation => assignment.Motivation;
    internal float Commitment => Active
        ? Mathf.Clamp01(Mathf.Lerp(0.68f, 0.93f, assignment.Motivation) + (Attached ? 0.05f : 0f))
        : 0f;
    internal Vector2 Goal => Active ? GoalForAssignment() : bat.mainBodyChunk?.pos ?? Vector2.zero;

    internal DB_DehydrationFeedingRuntime(DB_Creature bat)
    {
        this.bat = bat;
    }

    internal void RefreshState()
    {
        if (cooldownTicks > 0) cooldownTicks--;

        if (Active)
        {
            if (!CanMaintain() || !DB_FeedingCoordinator.TryGetExisting(bat, out DB_FeedingAssignment current))
            {
                Cancel(true, false);
                return;
            }

            if (attached && current.Role != DB_FeedingRole.Attach)
            {
                attached = false;
                attachedTicks = 0;
                DB_FeedingCoordinator.MarkAttached(bat, false);
                if (bat.movMode == Fly.MovementMode.Passive)
                    bat.movMode = Fly.MovementMode.BatFlight;
            }
            assignment = current;
            return;
        }

        if (cooldownTicks > 0 || !CanConsider()) return;
        if (scanTicks <= 0)
            scanTicks = StableInt(
                0x3A17 + (++scanSerial * 47),
                DB_Tuning.FeedingScanMinTicks,
                DB_Tuning.FeedingScanMaxTicks + 1);
        if (--scanTicks > 0) return;
        scanTicks = 0;

        if (DB_FeedingCoordinator.TryGetOrAcquire(bat, out DB_FeedingAssignment acquired))
        {
            assignment = acquired;
            attached = false;
            activeTicks = 0;
            attachedTicks = 0;
            shakeSerial = 0;
        }
    }

    internal bool ApplyOwnedBehavior()
    {
        if (!Active || !DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.Feeding) ||
            !CanMaintain() || !DB_FeedingCoordinator.TryGetExisting(bat, out DB_FeedingAssignment current))
            return false;

        assignment = current;
        bat.DesertAI.CancelPhysicalAttack();
        DB_SocialRuntime.CancelForPriority(bat, "dehydrated-player feeding owns frame");

        if (++activeTicks > DB_Tuning.FeedingCommitMaxTicks)
        {
            Cancel(true, true);
            return true;
        }

        if (assignment.Role == DB_FeedingRole.Cloud)
        {
            attached = false;
            float speed = Mathf.Lerp(
                DB_Tuning.FeedingCloudSpeedMin,
                DB_Tuning.FeedingCloudSpeedMax,
                assignment.Motivation);
            return DB_FlightMotor.TrySteer(
                bat,
                DB_BehaviorOwner.Feeding,
                GoalForAssignment(),
                speed,
                response: 0.24f);
        }

        if (assignment.Role != DB_FeedingRole.Attach)
            return false;

        if (Attached)
        {
            bat.movMode = Fly.MovementMode.Passive;
            return true;
        }

        BodyChunk chunk = AssignedChunk();
        if (chunk == null)
        {
            Cancel(true, false);
            return true;
        }

        Vector2 goal = AttachmentPosition(chunk);
        bool steered = DB_FlightMotor.TrySteer(
            bat,
            DB_BehaviorOwner.Feeding,
            goal,
            Mathf.Lerp(
                DB_Tuning.FeedingApproachSpeedMin,
                DB_Tuning.FeedingApproachSpeedMax,
                assignment.Motivation),
            response: 0.30f);

        float contactRadius = chunk.rad + bat.mainBodyChunk.rad + DB_Tuning.FeedingContactPadding;
        if ((chunk.pos - bat.mainBodyChunk.pos).sqrMagnitude <= contactRadius * contactRadius &&
            bat.room.VisualContact(bat.mainBodyChunk.pos, chunk.pos))
        {
            attached = true;
            attachedTicks = 0;
            DB_FeedingCoordinator.MarkAttached(bat, true);
            bat.movMode = Fly.MovementMode.Passive;
        }

        return steered || attached;
    }

    internal void AfterPhysics(bool eu)
    {
        if (!Attached) return;
        if (!DB_BehaviorArbiter.IsPrimaryOwner(bat, DB_BehaviorOwner.Feeding) ||
            !CanMaintain() || !DB_FeedingCoordinator.TryGetExisting(bat, out DB_FeedingAssignment current) ||
            current.Role != DB_FeedingRole.Attach)
        {
            Cancel(true, false);
            return;
        }

        assignment = current;
        BodyChunk chunk = AssignedChunk();
        if (chunk == null || bat.mainBodyChunk == null)
        {
            Cancel(true, false);
            return;
        }

        Vector2 position = AttachmentPosition(chunk);
        if (bat.room.GetTile(position).Solid ||
            !Custom.DistLess(bat.mainBodyChunk.pos, chunk.pos, DB_Tuning.FeedingAttachValidationRange))
        {
            Cancel(true, true);
            return;
        }

        bat.movMode = Fly.MovementMode.Passive;
        bat.mainBodyChunk.MoveFromOutsideMyUpdate(eu, position);
        bat.mainBodyChunk.vel = chunk.vel;
        attachedTicks++;

        if (attachedTicks >= DB_Tuning.FeedingAttachMaxTicks || ShouldShakeOff(chunk))
            Cancel(true, true);
    }

    /// <summary>
    /// Release only the physical pin when a higher-priority owner preempts Feeding.
    /// The room reservation survives, so a short evade does not churn group assignment.
    /// </summary>
    internal void YieldAttachmentForHigherPriority()
    {
        if (!Attached) return;
        attached = false;
        attachedTicks = 0;
        DB_FeedingCoordinator.MarkAttached(bat, false);
        if (bat.movMode == Fly.MovementMode.Passive)
            bat.movMode = Fly.MovementMode.BatFlight;
    }

    internal void CancelForGrab()
    {
        if (Active)
            Cancel(true, false);
    }

    internal void ClearTransient()
    {
        if (bat?.room != null)
            DB_FeedingCoordinator.Release(bat);
        if (attached && bat != null && bat.movMode == Fly.MovementMode.Passive)
            bat.movMode = Fly.MovementMode.BatFlight;
        assignment = default;
        attached = false;
        activeTicks = 0;
        attachedTicks = 0;
        cooldownTicks = 0;
        scanTicks = 0;
        scanSerial = 0;
        shakeSerial = 0;
    }

    private bool CanConsider()
    {
        if (bat?.room == null || bat.mainBodyChunk == null || bat.dead || !bat.Consious ||
            bat.stun > 0 || bat.inShortcut || bat.Restraint.IsPlayerHeld || bat.Rescue.Active ||
            bat.AI == null || bat.safariControlled || bat.AI.fleeFromRain || bat.AI.luredCounter > 0 ||
            bat.AI.behavior == FlyAI.Behavior.Burrow ||
            bat.Injury.BlocksCombat || DB_RestraintPolicy.IsRestrainedByNonFly(bat) ||
            bat.DesertAI.HasImmediateDanger || DB_FearRuntime.HasActiveFearSuppression(bat) ||
            DB_VengeanceRuntime.IsActive(bat))
            return false;

        if (DB_TravelRuntime.CanOwnRealizedFrame(bat, out _)) return false;
        if (DB_EnvironmentRuntime.TryGetInfluence(bat, out DB_EnvironmentInfluence environment) &&
            environment.Phase is DB_EnvironmentPhase.Preparation or
                DB_EnvironmentPhase.Sheltering or DB_EnvironmentPhase.Acute)
            return false;
        return true;
    }

    private bool CanMaintain()
    {
        if (!CanConsider() || !assignment.Valid || assignment.Target.dead ||
            assignment.Target.room != bat.room || assignment.Target.inShortcut)
            return false;
        return PlayerDehydrationFacts.For(assignment.Target).FeedingEligible;
    }

    private BodyChunk AssignedChunk()
    {
        Player target = assignment.Target;
        if (target?.bodyChunks == null || target.bodyChunks.Length == 0) return null;
        int preferred = assignment.SlotIndex switch
        {
            0 or 1 or 4 => 0,
            _ => 1
        };
        if (preferred >= target.bodyChunks.Length || target.bodyChunks[preferred] == null)
            preferred = 0;
        return target.bodyChunks[preferred];
    }

    private Vector2 AttachmentPosition(BodyChunk chunk)
    {
        float angle = assignment.SlotIndex switch
        {
            0 => 155f,
            1 => 25f,
            2 => 205f,
            3 => 335f,
            4 => 92f,
            5 => 268f,
            _ => 90f
        };
        Vector2 normal = Custom.DegToVec(angle);
        return chunk.pos + normal * (chunk.rad + bat.mainBodyChunk.rad * 0.52f);
    }

    private Vector2 GoalForAssignment()
    {
        if (!assignment.Valid || assignment.Target.mainBodyChunk == null)
            return bat.mainBodyChunk?.pos ?? Vector2.zero;

        if (assignment.Role == DB_FeedingRole.Attach)
        {
            BodyChunk chunk = AssignedChunk();
            return chunk != null ? AttachmentPosition(chunk) : assignment.Target.mainBodyChunk.pos;
        }

        int clock = bat.room?.game?.clock ?? 0;
        float phase = (clock * 0.035f) + assignment.SlotIndex * 1.63f +
                      (bat.Personality.VisualSeed & 255) * 0.013f;
        float radiusX = 30f + assignment.SlotIndex * 5f;
        float radiusY = 22f + (assignment.SlotIndex % 2) * 8f;
        Vector2 center = assignment.Target.mainBodyChunk.pos;
        return center + new Vector2(
            Mathf.Cos(phase) * radiusX,
            8f + Mathf.Sin(phase * 1.17f) * radiusY);
    }

    private bool ShouldShakeOff(BodyChunk attachedChunk)
    {
        if (attachedTicks < DB_Tuning.FeedingShakeGraceTicks ||
            attachedTicks % DB_Tuning.FeedingShakeCheckTicks != 0)
            return false;

        float speed = attachedChunk.vel.magnitude;
        if (speed < DB_Tuning.FeedingShakeSpeedMin) return false;
        float chance = Mathf.Lerp(
            DB_Tuning.FeedingShakeChanceMin,
            DB_Tuning.FeedingShakeChanceMax,
            Mathf.InverseLerp(
                DB_Tuning.FeedingShakeSpeedMin,
                DB_Tuning.FeedingShakeSpeedMax,
                speed));
        return Stable01(0x6B21 + (++shakeSerial * 83) + attachedTicks * 7) < chance;
    }

    private void Cancel(bool applyCooldown, bool impulseAway)
    {
        Player previousTarget = assignment.Target;
        if (attached)
            DB_FeedingCoordinator.MarkAttached(bat, false);
        DB_FeedingCoordinator.Release(bat);

        attached = false;
        assignment = default;
        activeTicks = 0;
        attachedTicks = 0;
        shakeSerial = 0;
        scanTicks = 0;
        if (applyCooldown)
            cooldownTicks = Mathf.Max(cooldownTicks, DB_Tuning.FeedingCooldownTicks);
        if (bat.movMode == Fly.MovementMode.Passive)
            bat.movMode = Fly.MovementMode.BatFlight;

        if (impulseAway && previousTarget?.mainBodyChunk != null && bat.mainBodyChunk != null)
        {
            Vector2 away = Custom.DirVec(previousTarget.mainBodyChunk.pos, bat.mainBodyChunk.pos);
            if (away.sqrMagnitude < 0.01f) away = Vector2.up;
            bat.mainBodyChunk.vel += away * 4.8f + Vector2.up * 1.8f;
        }
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