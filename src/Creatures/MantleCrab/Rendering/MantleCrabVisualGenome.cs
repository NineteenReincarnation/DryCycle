namespace DryCycle.Creatures.MantleCrab.Rendering;

/// <summary>Named FNV-1a channels followed by integer avalanche; never touches Unity Random.</summary>
internal sealed class MantleCrabVisualGenome
{
    internal readonly int Seed;
    internal readonly float Energy, Bravery, Sympathy, Dominance, Nervous, Aggression;
    internal MantleCrabVisualGenome(AbstractCreature creature)
    {
        Seed = creature.ID.RandomSeed;
        Energy = creature.personality.energy; Bravery = creature.personality.bravery;
        Sympathy = creature.personality.sympathy; Dominance = creature.personality.dominance;
        Nervous = creature.personality.nervous; Aggression = creature.personality.aggression;
    }

    internal float Channel(string name) => Hash(Seed, name);

    internal static float Hash(int seed, string channel)
    {
        unchecked
        {
            uint hash = 2166136261u;
            for (int i = 0; i < channel.Length; i++) { hash ^= channel[i]; hash *= 16777619u; }
            hash ^= (uint)seed; hash ^= hash >> 16; hash *= 0x7feb352du;
            hash ^= hash >> 15; hash *= 0x846ca68bu; hash ^= hash >> 16;
            return (hash & 0xffffff) / 16777216f;
        }
    }
}
