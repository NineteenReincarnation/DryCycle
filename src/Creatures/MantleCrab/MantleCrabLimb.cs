using UnityEngine;

namespace DryCycle.Creatures.MantleCrab;

internal sealed class MantleCrabLimb
{
    internal readonly int Index, AnchorChunk;
    internal readonly bool IsPincer;
    internal readonly float Side, StandHeight, Reach, LocalDepth;
    internal readonly float[] Lengths;
    private readonly float[] upperLengths;
    internal readonly Vector2[] Pos = new Vector2[4], LastPos = new Vector2[4], Velocity = new Vector2[4];
    internal Vector2 Tip => Pos[3];
    internal Vector2 TipDirection => (Pos[3] - Pos[2]).normalized;
    internal Vector2 Anchor, LastAnchor, GroundNormal = Vector2.up;
    internal bool Planted { get; private set; }
    private Vector2 contact;
    private bool hasTarget;
    private int searchTick;

    internal MantleCrabLimb(int index, bool pincer)
    {
        Index = index; IsPincer = pincer;
        Side = index % 2 == 0 ? -1f : 1f;
        AnchorChunk = pincer ? 2 : Side < 0 ? 1 : 3;
        LocalDepth = !pincer && index < 2 ? .7f : 0f;
        // Anatomical roles differ by pair and by side, within fixed species proportions.
        Lengths = pincer ? [64f, 84f, 82f, 32f] :
            index switch { 0 => [71f, 119f, 78f, 40f], 1 => [75f, 115f, 81f, 41f],
                2 => [78f, 122f, 73f, 43f], _ => [75f, 118f, 79f, 42f] };
        foreach (float length in Lengths) Reach += length;
        upperLengths = [Lengths[0], Lengths[1], Lengths[2]];
        StandHeight = index < 2 ? 275f : 280f;
    }

    internal void Reset(Vector2 anchor)
    {
        Anchor = LastAnchor = anchor;
        Vector2 previous = anchor;
        for (int i = 0; i < 4; i++)
        {
            Vector2 direction = new Vector2(Side * (i % 2 == 0 ? .7f : -.15f), -1f).normalized;
            Pos[i] = LastPos[i] = previous + direction * Lengths[i];
            Velocity[i] = Vector2.zero;
            previous = Pos[i];
        }
        Planted = hasTarget = false;
        searchTick = Index;
    }

    internal void Update(MantleCrab crab, Vector2 anchor)
    {
        LastAnchor = Anchor; Anchor = anchor;
        for (int i = 0; i < 4; i++) { LastPos[i] = Pos[i]; Pos[i] += Velocity[i] * .12f; }
        Vector2 target;
        if (IsPincer)
        {
            target = anchor + new Vector2(Side * 15f, -240f);
            target = Vector2.Lerp(LastPos[3], target, .14f);
        }
        else
        {
            if (hasTarget && (Vector2.Distance(anchor, contact) > Reach * .97f ||
                !MantleCrabTerrainProbe.StillSupported(crab.room, contact)))
                hasTarget = Planted = false;
            if (!hasTarget && searchTick-- <= 0)
            {
                searchTick = 8 + Index;
                Vector2 desired = anchor + new Vector2(Side * (Index < 2 ? 32f : 12f), -StandHeight);
                hasTarget = MantleCrabTerrainProbe.Find(crab.room, anchor, desired, Reach * .94f,
                    out contact, out GroundNormal);
            }
            target = hasTarget ? Vector2.MoveTowards(LastPos[3], contact, 13f) :
                Vector2.Lerp(LastPos[3], anchor + new Vector2(Side * 26f, -StandHeight), .08f);
        }
        SolvePose(anchor, target, hasTarget && !IsPincer);
        Planted = !IsPincer && hasTarget && Vector2.Distance(Tip, contact) < 1.2f;
        for (int i = 0; i < 4; i++) Velocity[i] = Vector2.ClampMagnitude(Pos[i] - LastPos[i], 18f);
    }

    internal void SolvePose(Vector2 anchor, Vector2 target, bool grounded)
    {
        Vector2 ankle = target + Vector2.Lerp(Vector2.up, GroundNormal, .65f).normalized * Lengths[3];
        if (grounded && Vector2.Distance(anchor, ankle) < Reach - Lengths[3] - .5f)
        {
            // The last link is a load-bearing tarsus, not a freely folding rope link.
            MantleCrabRigMath.Solve(anchor, ankle, upperLengths, Pos);
            Pos[3] = Pos[2] - (ankle - target).normalized * Lengths[3];
        }
        else MantleCrabRigMath.Solve(anchor, target, Lengths, Pos);
    }
}
