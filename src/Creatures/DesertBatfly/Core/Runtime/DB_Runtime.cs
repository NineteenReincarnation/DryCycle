using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Species-level realized update orchestration around the native Fly update boundary.
/// Rain World lifecycle/base.Update stays on DesertBatfly; this runtime only
/// sequences already-owned domain ticks before and after that native boundary.
/// </summary>
internal sealed class DB_Runtime
{
    private readonly DB_Creature bat;
    private int socialSampleTicks;

    internal DB_Runtime(DB_Creature bat)
    {
        this.bat = bat;
    }

    internal Vector2 BeforeVanillaUpdate()
    {
        bat.Injury.Tick();
        Vector2 previousFlightVelocity = bat.mainBodyChunk?.vel ?? Vector2.zero;
        bat.SandSpit.PreUpdate();
        bat.Restraint.Tick();

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

        bat.DesertState.Thirst = Mathf.Clamp01(
            bat.DesertState.Thirst + (bat.dead ? 0f : DB_Tuning.ThirstPerTick));
        if (bat.DesertState.Cooldown > 0) bat.DesertState.Cooldown--;
        bat.DesertAI.TickMemory();
        if (!bat.dead)
            DB_FearRuntime.UpdateState(bat);

        // Rescue refresh runs after current fear/vengeance facts are known. It may schedule a
        // Combat-owned rescue intent, but it never writes localGoal or velocity here.
        bat.Rescue.RefreshState();
        return previousFlightVelocity;
    }

    internal void AfterVanillaUpdate(bool eu, Vector2 previousFlightVelocity)
    {
        bool extremeVengeance = !bat.dead && DB_VengeanceRuntime.IsActive(bat);
        if (extremeVengeance) bat.DesertAI.CancelAttack();

        if (bat.room == null) return;
        bat.Emergence.Update(eu);
        if (!extremeVengeance)
        {
            // Feeding pinning is explicit special physics and only runs after the Feeding
            // PrimaryOwner has already been selected/executed by the AI frame.
            bat.Feeding.AfterPhysics(eu);

            // Confirm rescue contact after Fly physics. If the hit succeeds, Rescue moves the
            // bat to Escape before Combat.AfterPhysics can apply ordinary Attach/Interfere.
            bat.Rescue.AfterPhysics();
            bat.DesertAI.Combat.AfterPhysics(eu);
        }
        DB_FlightMotor.ApplyPostPhysics(bat, previousFlightVelocity);
    }
}