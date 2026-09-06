using DryCycle.Creatures.DesertBatfly;

namespace DryCycle.Debugging.AI;

// Fast providers are bound to tracked slots and may run once per Rain World simulation
// tick. They must remain allocation-free and read-only. Rich/Heavy providers will be
// separate lower-frequency paths; do not grow this interface into a snapshot builder.
internal interface IAIDebugRecorderFastProvider
{
    void CaptureFast(AbstractCreature creature, ref AIDebugFastState state);
}

internal static class AIDebugRecorderProviderRegistry
{
    private static readonly IAIDebugRecorderFastProvider GenericProvider = new GenericRecorderFastProvider();
    private static readonly IAIDebugRecorderFastProvider DesertBatflyProvider = new DesertBatflyRecorderFastProvider();

    internal static IAIDebugRecorderFastProvider Resolve(AbstractCreature creature)
    {
        return creature?.realizedCreature is DesertBatfly ? DesertBatflyProvider : GenericProvider;
    }

    private sealed class GenericRecorderFastProvider : IAIDebugRecorderFastProvider
    {
        public void CaptureFast(AbstractCreature creature, ref AIDebugFastState state)
        {
            // Generic Rain World AI has no universal behavior/target field. The base
            // fingerprint still captures lifecycle and destination exactly. Species- or
            // AI-specific providers can add mode/target tokens without inventing data.
        }
    }

    private sealed class DesertBatflyRecorderFastProvider : IAIDebugRecorderFastProvider
    {
        public void CaptureFast(AbstractCreature creature, ref AIDebugFastState state)
        {
            if (creature?.realizedCreature is not DesertBatfly bat) return;

            Creature target = bat.DesertAI.Target;
            int targetSpawner = AIDebugFastState.UnknownToken;
            int targetNumber = AIDebugFastState.UnknownToken;
            if (target?.abstractCreature != null)
            {
                targetSpawner = target.abstractCreature.ID.spawner;
                targetNumber = target.abstractCreature.ID.number;
            }

            AIDebugFastFlags flags = AIDebugFastFlags.None;
            if (bat.DesertAI.FormalAttack) flags |= AIDebugFastFlags.FormalAttack;
            if (bat.DesertAI.HasImmediateDanger) flags |= AIDebugFastFlags.HasImmediateDanger;

            state = state.WithSpeciesState(
                (int)bat.DesertAI.Mode,
                targetSpawner,
                targetNumber,
                flags);
        }
    }
}
