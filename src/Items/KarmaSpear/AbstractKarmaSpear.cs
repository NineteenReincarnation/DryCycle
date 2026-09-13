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
        KarmaLevel = Mathf.Clamp(karmaLevel, 1, 10);
        Spent = spent;
    }

    internal int KarmaLevel { get; set; }
    internal bool Spent { get; set; }

    public override string ToString()
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}<oA>DRYCYCLE_KARMA_LEVEL={1}<oA>DRYCYCLE_KARMA_SPENT={2}",
            base.ToString(),
            KarmaLevel,
            Spent ? 1 : 0);
    }
}
