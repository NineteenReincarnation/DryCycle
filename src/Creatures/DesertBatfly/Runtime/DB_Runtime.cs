using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Species-level realized update orchestration around the native Fly update boundary.
/// Rain World lifecycle/base.Update stays on DesertBatfly; this runtime only
/// sequences already-owned domain ticks before and after that native boundary.
/// </summary>
internal sealed class DB_Runtime
{
    private readonly DesertBatfly bat;
    private int socialSampleTicks;

    internal DB_Runtime(DesertBatfly bat)
    {
        this.bat = bat;
    }

    internal Vector2 BeforeVanillaUpdate()
    {
        bat.Injury.Tick();
        Vector2 previousFlightVelocity = bat.mainBodyChunk?.vel ?? Vector2.zero;
        bat.SandSpit.PreUpdate();

        if (!bat.dead)
        {
            bat.DesertState.TickTrauma();
            bat.DesertState.TickGrief();
            if (++socialSampleTicks >= 180)
            {
                socialSampleTicks = 0;
                DB_SocialBond.SampleChain(bat);
            }
        }

        if (bat.room == null)
            return previousFlightVelocity;

        bat.SandSpit.UpdateHeldStruggle();
        bat.DesertState.Thirst = Mathf.Clamp01(
            bat.DesertState.Thirst + (bat.dead ? 0f : DB_Tuning.ThirstPerTick));
        if (bat.DesertState.Cooldown > 0) bat.DesertState.Cooldown--;
        bat.DesertAI.TickMemory();
        if (!bat.dead)
            DesertBatflyIntimidation.UpdateState(bat);

        return previousFlightVelocity;
    }

    internal void AfterVanillaUpdate(bool eu, Vector2 previousFlightVelocity)
    {
        bool extremeVengeance = !bat.dead && DesertBatflyIntimidation.IsExtremeVengeanceActive(bat);
        if (extremeVengeance) bat.DesertAI.CancelAttack();

        if (bat.room == null) return;
        bat.Emergence.Update(eu);
        if (!extremeVengeance)
            bat.DesertAI.Combat.AfterPhysics(eu);
        DB_FlightMotor.ApplyPostPhysics(bat, previousFlightVelocity);
    }
}
