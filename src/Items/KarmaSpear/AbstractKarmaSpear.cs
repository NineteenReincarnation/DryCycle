using System.Globalization;

namespace DryCycle.Items.KarmaSpear;

internal sealed class AbstractKarmaSpear : AbstractSpear
{
    internal AbstractKarmaSpear(
        World world,
        WorldCoordinate pos,
        EntityID id,
        int karmaLevel,
        bool spent)
        : base(world, null, pos, id, explosive: false)
    {
        type = KarmaSpearHooks.ObjectType;
        KarmaLevel = spent ? 0 : Mathf.Clamp(karmaLevel, 0, 10);
        Spent = spent || KarmaLevel <= 0;
    }

    /// <summary>
    /// Stored karmic charge. Unlike the player's karma meter this is a consumable copy:
    /// every Karma Spear activation removes exactly one point until it reaches zero.
    /// </summary>
    internal int KarmaLevel { get; set; }

    internal bool Spent { get; set; }

    public override string ToString()
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}<oA>DRYCYCLE_KARMA_LEVEL={1}<oA>DRYCYCLE_KARMA_SPENT={2}",
            base.ToString(),
            Mathf.Clamp(KarmaLevel, 0, 10),
            Spent || KarmaLevel <= 0 ? 1 : 0);
    }
}
