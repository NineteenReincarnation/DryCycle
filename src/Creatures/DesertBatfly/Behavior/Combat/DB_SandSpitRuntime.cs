using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Player-held defensive sand-spit action. The player-grasp session itself is owned by
/// DB_RestraintRuntime; this class owns only sand meter, windup, cooldown and presentation.
/// </summary>
internal sealed class DB_SandSpitRuntime
{
    private readonly DB_Creature bat;
    private float sandStruggleMeter, sandSpitThreshold;
    private int sandSpitCooldown, sandSpitWindup, sandSpitCycle;

    internal bool WindingUp => sandSpitWindup > 0;
    internal int WindupRemaining => sandSpitWindup;

    internal DB_SandSpitRuntime(DB_Creature bat)
    {
        this.bat = bat;
        PrepareNextSandThreshold();
    }

    internal void PreUpdate()
    {
        if (sandSpitCooldown > 0) sandSpitCooldown--;
    }

    internal void ClearTransient()
    {
        sandStruggleMeter = 0f;
        sandSpitWindup = 0;
    }

    internal void BeginPlayerHold()
    {
        sandStruggleMeter = 0f;
        sandSpitWindup = 0;
        sandSpitCooldown = Mathf.Max(sandSpitCooldown, 18);
        PrepareNextSandThreshold();
    }

    internal void EndPlayerHold()
    {
        sandStruggleMeter = 0f;
        sandSpitWindup = 0;
    }

    internal void UpdateHeldStruggle(Player holder)
    {
        if (holder == null || !bat.Personality.CanSandSpit || bat.dead || !bat.Consious ||
            bat.inShortcut || holder.room != bat.room)
        {
            sandSpitWindup = 0;
            sandStruggleMeter = Mathf.Max(0f, sandStruggleMeter - 0.02f);
            return;
        }

        if (sandSpitWindup > 0)
        {
            sandSpitWindup--;
            if (sandSpitWindup == 0)
                EmitSandSpit(holder);
            return;
        }

        if (sandSpitCooldown > 0) return;

        float movement = holder.mainBodyChunk != null
            ? Mathf.Clamp01(holder.mainBodyChunk.vel.magnitude / 8f)
            : 0f;
        sandStruggleMeter += bat.Personality.SandSpitMeterRate +
            movement * DB_Tuning.SandSpitMovementBonus;

        if (sandStruggleMeter < sandSpitThreshold) return;
        sandStruggleMeter = 0f;
        sandSpitWindup = DB_Tuning.SandSpitWindupTicks;
    }

    private void EmitSandSpit(Player holder)
    {
        if (bat.room == null || holder == null || holder.room != bat.room || bat.dead ||
            !bat.Consious || !bat.Personality.CanSandSpit)
            return;

        int seed = unchecked(bat.Personality.VisualSeed ^ (sandSpitCycle * 1103515245));
        DB_SandBurst.Emit(
            bat.room,
            bat,
            holder,
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
}
