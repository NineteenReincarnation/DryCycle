using UnityEngine;

namespace DryCycle.Creatures.MantleCrab;

internal sealed class MantleCrabLimb
{
    internal readonly int Index, AnchorChunk;
    internal readonly bool IsPincer;
    internal readonly float Side, StandHeight, Reach, LocalDepth;
    internal readonly float[] Lengths;
    private readonly float[] upperLengths;
    internal readonly Vector2[] Rest;
    internal Vector2 RestTipOffset => Rest[4] - Rest[0];
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
        Rest = MantleCrabAnatomy.Landmarks(index, pincer);
        Lengths = new float[4];
        for (int i = 0; i < 4; i++) Lengths[i] = Vector2.Distance(Rest[i], Rest[i + 1]);
        foreach (float length in Lengths) Reach += length;
        upperLengths = [Lengths[0], Lengths[1], Lengths[2]];
        StandHeight = -RestTipOffset.y;
    }

    internal void Reset(Vector2 anchor)
    {
        Anchor = LastAnchor = anchor;
        Vector2 previous = anchor;
        for (int i = 0; i < 4; i++)
        {
            Pos[i] = LastPos[i] = anchor + Rest[i + 1] - Rest[0];
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
            target = anchor + RestTipOffset;
            target = Vector2.Lerp(LastPos[3], target, .14f);
        }
        else
        {
            if (hasTarget && (Vector2.Distance(anchor, contact) > Reach * .999f ||
                !MantleCrabTerrainProbe.StillSupported(crab.room, contact)))
                hasTarget = Planted = false;
            if (!hasTarget && searchTick-- <= 0)
            {
                searchTick = 8 + Index;
                Vector2 desired = anchor + RestTipOffset;
                hasTarget = MantleCrabTerrainProbe.Find(crab.room, anchor, desired, Reach * .995f,
                    out contact, out GroundNormal);
            }
            target = hasTarget ? Vector2.MoveTowards(LastPos[3], contact, 13f) :
                Vector2.Lerp(LastPos[3], anchor + RestTipOffset, .08f);
        }
        SolvePose(anchor, target, hasTarget && !IsPincer);
        Planted = !IsPincer && hasTarget && Vector2.Distance(Tip, contact) < 1.2f;
        for (int i = 0; i < 4; i++) Velocity[i] = Vector2.ClampMagnitude(Pos[i] - LastPos[i], 18f);
    }

    internal void SolvePose(Vector2 anchor, Vector2 target, bool grounded)
    {
        Vector2 displacement = target - (anchor + RestTipOffset);
        float along = 0f;
        for (int i = 0; i < 3; i++)
        {
            along += Lengths[i];
            Vector2 preferred = anchor + Rest[i + 1] - Rest[0] + displacement * (along / Reach);
            Pos[i] = Vector2.Lerp(Pos[i], preferred, .65f);
        }
        Vector2 restNormal = (Rest[3] - Rest[4]).normalized;
        Vector2 endNormal = IsPincer ? restNormal : (restNormal + (GroundNormal - Vector2.up) * .65f).normalized;
        Vector2 ankle = target + endNormal * Lengths[3];
        if ((grounded || IsPincer) && Vector2.Distance(anchor, ankle) < Reach - Lengths[3] - .5f)
        {
            // The foot keeps a bearing face; the passive claw has a downward rest direction.
            MantleCrabRigMath.Solve(anchor, ankle, upperLengths, Pos);
            Pos[3] = Pos[2] - (ankle - target).normalized * Lengths[3];
        }
        else MantleCrabRigMath.Solve(anchor, target, Lengths, Pos);
    }
}
