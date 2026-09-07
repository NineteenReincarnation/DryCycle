using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

// No runtime table or relationship graph: all persistent data occupies one state slot.
internal static class DB_SocialBond
{
    internal const float DirectDeathWitnessRadius = 340f;
    internal static bool Available(DesertBatfly bat) => bat != null && !bat.dead &&
        !bat.slatedForDeletetion && bat.room != null && !bat.inShortcut;

    internal static bool CanRespond(DesertBatfly bat)
    {
        if (!Available(bat) || !bat.Consious) return false;
        foreach (var grasp in bat.grabbedBy)
            if (grasp?.grabber != null && grasp.grabber is not Fly) return false;
        return true;
    }

    internal static void AddBond(DesertBatfly source, DesertBatfly target, float gain)
    {
        if (!Available(source) || !Available(target) || source == target || source.room != target.room) return;
        source.DesertState.StrengthenBond(target.abstractCreature.ID, gain);
    }

    internal static float GetBondStrength(DesertBatfly source, DesertBatfly target) =>
        source != null && target != null ? source.DesertState.BondStrength(target.abstractCreature.ID) : 0f;

    internal static void OnSuccessfulRescue(DesertBatfly rescuer, DesertBatfly victim)
    {
        AddBond(victim, rescuer, 0.30f * Mathf.Lerp(0.96f, 1.04f, victim.Personality.Conformity));
        AddBond(rescuer, victim, 0.12f * Mathf.Lerp(0.96f, 1.04f, rescuer.Personality.Conformity));
    }

    internal static bool TryResolveBondPartner(DesertBatfly source, out DesertBatfly partner)
    {
        partner = null;
        if (!Available(source) || !source.DesertState.SocialBondTarget.HasValue) return false;
        foreach (Fly member in DB_SwarmRoom.For(source.room).Hive.flies)
            if (member is DesertBatfly candidate && candidate != source && Available(candidate) &&
                candidate.room == source.room && GetBondStrength(source, candidate) > 0f)
            { partner = candidate; return true; }
        return false;
    }

    // Called only every 180 realized ticks. NextInChain is the direct physical neighbour.
    internal static void SampleChain(DesertBatfly source)
    {
        if (!Available(source) || !source.Consious || source.AI?.behavior != FlyAI.Behavior.Chain) return;
        DesertBatfly next = source.NextInChain() as DesertBatfly;
        if (next == null && source.grasps != null && source.grasps.Length > 0)
            next = source.grasps[0]?.grabbed as DesertBatfly;
        if (next != null && CanRespond(source) && CanRespond(next) && next.AI?.behavior == FlyAI.Behavior.Chain)
            AddBond(source, next, 0.004f);
    }

    // Evaluated at the existing roost scan, and only changes willingness at a valid local hang point.
    internal static float RoostScale(DesertBatfly source)
    {
        var state = source.DesertState;
        if (!Available(source) || !source.Consious ||
            state.PlayerTraumaStrength >= DB_Tuning.TraumaSevere ||
            state.PredatorTraumaStrength >= DB_Tuning.TraumaSevere ||
            DesertBatflyIntimidation.IsExtremeVengeanceActive(source)) return 1f;
        float scale = state.GriefRoostScale;
        if (TryResolveBondPartner(source, out var partner) && partner.AI?.behavior == FlyAI.Behavior.Chain &&
            Vector2.Distance(source.mainBodyChunk.pos, partner.mainBodyChunk.pos) < 100f &&
            source.room.VisualContact(source.mainBodyChunk.pos, partner.mainBodyChunk.pos))
            scale *= 1f + 0.35f * Mathf.InverseLerp(0.30f, 1f, GetBondStrength(source, partner));
        return scale;
    }

    internal static float Motivation(DesertBatfly source, DesertBatfly victim, Creature threat)
    {
        if (source == null) return 0f;
        float grief = source.DesertState.GriefThreatIdentity.HasValue && threat?.abstractCreature != null &&
            source.DesertState.GriefThreatIdentity.Value.spawner == threat.abstractCreature.ID.spawner &&
            source.DesertState.GriefThreatIdentity.Value.number == threat.abstractCreature.ID.number
            ? source.DesertState.GriefStrength * source.DesertState.GriefAnger * 0.15f : 0f;

        float signal = 0f;
        if (DB_SignalRuntime.TryGetInfluence(source, out DB_SignalInfluence influence))
        {
            if (victim != null && influence.DistressSource == victim)
                signal += influence.DistressInterest * 0.20f;
            if (threat != null && influence.RallyTarget == threat)
                signal += influence.RallyInterest * 0.16f;
        }

        return GetBondStrength(source, victim) * 0.18f + grief + signal;
    }

    internal static bool IsDirectDeathWitness(
        DesertBatfly observer,
        DesertBatfly victim,
        Creature killer)
    {
        if (!Available(observer) || !observer.Consious || victim == null || observer == victim ||
            observer.room == null || victim.room != observer.room)
            return false;

        float distance = Vector2.Distance(observer.mainBodyChunk.pos, victim.mainBodyChunk.pos);
        if (distance <= DirectDeathWitnessRadius &&
            (observer.room.VisualContact(observer.mainBodyChunk.pos, victim.mainBodyChunk.pos) ||
             (killer?.mainBodyChunk != null && killer.room == observer.room &&
              observer.room.VisualContact(observer.mainBodyChunk.pos, killer.mainBodyChunk.pos))))
            return true;

        return SamePhysicalChain(observer, victim);
    }

    internal static void OnBondPartnerDeath(DesertBatfly observer, DesertBatfly victim, Creature killer)
    {
        if (victim == null || !victim.dead || !IsDirectDeathWitness(observer, victim, killer)) return;
        float gain = observer.DesertState.BeginGrief(victim.abstractCreature.ID, killer?.abstractCreature?.ID);
        if (gain <= 0f) return;
        DesertBatflyIntimidation.AddTrauma(observer, killer, gain);
        observer.DesertAI.BeginGriefResponse();
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
