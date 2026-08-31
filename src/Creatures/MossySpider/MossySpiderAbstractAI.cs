using System.Collections.Generic;
using UnityEngine;

namespace DryCycle.Creatures.MossySpider;

/// <summary>
/// Minimal ecology for MossySpider: no hunting, fear, den-seeking or rain escape.
/// It simply migrates slowly between side-access / off-screen nodes.
/// </summary>
internal sealed class MossySpiderAbstractAI : AbstractCreatureAI
{
    private const int MinimumSideExitWidth = 3;
    private const int RetargetTimeout = 3600;

    private readonly List<WorldCoordinate> allowedSideNodes = new();
    private WorldCoordinate? roamTarget;
    private WorldCoordinate? previousTarget;
    private int retargetCounter;

    internal MossySpiderAbstractAI(World world, AbstractCreature parent)
        : base(world, parent)
    {
        RebuildAllowedNodes();
        retargetCounter = 1;
    }

    /// <summary>
    /// Combined with CreatureTemplate.offScreenSpeed, keeps abstract migration slow.
    /// </summary>
    public override float offscreenSpeedFac => 0.20f;

    internal WorldCoordinate? RoamTarget => roamTarget;

    public override void NewWorld(World newWorld)
    {
        base.NewWorld(newWorld);
        roamTarget = null;
        previousTarget = null;
        retargetCounter = 1;
        RebuildAllowedNodes();
    }

    public override void AbstractBehavior(int time)
    {
        int elapsed = Mathf.Max(1, time);
        retargetCounter -= elapsed;

        if (parent.realizedCreature == null && path.Count > 0)
        {
            FollowPath(elapsed);
            return;
        }

        if (!roamTarget.HasValue || TargetReached() || retargetCounter <= 0)
        {
            PickNewTarget();
        }

        if (!roamTarget.HasValue)
        {
            return;
        }

        if (!destination.CompareDisregardingTile(roamTarget.Value))
        {
            SetDestination(roamTarget.Value);
        }
    }

    internal void ForceRetarget()
    {
        retargetCounter = 0;
    }

    internal void OnRealizedRoomEntered(int roomIndex)
    {
        // sideAccessNodes are node-only coordinates. Reaching the destination room via
        // SideSpace means that migration leg is complete. Pick the next node now, before
        // the realized pather gets a chance to steer back toward the entry border.
        if (roamTarget.HasValue && roamTarget.Value.room == roomIndex)
        {
            PickNewTarget();
        }
    }

    private bool TargetReached()
    {
        if (!roamTarget.HasValue)
        {
            return true;
        }

        // While realized, parent.pos can retain an abstract node even when the physical
        // body is hundreds of pixels away from that border. Treating matching node IDs
        // as arrival caused the target to be replaced repeatedly and made the creature
        // alternate left/right. Realized arrival is handled by NewRoom instead.
        if (parent.realizedCreature != null)
        {
            return false;
        }

        WorldCoordinate target = roamTarget.Value;
        return parent.pos.room == target.room &&
               parent.pos.NodeDefined &&
               parent.pos.abstractNode == target.abstractNode;
    }

    private void PickNewTarget()
    {
        if (allowedSideNodes.Count == 0)
        {
            RebuildAllowedNodes();
        }

        if (allowedSideNodes.Count == 0)
        {
            roamTarget = null;
            retargetCounter = 600;
            return;
        }

        List<WorldCoordinate> candidates = new();
        for (int i = 0; i < allowedSideNodes.Count; i++)
        {
            WorldCoordinate node = allowedSideNodes[i];

            if (parent.pos.NodeDefined &&
                node.room == parent.pos.room &&
                node.abstractNode == parent.pos.abstractNode)
            {
                continue;
            }

            if (previousTarget.HasValue &&
                node.room == previousTarget.Value.room &&
                node.abstractNode == previousTarget.Value.abstractNode &&
                allowedSideNodes.Count > 2)
            {
                continue;
            }

            candidates.Add(node);
        }

        if (candidates.Count == 0)
        {
            candidates.AddRange(allowedSideNodes);
        }

        // Prefer a node outside the current room when possible so the creature
        // actually travels through OFFSCREEN rather than pacing around one room.
        List<WorldCoordinate> interRoom = new();
        for (int i = 0; i < candidates.Count; i++)
        {
            if (candidates[i].room != parent.pos.room)
            {
                interRoom.Add(candidates[i]);
            }
        }

        List<WorldCoordinate> source = interRoom.Count > 0 ? interRoom : candidates;
        WorldCoordinate next = source[Random.Range(0, source.Count)];

        previousTarget = roamTarget;
        roamTarget = next;
        retargetCounter = RetargetTimeout + Random.Range(-600, 901);
        SetDestination(next);
    }

    private void RebuildAllowedNodes()
    {
        allowedSideNodes.Clear();

        if (world?.sideAccessNodes == null)
        {
            return;
        }

        for (int i = 0; i < world.sideAccessNodes.Length; i++)
        {
            WorldCoordinate node = world.sideAccessNodes[i];
            AbstractRoom room = world.GetAbstractRoom(node.room);
            if (room == null ||
                !node.NodeDefined ||
                node.abstractNode < 0 ||
                node.abstractNode >= room.nodes.Length)
            {
                continue;
            }

            if (room.nodes[node.abstractNode].type != AbstractRoomNode.Type.SideExit ||
                room.nodes[node.abstractNode].entranceWidth < MinimumSideExitWidth)
            {
                continue;
            }

            if (room.AttractionForCreature(parent) == AbstractRoom.CreatureRoomAttraction.Forbidden)
            {
                continue;
            }

            allowedSideNodes.Add(node);
        }
    }
}
