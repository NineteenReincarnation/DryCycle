using MonoMod.RuntimeDetour;
using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Keeps persistent Bond-death Grief/Trauma on the direct-experience side of Task12.
/// Historical Intimidation tier-1 proximity is no longer enough to learn that a bonded
/// individual died: the observer must actually see the victim/killer or still be part of
/// the same physical Fly chain. Indirect receivers learn only the bounded Alarm signal.
/// </summary>
internal static class DesertBatflySignalDirectWitnessBridge
{
    private delegate void BondDeathOrig(
        DesertBatfly observer,
        DesertBatfly victim,
        Creature killer);
    private delegate void BondDeathDetour(
        BondDeathOrig orig,
        DesertBatfly observer,
        DesertBatfly victim,
        Creature killer);

    private const float DirectWitnessRadius = 340f;
    private static Hook bondDeathHook;

    internal static bool Installed => bondDeathHook != null;

    internal static void Enable()
    {
        if (Installed) return;
        Disable();
        try
        {
            var method = typeof(DesertBatflySocialBond).GetMethod(
                "OnBondPartnerDeath",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Static);
            if (method == null) return;
            bondDeathHook = new Hook(method, (BondDeathDetour)BondDeathHook);
        }
        catch
        {
            Disable();
        }
    }

    internal static void Disable()
    {
        try { bondDeathHook?.Dispose(); } catch { }
        bondDeathHook = null;
    }

    private static void BondDeathHook(
        BondDeathOrig orig,
        DesertBatfly observer,
        DesertBatfly victim,
        Creature killer)
    {
        if (!IsDirectWitness(observer, victim, killer)) return;
        orig(observer, victim, killer);
    }

    internal static bool IsDirectWitness(
        DesertBatfly observer,
        DesertBatfly victim,
        Creature killer)
    {
        if (observer == null || victim == null || observer == victim ||
            observer.dead || !observer.Consious || observer.room == null ||
            victim.room != observer.room)
            return false;

        float distance = Vector2.Distance(
            observer.mainBodyChunk.pos,
            victim.mainBodyChunk.pos);
        if (distance <= DirectWitnessRadius &&
            (observer.room.VisualContact(observer.mainBodyChunk.pos, victim.mainBodyChunk.pos) ||
             (killer?.mainBodyChunk != null && killer.room == observer.room &&
              observer.room.VisualContact(observer.mainBodyChunk.pos, killer.mainBodyChunk.pos))))
            return true;

        return SamePhysicalChain(observer, victim);
    }

    private static bool SamePhysicalChain(Fly a, Fly b)
    {
        if (a == null || b == null || a.room == null || a.room != b.room) return false;

        Fly member = a.FirstInChain();
        int guard = 0;
        while (member != null && guard++ < 32)
        {
            if (member == b) return true;
            member = member.NextInChain();
        }

        member = b.FirstInChain();
        guard = 0;
        while (member != null && guard++ < 32)
        {
            if (member == a) return true;
            member = member.NextInChain();
        }
        return false;
    }
}
