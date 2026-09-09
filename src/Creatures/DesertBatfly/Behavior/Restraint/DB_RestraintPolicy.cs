namespace DryCycle.Creatures.DesertBatfly;

/// <summary>
/// Canonical read-only classification for external grasps that suspend ordinary Desert
/// Batfly behavior. The restraint runtime owns player-held escape state; this policy only
/// answers whether a non-Fly currently owns a physical grasp on the bat.
/// </summary>
internal static class DB_RestraintPolicy
{
    internal static bool IsRestrainedByNonFly(DB_Creature bat)
    {
        if (bat?.grabbedBy == null) return false;
        for (int i = 0; i < bat.grabbedBy.Count; i++)
        {
            Creature.Grasp grasp = bat.grabbedBy[i];
            if (grasp?.grabber != null && grasp.grabber is not Fly)
                return true;
        }
        return false;
    }
}
