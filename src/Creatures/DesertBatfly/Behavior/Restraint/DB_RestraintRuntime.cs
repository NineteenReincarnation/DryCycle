using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Sole owner of a Desert Batfly's player-grasp session. It tracks the exact grasp,
/// accumulates personality/condition-driven escape pressure, performs discrete struggle
/// checks and releases only that grasp. Sand-spit is a subordinate held-defense action.
/// </summary>
internal sealed class DB_RestraintRuntime
{
    private readonly DB_Creature bat;
    private Player holder;
    private Creature.Grasp grasp;
    private int holdTicks;
    private int pulseTicks;
    private int pulseSerial;
    private int holdSerial;
    private float escapePressure;
    private float escapeThreshold = 1f;

    internal bool IsPlayerHeld => holder != null && IsCurrentGrasp();
    internal Player Holder => IsPlayerHeld ? holder : null;
    internal float EscapePressure => escapePressure;
    internal float EscapeThreshold => escapeThreshold;
    internal int HoldTicks => holdTicks;

    internal DB_RestraintRuntime(DB_Creature bat)
    {
        this.bat = bat;
    }

    internal bool BeginPlayerHold(Player player, Creature.Grasp playerGrasp)
    {
        if (player == null || playerGrasp == null || playerGrasp.grabbed != bat ||
            playerGrasp.grabber != player)
            return false;

        bool newSession = holder != player || !ReferenceEquals(grasp, playerGrasp);
        holder = player;
        grasp = playerGrasp;
        if (!newSession) return false;

        holdSerial++;
        holdTicks = 0;
        pulseSerial = 0;
        pulseTicks = NextPulseInterval();
        escapePressure = 0f;
        PrepareThreshold();
        bat.SandSpit.BeginPlayerHold();
        return true;
    }

    internal void Tick()
    {
        if (holder == null) return;
        if (!IsCurrentGrasp())
        {
            EndExternalRelease();
            return;
        }

        if (bat.dead || bat.slatedForDeletetion || bat.room == null || holder.room != bat.room)
            return;

        bat.SandSpit.UpdateHeldStruggle(holder);
        holdTicks++;

        // Unconscious/stunned animals stay grabbable; shock and injury reduce effective
        // struggle rather than being bypassed by an unrelated escape timer.
        if (!bat.Consious || bat.stun > 0 || bat.inShortcut) return;
        if (--pulseTicks > 0) return;
        pulseSerial++;
        pulseTicks = NextPulseInterval();

        float capability = Mathf.Clamp01(bat.Injury.PhysicalCapability);
        float shockScale = Mathf.Lerp(1f, 0.48f, bat.Injury.PostStunShock);
        float movement = holder.mainBodyChunk != null
            ? Mathf.Clamp01(holder.mainBodyChunk.vel.magnitude / 9f)
            : 0f;
        float drive = bat.Personality.EscapeDrive;
        float gain = Mathf.Lerp(
            DB_Tuning.GrabEscapePressureMinPerPulse,
            DB_Tuning.GrabEscapePressureMaxPerPulse,
            drive);
        gain *= Mathf.Lerp(0.58f, 1f, capability) * shockScale;
        gain *= 1f + movement * DB_Tuning.GrabEscapeHolderMovementBonus;
        escapePressure = Mathf.Clamp(escapePressure + gain, 0f, escapeThreshold * 1.25f);

        if (holdTicks < DB_Tuning.GrabEscapeMinimumHoldTicks) return;

        float pressureT = Mathf.Clamp01(escapePressure / Mathf.Max(0.01f, escapeThreshold));
        float burstChance = Mathf.Lerp(
            DB_Tuning.GrabEscapeBurstChanceMin,
            DB_Tuning.GrabEscapeBurstChanceMax,
            pressureT * pressureT) * Mathf.Lerp(0.78f, 1.22f, drive);

        int burstSalt = 0x2D51 + pulseSerial * 43 + holdSerial * 131;
        if (escapePressure >= escapeThreshold || Stable01(burstSalt) < burstChance)
            Release(DB_GrabEscapeCause.SelfStruggle, 0f);
    }

    internal bool RegisterRescueImpact(DB_Creature rescuer, Player struckHolder, float closingSpeed)
    {
        if (rescuer == null || struckHolder == null || holder != struckHolder || !IsCurrentGrasp() ||
            bat.dead || !bat.Consious)
            return false;

        float impactT = Mathf.InverseLerp(
            DB_Tuning.RescueImpactMinSpeed,
            DB_Tuning.RescueStrongImpactSpeed,
            closingSpeed);
        float gain = Mathf.Lerp(
            DB_Tuning.RescuePressureGainMin,
            DB_Tuning.RescuePressureGainMax,
            impactT);
        escapePressure = Mathf.Clamp(escapePressure + gain, 0f, escapeThreshold * 1.35f);

        bool forceRelease = closingSpeed >= DB_Tuning.RescueStrongImpactSpeed &&
                            rescuer.Personality.RescueDrive >= DB_Tuning.RescueStrongImpactDrive;
        if (!forceRelease && escapePressure < escapeThreshold) return false;
        return Release(DB_GrabEscapeCause.RescueImpact, closingSpeed);
    }

    internal void ClearTransient()
    {
        holder = null;
        grasp = null;
        holdTicks = 0;
        pulseTicks = 0;
        pulseSerial = 0;
        escapePressure = 0f;
        bat.SandSpit.ClearTransient();
    }

    private bool Release(DB_GrabEscapeCause cause, float impactSpeed)
    {
        if (!IsCurrentGrasp()) return false;

        Player releasedBy = holder;
        Creature.Grasp releasedGrasp = grasp;
        int graspIndex = releasedGrasp.graspUsed;
        Vector2 holderPos = releasedBy.mainBodyChunk?.pos ?? bat.mainBodyChunk.pos - Vector2.up;
        float releaseSpeed = bat.mainBodyChunk.vel.magnitude;

        holder = null;
        grasp = null;
        holdTicks = 0;
        pulseTicks = 0;
        pulseSerial = 0;
        escapePressure = 0f;
        bat.SandSpit.EndPlayerHold();

        // IsCurrentGrasp() was true immediately above, so this exact slot must still own the
        // captured bat. Never call LoseAllGrasps and never release the player's other hand.
        if (graspIndex >= 0 && graspIndex < releasedBy.grasps.Length &&
            ReferenceEquals(releasedBy.grasps[graspIndex], releasedGrasp))
            releasedBy.ReleaseGrasp(graspIndex);
        else
            return false;

        releasedBy.noPickUpOnRelease = Mathf.Max(
            releasedBy.noPickUpOnRelease,
            DB_Tuning.GrabEscapeRegrabBlockTicks);

        Vector2 away = Custom.DirVec(holderPos, bat.mainBodyChunk.pos);
        if (away.sqrMagnitude < 0.01f) away = Vector2.up;
        float impulse = cause == DB_GrabEscapeCause.RescueImpact
            ? Mathf.Lerp(5.2f, 7.6f, Mathf.InverseLerp(
                DB_Tuning.RescueImpactMinSpeed,
                DB_Tuning.RescueStrongImpactSpeed,
                impactSpeed))
            : Mathf.Lerp(
                DB_Tuning.GrabEscapeImpulseMin,
                DB_Tuning.GrabEscapeImpulseMax,
                bat.Personality.EscapeDrive);
        bat.mainBodyChunk.vel += away * impulse + Vector2.up * 1.2f;
        bat.DesertAI.PlayerReleased(releasedBy, Mathf.Max(releaseSpeed, impulse));
        return true;
    }

    private void EndExternalRelease()
    {
        Player releasedBy = holder;
        float releaseSpeed = bat.mainBodyChunk?.vel.magnitude ?? 0f;
        holder = null;
        grasp = null;
        holdTicks = 0;
        pulseTicks = 0;
        pulseSerial = 0;
        escapePressure = 0f;
        bat.SandSpit.EndPlayerHold();
        if (releasedBy != null && !bat.dead && !bat.slatedForDeletetion)
            bat.DesertAI.PlayerReleased(releasedBy, releaseSpeed);
    }

    private bool IsCurrentGrasp()
    {
        if (holder == null || grasp == null || grasp.discontinued || grasp.grabber != holder ||
            grasp.grabbed != bat || holder.grasps == null)
            return false;
        int index = grasp.graspUsed;
        return index >= 0 && index < holder.grasps.Length && ReferenceEquals(holder.grasps[index], grasp);
    }

    private void PrepareThreshold()
    {
        float t = Stable01(0x61C3 + holdSerial * 97);
        escapeThreshold = Mathf.Lerp(
            DB_Tuning.GrabEscapeThresholdMin,
            DB_Tuning.GrabEscapeThresholdMax,
            t);
    }

    private int NextPulseInterval()
    {
        float t = Stable01(0x18D7 + (pulseSerial + 1) * 59 + holdSerial * 17);
        return Mathf.RoundToInt(Mathf.Lerp(
            DB_Tuning.GrabEscapePulseMinTicks,
            DB_Tuning.GrabEscapePulseMaxTicks,
            t));
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

internal enum DB_GrabEscapeCause
{
    SelfStruggle,
    RescueImpact
}
