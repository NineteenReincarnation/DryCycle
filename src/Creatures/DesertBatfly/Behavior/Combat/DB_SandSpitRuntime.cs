using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Player-held defensive sand-spit state. Owns struggle meter, windup and cooldown;
/// presentation remains DB_SandBurst and ordinary locomotion remains outside this runtime.
/// </summary>
internal sealed class DB_SandSpitRuntime
{
    private readonly DesertBatfly bat;
    private Player playerHolder;
    private float sandStruggleMeter, sandSpitThreshold;
    private int sandSpitCooldown, sandSpitWindup, sandSpitCycle;

    internal bool WindingUp => sandSpitWindup > 0;
    internal int WindupRemaining => sandSpitWindup;

    internal DB_SandSpitRuntime(DesertBatfly bat)
    {
        this.bat = bat;
        PrepareNextSandThreshold();
    }

    internal void PreUpdate()
    {
        TrackPlayerRelease();
        if (sandSpitCooldown > 0) sandSpitCooldown--;
    }

    internal void ClearTransient()
    {
        playerHolder = null;
        sandStruggleMeter = 0f;
        sandSpitWindup = 0;
    }

    internal void UpdateHeldStruggle()
    {
        if (playerHolder == null || !bat.Personality.CanSandSpit || bat.dead || !bat.Consious ||
            bat.inShortcut || playerHolder.room != bat.room)
        {
            sandSpitWindup = 0;
            sandStruggleMeter = Mathf.Max(0f, sandStruggleMeter - 0.02f);
            return;
        }

        if (sandSpitWindup > 0)
        {
            sandSpitWindup--;
            if (sandSpitWindup == 0)
                EmitSandSpit();
            return;
        }

        if (sandSpitCooldown > 0) return;

        float movement = Mathf.Clamp01(playerHolder.mainBodyChunk.vel.magnitude / 8f);
        sandStruggleMeter += bat.Personality.SandSpitMeterRate +
            movement * DB_Tuning.SandSpitMovementBonus;

        if (sandStruggleMeter < sandSpitThreshold) return;
        sandStruggleMeter = 0f;
        sandSpitWindup = DB_Tuning.SandSpitWindupTicks;
    }

    private void EmitSandSpit()
    {
        if (bat.room == null || playerHolder == null || bat.dead || !bat.Consious ||
            !bat.Personality.CanSandSpit) return;

        int seed = unchecked(bat.Personality.VisualSeed ^ (sandSpitCycle * 1103515245));
        DB_SandBurst.Emit(
            bat.room,
            bat,
            playerHolder,
            bat.Personality.SandSpitIntensity,
            seed);

        float cooldownT = Stable01(0x45D9F3B + sandSpitCycle * 17);
        sandSpitCooldown = Mathf.RoundToInt(Mathf.Lerp(
            DB_Tuning.SandSpitCooldownMaxTicks,
            DB_Tuning.SandSpitCooldownMinTicks,
            Mathf.Clamp01(bat.Personality.SandSpitDrive * 0.7f + cooldownT * 0.3f)));

        sandSpitCycle++;
        PrepareNextSandThreshold();
    }

    private void PrepareNextSandThreshold()
    {
        float t = Stable01(0x1F123BB5 + sandSpitCycle * 31);
        sandSpitThreshold = Mathf.Lerp(
            DB_Tuning.SandSpitThresholdMin,
            DB_Tuning.SandSpitThresholdMax,
            t);
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

    internal bool BeginPlayerHold(Player player)
    {
        if (player == null || playerHolder == player) return false;
        playerHolder = player;
        sandStruggleMeter = 0f;
        sandSpitWindup = 0;
        sandSpitCooldown = Mathf.Max(sandSpitCooldown, 18);
        PrepareNextSandThreshold();
        return true;
    }

    private void TrackPlayerRelease()
    {
        if (playerHolder == null) return;

        bool stillHeld = false;
        for (int i = 0; i < bat.grabbedBy.Count; i++)
        {
            if (bat.grabbedBy[i]?.grabber == playerHolder)
            {
                stillHeld = true;
                break;
            }
        }
        if (stillHeld) return;

        Player releasedBy = playerHolder;
        playerHolder = null;
        sandStruggleMeter = 0f;
        sandSpitWindup = 0;
        if (!bat.dead && !bat.slatedForDeletetion)
            bat.DesertAI.PlayerReleased(releasedBy, bat.mainBodyChunk.vel.magnitude);
    }
}
