using RWCustom;
using UnityEngine;

namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Realized-room creature perception for the Desert Batfly brain.
/// Owns scan cadence, current ordinary danger observation and approach pursuit evidence;
/// it does not own locomotion or behavior arbitration.
/// </summary>
internal sealed class DB_CreaturePerception
{
    private readonly DB_AI brain;
    private readonly DB_Creature fly;
    private int scan, pursuit;

    internal Creature Danger { get; private set; }
    internal bool IsScanFrame => scan == 0;

    internal DB_CreaturePerception(DB_AI brain, DB_Creature fly)
    {
        this.brain = brain;
        this.fly = fly;
    }

    internal void Reset()
    {
        Danger = null;
        scan = pursuit = 0;
    }

    internal void ClearPursuit() => pursuit = 0;

    internal void UpdateScan()
    {
        if (++scan < 8) return;
        scan = 0;
        ScanCreatures();
    }

    internal bool Valid(Creature creature)
    {
        return creature != null && !creature.dead &&
               !creature.slatedForDeletetion && creature.room == fly.room &&
               !creature.inShortcut && creature.grabbedBy.Count == 0 &&
               (creature.abstractCreature.rippleLayer == fly.abstractCreature.rippleLayer ||
                creature.abstractCreature.rippleBothSides ||
                fly.abstractCreature.rippleBothSides);
    }

    private void ScanCreatures()
    {
        Danger = null;
        brain.Combat.BeginCandidateScan();

        DB_RoomContext context = DB_RoomContext.For(fly.room);
        var creatures = context?.Creatures;
        if (creatures == null) return;

        for (int i = 0; i < creatures.Count; i++)
        {
            Creature creature = creatures[i];
            if (creature == fly || creature is DB_Creature || !Valid(creature))
                continue;

            float distance = Vector2.Distance(fly.mainBodyChunk.pos, creature.mainBodyChunk.pos);
            DB_VisibilityChannel channel = creature is Player
                ? DB_VisibilityChannel.Player
                : DB_VisibilityChannel.Creature;
            if (distance > DB_Tuning.SightRange ||
                !DB_VisibilityPolicy.CanObserve(
                    fly, creature.mainBodyChunk.pos, DB_Tuning.SightRange, channel))
                continue;

            CreatureTemplate.Relationship relation = fly.Template.CreatureRelationship(creature.Template);
            CreatureTemplate.Relationship reverse = creature.Template.CreatureRelationship(fly.Template);
            bool predator = creature is not Player &&
                (relation.type == CreatureTemplate.Relationship.Type.Afraid ||
                 reverse.type == CreatureTemplate.Relationship.Type.Eats ||
                 reverse.type == CreatureTemplate.Relationship.Type.Attacks);

            if (predator)
            {
                float ordinaryThreatDistance = Mathf.Lerp(90f, 260f, Mathf.Clamp01(creature.TotalMass));
                float nerveScale = Mathf.Lerp(1.15f, 0.58f, fly.Personality.Nerve);
                float threatDistance = Mathf.Max(55f, ordinaryThreatDistance * nerveScale);
                if (distance < threatDistance) Danger = creature;
            }

            if (creature is Player player)
            {
                bool traumatized = brain.IsTraumatizedPlayer(player);
                bool remembered = !traumatized && brain.IsRememberedPlayer(player);
                if (remembered && !fly.Personality.Aggressive)
                {
                    float fearDistance = Mathf.Lerp(
                        DB_Tuning.GrabFearMinDistance,
                        DB_Tuning.GrabFearMaxDistance,
                        fly.DesertState.GrabMemoryStrength);
                    fearDistance *= Mathf.Lerp(1.12f, 0.72f, fly.Personality.Nerve);
                    if (distance < fearDistance) Danger = player;
                }

                float reactionDistance = Mathf.Lerp(125f, 78f, fly.Personality.Nerve);
                float closingThreshold = Mathf.Lerp(2.1f, 4.4f, fly.Personality.Nerve);
                int pursuitThreshold = Mathf.RoundToInt(Mathf.Lerp(16f, 44f, fly.Personality.Nerve));
                if (remembered && DB_EnvironmentalPolicy.AggressionAuthorized(fly))
                {
                    reactionDistance *= 0.72f;
                    closingThreshold *= 1.25f;
                    pursuitThreshold = Mathf.RoundToInt(pursuitThreshold * 1.35f);
                }

                if (distance < reactionDistance)
                {
                    float closing = Vector2.Dot(
                        player.mainBodyChunk.vel,
                        Custom.DirVec(player.mainBodyChunk.pos, fly.mainBodyChunk.pos));
                    if (closing > closingThreshold) pursuit += 8;
                    else pursuit = Mathf.Max(0, pursuit - 4);
                    if (pursuit >= pursuitThreshold)
                    {
                        brain.DisturbedByApproach(player);
                        pursuit = 0;
                    }
                }
                else
                {
                    pursuit = Mathf.Max(0, pursuit - 2);
                }
            }

            brain.Combat.ConsiderCandidate(creature, distance);
        }

        brain.Combat.CompleteCandidateScan(brain.RetreatActive);
    }
}
